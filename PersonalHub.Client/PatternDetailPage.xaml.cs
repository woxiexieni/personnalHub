using PersonalHub.Client.Data;
using PersonalHub.Client.Services;

namespace PersonalHub.Client
{
    /// <summary>
    /// 模式详情：四段拆解 + 三个次数 + 归因反查 + 练习时间线。
    ///
    /// 三样刻意不做的东西：
    ///   成功率 —— 有比率就会开始不记失败那次；
    ///   阶段字段 —— 阶段是推断出来的，存下来两个月后必然过期；
    ///   每日打分 —— 一天点一下就够了，评分会让人开始糊弄。
    /// </summary>
    [QueryProperty(nameof(PatternId), "id")]
    public partial class PatternDetailPage : ContentPage
    {
        private const int MinSample = 10;      // 少于 10 次不给归因结论，防"新迷信"
        private const long WindowMs = 3L * 60 * 60 * 1000;   // 前后 3 小时

        private readonly LocalRecordStore<LocalPattern> _patterns;
        private readonly LocalRecordStore<LocalPractice> _practices;
        private readonly LocalRecordStore<LocalBlock> _blocks;
        private readonly RecordSyncService<LocalPattern> _patternSync;
        private readonly RecordSyncService<LocalPractice> _practiceSync;

        private LocalPattern? _pattern;
        private List<LocalPractice> _mine = new();

        public string PatternId { get; set; } = string.Empty;

        public PatternDetailPage(
            LocalRecordStore<LocalPattern> patterns,
            LocalRecordStore<LocalPractice> practices,
            LocalRecordStore<LocalBlock> blocks,
            RecordSyncService<LocalPattern> patternSync,
            RecordSyncService<LocalPractice> practiceSync)
        {
            InitializeComponent();

            _patterns = patterns;
            _practices = practices;
            _blocks = blocks;
            _patternSync = patternSync;
            _practiceSync = practiceSync;
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();
            await LoadAsync();
        }

        private async Task LoadAsync()
        {
            if (string.IsNullOrEmpty(PatternId)) return;

            _pattern = await _patterns.FindAsync(PatternId);
            if (_pattern is null)
            {
                TitleLabel.Text = "模式不在了";
                StateLabel.Text = "可能已在另一台设备上删除";
                return;
            }

            TitleLabel.Text = _pattern.Title;
            StateLabel.Text = StateText(_pattern.Status);
            PauseBtn.Text = _pattern.Status == 1 ? "恢复" : "暂停";

            TriggerLabel.Text = Dash(_pattern.Trigger);
            OldLabel.Text = Dash(_pattern.OldResponse);
            NewLabel.Text = Dash(_pattern.NewResponse);
            RewardLabel.Text = Dash(_pattern.RewardKept);

            var (from, to) = Period.LastDays(30);
            var all = await _practices.GetByRangeAsync(from, to);
            _mine = all.Where(x => x.PatternId == _pattern.Id)
                .OrderByDescending(x => x.OccurredAt)
                .ToList();

            int good = _mine.Count(x => x.Result == 1);
            int back = _mine.Count(x => x.Result == 0);

            GoodLabel.Text = good.ToString();
            BackLabel.Text = back.ToString();
            StreakLabel.Text = WeekStreak().ToString();
            StageLabel.Text = "推断阶段：" + InferStage(good, back) +
                              "（系统猜的，不同意就当没看见）";

            BuildPracticeTimeline();
            await BuildInsightAsync(back);

            _ = SyncAsync();
        }

        private static string Dash(string s) => string.IsNullOrWhiteSpace(s) ? "—" : s;

        private static string StateText(int status) => status switch
        {
            1 => "已暂停",
            2 => "已自动化",
            3 => "已放弃",
            _ => "在练",
        };

        /// <summary>
        /// 连续周数：从本周往回数，每周至少 1 次练习（新反应或旧反应都算在练）。
        /// 断一周就停 —— 这不是打卡，只是让人知道自己最近还在不在做。
        /// </summary>
        private int WeekStreak()
        {
            var today = Period.StartOfDay(DateTimeOffset.UtcNow);
            var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));

            int streak = 0;
            for (int i = 0; i < 52; i++)
            {
                var start = monday.AddDays(-7 * i);
                long from = start.ToUnixTimeMilliseconds();
                long to = start.AddDays(7).ToUnixTimeMilliseconds();

                bool any = _mine.Any(x => x.OccurredAt >= from && x.OccurredAt < to);
                if (!any)
                {
                    // 本周还没练不算断；往前更早的周没练才是断
                    if (i == 0) continue;
                    break;
                }
                streak++;
            }
            return streak;
        }

        /// <summary>TTM 阶段：只按次数与连续周推断，永远不是用户维护的字段。</summary>
        private string InferStage(int good, int back)
        {
            int streak = WeekStreak();

            if (streak >= 8 && back == 0) return "可能已经自动化了";
            if (streak >= 3 && good >= 2) return "维持期";
            if (good + back <= 2) return "行动期（刚开始）";
            return "准备期（还在试）";
        }

        private void BuildPracticeTimeline()
        {
            PracticePanel.Children.Clear();

            if (_mine.Count == 0)
            {
                PracticePanel.Children.Add(new Label
                {
                    Text = "还没有练习记录。在时间块页顶部点一下「守住了」就会出现在这里。",
                    FontSize = 12,
                    TextColor = Color.FromArgb("#8A8A8E"),
                    Margin = new Thickness(0, 4),
                });
                return;
            }

            foreach (var p in _mine.Take(20))
            {
                bool ok = p.Result == 1;
                var local = Period.ToLocalTime(p.OccurredAt);

                var row = new Grid
                {
                    ColumnDefinitions =
                    {
                        new ColumnDefinition(new GridLength(88)),
                        new ColumnDefinition(GridLength.Star),
                        new ColumnDefinition(GridLength.Auto),
                    },
                    Padding = new Thickness(0, 6),
                    ColumnSpacing = 8,
                };

                row.Add(new Label
                {
                    Text = local.ToString("MM-dd HH:mm"),
                    FontSize = 11,
                    TextColor = Color.FromArgb("#8A8A8E"),
                    VerticalOptions = LayoutOptions.Center,
                }, 0);

                row.Add(new Label
                {
                    Text = ok ? "守住了" : "又回去了",
                    FontSize = 13,
                    VerticalOptions = LayoutOptions.Center,
                }, 1);

                row.Add(new Label
                {
                    Text = ok ? "新反应" : "旧反应",
                    FontSize = 11,
                    TextColor = ok ? Color.FromArgb("#3B6D11") : Color.FromArgb("#A32D2D"),
                    VerticalOptions = LayoutOptions.Center,
                }, 2);

                PracticePanel.Children.Add(row);
            }
        }

        /// <summary>
        /// 归因反查（本系统的核心价值之一）：
        /// 每次"又回去了"的时刻，往前/往后 3 小时窗口内找时长最长的那个时间块，
        /// 统计它们落在哪些片后面 —— 人认不出自己的触发，数据可以。
        ///
        /// 样本 < 10 次不给结论：12 次里的 8 次会被当成规律，那是新的迷信。
        /// </summary>
        private async Task BuildInsightAsync(int backCount)
        {
            InsightCard.IsVisible = false;
            if (backCount < MinSample) return;

            var (from, to) = Period.LastDays(BlockMirrorService.WindowDays);
            var blocks = await _blocks.GetByRangeAsync(from, to);
            if (blocks.Count == 0) return;

            var tally = new Dictionary<string, int>(StringComparer.Ordinal);
            int matched = 0;

            foreach (var p in _mine.Where(x => x.Result == 0))
            {
                long lo = p.OccurredAt - WindowMs;
                long hi = p.OccurredAt + WindowMs;

                var near = blocks
                    .Where(b => b.OccurredAt >= lo && b.OccurredAt <= hi)
                    .OrderByDescending(b => Math.Max(0, b.DurationMin))
                    .FirstOrDefault();

                if (near is null) continue;

                matched++;
                tally.TryGetValue(near.PieceCode, out int n);
                tally[near.PieceCode] = n + 1;
            }

            if (matched == 0) return;

            var top = tally.OrderByDescending(kv => kv.Value).First();
            if (top.Value < 2) return;

            string name = Pieces.ByCode(top.Key)?.DefaultName ?? top.Key;
            InsightLabel.Text =
                $"你标记「又回去了」的 {backCount} 次里，{top.Value} 次紧挨着「{name}」前后 3 小时。\n" +
                "这不是让你少做这件事，而是说 —— 触发可能就在它后面那段时间，而不是你的性格。";
            InsightCard.IsVisible = true;
        }

        // ============================================================
        // 操作
        // ============================================================

        private async void OnEditClicked(object? sender, EventArgs e)
        {
            if (_pattern is null) return;

            string pick = await DisplayActionSheet("改哪一段", "取消", null,
                "触发场景", "旧反应", "新反应（要练的动作）", "保留的回报");

            switch (pick)
            {
                case "触发场景":
                    await EditFieldAsync("触发场景", _pattern.Trigger,
                        v => _pattern.Trigger = v);
                    break;
                case "旧反应":
                    await EditFieldAsync("旧反应", _pattern.OldResponse,
                        v => _pattern.OldResponse = v);
                    break;
                case "新反应（要练的动作）":
                    await EditFieldAsync("新反应", _pattern.NewResponse,
                        v => _pattern.NewResponse = string.IsNullOrWhiteSpace(v) ? _pattern.NewResponse : v);
                    break;
                case "保留的回报":
                    await EditFieldAsync("保留的回报", _pattern.RewardKept,
                        v => _pattern.RewardKept = v);
                    break;
            }
        }

        private async Task EditFieldAsync(string title, string current, Action<string> apply)
        {
            if (_pattern is null) return;

            string? input = await DisplayPromptAsync(title, "一句话就够",
                initialValue: current, maxLength: 100, keyboard: Keyboard.Text);
            if (input is null) return;

            apply(input.Trim());
            _pattern.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _pattern.Synced = false;

            await _patterns.UpdateAsync(_pattern);
            await LoadAsync();
            _ = _patternSync.SyncAsync();
        }

        private async void OnPauseClicked(object? sender, EventArgs e)
        {
            if (_pattern is null) return;

            if (_pattern.Status == 0)
            {
                _pattern.Status = 1;
            }
            else
            {
                // 恢复成活跃：先看名额（服务端也会拦，但那边拦了用户看不见原因）
                var all = await _patterns.GetByRangeAsync(0, long.MaxValue);
                if (all.Count(p => p.Status == 0 && p.Id != _pattern.Id) >= 3)
                {
                    StatusLabel.Text = "已有 3 个模式在练，先暂停一个";
                    return;
                }
                _pattern.Status = 0;
            }

            _pattern.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _pattern.Synced = false;

            await _patterns.UpdateAsync(_pattern);
            await LoadAsync();
            _ = _patternSync.SyncAsync();
        }

        private async void OnDeleteClicked(object? sender, EventArgs e)
        {
            if (_pattern is null) return;

            bool ok = await DisplayAlert("删除这个模式？",
                $"{_pattern.Title}\n\n只删模式本身，已有的练习记录会保留（它们不再属于任何模式）。",
                "删除", "取消");
            if (!ok) return;

            if (_pattern.Synced)
            {
                _pattern.Deleted = true;
                _pattern.Synced = false;
                _pattern.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                await _patterns.UpdateAsync(_pattern);
            }
            else
            {
                await _patterns.DeleteAsync(_pattern.Id);
            }

            _ = _patternSync.SyncAsync();
            await Shell.Current.GoToAsync("..");
        }

        private async Task SyncAsync()
        {
            try
            {
                await _patternSync.SyncAsync();
                await _practiceSync.SyncAsync();
            }
            catch
            {
                // 离线不影响阅读
            }
        }
    }
}
