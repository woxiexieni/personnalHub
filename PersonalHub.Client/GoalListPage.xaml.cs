using Personalhub;
using PersonalHub.Client.Controls;
using PersonalHub.Client.Data;
using PersonalHub.Client.Services;

namespace PersonalHub.Client
{
    /// <summary>
    /// 目标列表（长目标层，极简）。
    ///
    /// 这一页刻意只有：名字、一条下一步、三样算出来的数字。
    /// **没有进度百分比、没有截止日期、没有子任务** ——
    /// 前两个要人估（一估就假），后一个会引入规划成本（成本一高录入就停）。
    ///
    /// 排序与折叠：
    ///   「在推进」= 当前目标优先，其余按最后活动倒序；
    ///   「停滞」= 14 天没有任何块，自动折叠进去，动一下自动回来。
    ///   停滞不是一种状态（表里没有这个字段），是算出来的 ——
    ///   任何需要你手动标记"我还想继续"的设计，最后都会变成你没去标记、然后数据说你放弃了。
    /// </summary>
    public partial class GoalListPage : ContentPage
    {
        private const int StalledDays = 14;

        /// <summary>
        /// 列表行。本地和 proto 是两个不同的 Goal 类型（LocalGoal / Personalhub.Goal），
        /// 这里统一成同一个视图模型 —— 否则 Render 里要写两套分支。
        /// </summary>
        private sealed record GoalVm(
            string Id,
            string Title,
            string NextStep,
            bool IsCurrent,
            int Status,
            long StartedAt,
            int TotalMinutes,
            long LastAt);

        private readonly LocalRecordStore<LocalGoal> _goals;
        private readonly LocalRecordStore<LocalBlock> _blocks;
        private readonly RecordSyncService<LocalGoal> _goalSync;
        private readonly BlockMirrorService _mirror;

        private List<GoalVm> _items = new();
        private bool _stalledOpen;

        public GoalListPage(
            LocalRecordStore<LocalGoal> goals,
            LocalRecordStore<LocalBlock> blocks,
            RecordSyncService<LocalGoal> goalSync,
            BlockMirrorService mirror)
        {
            InitializeComponent();
            _goals = goals;
            _blocks = blocks;
            _goalSync = goalSync;
            _mirror = mirror;
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();
            await ReloadAsync();
            _ = SyncAndReloadAsync();
        }

        // ============================================================
        // 加载
        // ============================================================

        private async Task ReloadAsync()
        {
            // 1) 本地目标（离线可读）
            var local = await _goals.GetByRangeAsync(0, long.MaxValue);

            // 2) 本地块 → 算累计投入与最后活动（30 天镜像，够判断"还在不在动"）
            var blockTotals = await LocalTotalsAsync();

            _items = local
                .Where(g => g.Status != 2 && g.Status != 3)     // 已完成 / 已放弃不在这页
                .Select(g =>
                {
                    blockTotals.TryGetValue(g.Id, out var t);
                    return new GoalVm(g.Id, g.Title, g.NextStep, g.IsCurrent, g.Status,
                        g.StartedAt, t.Minutes, t.LastAt);
                })
                .ToList();

            Render();

            // 3) 服务端有更准的全量累计（本地只有 30 天），**后台**拉，拿到再覆盖。
            //    以前这里是 await：一旦没缓存地址、要扫网段找服务端，这一页就会白等 7~8 秒。
            //    本地数据本来就够画这一页，服务端只是来校正数字的，不该阻塞首屏。
            _ = RefreshFromServerAsync();
        }

        private async Task RefreshFromServerAsync()
        {
            try
            {
                var server = await _mirror.FetchGoalsAsync().ConfigureAwait(true);
                if (server.Count == 0) return;   // 离线：保持本地视图

                _items = server
                    .Where(g => !g.Deleted && g.Status != 2 && g.Status != 3)
                    .Select(g => new GoalVm(g.Id, g.Title, g.NextStep, g.IsCurrent, g.Status,
                        g.StartedAt, g.TotalMinutes, g.LastActivityAt))
                    .ToList();

                Render();
            }
            catch
            {
                // 离线不动界面
            }
        }

        private async Task<Dictionary<string, (int Minutes, long LastAt)>> LocalTotalsAsync()
        {
            // 30 天就够"最后活动"了：超过 30 天没块的目标一定在停滞区
            var (from, to) = Period.LastDays(BlockMirrorService.WindowDays);
            var blocks = await _blocks.GetByRangeAsync(from, to);

            return blocks
                .Where(b => !string.IsNullOrEmpty(b.GoalId))
                .GroupBy(b => b.GoalId)
                .ToDictionary(
                    g => g.Key,
                    g => (g.Sum(x => Math.Max(0, x.DurationMin)), g.Max(x => x.OccurredAt)),
                    StringComparer.Ordinal);
        }

        private async Task SyncAndReloadAsync()
        {
            try
            {
                await _goalSync.SyncAsync();
                await _mirror.FetchGoalsAsync();
                await MainThread.InvokeOnMainThreadAsync(ReloadAsync);
            }
            catch
            {
                // 离线时保持本地视图，不弹错
            }
        }

        // ============================================================
        // 渲染
        // ============================================================

        private void Render()
        {
            ActivePanel.Children.Clear();
            StalledPanel.Children.Clear();

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long stalledBefore = now - StalledDays * 24L * 60 * 60 * 1000;

            var active = new List<GoalVm>();
            var stalled = new List<GoalVm>();

            foreach (var it in _items)
            {
                // 从未有块：用创建时间判断（刚建的目标不该立刻进停滞区）
                long marker = it.LastAt != 0 ? it.LastAt : it.StartedAt;
                if (marker != 0 && marker < stalledBefore) stalled.Add(it);
                else active.Add(it);
            }

            active = active
                .OrderByDescending(x => x.IsCurrent)
                .ThenByDescending(x => x.LastAt)
                .ToList();

            foreach (var it in active) ActivePanel.Children.Add(BuildCard(it, dimmed: false));
            foreach (var it in stalled) StalledPanel.Children.Add(BuildCard(it, dimmed: true));

            StalledHeader.IsVisible = stalled.Count > 0;
            StalledHeader.Text = $"停滞 {stalled.Count} 个 ▾";
            StalledPanel.IsVisible = _stalledOpen && stalled.Count > 0;

            EmptyLabel.IsVisible = _items.Count == 0;
            EmptyLabel.Text = "还没有目标。建一个（比如「做完 PersonalHub」），之后的块才有地方归。";
        }

        private View BuildCard(GoalVm g, bool dimmed)
        {
            var stack = new VerticalStackLayout { Spacing = 3 };

            var titleRow = new HorizontalStackLayout { Spacing = 6 };
            titleRow.Children.Add(TextPeek.Attach(new Label
            {
                Text = g.Title,
                FontSize = 15,
                FontAttributes = FontAttributes.Bold,
                TextColor = dimmed ? Color.FromArgb("#8A8A8E") : Color.FromArgb("#2C2C2A"),
                LineBreakMode = LineBreakMode.TailTruncation,
            }));

            if (g.IsCurrent)
            {
                titleRow.Children.Add(new Label
                {
                    Text = "当前",
                    FontSize = 11,
                    TextColor = Colors.White,
                    BackgroundColor = Color.FromArgb("#185FA5"),
                    Padding = new Thickness(6, 1),
                    VerticalOptions = LayoutOptions.Center,
                });
            }

            stack.Children.Add(titleRow);

            if (!string.IsNullOrWhiteSpace(g.NextStep))
            {
                stack.Children.Add(TextPeek.Attach(new Label
                {
                    Text = "下一步：" + g.NextStep,
                    FontSize = 12,
                    TextColor = Color.FromArgb("#5F5E5A"),
                    LineBreakMode = LineBreakMode.TailTruncation,
                }));
            }

            stack.Children.Add(new Label
            {
                Text = StatLine(g.TotalMinutes, g.LastAt),
                FontSize = 11,
                TextColor = Color.FromArgb("#8A8A8E"),
            });

            var border = new Border
            {
                Stroke = Color.FromArgb("#E3E1DA"),
                StrokeThickness = 1,
                Padding = new Thickness(12, 10),
                Content = stack,
            };

            var tap = new TapGestureRecognizer();
            tap.Tapped += async (_, _) =>
            {
                // 点文字 = 看全文，点行 = 进详情。两个手势谁先到谁生效，
                // 后到的那个 400ms 内被吞掉（见 TapArbiter）。
                if (!TapArbiter.TryClaim()) return;

                await Shell.Current.GoToAsync($"{RecordCatalog.GoalDetailRoute}?id={g.Id}");
            };
            border.GestureRecognizers.Add(tap);

            return border;
        }

        private static string StatLine(int totalMinutes, long lastAt)
        {
            string total = totalMinutes > 0
                ? "累计 " + BlockEntryPage.FormatMinutes(totalMinutes)
                : "还没投入时间";

            string last;
            if (lastAt == 0)
            {
                last = "还没动过";
            }
            else
            {
                int days = (int)((DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - lastAt) / (24L * 60 * 60 * 1000));
                last = days <= 0 ? "今天动过" : $"{days} 天前动过";
            }

            return $"{total} · {last}";
        }

        private async void OnReviewClicked(object? sender, EventArgs e)
        {
            await Shell.Current.GoToAsync(RecordCatalog.QuarterReviewRoute);
        }

        private void OnToggleStalledClicked(object sender, EventArgs e)
        {
            _stalledOpen = !_stalledOpen;
            StalledPanel.IsVisible = _stalledOpen && StalledPanel.Children.Count > 0;
            StalledHeader.Text = StalledHeader.Text.Replace("▾", _stalledOpen ? "▴" : "▾");
        }

        // ============================================================
        // 新建
        // ============================================================

        private async void OnCreateClicked(object sender, EventArgs e)
        {
            string title = (NewGoalEntry.Text ?? string.Empty).Trim();
            if (title.Length == 0)
            {
                StatusLabel.Text = "先给它起个名字";
                return;
            }

            int activeCount = _items.Count(x =>
                x.Status == 0 &&
                (x.LastAt == 0 ? x.StartedAt : x.LastAt) >=
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - StalledDays * 24L * 60 * 60 * 1000);

            if (activeCount >= 3)
            {
                StatusLabel.Text = "已有 3 个目标在推进，先暂停一个再建新的";
                return;
            }

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var goal = new LocalGoal
            {
                Id = Guid.NewGuid().ToString("N"),
                Title = title,
                Status = 0,
                NextStep = string.Empty,
                // 新建的自动成为"当前目标"：否则你会建完目标、记了一整天，然后发现全都没归上
                IsCurrent = true,
                StartedAt = now,
                CreatedAt = now,
                UpdatedAt = now,
                Deleted = false,
                Synced = false,
                SyncedAt = 0,
            };

            await _goals.InsertAsync(goal);

            // 本地先把旧的"当前"清掉（服务端也会清，但离线时不能等服务端）
            var others = await _goals.GetByRangeAsync(0, long.MaxValue);
            foreach (var o in others.Where(o => o.Id != goal.Id && o.IsCurrent))
            {
                o.IsCurrent = false;
                o.UpdatedAt = now;
                o.Synced = false;
                await _goals.UpdateAsync(o);
            }

            NewGoalEntry.Text = string.Empty;
            StatusLabel.Text = $"已建「{title}」，并设为当前目标";

            await ReloadAsync();
            _ = SyncAndReloadAsync();
        }
    }
}
