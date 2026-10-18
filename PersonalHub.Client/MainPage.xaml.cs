using PersonalHub.Client.Services;

namespace PersonalHub.Client
{
    /// <summary>
    /// 「统计」页：四个档位（今日/本周/本月/本年）的支出结构。
    ///
    /// 数据全部来自服务端 <c>GetSpendingStructure</c>，**客户端不自己算区间、不自己聚合**：
    /// 本地只有最近 30 天的副本，拿它算"本月"必然缺数据，一旦允许本地算，
    /// 就会出现"手机说 3200、网页说 3100"，而且两边都能自圆其说。
    ///
    /// 失败时的行为是明确的：有上次结果就显示上次结果 + 「离线」标识，没有就显示空态，
    /// 不会假装数据是最新的。
    /// </summary>
    public partial class MainPage : ContentPage
    {
        private static readonly Color ActiveBg = Color.FromArgb("#185FA5");
        private static readonly Color InactiveBg = Color.FromArgb("#F1EFE8");
        private static readonly Color ChipText = Color.FromArgb("#2C2C2A");
        private static readonly Color Muted = Color.FromArgb("#78766E");
        private static readonly Color ExpenseColor = Color.FromArgb("#B3261E");
        private static readonly Color IncomeColor = Color.FromArgb("#1C7C54");
        private static readonly Color PayColor = Color.FromArgb("#6B5CA5");

        private readonly StatsService _stats;

        private string _period = "today";
        private bool _busy;

        public MainPage(StatsService stats)
        {
            InitializeComponent();
            _stats = stats;

            StylePeriodButtons();
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();
            await LoadAsync();
        }

        // ============================================================
        // 档位切换
        // ============================================================

        private async void OnPeriodClicked(object? sender, EventArgs e)
        {
            if (sender is not Button b || b.CommandParameter is not string p) return;
            if (p == _period) return;

            _period = p;
            StylePeriodButtons();
            await LoadAsync();
        }

        private void StylePeriodButtons()
        {
            foreach (var (btn, key) in new[]
                     {
                         (TodayBtn, "today"), (WeekBtn, "week"), (MonthBtn, "month"), (YearBtn, "year"),
                     })
            {
                bool active = key == _period;
                btn.BackgroundColor = active ? ActiveBg : InactiveBg;
                btn.TextColor = active ? Colors.White : ChipText;
            }
        }

        // ============================================================
        // 取数 + 渲染
        // ============================================================

        private async void OnRefreshClicked(object sender, EventArgs e) => await LoadAsync(force: true);

        private async void OnRefreshing(object sender, EventArgs e)
        {
            await LoadAsync(force: true);
            Refresher.IsRefreshing = false;
        }

        private async Task LoadAsync(bool force = false)
        {
            if (_busy) return;
            _busy = true;
            RefreshBtn.IsEnabled = false;
            RefreshBtn.Text = "刷新中…";
            ConnLabel.Text = "连接中…";

            try
            {
                var fetch = await _stats.GetAsync(_period, force);

                if (fetch.Data is null)
                {
                    ShowEmpty("连不上服务端：" + (fetch.Error ?? "未知原因"));
                    ConnLabel.Text = "离线";
                    FooterLabel.Text = "检查：手机与电脑同一 Wi-Fi？服务端窗口还开着？";
                    return;
                }

                Render(fetch.Data, fetch.IsStale, fetch.Error);
            }
            finally
            {
                _busy = false;
                RefreshBtn.IsEnabled = true;
                RefreshBtn.Text = "刷新";
            }
        }

        private void Render(StructureDto d, bool stale, string? error)
        {
            // ---- 大数字 ----
            AmountLabel.Text = Money(d.ExpenseCents);
            IncomeLabel.Text = Money(d.IncomeCents);

            long balance = d.IncomeCents - d.ExpenseCents;
            BalanceLabel.Text = (balance < 0 ? "−" : string.Empty) + Money(Math.Abs(balance));
            BalanceLabel.TextColor = balance < 0 ? ExpenseColor : Colors.Black;

            // ---- 环比：没有上期数据时显示「—」，不显示「↑100%」----
            if (d.HasPrev)
            {
                DeltaLabel.Text = "较上期同期 " + Delta(d.DeltaRatio) + "（上期 " + Money(d.PrevExpenseCents) + "）";
                // 支出涨 = 红，跌 = 绿（中式习惯）
                DeltaLabel.TextColor = d.DeltaRatio >= 0 ? ExpenseColor : IncomeColor;
            }
            else
            {
                DeltaLabel.Text = "较上期同期 —（上期无支出）";
                DeltaLabel.TextColor = Muted;
            }

            RangeLabel.Text = RangeText(d) + $" · {d.ExpenseCount} 笔 · 日均 {Money(d.DailyAvgCents)}";

            // ---- 分类 / 支付方式 ----
            CategoryRows.Children.Clear();
            CategorySection.IsVisible = d.Categories.Count > 0;
            CategoryHint.Text = d.Categories.Count > 0
                ? $"共 {d.Categories.Count} 类"
                : string.Empty;

            long total = Math.Max(1, d.ExpenseCents);
            foreach (var c in d.Categories)
            {
                string? delta = c.HasPrev ? "较上期 " + Delta(c.DeltaRatio) : null;
                var deltaColor = c.HasPrev
                    ? (c.DeltaRatio >= 0 ? ExpenseColor : IncomeColor)
                    : Muted;

                CategoryRows.Children.Add(BuildRow(c.Name, c.AmountCents, c.AmountCents / (double)total, ExpenseColor, delta, deltaColor));
            }

            PayRows.Children.Clear();
            PaySection.IsVisible = d.PayMethods.Count > 0;
            foreach (var p in d.PayMethods)
            {
                PayRows.Children.Add(BuildRow(p.Name, p.AmountCents, p.AmountCents / (double)total, PayColor, null, Muted));
            }

            EmptySection.IsVisible = d.ExpenseCount == 0 && d.IncomeCents == 0;

            // ---- 状态行 ----
            if (stale)
            {
                ConnLabel.Text = "离线 · 上次结果";
                FooterLabel.Text = $"没连上服务端（{error}），下面是上次拿到的数据，不是最新的。";
            }
            else
            {
                ConnLabel.Text = string.IsNullOrEmpty(_stats.Address) ? "已连接" : "已连接 " + HostOf(_stats.Address!);
                FooterLabel.Text = "数据截至 " + TimeText(d.GeneratedAt) + " · 统计口径由服务端统一计算";
            }
        }

        private void ShowEmpty(string text)
        {
            AmountLabel.Text = Money(0);
            IncomeLabel.Text = Money(0);
            BalanceLabel.Text = Money(0);
            DeltaLabel.Text = " ";
            RangeLabel.Text = " ";
            CategorySection.IsVisible = false;
            PaySection.IsVisible = false;
            EmptySection.IsVisible = true;
            EmptyLabel.Text = text;
        }

        // ============================================================
        // 单行「名称 + 金额 + 占比 + 进度条」
        // ============================================================

        private static View BuildRow(string name, long cents, double ratio, Color color, string? delta, Color deltaColor)
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

            var nameLabel = new Label
            {
                Text = name,
                FontSize = 13,
                VerticalOptions = LayoutOptions.Center,
                LineBreakMode = LineBreakMode.TailTruncation,
            };
            var amountLabel = new Label
            {
                Text = Money(cents),
                FontSize = 13,
                HorizontalTextAlignment = TextAlignment.End,
                VerticalOptions = LayoutOptions.Center,
            };
            var pctLabel = new Label
            {
                Text = $"{ratio * 100:F0}%",
                FontSize = 11,
                TextColor = Muted,
                HorizontalTextAlignment = TextAlignment.End,
                VerticalOptions = LayoutOptions.Center,
            };

            Grid.SetColumn(amountLabel, 1);
            Grid.SetColumn(pctLabel, 2);
            head.Children.Add(nameLabel);
            head.Children.Add(amountLabel);
            head.Children.Add(pctLabel);

            var bar = new ProgressBar
            {
                Progress = Math.Clamp(ratio, 0, 1),
                ProgressColor = color,
                HeightRequest = 4,
            };

            var stack = new VerticalStackLayout { Spacing = 4 };
            stack.Children.Add(head);
            stack.Children.Add(bar);

            if (delta is not null)
            {
                stack.Children.Add(new Label { Text = delta, FontSize = 11, TextColor = deltaColor });
            }

            return stack;
        }

        // ============================================================
        // 格式化
        // ============================================================

        private static string Money(long cents) => "¥ " + (cents / 100.0).ToString("N2");

        private static string Delta(double ratio) => (ratio >= 0 ? "+" : string.Empty) + (ratio * 100).ToString("F1") + "%";

        private static string TimeText(long unixMs)
            => DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToOffset(Period.Cst).ToString("MM-dd HH:mm");

        private static string RangeText(StructureDto d)
        {
            var s = DateTimeOffset.FromUnixTimeMilliseconds(d.RangeStart).ToOffset(Period.Cst);
            var e = DateTimeOffset.FromUnixTimeMilliseconds(d.RangeEnd).ToOffset(Period.Cst).AddDays(-1); // 右开 → 回退一天

            return d.Period switch
            {
                "today" => s.ToString("yyyy-MM-dd") + "（今天）",
                "year" => s.ToString("yyyy-MM-dd") + " ~ " + e.ToString("yyyy-MM-dd"),
                _ => s.ToString("MM-dd") + " ~ " + e.ToString("MM-dd"),
            };
        }

        private async void OnGoRecordClicked(object sender, EventArgs e)
        {
            try
            {
                // 去「记录」Tab 的目录，而不是直接跳账单页：
                // 以后会有多种记录类型，这一跳不该替用户决定记哪种。
                await Shell.Current.GoToAsync("//record");
            }
            catch
            {
                // 路由名变了就退化成提示，不让页面崩
                FooterLabel.Text = "没找到记录页，请点底部「记录」";
            }
        }

        private static string HostOf(string url)
        {
            var s = url.Replace("http://", string.Empty).Replace("https://", string.Empty);
            int idx = s.LastIndexOf(':');
            return idx > 0 ? s.Substring(0, idx) : s;
        }
    }
}
