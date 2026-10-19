using PersonalHub.Client.Data;
using PersonalHub.Client.Services;

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
        private static readonly Color InactiveBg = Color.FromArgb("#F1EFE8");
        private static readonly Color ChipText = Color.FromArgb("#2C2C2A");
        private static readonly Color Muted = Color.FromArgb("#78766E");
        private static readonly Color BarColor = Color.FromArgb("#185FA5");
        private static readonly Color GoalColor = Color.FromArgb("#6B5CA5");

        private readonly LocalRecordStore<LocalBlock> _blocks;
        private readonly LocalRecordStore<LocalGoal> _goals;
        private readonly BlockMirrorService _mirror;
        private readonly PieceSettings _settings;

        private string _period = "today";
        private bool _busy;

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
                btn.BackgroundColor = active ? ActiveBg : InactiveBg;
                btn.TextColor = active ? Colors.White : ChipText;
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
                head.Add(new Label
                {
                    Text = g.Title,
                    FontSize = 13,
                    LineBreakMode = LineBreakMode.TailTruncation,
                    VerticalOptions = LayoutOptions.Center,
                }, 0);
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
                trendRow.Children.Add(new Label
                {
                    Text = trend[3] > 0
                        ? $"本周 {Fmt(trend[3])} · 最后活动 {last}"
                        : $"本周没动 · 最后活动 {last}",
                    FontSize = 11,
                    TextColor = Muted,
                    VerticalOptions = LayoutOptions.End,
                });

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
                row.Children.Add(new BoxView
                {
                    WidthRequest = 7,
                    HeightRequest = h,
                    CornerRadius = 2,
                    Color = m == 0 ? Color.FromArgb("#E3E1DA") : GoalColor,
                    VerticalOptions = LayoutOptions.End,
                });
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

            head.Add(new Label
            {
                Text = name,
                FontSize = 13,
                VerticalOptions = LayoutOptions.Center,
                LineBreakMode = LineBreakMode.TailTruncation,
            }, 0);

            head.Add(new Label
            {
                Text = value,
                FontSize = 13,
                HorizontalTextAlignment = TextAlignment.End,
                VerticalOptions = LayoutOptions.Center,
            }, 1);

            head.Add(new Label
            {
                Text = pct,
                FontSize = 11,
                TextColor = Muted,
                HorizontalTextAlignment = TextAlignment.End,
                VerticalOptions = LayoutOptions.Center,
            }, 2);

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
