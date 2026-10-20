using PersonalHub.Client.Controls;
using PersonalHub.Client.Data;
using PersonalHub.Client.Services;

namespace PersonalHub.Client
{
    /// <summary>
    /// 回顾：三个数（投入 / 方向 / 节律）+ 停滞目标处理 + 奖励自决。
    ///
    /// 为什么把"坟场"和"奖励"放在同一页：这两件事都是**低频、需要一次决断**的动作。
    /// 分开做只会两个都不做。
    ///
    /// 三条克制：
    ///   1. 三个数不合成为一个总分（有总分就会优化分数而不是生活）；
    ///   2. 奖励只勾选、不自动判定（系统不判断你配不配）；
    ///   3. 不做惩罚（有惩罚就会开始隐瞒数据，整库废掉）。
    ///
    /// 口径说明：手机只有 30 天镜像，所以这里算"最近四周"。全量的季度统计要等
    /// 服务端出聚合接口 —— 宁可标注清楚，也不给一个看着像季度、其实是四周的数。
    /// </summary>
    public partial class QuarterReviewPage : ContentPage
    {
        private const int StalledDays = 14;

        private static readonly string[] Rewards =
        {
            "半天空闲，不设任何记录",
            "买一本想看的书",
            "一次短途出行",
        };

        private readonly LocalRecordStore<LocalGoal> _goals;
        private readonly LocalRecordStore<LocalBlock> _blocks;
        private readonly RecordSyncService<LocalGoal> _goalSync;

        public QuarterReviewPage(
            LocalRecordStore<LocalGoal> goals,
            LocalRecordStore<LocalBlock> blocks,
            RecordSyncService<LocalGoal> goalSync)
        {
            InitializeComponent();

            _goals = goals;
            _blocks = blocks;
            _goalSync = goalSync;
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();
            await LoadAsync();
        }

        private async Task LoadAsync()
        {
            var (from, to) = Period.LastDays(28);
            var blocks = await _blocks.GetByRangeAsync(from, to);
            var goals = await _goals.GetByRangeAsync(0, long.MaxValue);

            // ---- 三个数 ----
            int minutes = blocks.Sum(b => Math.Max(0, b.DurationMin));
            InvestLabel.Text = minutes > 0 ? BlockEntryPage.FormatMinutes(minutes) : "还没记";

            int rated = blocks.Count(b => b.Worth is >= 1 and <= 3);
            int good = blocks.Count(b => b.Worth == 1);
            RateLabel.Text = rated == 0 ? "—" : $"{good * 100.0 / rated:F0}%";

            var monday = Period.StartOfDay(DateTimeOffset.UtcNow);
            monday = monday.AddDays(-(((int)monday.DayOfWeek + 6) % 7)).AddDays(-21);

            int activeWeeks = 0;
            for (int i = 0; i < 4; i++)
            {
                long wf = monday.AddDays(i * 7).ToUnixTimeMilliseconds();
                long wt = monday.AddDays((i + 1) * 7).ToUnixTimeMilliseconds();
                if (blocks.Any(b => b.OccurredAt >= wf && b.OccurredAt < wt)) activeWeeks++;
            }
            WeeksLabel.Text = $"{activeWeeks} / 4";

            // ---- 停滞目标 ----
            var (f30, t30) = Period.LastDays(30);
            var recent = await _blocks.GetByRangeAsync(f30, t30);
            long stalledBefore = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                                 - StalledDays * 24L * 60 * 60 * 1000;

            var stalled = goals
                .Where(g => g.Status == 0)
                .Select(g =>
                {
                    long last = recent.Where(b => b.GoalId == g.Id)
                        .Select(b => b.OccurredAt)
                        .DefaultIfEmpty(0)
                        .Max();
                    long marker = last != 0 ? last : g.StartedAt;
                    return (Goal: g, Last: marker);
                })
                .Where(x => x.Last != 0 && x.Last < stalledBefore)
                .OrderBy(x => x.Last)
                .ToList();

            StalledPanel.Children.Clear();
            StalledEmpty.IsVisible = stalled.Count == 0;

            foreach (var (goal, last) in stalled)
            {
                int days = (int)((DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - last) / (24L * 60 * 60 * 1000));
                StalledPanel.Children.Add(BuildStalledRow(goal, days));
            }

            BuildRewardPanel();
        }

        private View BuildStalledRow(LocalGoal goal, int days)
        {
            var info = new VerticalStackLayout { Spacing = 1 };
            info.Children.Add(TextPeek.Attach(new Label
            {
                Text = goal.Title,
                FontSize = 14,
                LineBreakMode = LineBreakMode.TailTruncation,
            }));
            info.Children.Add(TextPeek.Attach(new Label
            {
                Text = $"{days} 天没动" + (string.IsNullOrEmpty(goal.NextStep) ? "" : " · 卡在：" + goal.NextStep),
                FontSize = 11,
                TextColor = Color.FromArgb("#8A8A8E"),
                LineBreakMode = LineBreakMode.TailTruncation,
            }));

            var handle = new Button
            {
                Text = "处理",
                FontSize = 12,
                HeightRequest = 32,
                Padding = new Thickness(12, 0),
                BackgroundColor = Colors.Transparent,
                BorderWidth = 1,
                BorderColor = Color.FromArgb("#D9D3E8"),
                TextColor = Color.FromArgb("#5F5E5A"),
                CommandParameter = goal.Id,
            };
            handle.Clicked += OnHandleStalledClicked;

            var grid = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(GridLength.Auto),
                },
                ColumnSpacing = 8,
            };
            grid.Add(info, 0);
            grid.Add(handle, 1);

            return new Border
            {
                Stroke = Color.FromArgb("#E3E0D8"),
                StrokeThickness = 1,
                Padding = new Thickness(14, 10),
                Content = grid,
            };
        }

        private async void OnHandleStalledClicked(object? sender, EventArgs e)
        {
            if (sender is not Button b || b.CommandParameter is not string id) return;

            var goal = await _goals.FindAsync(id);
            if (goal is null) return;

            string pick = await DisplayActionSheet(goal.Title, "取消", null, "改小", "暂停", "放弃");

            switch (pick)
            {
                case "改小":
                    string? input = await TextEditorPage.ShowAsync("改小一点",
                        initial: goal.Title,
                        placeholder: "改成一句更小的标题（比如只读前 6 章）",
                        compact: true,
                        maxLength: 120);
                    if (string.IsNullOrWhiteSpace(input)) return;
                    goal.Title = input;
                    break;

                case "暂停":
                    goal.Status = 1;
                    break;

                case "放弃":
                {
                    bool ok = await DisplayAlert("放弃这个目标？",
                        $"{goal.Title}\n\n放弃可逆（以后能恢复），历史记录都保留。砍掉不是失败，是把注意力收回来。",
                        "放弃", "取消");
                    if (!ok) return;
                    goal.Status = 3;
                    break;
                }

                default:
                    return;
            }

            goal.IsCurrent = goal.Status == 0 ? goal.IsCurrent : false;
            goal.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            goal.Synced = false;

            await _goals.UpdateAsync(goal);
            _ = _goalSync.SyncAsync();

            StatusLabel.Text = pick == "改小" ? "已改小，记一笔它就会重新算作活跃" : $"已{pick}";
            await LoadAsync();
        }

        private void BuildRewardPanel()
        {
            RewardPanel.Children.Clear();
            string quarter = QuarterKey();

            for (int i = 0; i < Rewards.Length; i++)
            {
                string key = $"ph.reward.{quarter}.{i}";
                bool done = Preferences.Default.Get(key, false);

                var btn = new Button
                {
                    Text = (done ? "✓ " : "○ ") + Rewards[i],
                    FontSize = 13,
                    HeightRequest = 40,
                    CornerRadius = 10,
                    BackgroundColor = done ? Color.FromArgb("#EAF3DE") : Color.FromArgb("#F1EFE8"),
                    TextColor = done ? Color.FromArgb("#3B6D11") : Color.FromArgb("#2C2C2A"),
                    CommandParameter = key,
                };
                btn.Clicked += (s, _) =>
                {
                    if (s is not Button b || b.CommandParameter is not string k) return;
                    bool now = !Preferences.Default.Get(k, false);
                    Preferences.Default.Set(k, now);
                    BuildRewardPanel();
                };

                RewardPanel.Children.Add(btn);
            }
        }

        /// <summary>例如 2026-Q4。奖励按季度记，不会跨季度累积。</summary>
        private static string QuarterKey()
        {
            var now = DateTimeOffset.UtcNow.ToOffset(Period.Cst);
            int q = (now.Month - 1) / 3 + 1;
            return $"{now.Year}-Q{q}";
        }
    }
}
