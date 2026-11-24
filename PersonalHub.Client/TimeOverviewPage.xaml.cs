using PersonalHub.Client.Controls;
using PersonalHub.Client.Data;
using PersonalHub.Client.Services;
using Microsoft.Maui.Layouts;
using Microsoft.Maui.Controls.Shapes;

namespace PersonalHub.Client
{
    /// <summary>
    /// 「概览 → 时间概览」：一段时间里时间花在哪些事上。
    ///
    /// 和「支出概览」同一条思路，也是同一个理由：
    ///  1. **先本地渲染（0ms），再让服务端校正**。本地只有最近 30 天副本，
    ///     但它足够画出"今天/本周"；等服务端 <c>GetPieceStats</c> 回来直接覆盖。
    ///  2. **未记录必须显示**。一天 24 小时只记了 8 小时，剩下 16 小时不能从页面上消失 ——
    ///     否则你会以为自己一天只活了 8 小时，而真正浪费掉的那几小时反而看不见。
    ///  3. **不显示"完成度/效率"这类需要估的数字**。分钟数是记出来的，效率不是。
    /// </summary>
    public partial class TimeOverviewPage : ContentPage
    {
        private static readonly Color ActiveBg = Color.FromArgb("#185FA5");
        private static readonly Color BarColor = Color.FromArgb("#185FA5");
        private static readonly Color GoalColor = Color.FromArgb("#6B5CA5");

        // 主题色对：这一页有数字和日历两套视图，深色主题下硬编码浅色底会翻车
        // （BlockLibraryPage 踩过同款坑），文字/底色一律成对给。
        private static readonly Color InactiveBgLight = Color.FromArgb("#F1EFE8");
        private static readonly Color InactiveBgDark = Color.FromArgb("#2C2C2A");
        private static readonly Color ChipTextLight = Color.FromArgb("#2C2C2A");
        private static readonly Color ChipTextDark = Color.FromArgb("#D9D7CF");
        private static readonly Color MutedLight = Color.FromArgb("#78766E");
        private static readonly Color MutedDark = Color.FromArgb("#A8A69E");
        private static readonly Color LineLight = Color.FromArgb("#E3E1DA");
        private static readonly Color LineDark = Color.FromArgb("#3A3A36");

        private readonly LocalRecordStore<LocalBlock> _blocks;
        private readonly LocalRecordStore<LocalGoal> _goals;
        private readonly BlockMirrorService _mirror;
        private readonly PieceSettings _settings;

        private string _period = "today";
        private bool _busy;

        // R14：视图状态。_viewMode = "stats" | "calendar"；_calendarDay 是日历态里选中的那天。
        private string _viewMode = "stats";
        private DateTimeOffset _calendarDay = Period.StartOfDay(DateTimeOffset.UtcNow);
        private List<LocalBlock> _calBlocks = new();

        public TimeOverviewPage(
            LocalRecordStore<LocalBlock> blocks,
            LocalRecordStore<LocalGoal> goals,
            BlockMirrorService mirror,
            PieceSettings settings)
        {
            InitializeComponent();

            _blocks = blocks;
            _goals = goals;
            _mirror = mirror;
            _settings = settings;

            StylePeriodButtons();
            StyleModeButtons();
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();

            await LoadLocalAsync();
            await RefreshFromServerAsync();
        }

        // ============================================================
        // 档位
        // ============================================================

        private async void OnPeriodClicked(object? sender, EventArgs e)
        {
            if (sender is not Button b || b.CommandParameter is not string p) return;
            if (p == _period) return;

            _period = p;
            StylePeriodButtons();

            // R14：日历态切档 = 重画日历（纯本地数据）；统计态照旧走本地 + 服务端校正
            if (_viewMode == "calendar")
            {
                await RenderCalendarAsync();
                return;
            }

            await LoadLocalAsync();
            await RefreshFromServerAsync();
        }

        private void StylePeriodButtons()
        {
            foreach (var (btn, key) in new[]
                     {
                         (TodayBtn, "today"), (WeekBtn, "week"), (MonthBtn, "month"),
                     })
            {
                bool active = key == _period;
                btn.SetAppThemeColor(Button.BackgroundColorProperty,
                    active ? ActiveBg : InactiveBgLight, active ? ActiveBg : InactiveBgDark);
                btn.TextColor = active ? Colors.White
                    : (Application.Current?.RequestedTheme == AppTheme.Dark ? ChipTextDark : ChipTextLight);
            }
        }

        // ============================================================
        // R14：统计 / 日历视图切换
        // ============================================================

        private async void OnViewModeClicked(object? sender, EventArgs e)
        {
            if (sender is not Button b || b.CommandParameter is not string m) return;
            if (m == _viewMode) return;

            _viewMode = m;
            StyleModeButtons();
            StatsBody.IsVisible = _viewMode == "stats";
            CalendarBody.IsVisible = _viewMode == "calendar";

            if (_viewMode == "calendar")
            {
                _calendarDay = Period.StartOfDay(DateTimeOffset.UtcNow);
                await RenderCalendarAsync();
            }
            else
            {
                await LoadLocalAsync();
                await RefreshFromServerAsync();
            }
        }

        private void StyleModeButtons()
        {
            foreach (var (btn, key) in new[]
                     {
                         (StatsModeBtn, "stats"), (CalendarModeBtn, "calendar"),
                     })
            {
                bool active = key == _viewMode;
                btn.SetAppThemeColor(Button.BackgroundColorProperty,
                    active ? ActiveBg : InactiveBgLight, active ? ActiveBg : InactiveBgDark);
                btn.TextColor = active ? Colors.White
                    : (Application.Current?.RequestedTheme == AppTheme.Dark ? ChipTextDark : ChipTextLight);
            }
        }

        // ============================================================
        // 本地渲染（离线也能看）
        // ============================================================

        private async Task LoadLocalAsync()
        {
            var (from, to) = RangeOf(_period);

            var pieces = await _blocks.AggregateByPieceAsync(from, to);
            int recorded = pieces.Sum(p => p.Minutes);
            int elapsed = Elapsed(from, to);

            Render(
                pieces.Select(p => (p.PieceCode, p.Minutes, p.Count)).ToList(),
                recorded,
                elapsed,
                Math.Max(0, elapsed - recorded),
                $"本地副本 · 最近 {BlockMirrorService.WindowDays} 天内");
        }

        /// <summary>本地副本算出来的"至今"和区间一起交给 Render，避免两处各算一遍。</summary>
        private async Task RefreshFromServerAsync()
        {
            ConnLabel.Text = "校正中…";

            try
            {
                // 服务端算好的整块统计（区间由它定，两端口径不会分叉）
                var server = await _mirror.GetPieceStatsAsync(_period);

                if (server is not null)
                {
                    Render(server.Pieces, server.CoveredMinutes, server.TotalMinutes,
                        server.UnrecordedMinutes,
                        "服务端统计 · " + RangeText(server.RangeStart, server.RangeEnd) + " · 口径与网页一致");
                    ConnLabel.Text = "已连接";
                }
                else
                {
                    ConnLabel.Text = "离线";
                    FooterLabel.Text = "没连上服务端，上面是本地副本算的（最近 30 天）。";
                }

                await RenderGoalsAsync(server is not null);
            }
            catch (Exception ex)
            {
                ConnLabel.Text = "离线";
                FooterLabel.Text = "服务端校正失败：" + ex.Message;
            }
        }

        // ============================================================
        // 渲染
        // ============================================================

        private void Render(
            IReadOnlyList<(string PieceCode, int Minutes, int Count)> pieces,
            int recorded,
            int totalMinutes,
            int unrecorded,
            string sourceNote)
        {
            RecordedLabel.Text = Fmt(recorded);

            double cover = totalMinutes > 0 ? recorded / (double)totalMinutes : 0;
            CoverBar.Progress = Math.Clamp(cover, 0, 1);

            // 未记录是整页最值钱的一栏：它不是"你不够努力"，是"那几小时没被看见"。
            // 注明"含睡眠"是必须的 —— 否则一周下来 150 小时未记录，人会以为系统在指责他。
            UnrecordedLabel.Text = totalMinutes > 0
                ? $"未记录 {Fmt(unrecorded)}（占 {unrecorded * 100.0 / totalMinutes:F0}）· 已过去 {Fmt(totalMinutes)}，含睡眠"
                : "还没有过去的时间";

            var (from, to) = RangeOf(_period);
            RangeLabel.Text = RangeText(from, to) + $" · {pieces.Sum(p => p.Count)} 条";

            // ---- 按片 ----
            PieceRows.Children.Clear();
            var visible = pieces.Where(p => p.Minutes > 0).ToList();
            PieceSection.IsVisible = visible.Count > 0;
            PieceHint.Text = visible.Count > 0 ? $"共 {visible.Count} 类" : string.Empty;

            int max = visible.Count == 0 ? 1 : Math.Max(1, visible.Max(p => p.Minutes));
            foreach (var p in visible)
            {
                string name = _settings.DisplayName(p.PieceCode);
                double ratio = recorded > 0 ? p.Minutes / (double)recorded : 0;
                PieceRows.Children.Add(BuildRow(name, Fmt(p.Minutes), ratio, p.Minutes / (double)max, BarColor, $"{ratio * 100:F0}%"));
            }

            EmptySection.IsVisible = visible.Count == 0;
            EmptyLabel.Text = "这段时间还没有时间块";
            FooterLabel.Text = sourceNote;
        }

        private async Task RenderGoalsAsync(bool online)
        {
            GoalRows.Children.Clear();

            var goals = await _mirror.FetchGoalsAsync();

            List<(string Id, string Title, int Minutes, long LastAt)> rows;

            if (goals.Count > 0)
            {
                rows = goals
                    .Where(g => !g.Deleted && g.Status == 0)
                    .Select(g => (Id: g.Id, Title: g.Title, Minutes: g.TotalMinutes, LastAt: g.LastActivityAt))
                    .OrderByDescending(x => x.Minutes)
                    .Take(5)
                    .ToList();
            }
            else
            {
                // 离线：本地只有 30 天副本，算不出"全部累计"。宁可显示"—"，也不给一个偏小的假数字。
                var local = await _goals.GetByRangeAsync(0, long.MaxValue);
                rows = local
                    .Where(g => g.Status == 0)
                    .Select(g => (Id: g.Id, Title: g.Title, Minutes: -1, LastAt: 0L))
                    .Take(5)
                    .ToList();
            }

            GoalSection.IsVisible = rows.Count > 0;
            GoalHint.Text = online ? "全部时间 · 四周柱子左旧右新" : "离线：本地算不出全量累计";

            if (rows.Count == 0)
            {
                return;
            }

            // 四周趋势用本地 30 天副本算：这个窗口正好覆盖最近四周，不依赖服务端。
            var (lf, lt) = Period.LastDays(28);
            var localBlocks = await _blocks.GetByRangeAsync(lf, lt);
            var monday = WeekStart(DateTimeOffset.UtcNow).AddDays(-21);

            int[] TrendOf(string goalId)
            {
                var buckets = new int[4];
                for (int i = 0; i < 4; i++)
                {
                    long from = monday.AddDays(i * 7).ToUnixTimeMilliseconds();
                    long to = monday.AddDays((i + 1) * 7).ToUnixTimeMilliseconds();
                    buckets[i] = localBlocks
                        .Where(b => b.GoalId == goalId && b.OccurredAt >= from && b.OccurredAt < to)
                        .Sum(b => Math.Max(0, b.DurationMin));
                }
                return buckets;
            }

            int max = Math.Max(1, rows.Max(r => Math.Max(0, r.Minutes)));

            foreach (var g in rows)
            {
                string minutes = g.Minutes < 0 ? "—" : Fmt(g.Minutes);
                string last = g.Minutes < 0 ? "—" : LastActiveText(g.LastAt);

                var stack = new VerticalStackLayout { Spacing = 4 };

                var head = new Grid
                {
                    ColumnDefinitions =
                    {
                        new ColumnDefinition(GridLength.Star),
                        new ColumnDefinition(GridLength.Auto),
                    },
                    ColumnSpacing = 8,
                };
                head.Add(TextPeek.Attach(new Label
                {
                    Text = g.Title,
                    FontSize = 13,
                    LineBreakMode = LineBreakMode.TailTruncation,
                    VerticalOptions = LayoutOptions.Center,
                }), 0);
                head.Add(new Label
                {
                    Text = minutes,
                    FontSize = 13,
                    HorizontalTextAlignment = TextAlignment.End,
                    VerticalOptions = LayoutOptions.Center,
                }, 1);

                stack.Children.Add(head);
                stack.Children.Add(new ProgressBar
                {
                    Progress = g.Minutes < 0 ? 0 : Math.Clamp(g.Minutes / (double)max, 0, 1),
                    ProgressColor = GoalColor,
                    HeightRequest = 4,
                });

                // 四周趋势：和趋势柱同一套读法（左旧右新，空 = 那周没动）
                var trend = TrendOf(g.Id);
                var trendRow = new HorizontalStackLayout { Spacing = 8 };
                trendRow.Children.Add(BuildSpark(trend));
                var trendLabel = new Label
                {
                    Text = trend[3] > 0
                        ? $"本周 {Fmt(trend[3])} · 最后活动 {last}"
                        : $"本周没动 · 最后活动 {last}",
                    FontSize = 11,
                    VerticalOptions = LayoutOptions.End,
                };
                trendLabel.SetAppThemeColor(Label.TextColorProperty, MutedLight, MutedDark);
                trendRow.Children.Add(trendLabel);

                stack.Children.Add(trendRow);

                GoalRows.Children.Add(stack);
            }
        }

        /// <summary>四周迷你柱：空周画一根细灰线，不是留白（留白会被读成"没有数据"）。</summary>
        private static View BuildSpark(int[] buckets)
        {
            int max = Math.Max(1, buckets.Max());

            var row = new HorizontalStackLayout { Spacing = 3, VerticalOptions = LayoutOptions.End };
            foreach (int m in buckets)
            {
                double h = m == 0 ? 3 : Math.Max(6, m * 22.0 / max);
                var bar = new BoxView
                {
                    WidthRequest = 7,
                    HeightRequest = h,
                    CornerRadius = 2,
                    VerticalOptions = LayoutOptions.End,
                };
                if (m == 0)
                {
                    bar.SetAppThemeColor(BoxView.ColorProperty, LineLight, LineDark);
                }
                else
                {
                    bar.Color = GoalColor;
                }
                row.Children.Add(bar);
            }
            return row;
        }

        /// <summary>本周一 00:00（固定 UTC+8，和周统计口径一致）。</summary>
        private static DateTimeOffset WeekStart(DateTimeOffset now)
        {
            var day = Period.StartOfDay(now);
            return day.AddDays(-(((int)day.DayOfWeek + 6) % 7));
        }

        /// <summary>名称 + 数值 + 占比 + 进度条。与支出页同一套视觉，两页看起来才是一套系统。</summary>
        private static View BuildRow(
            string name, string value, double ratio, double barRatio, Color color, string pct)
        {
            var head = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(new GridLength(46)),
                },
                ColumnSpacing = 8,
            };

            head.Add(TextPeek.Attach(new Label
            {
                Text = name,
                FontSize = 13,
                VerticalOptions = LayoutOptions.Center,
                LineBreakMode = LineBreakMode.TailTruncation,
            }), 0);

            head.Add(new Label
            {
                Text = value,
                FontSize = 13,
                HorizontalTextAlignment = TextAlignment.End,
                VerticalOptions = LayoutOptions.Center,
            }, 1);

            var pctLabel = new Label
            {
                Text = pct,
                FontSize = 11,
                HorizontalTextAlignment = TextAlignment.End,
                VerticalOptions = LayoutOptions.Center,
            };
            pctLabel.SetAppThemeColor(Label.TextColorProperty, MutedLight, MutedDark);
            head.Add(pctLabel, 2);

            var stack = new VerticalStackLayout { Spacing = 4 };
            stack.Children.Add(head);
            stack.Children.Add(new ProgressBar
            {
                Progress = Math.Clamp(barRatio, 0, 1),
                ProgressColor = color,
                HeightRequest = 4,
            });

            return stack;
        }

        // ============================================================
        // 交互
        // ============================================================

        private async void OnRefreshClicked(object sender, EventArgs e)
        {
            await LoadLocalAsync();
            await RefreshFromServerAsync();
        }

        private async void OnRefreshing(object sender, EventArgs e)
        {
            await LoadLocalAsync();
            await RefreshFromServerAsync();
            Refresher.IsRefreshing = false;
        }

        private async void OnGoRecordClicked(object sender, EventArgs e)
        {
            try
            {
                // 去「记录」目录，不直接跳到时间块页：以后记录类型会更多，这一跳不该替用户决定记哪种
                await Shell.Current.GoToAsync("//record");
            }
            catch
            {
                FooterLabel.Text = "没找到记录页，请点底部「记录」";
            }
        }

        /// <summary>R10：去时间块库翻明细。不带参数 = 全部，想看哪段在库里自己筛。</summary>
        private async void OnLibraryClicked(object sender, EventArgs e)
        {
            try
            {
                await Shell.Current.GoToAsync(OverviewCatalog.BlockLibraryRoute);
            }
            catch
            {
                FooterLabel.Text = "没找到时间块库";
            }
        }

        // ============================================================
        // R14：日历视图（只读）
        //
        // 三档三个形态：
        //   today → 单日时刻轴；week → 一周日期条 + 单日时刻轴；month → 月历格子 + 单日时刻轴。
        //
        // 数据分两层（决定了"打开空不空"）：
        //   * 月历格子 / 周日期条按 OccurredAt 分天聚合 —— 手动块也能画；
        //   * 单日时刻轴必须 StartedAt/EndedAt —— 只有 R13 自动切片确认入库的块才有。
        //     手动块没有起止，不画：按录入时刻假摆 = "我几点点的按钮"，不是"我几点干了啥"。
        //
        // 重叠规则：贪心分轨（轨尾 ≤ 本段起点才能进），竖屏最多 2 轨，塞不下的计数说明。
        // ============================================================

        private const double HourPx = 52;   // 一小时的高度
        private const double Gutter = 0.07; // 左侧时刻标签占的宽度比例

        private async Task RenderCalendarAsync()
        {
            var (from, to) = RangeOf(_period);
            _calBlocks = await _blocks.GetByRangeAsync(from, to);

            // 切档时把选中日重置到今天：避免停在上一档选过的那天让人找不到
            _calendarDay = Period.StartOfDay(DateTimeOffset.UtcNow);
            RenderCalendar();
        }

        private void RenderCalendar()
        {
            CalendarBody.Children.Clear();

            switch (_period)
            {
                case "week":
                    var monday = DateTimeOffset.FromUnixTimeMilliseconds(RangeOf("week").From);
                    CalendarBody.Children.Add(BuildWeekBar(monday));
                    break;

                case "month":
                    CalendarBody.Children.Add(BuildMonthGrid());
                    break;
            }

            CalendarBody.Children.Add(BuildDayTimeline());
        }

        /// <summary>周档顶部的一行日期条：点选切天，"•"= 那天有记录。</summary>
        private View BuildWeekBar(DateTimeOffset monday)
        {
            long today = Period.StartOfDay(DateTimeOffset.UtcNow).ToUnixTimeMilliseconds();
            string[] wd = { "一", "二", "三", "四", "五", "六", "日" };

            var row = new Grid { ColumnSpacing = 4 };
            for (int i = 0; i < 7; i++) row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

            for (int i = 0; i < 7; i++)
            {
                var day = monday.AddDays(i);
                long ds = day.ToUnixTimeMilliseconds();
                long de = day.AddDays(1).ToUnixTimeMilliseconds();
                bool has = _calBlocks.Any(b => b.OccurredAt >= ds && b.OccurredAt < de);
                bool sel = ds == _calendarDay.ToUnixTimeMilliseconds();

                var btn = new Button
                {
                    Text = wd[i] + "\n" + day.Day,
                    FontSize = 12,
                    Padding = 0,
                    HeightRequest = 46,
                    CornerRadius = 8,
                };

                if (sel)
                {
                    btn.BackgroundColor = ActiveBg;
                    btn.TextColor = Colors.White;
                }
                else
                {
                    btn.SetAppThemeColor(Button.BackgroundColorProperty, InactiveBgLight, InactiveBgDark);
                    btn.SetAppThemeColor(Button.TextColorProperty, ChipTextLight, ChipTextDark);
                }

                if (has && !sel) btn.Text = wd[i] + "\n" + day.Day + " •";

                var captured = day;
                btn.Clicked += (_, _) =>
                {
                    _calendarDay = captured;
                    RenderCalendar();
                };
                row.Add(btn, i, 0);
            }
            return row;
        }

        /// <summary>月历格子：格内数字 + 1~3 根片色条（那天记过的前三类片）。点格子看那天的时刻轴。</summary>
        private View BuildMonthGrid()
        {
            var monthStart = new DateTimeOffset(_calendarDay.Year, _calendarDay.Month, 1, 0, 0, 0, Period.Cst);
            int daysInMonth = DateTime.DaysInMonth(monthStart.Year, monthStart.Month);
            int lead = ((int)monthStart.DayOfWeek + 6) % 7; // 周一=0
            int rows = (lead + daysInMonth + 6) / 7;

            var grid = new Grid { RowSpacing = 3, ColumnSpacing = 3 };
            for (int c = 0; c < 7; c++) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); // 表头
            for (int r = 0; r < rows; r++) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            string[] wd = { "一", "二", "三", "四", "五", "六", "日" };
            for (int c = 0; c < 7; c++)
            {
                var head = new Label { Text = wd[c], FontSize = 10, HorizontalTextAlignment = TextAlignment.Center };
                head.SetAppThemeColor(Label.TextColorProperty, MutedLight, MutedDark);
                grid.Add(head, c, 0);
            }

            long today = Period.StartOfDay(DateTimeOffset.UtcNow).ToUnixTimeMilliseconds();

            for (int d = 1; d <= daysInMonth; d++)
            {
                var day = monthStart.AddDays(d - 1);
                long ds = day.ToUnixTimeMilliseconds();
                long de = day.AddDays(1).ToUnixTimeMilliseconds();
                bool isToday = ds == today;
                bool sel = ds == _calendarDay.ToUnixTimeMilliseconds();

                var cell = new VerticalStackLayout { Spacing = 2 };

                var num = new Label
                {
                    Text = d.ToString(),
                    FontSize = 11,
                    HorizontalTextAlignment = TextAlignment.Center,
                    FontAttributes = isToday || sel ? FontAttributes.Bold : FontAttributes.None,
                };
                if (sel || isToday) num.TextColor = ActiveBg;
                else num.SetAppThemeColor(Label.TextColorProperty, ChipTextLight, ChipTextDark);
                cell.Children.Add(num);

                // 那天记过哪些片：按片聚合分钟，前三类各一根色条
                var bars = new HorizontalStackLayout { Spacing = 2, HorizontalOptions = LayoutOptions.Center };
                var piecesOfDay = _calBlocks
                    .Where(b => b.OccurredAt >= ds && b.OccurredAt < de && !string.IsNullOrEmpty(b.PieceCode))
                    .GroupBy(b => Pieces.Canonical(b.PieceCode))
                    .Select(g => (Code: g.Key, Minutes: g.Sum(b => Math.Max(0, b.DurationMin))))
                    .Where(x => x.Minutes > 0)
                    .OrderByDescending(x => x.Minutes)
                    .Take(3)
                    .ToList();
                foreach (var p in piecesOfDay)
                {
                    bars.Children.Add(new BoxView
                    {
                        WidthRequest = 10,
                        HeightRequest = 4,
                        CornerRadius = 2,
                        Color = PieceColors.For(p.Code),
                    });
                }
                if (piecesOfDay.Count > 0) cell.Children.Add(bars);

                var tap = new TapGestureRecognizer();
                var captured = day;
                tap.Tapped += (_, _) =>
                {
                    _calendarDay = captured;
                    RenderCalendar();
                };
                cell.GestureRecognizers.Add(tap);

                int col = (lead + d - 1) % 7;
                int row = (lead + d - 1) / 7 + 1;
                grid.Add(cell, col, row);
            }
            return grid;
        }

        /// <summary>
        /// 单日时刻轴：纵轴 0~24 点，块按起止时刻贴上去，同段并排最多 2 轨。
        /// 手动块（无起止）不画，只在底下说明数量。
        /// </summary>
        private View BuildDayTimeline()
        {
            var stack = new VerticalStackLayout { Spacing = 6 };

            var dayStart = Period.StartOfDay(_calendarDay);
            long ds = dayStart.ToUnixTimeMilliseconds();
            long de = dayStart.AddDays(1).ToUnixTimeMilliseconds();

            var dayBlocks = _calBlocks.Where(b => b.OccurredAt >= ds && b.OccurredAt < de).ToList();

            // 只有带真实起止的块才进时刻轴；跨天块按天夹紧（Anomaly 已有标记，这里不重复处理）
            var timed = dayBlocks
                .Where(b => b.StartedAt > 0 && b.EndedAt > b.StartedAt)
                .Select(b => (B: b, S: Math.Max(b.StartedAt, ds), E: Math.Min(b.EndedAt, de)))
                .Where(x => x.E > x.S)
                .OrderBy(x => x.S)
                .ToList();
            int manual = dayBlocks.Count(b => !(b.StartedAt > 0 && b.EndedAt > b.StartedAt));

            // 标题行
            var head = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(GridLength.Auto),
                },
                ColumnSpacing = 8,
            };
            var title = new Label
            {
                Text = dayStart.ToString("M月d日") + WeekName(dayStart) + $" · {timed.Count} 段",
                FontSize = 14,
                FontAttributes = FontAttributes.Bold,
                VerticalOptions = LayoutOptions.Center,
            };
            title.SetAppThemeColor(Label.TextColorProperty, ChipTextLight, ChipTextDark);
            head.Add(title, 0);
            var legend = new Label
            {
                Text = "只画自动切片（带起止）的块",
                FontSize = 10,
                VerticalOptions = LayoutOptions.Center,
                HorizontalTextAlignment = TextAlignment.End,
            };
            legend.SetAppThemeColor(Label.TextColorProperty, MutedLight, MutedDark);
            head.Add(legend, 1);
            stack.Children.Add(head);

            // 贪心分轨：轨尾 ≤ 本段起点才能进这条轨；最多 2 轨，塞不下的进 overflow
            var trackEnds = new List<long>();
            var placed = new List<((LocalBlock B, long S, long E) X, int Track)>();
            int overflow = 0;
            foreach (var x in timed)
            {
                int t = trackEnds.FindIndex(end => end <= x.S);
                if (t < 0)
                {
                    if (trackEnds.Count >= 2)
                    {
                        overflow++;
                        continue;
                    }
                    trackEnds.Add(x.E);
                    t = trackEnds.Count - 1;
                }
                else
                {
                    trackEnds[t] = x.E;
                }
                placed.Add((x, t));
            }
            int tracks = Math.Max(1, trackEnds.Count);

            var layout = new AbsoluteLayout { HeightRequest = 24 * HourPx };

            // 时刻刻度线 + 左侧标签
            for (int h = 0; h <= 24; h++)
            {
                double y = h * HourPx;

                var line = new BoxView { HeightRequest = 1 };
                line.SetAppThemeColor(BoxView.ColorProperty, LineLight, LineDark);
                AbsoluteLayout.SetLayoutBounds(line, new Rect(Gutter, y, 1 - Gutter, 1));
                AbsoluteLayout.SetLayoutFlags(line,
                    AbsoluteLayoutFlags.XProportional | AbsoluteLayoutFlags.WidthProportional);
                layout.Children.Add(line);

                if (h < 24)
                {
                    var lab = new Label { Text = $"{h:00}:00", FontSize = 9 };
                    lab.SetAppThemeColor(Label.TextColorProperty, MutedLight, MutedDark);
                    AbsoluteLayout.SetLayoutBounds(lab, new Rect(0, y, 44, 12));
                    layout.Children.Add(lab);
                }
            }

            // 时间块
            foreach (var (x, track) in placed)
            {
                double fx = Gutter + track * ((1 - Gutter) / tracks);
                double fw = (1 - Gutter) / tracks - 0.006;
                double yStart = (x.S - ds) / 60000.0 / 60.0 * HourPx;
                double hPx = Math.Max(4, (x.E - x.S) / 60000.0 / 60.0 * HourPx);

                var fill = PieceColors.For(x.B.PieceCode);
                string name = _settings.DisplayName(x.B.PieceCode);

                View block;
                if (hPx >= 18)
                {
                    // 高度够才写字：太窄的块只出色条，字挤进去谁都读不了
                    string text = hPx >= 34 ? $"{name} {FormatHm(x.S)}-{FormatHm(x.E)}" : name;
                    block = new Border
                    {
                        BackgroundColor = fill,
                        StrokeThickness = 0,
                        // Border 没有 CornerRadius（CS0117），圆角走 StrokeShape
                        StrokeShape = new RoundRectangle { CornerRadius = 4 },
                        Content = new Label
                        {
                            Text = text,
                            FontSize = 10,
                            TextColor = PieceColors.OnFill,
                            Padding = new Thickness(4, 1),
                            LineBreakMode = LineBreakMode.TailTruncation,
                            VerticalOptions = LayoutOptions.Center,
                        },
                    };
                }
                else
                {
                    block = new BoxView { Color = fill, CornerRadius = 4 };
                }

                AbsoluteLayout.SetLayoutBounds(block, new Rect(fx, yStart, fw, hPx));
                AbsoluteLayout.SetLayoutFlags(block,
                    AbsoluteLayoutFlags.XProportional | AbsoluteLayoutFlags.WidthProportional);
                layout.Children.Add(block);
            }

            // "现在"红线（只有看今天时画）：一眼看到此刻在轴上哪儿
            if (ds == Period.StartOfDay(DateTimeOffset.UtcNow).ToUnixTimeMilliseconds())
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (now >= ds && now < de)
                {
                    double y = (now - ds) / 60000.0 / 60.0 * HourPx;
                    var nowLine = new BoxView { Color = Color.FromArgb("#C24A4A"), HeightRequest = 2 };
                    AbsoluteLayout.SetLayoutBounds(nowLine, new Rect(Gutter, y, 1 - Gutter, 2));
                    AbsoluteLayout.SetLayoutFlags(nowLine,
                        AbsoluteLayoutFlags.XProportional | AbsoluteLayoutFlags.WidthProportional);
                    layout.Children.Add(nowLine);
                }
            }

            stack.Children.Add(layout);

            // 底部说明：说清楚"轴上没有"不等于"没记录"
            void Note(string text)
            {
                var lab = new Label { Text = text, FontSize = 10 };
                lab.SetAppThemeColor(Label.TextColorProperty, MutedLight, MutedDark);
                stack.Children.Add(lab);
            }

            if (timed.Count == 0 && manual == 0) Note("这一天还没有时间块");
            if (overflow > 0) Note($"另有 {overflow} 段重叠没画下（一天最多并排 2 轨）");
            if (manual > 0) Note($"{manual} 条手动记录没有起止时刻，画不进时间轴");

            return stack;
        }

        private static string FormatHm(long unixMs)
            => DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToOffset(Period.Cst).ToString("HH:mm");

        private static string WeekName(DateTimeOffset d)
            => "周" + "一二三四五六日"[((int)d.DayOfWeek + 6) % 7];

        // ============================================================
        // 口径与格式化
        // ============================================================

        private static (long From, long To) RangeOf(string period) => period switch
        {
            "week" => Period.ThisWeek(),
            "month" => Period.ThisMonth(),
            _ => Period.Today(),
        };

        /// <summary>区间内"已经过去"的分钟数：未来不算漏记，否则早上八点会看到 16 小时未记录。</summary>
        private static int Elapsed(long from, long to)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long end = Math.Min(to, now);
            return end <= from ? 0 : (int)((end - from) / 60000L);
        }

        private static string Fmt(int minutes) => BlockEntryPage.FormatMinutes(minutes);

        private static string RangeText(long from, long to)
        {
            var s = DateTimeOffset.FromUnixTimeMilliseconds(from).ToOffset(Period.Cst);
            var e = DateTimeOffset.FromUnixTimeMilliseconds(to).ToOffset(Period.Cst).AddDays(-1);

            return s.Date == e.Date
                ? s.ToString("yyyy-MM-dd") + "（今天）"
                : s.ToString("MM-dd") + " ~ " + e.ToString("MM-dd");
        }

        private static string LastActiveText(long lastAt)
        {
            if (lastAt <= 0) return "从未";

            var last = Period.StartOfDay(DateTimeOffset.FromUnixTimeMilliseconds(lastAt));
            var today = Period.StartOfDay(DateTimeOffset.UtcNow);
            int days = (int)Math.Round((today - last).TotalDays);

            return days switch
            {
                <= 0 => "今天",
                1 => "昨天",
                _ => days + " 天前",
            };
        }
    }
}
