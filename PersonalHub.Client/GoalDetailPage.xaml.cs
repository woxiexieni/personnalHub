using PersonalHub.Client.Controls;
using PersonalHub.Client.Data;
using PersonalHub.Client.Services;
using Microsoft.Maui.Controls.Shapes;

namespace PersonalHub.Client
{
    /// <summary>
    /// 目标详情：主体三块（数字 / 四周趋势 / 一条下一步），其余动作收进底部「改状态」弹层。
    ///
    /// 收纳原则：**频率决定位置**。改状态一季度一两次，不配常驻大按钮；
    /// 数字和下一步每次打开都看，必须留在首屏。
    ///
    /// 这里没有的东西比有的东西更重要：
    ///   没有"完成 63%"（要人估，一估就假）
    ///   没有截止日期（会制造"超期失败"，而真实推进本来就是断的）
    ///   没有子任务树（引入层级就有规划成本）
    /// </summary>
    [QueryProperty(nameof(GoalId), "id")]
    public partial class GoalDetailPage : ContentPage
    {
        /// <summary>停滞判定：14 天无块自动折叠（折叠发生在列表页，这里只影响措辞）。</summary>
        private const int StalledDays = 14;

        /// <summary>活跃目标上限（与服务端 GoalStore.MaxActive 一致）。</summary>
        private const int MaxActiveGoals = 3;

        private readonly LocalRecordStore<LocalGoal> _goals;
        private readonly LocalRecordStore<LocalBlock> _blocks;
        private readonly LocalRecordStore<LocalPlan> _plans;
        private readonly RecordSyncService<LocalGoal> _goalSync;
        private readonly RecordSyncService<LocalBlock> _blockSync;
        private readonly RecordSyncService<LocalPlan> _planSync;
        private readonly BlockMirrorService _mirror;
        private readonly PieceSettings _settings;

        private LocalGoal? _goal;

        public string GoalId { get; set; } = string.Empty;

        public GoalDetailPage(
            LocalRecordStore<LocalGoal> goals,
            LocalRecordStore<LocalBlock> blocks,
            LocalRecordStore<LocalPlan> plans,
            RecordSyncService<LocalGoal> goalSync,
            RecordSyncService<LocalBlock> blockSync,
            RecordSyncService<LocalPlan> planSync,
            BlockMirrorService mirror,
            PieceSettings settings)
        {
            InitializeComponent();
            _goals = goals;
            _blocks = blocks;
            _plans = plans;
            _goalSync = goalSync;
            _blockSync = blockSync;
            _planSync = planSync;
            _mirror = mirror;
            _settings = settings;
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();
            await LoadAsync();
        }

        // ============================================================
        // 加载
        // ============================================================

        private async Task LoadAsync()
        {
            if (string.IsNullOrEmpty(GoalId)) return;

            _goal = await _goals.FindAsync(GoalId);
            if (_goal is null)
            {
                TitleLabel.Text = "目标不在了";
                StateLabel.Text = "可能已经在另一台设备上被删除";
                StatusRowBtn.IsVisible = false;
                return;
            }

            TitleLabel.Text = _goal.Title;
            NextStepEntry.Text = _goal.NextStep ?? string.Empty;
            StateLabel.Text = StateText(_goal.Status) + (_goal.IsCurrent ? " · 当前目标" : string.Empty);
            StatusRowBtn.Text = $"状态：{StateText(_goal.Status)} · 改状态 ▸";
            StatusRowBtn.IsVisible = true;
            ApplyPlanTodayState();

            // 本地先渲染（0ms），再尝试用服务端的全量数字覆盖
            await RenderStatsAsync(serverTotal: null, serverLastAt: null);
            _ = RefreshFromServerAsync();
        }

        private async Task RefreshFromServerAsync()
        {
            try
            {
                var server = await _mirror.FetchGoalsAsync();
                var remote = server.FirstOrDefault(g => g.Id == GoalId);
                if (remote is null) return;

                await MainThread.InvokeOnMainThreadAsync(async () =>
                    await RenderStatsAsync(remote.TotalMinutes, remote.LastActivityAt));
            }
            catch
            {
                // 离线：显示本地 30 天的近似值，并在下面注明
            }
        }

        private async Task RenderStatsAsync(int? serverTotal, long? serverLastAt)
        {
            if (_goal is null) return;

            var (from, to) = Period.LastDays(BlockMirrorService.WindowDays);
            var blocks = await _blocks.GetByRangeAsync(from, to);
            var mine = blocks.Where(b => b.GoalId == _goal.Id).ToList();

            int localMinutes = mine.Sum(b => Math.Max(0, b.DurationMin));
            long localLast = mine.Count == 0 ? 0 : mine.Max(b => b.OccurredAt);

            int totalMinutes = serverTotal ?? localMinutes;
            long lastAt = serverLastAt ?? localLast;

            TotalLabel.Text = totalMinutes > 0 ? BlockEntryPage.FormatMinutes(totalMinutes) : "还没投入";
            LastLabel.Text = LastText(lastAt);

            if (serverTotal is null)
            {
                StateLabel.Text = StateText(_goal.Status) + " · 累计为本地最近 30 天的近似值";
            }

            RenderWeekBars(mine);
            RenderTimeline(mine);
            RenderEvaluation(mine);
        }

        /// <summary>
        /// 两个「方向 / 节律」数，都来自最近 30 天，不存字段：
        ///   值得率 = 值得 ÷ 已评（没评过就显示 —，不当成 0%）
        ///   活跃周数 = 最近四周里有记录的那几周（比"累计 40 小时"更能说明还在不在动）
        /// </summary>
        private void RenderEvaluation(List<LocalBlock> mine)
        {
            int rated = mine.Count(b => b.Worth is >= 1 and <= 3);
            int good = mine.Count(b => b.Worth == 1);

            WorthRateLabel.Text = rated == 0 ? "—" : $"{good * 100.0 / rated:F0}%";
            WorthSubLabel.Text = rated == 0
                ? "最近 30 天 · 还没评过"
                : $"最近 30 天 · 已评 {rated} 条";

            var monday = Period.StartOfDay(DateTimeOffset.UtcNow);
            monday = monday.AddDays(-(((int)monday.DayOfWeek + 6) % 7)).AddDays(-21);

            int activeWeeks = 0;
            for (int i = 0; i < 4; i++)
            {
                long from = monday.AddDays(i * 7).ToUnixTimeMilliseconds();
                long to = monday.AddDays((i + 1) * 7).ToUnixTimeMilliseconds();
                if (mine.Any(b => b.OccurredAt >= from && b.OccurredAt < to)) activeWeeks++;
            }

            ActiveWeeksLabel.Text = $"{activeWeeks} / 4";
        }

        // ============================================================
        // 演进：把目标还原成一长串时间片
        // ============================================================
        //
        // 目标不是「完成 63%」，是一条线。线就是它下面所有块按时间排下来。
        // 这里只画痕迹：哪一天、哪个片、多久、备注、复盘评价。不做任何进度推断。

        private void RenderTimeline(List<LocalBlock> mine)
        {
            TimelinePanel.Children.Clear();

            if (mine.Count == 0)
            {
                TimelineHint.Text = string.Empty;
                TimelinePanel.Children.Add(new Label
                {
                    Text = "还没有时间块。记一笔归到这个目标的块，这里就会长出一个节点。",
                    FontSize = 12,
                    TextColor = Color.FromArgb("#8A8A8E"),
                    Margin = new Thickness(0, 4),
                });
                return;
            }

            int total = mine.Sum(b => Math.Max(0, b.DurationMin));
            TimelineHint.Text = $"{mine.Count} 个节点 · {BlockEntryPage.FormatMinutes(total)}";

            var ordered = mine.OrderByDescending(b => b.OccurredAt).ToList();

            DateTime? lastDay = null;

            foreach (var b in ordered)
            {
                var local = Period.ToLocalTime(b.OccurredAt);
                var day = local.Date;

                if (lastDay != day)
                {
                    lastDay = day;
                    var dayMinutes = ordered
                        .Where(x => Period.ToLocalTime(x.OccurredAt).Date == day)
                        .Sum(x => Math.Max(0, x.DurationMin));

                    var header = new Grid
                    {
                        ColumnDefinitions =
                        {
                            new ColumnDefinition(GridLength.Star),
                            new ColumnDefinition(GridLength.Auto),
                        },
                        Margin = new Thickness(0, 12, 0, 4),
                    };
                    header.Add(new Label
                    {
                        Text = DayText(local),
                        FontSize = 12,
                        TextColor = Color.FromArgb("#5F5E5A"),
                    }, 0);
                    header.Add(new Label
                    {
                        Text = BlockEntryPage.FormatMinutes(dayMinutes),
                        FontSize = 11,
                        TextColor = Color.FromArgb("#8A8A8E"),
                        HorizontalTextAlignment = TextAlignment.End,
                    }, 1);

                    TimelinePanel.Children.Add(header);
                }

                TimelinePanel.Children.Add(BuildTimelineRow(b, local));
            }
        }

        private View BuildTimelineRow(LocalBlock b, DateTimeOffset local)
        {
            Color dotColor = b.Worth switch
            {
                1 => Color.FromArgb("#3B6D11"),   // 值得
                2 => Color.FromArgb("#8A5B00"),   // 一般
                3 => Color.FromArgb("#A32D2D"),   // 别做了
                _ => Color.FromArgb("#B9B7AE"),
            };

            // 左：竖线 + 节点。线贯穿整行，视觉上才是「一长串」
            var rail = new Grid { WidthRequest = 16 };
            rail.Add(new BoxView
            {
                WidthRequest = 1,
                Color = Color.FromArgb("#E3E0D8"),
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Fill,
            });
            rail.Add(new Ellipse
            {
                WidthRequest = 9,
                HeightRequest = 9,
                Fill = new SolidColorBrush(dotColor),
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Start,
                Margin = new Thickness(0, 6, 0, 0),
            });

            var body = new VerticalStackLayout { Spacing = 1, Padding = new Thickness(0, 4, 0, 8) };

            var line1 = new HorizontalStackLayout { Spacing = 8 };
            line1.Children.Add(new Label
            {
                Text = local.ToString("HH:mm"),
                FontSize = 11,
                TextColor = Color.FromArgb("#8A8A8E"),
                VerticalOptions = LayoutOptions.Center,
            });
            line1.Children.Add(new Label
            {
                Text = _settingsName(b.PieceCode),
                FontSize = 13,
                VerticalOptions = LayoutOptions.Center,
            });
            line1.Children.Add(new Label
            {
                Text = b.DurationMin > 0 ? BlockEntryPage.FormatMinutes(b.DurationMin) : "未填时长",
                FontSize = 11,
                TextColor = Color.FromArgb("#8A8A8E"),
                VerticalOptions = LayoutOptions.Center,
            });
            body.Children.Add(line1);

            if (!string.IsNullOrEmpty(b.Note))
            {
                body.Children.Add(new Label
                {
                    Text = b.Note,
                    FontSize = 11,
                    TextColor = Color.FromArgb("#8A8A8E"),
                    LineBreakMode = LineBreakMode.WordWrap,
                });
            }

            if (!string.IsNullOrEmpty(b.Lesson))
            {
                body.Children.Add(new Label
                {
                    Text = "↳ " + b.Lesson,
                    FontSize = 11,
                    TextColor = Color.FromArgb("#3B6D11"),
                    LineBreakMode = LineBreakMode.WordWrap,
                });
            }

            var row = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(new GridLength(16)),
                    new ColumnDefinition(GridLength.Star),
                },
            };
            row.Add(rail, 0);
            row.Add(body, 1);
            return row;
        }

        private string _settingsName(string code) => _settings.DisplayName(code);

        private static string DayText(DateTimeOffset local)
        {
            var today = Period.StartOfDay(DateTimeOffset.UtcNow);
            var day = Period.StartOfDay(local);
            int diff = (int)Math.Round((today - day).TotalDays);

            string weekday = local.DayOfWeek switch
            {
                DayOfWeek.Monday => "周一",
                DayOfWeek.Tuesday => "周二",
                DayOfWeek.Wednesday => "周三",
                DayOfWeek.Thursday => "周四",
                DayOfWeek.Friday => "周五",
                DayOfWeek.Saturday => "周六",
                _ => "周日",
            };

            return diff switch
            {
                <= 0 => $"今天 · {weekday}",
                1 => $"昨天 · {weekday}",
                _ => local.ToString("MM-dd") + " · " + weekday,
            };
        }

        private void RenderWeekBars(List<LocalBlock> mine)
        {
            WeekBars.Children.Clear();

            var monday = Period.StartOfDay(DateTimeOffset.UtcNow);
            monday = monday.AddDays(-(((int)monday.DayOfWeek + 6) % 7)).AddDays(-21);

            var buckets = new int[4];
            for (int i = 0; i < 4; i++)
            {
                long bFrom = monday.AddDays(i * 7).ToUnixTimeMilliseconds();
                long bTo = monday.AddDays((i + 1) * 7).ToUnixTimeMilliseconds();
                buckets[i] = mine
                    .Where(b => b.OccurredAt >= bFrom && b.OccurredAt < bTo)
                    .Sum(b => Math.Max(0, b.DurationMin));
            }

            int max = Math.Max(1, buckets.Max());

            for (int i = 0; i < 4; i++)
            {
                bool empty = buckets[i] == 0;
                double ratio = (double)buckets[i] / max;
                double h = empty ? 3 : Math.Max(6, ratio * 40);

                var col = new VerticalStackLayout
                {
                    Spacing = 3,
                    VerticalOptions = LayoutOptions.End,
                };

                // 空周必须明说「没动」：之前的画法是一根细灰线，看起来像加载失败
                if (empty)
                {
                    col.Children.Add(new Label
                    {
                        Text = "没动",
                        FontSize = 10,
                        TextColor = Color.FromArgb("#B0AEA5"),
                        HorizontalTextAlignment = TextAlignment.Center,
                    });
                }

                col.Children.Add(new BoxView
                {
                    HeightRequest = h,
                    WidthRequest = 28,
                    CornerRadius = 3,
                    Color = empty
                        ? Color.FromArgb("#E3E1DA")
                        : Color.FromArgb("#185FA5"),
                    VerticalOptions = LayoutOptions.End,
                });

                col.Children.Add(new Label
                {
                    Text = i == 3 ? "本周" : $"-{3 - i}w",
                    FontSize = 10,
                    TextColor = Color.FromArgb("#8A8A8E"),
                    HorizontalTextAlignment = TextAlignment.Center,
                });

                WeekBars.Add(col, i, 0);
            }
        }

        private static string StateText(int status) => status switch
        {
            1 => "已暂停",
            2 => "已完成",
            3 => "已放弃",
            _ => "进行中",
        };

        private static string LastText(long lastAt)
        {
            if (lastAt == 0) return "还没动过";

            long days = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - lastAt) / (24L * 60 * 60 * 1000);
            return days <= 0 ? "今天" : $"{days} 天前";
        }

        // ============================================================
        // 一条下一步
        // ============================================================

        private async void OnSaveNextStepClicked(object sender, EventArgs e)
        {
            if (_goal is null) return;

            _goal.NextStep = (NextStepEntry.Text ?? string.Empty).Trim();
            _goal.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _goal.Synced = false;

            await _goals.UpdateAsync(_goal);
            StatusLabel.Text = "已保存";
            ApplyPlanTodayState();
            _ = _goalSync.SyncAsync();
        }

        // ============================================================
        // 下一步 → 今天的计划
        // ============================================================
        //
        // 为什么走「生成一条 plans」而不是「直接建一个时间块」：
        // 直接建块会让一天里出现"有计划"和"临时加的块"两条并行路径，
        // 月底统计分不清哪些是兑现的、哪些是临时起意。
        // 走计划则完全复用 R4 已经跑通的链路：进计划卡 → 点它记块 → 块自动带 GoalId/PlanId。
        //
        // 「排哪个片」由历史推断：这个目标最近 30 天用得最多的那个片。
        // 目标从来没记过块时才问用户一次 —— 那是系统唯一没有依据的时刻。

        private void ApplyPlanTodayState()
        {
            bool hasNext = _goal is not null && !string.IsNullOrWhiteSpace(_goal.NextStep);
            PlanTodayBtn.IsEnabled = hasNext;
            PlanTodayBtn.BackgroundColor = hasNext ? Color.FromArgb("#185FA5") : Color.FromArgb("#C9D8EA");
        }

        private async void OnPlanTodayClicked(object sender, EventArgs e)
        {
            if (_goal is null) return;

            string next = (NextStepEntry.Text ?? _goal.NextStep ?? string.Empty).Trim();
            if (next.Length == 0)
            {
                StatusLabel.Text = "先写一句下一步，才知道今天要做什么。";
                return;
            }

            // 下一步可能刚改还没保存，先落库，避免"排进今天了但这句话没存"
            if (next != _goal.NextStep)
            {
                _goal.NextStep = next;
                _goal.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                _goal.Synced = false;
                await _goals.UpdateAsync(_goal);
                _ = _goalSync.SyncAsync();
            }

            // 1. 今天还能不能排（上限 3，与服务端 PlanStore.MaxPerDay 一致）
            var (dayFrom, dayTo) = Period.Today();
            var todayPlans = (await _plans.GetByRangeAsync(dayFrom, dayTo))
                .Where(p => !p.Deleted)
                .ToList();

            if (todayPlans.Count >= 3)
            {
                StatusLabel.Text = "今天已经排满 3 件了。想换的话，先去时间块页去掉一件。";
                return;
            }

            // 2. 推断片：这个目标最近 30 天用得最多的片（次数优先，次数相同看总时长）
            var (from, to) = Period.LastDays(30);
            var blocks = (await _blocks.GetByRangeAsync(from, to))
                .Where(b => b.GoalId == _goal.Id && b.DurationMin > 0)
                .ToList();

            string? code = blocks
                .Where(b => !string.IsNullOrEmpty(b.PieceCode))
                .GroupBy(b => b.PieceCode!)
                .OrderByDescending(g => g.Count())
                .ThenByDescending(g => g.Sum(b => Math.Max(0, b.DurationMin)))
                .Select(g => g.Key)
                .FirstOrDefault();

            int budget;

            if (code is null)
            {
                // 没有历史依据时只问一次：问的是"做哪一类"，不是"写一句话"。
                var names = Pieces.All.Select(p => _settings.DisplayName(p.Code)).ToArray();
                string pick = await DisplayActionSheet(
                    "这个目标还没记过时间块。今天打算做哪一类？", "取消", null, names);

                int idx = Array.IndexOf(names, pick);
                if (idx < 0) return;

                code = Pieces.All[idx].Code;
                budget = 30;
            }
            else
            {
                // 预算取这个目标在这个片上的中位时长，再吸到最近的固定档 ——
                // 平均会被一次马拉松拉飞，中位数不会。
                var durations = blocks
                    .Where(b => b.PieceCode == code)
                    .Select(b => b.DurationMin)
                    .OrderBy(x => x)
                    .ToList();

                budget = NearestDuration(durations[durations.Count / 2]);
            }

            // 3. 今天已经排过同一个片就不重复排（同名两行会让人以为记错了）
            if (todayPlans.Any(p => p.PieceCode == code))
            {
                StatusLabel.Text = $"今天已经排过「{_settings.DisplayName(code)}」了，直接去时间块页点它就行。";
                return;
            }

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var plan = new LocalPlan
            {
                Id = Guid.NewGuid().ToString("N"),
                Day = DateTimeOffset.UtcNow.ToOffset(Period.Cst).ToString("yyyy-MM-dd"),
                PieceCode = code,
                BudgetMin = budget,
                GoalId = _goal.Id,
                CreatedAt = now,
                UpdatedAt = now,
                Deleted = false,
                Synced = false,
                SyncedAt = 0,
            };

            await _plans.InsertAsync(plan);

            StatusLabel.Text =
                $"已排进今天：{_settings.DisplayName(code)} {BlockEntryPage.FormatMinutes(budget)}。" +
                "去时间块页点它就能记一条。";

            _ = _planSync.SyncAsync();
        }

        /// <summary>把分钟数吸到最接近的固定时长档（5/15/30/45/60/90/120/180）。</summary>
        private static int NearestDuration(int minutes)
        {
            int best = Pieces.Durations[0];
            foreach (int d in Pieces.Durations)
            {
                if (Math.Abs(d - minutes) < Math.Abs(best - minutes)) best = d;
            }
            return best;
        }

        // ============================================================
        // 低频动作：全部收进一个弹层
        // ============================================================

        private async void OnStatusRowClicked(object sender, EventArgs e)
        {
            if (_goal is null) return;

            // 选项随状态变：进行中才有"暂停/完成/设当前"，完成/放弃后只有"恢复"
            var options = new List<string>();
            switch (_goal.Status)
            {
                case 0:
                    options.Add("暂停");
                    options.Add("标记完成");
                    options.Add("放弃");
                    if (!_goal.IsCurrent) options.Add("设为当前目标");
                    break;
                case 1:
                    options.Add("恢复进行中");
                    break;
                default: // 2 / 3
                    options.Add("恢复进行中");
                    break;
            }

            options.Add("改名字");
            options.Add("把最近 30 天的块归入");
            options.Add("删除目标");

            string pick = await DisplayActionSheet(
                $"{_goal.Title}（{StateText(_goal.Status)}）",
                "取消", null, options.ToArray());

            switch (pick)
            {
                case "暂停":
                    await SetStatusAsync(1, "已暂停（不算进 3 个活跃名额）");
                    break;
                case "恢复进行中":
                    await RestoreAsync();
                    break;
                case "标记完成":
                    await CompleteAsync();
                    break;
                case "放弃":
                    await AbandonAsync();
                    break;
                case "设为当前目标":
                    await SetCurrentAsync();
                    break;
                case "改名字":
                    await RenameAsync();
                    break;
                case "把最近 30 天的块归入":
                    await BackfillAsync();
                    break;
                case "删除目标":
                    await DeleteAsync();
                    break;
            }
        }

        private async Task SetStatusAsync(int status, string message)
        {
            if (_goal is null) return;

            _goal.Status = status;
            if (status != 0) _goal.IsCurrent = false;   // 非活跃目标不能占着"当前"
            _goal.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _goal.Synced = false;

            await _goals.UpdateAsync(_goal);
            StatusLabel.Text = message;
            await LoadAsync();
            _ = _goalSync.SyncAsync();
        }

        private async Task RestoreAsync()
        {
            if (_goal is null) return;

            // 活跃名额在客户端也拦一次：服务端会拒，但那里拒了用户看不见原因
            var all = await _goals.GetByRangeAsync(0, long.MaxValue);
            int active = all.Count(g => g.Status == 0 && g.Id != _goal.Id && !g.Deleted);
            if (active >= MaxActiveGoals)
            {
                StatusLabel.Text = $"已有 {MaxActiveGoals} 个目标在推进，先暂停或完成一个";
                return;
            }

            await SetStatusAsync(0, "已恢复");
        }

        private async Task CompleteAsync()
        {
            if (_goal is null) return;

            bool ok = await DisplayAlert("标记为完成？",
                $"{_goal.Title}\n\n完成后不再出现在列表里，历史记录和累计投入都保留。",
                "完成", "取消");
            if (!ok) return;

            await SetStatusAsync(2, "已完成");
        }

        private async Task AbandonAsync()
        {
            if (_goal is null) return;

            bool ok = await DisplayAlert("放弃这个目标？",
                $"{_goal.Title}\n\n放弃和完成一样可逆（随时能恢复），历史记录都保留。砍掉不是失败，是把注意力收回来。",
                "放弃", "取消");
            if (!ok) return;

            await SetStatusAsync(3, "已放弃");
        }

        private async Task RenameAsync()
        {
            if (_goal is null) return;

            string? input = await TextEditorPage.ShowAsync("改名字",
                initial: _goal.Title,
                placeholder: "目标的名字",
                compact: true,
                maxLength: 120);
            if (string.IsNullOrWhiteSpace(input) || input == _goal.Title) return;

            _goal.Title = input;
            _goal.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _goal.Synced = false;

            await _goals.UpdateAsync(_goal);
            await LoadAsync();
            _ = _goalSync.SyncAsync();
        }

        private async Task SetCurrentAsync()
        {
            if (_goal is null) return;

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var all = await _goals.GetByRangeAsync(0, long.MaxValue);

            foreach (var g in all)
            {
                bool shouldBeCurrent = g.Id == _goal.Id;
                if (g.IsCurrent == shouldBeCurrent) continue;

                g.IsCurrent = shouldBeCurrent;
                g.UpdatedAt = now;
                g.Synced = false;
                await _goals.UpdateAsync(g);
            }

            _goal.IsCurrent = true;
            StatusLabel.Text = $"已设为当前，之后记的块默认归「{_goal.Title}」";
            await LoadAsync();
            _ = _goalSync.SyncAsync();
        }

        // ============================================================
        // 历史块批量归入（一次性操作）
        // ============================================================

        private async Task BackfillAsync()
        {
            if (_goal is null) return;

            var (from, to) = Period.LastDays(BlockMirrorService.WindowDays);
            var blocks = await _blocks.GetByRangeAsync(from, to);
            var orphans = blocks.Where(b => string.IsNullOrEmpty(b.GoalId)).ToList();

            if (orphans.Count == 0)
            {
                StatusLabel.Text = "最近 30 天没有未归属的块，不用归";
                return;
            }

            bool ok = await DisplayAlert("批量归入？",
                $"最近 30 天有 {orphans.Count} 条还没归属的块。\n" +
                $"全部归到「{_goal.Title}」？\n\n（已经归到别的目标的不动）",
                "全部归入", "取消");
            if (!ok) return;

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var b in orphans)
            {
                b.GoalId = _goal.Id;
                b.UpdatedAt = now;
                b.Synced = false;
                await _blocks.UpdateAsync(b);
            }

            StatusLabel.Text = $"已归入 {orphans.Count} 条";
            await LoadAsync();
            _ = _blockSync.SyncAsync();
        }

        private async Task DeleteAsync()
        {
            if (_goal is null) return;

            bool ok = await DisplayAlert("删除这个目标？",
                $"{_goal.Title}\n\n只删目标本身，已经记过的时间块不会消失（它们会变成「未归属」）。",
                "删除", "取消");
            if (!ok) return;

            if (_goal.Synced)
            {
                _goal.Deleted = true;
                _goal.Synced = false;
                _goal.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                await _goals.UpdateAsync(_goal);
            }
            else
            {
                await _goals.DeleteAsync(_goal.Id);
            }

            _ = _goalSync.SyncAsync();
            await Shell.Current.GoToAsync("..");
        }
    }
}
