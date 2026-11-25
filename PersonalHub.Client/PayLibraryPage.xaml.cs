using System.Text;
using PersonalHub.Client.Controls;
using PersonalHub.Client.Data;
using PersonalHub.Client.Services;

namespace PersonalHub.Client
{
    /// <summary>
    /// 账单明细库（R14.5）：支出的第一个"随时可翻"的地方。
    ///
    /// 之前的现状：支出概览只有大数字和分类占比，想翻"上周三买了什么"没入口 ——
    /// 这正是用户点名要的「明细」功能。照时间块库（R10）的模子：
    /// 全量（本地副本内）+ 按天倒序 + 区间/方向/分类筛选 + 搜索。
    ///
    /// 编辑口径：**改备注 + 删除**（账单跟钱有关，留删除口）。
    /// 金额/分类改动要走录入表单，库里不提供 —— 改错金额就删了重记，比在手机上
    /// 摆一排输入框干净。
    ///
    /// 数据边界：本地是服务端 30 天窗口的镜像，更早的在电脑管理端看。
    /// 跳转参数：range=today/week/month；dir=expense/income；q=预填搜索词
    /// </summary>
    [QueryProperty(nameof(RangeKey), "range")]
    [QueryProperty(nameof(DirKey), "dir")]
    [QueryProperty(nameof(SearchKey), "q")]
    public partial class PayLibraryPage : ContentPage
    {
        private static readonly Color Ink = Color.FromArgb("#2C2C2A");
        private static readonly Color Muted = Color.FromArgb("#78766E");
        private static readonly Color Faint = Color.FromArgb("#8A8A8E");
        private static readonly Color IncomeClr = Color.FromArgb("#3B6D11");
        private static readonly Color ChipBg = Color.FromArgb("#F1EFE8");
        private static readonly Color ChipActiveBg = Color.FromArgb("#185FA5");

        private const double ChipFontSize = 12;

        private readonly LocalRecordStore<LocalRecord> _store;
        private readonly RecordSyncService<LocalRecord> _sync;
        private readonly LocalCacheService _cache;

        private const int RenderPage = 200;
        private int _renderLimit = RenderPage;
        private List<LocalRecord> _lastRows = new();

        /// <summary>null = 全部。</summary>
        private (long From, long To, string Label)? _range;
        private int? _direction;                 // null = 支出收入都看
        private int? _category;

        private string _search = string.Empty;
        private List<string> _searchTerms = new();
        private List<string> _searchExcludes = new();
        private int _searchGen;                  // 防抖代数（想法库崩溃那次的教训：不用 CTS）

        private List<LocalRecord> _all = new();
        private IReadOnlyList<CategoryDto> _categories = Array.Empty<CategoryDto>();

        private readonly List<Button> _chipBuffer = new();
        private double _chipAvail = -1;

        public string RangeKey { get; set; } = string.Empty;
        public string DirKey { get; set; } = string.Empty;
        public string SearchKey { get; set; } = string.Empty;

        public PayLibraryPage(
            LocalRecordStore<LocalRecord> store,
            RecordSyncService<LocalRecord> sync,
            LocalCacheService cache)
        {
            InitializeComponent();
            _store = store;
            _sync = sync;
            _cache = cache;
        }

        protected override async void OnNavigatedTo(NavigatedToEventArgs args)
        {
            base.OnNavigatedTo(args);

            ApplyRouteParams();
            await LoadAsync();

            if (!string.IsNullOrEmpty(SearchKey))
            {
                SearchEntry.Text = SearchKey;
            }
        }

        private void ApplyRouteParams()
        {
            _range = RangeKey switch
            {
                "today" => Map(Period.Today(), "今天"),
                "week" => Map(Period.ThisWeek(), "本周"),
                "month" => Map(Period.ThisMonth(), "本月"),
                _ => null,
            };

            _direction = DirKey switch
            {
                "expense" => 0,
                "income" => 1,
                _ => null,
            };
        }

        private static (long From, long To, string Label) Map((long From, long To) r, string label)
            => (r.From, r.To, label);

        // ============================================================
        // 筛选 chips（照 BlockLibraryPage 的三段式：AddChip 攒 buffer → PackChips 折行）
        // ============================================================

        private void BuildChips()
        {
            _chipBuffer.Clear();

            AddChip("全部时段", () => _range is null, () => { _range = null; });
            AddChip("今天", () => _range?.Label == "今天", () => _range = Map(Period.Today(), "今天"));
            AddChip("本周", () => _range?.Label == "本周", () => _range = Map(Period.ThisWeek(), "本周"));
            AddChip("本月", () => _range?.Label == "本月", () => _range = Map(Period.ThisMonth(), "本月"));

            AddChip("支出+收入", () => _direction is null, () => _direction = null);
            AddChip("只看支出", () => _direction == 0, () => _direction = 0);
            AddChip("只看收入", () => _direction == 1, () => _direction = 1);

            // 分类：只列数据里出现过的
            var seen = _all.Select(r => r.CategoryId).Where(c => c > 0).Distinct().ToHashSet();
            foreach (var cat in _categories.Where(c => seen.Contains(c.Id)))
            {
                int captured = cat.Id;
                AddChip(cat.Name, () => _category == captured,
                    () => _category = _category == captured ? null : captured);
            }

            PackChips();
        }

        private void PackChips()
        {
            ChipRow.Children.Clear();
            if (_chipBuffer.Count == 0) return;

            double avail = _chipAvail > 0 ? _chipAvail : FallbackChipWidth();

            HorizontalStackLayout? row = null;
            double used = 0;

            foreach (var chip in _chipBuffer)
            {
                double w = EstimateChipWidth(chip.Text);
                if (w > avail)
                {
                    chip.MaximumWidthRequest = avail;
                    w = avail;
                }

                if (row is null || (used + 6 + w > avail && used > 0))
                {
                    if (row is not null) ChipRow.Children.Add(row);
                    row = new HorizontalStackLayout { Spacing = 6 };
                    used = 0;
                }

                row.Children.Add(chip);
                used += (used > 0 ? 6 : 0) + w;
            }

            if (row is not null) ChipRow.Children.Add(row);
        }

        private static double EstimateChipWidth(string? text)
        {
            const double padding = 26;
            if (string.IsNullOrEmpty(text)) return padding;

            double w = 0;
            foreach (var rune in text.EnumerateRunes())
            {
                int v = rune.Value;
                if (v == 0xFE0F || v == 0xFE0E || v == 0x200D) continue;
                if (v >= 0x1F000) w += ChipFontSize * 1.25;
                else if (v >= 0x1100) w += ChipFontSize * 1.0;
                else w += ChipFontSize * 0.56;
            }
            return w + padding;
        }

        private static double FallbackChipWidth()
        {
            var info = DeviceDisplay.Current.MainDisplayInfo;
            double dp = info.Width / Math.Max(1, info.Density);
            return Math.Max(120, dp - 40);
        }

        protected override void OnSizeAllocated(double width, double height)
        {
            base.OnSizeAllocated(width, height);
            if (width <= 0) return;

            double avail = width - 40;
            if (Math.Abs(avail - _chipAvail) <= 1) return;
            _chipAvail = avail;
            if (_chipBuffer.Count > 0) PackChips();
        }

        private void AddChip(string text, Func<bool> isActive, Action onPick)
        {
            var chip = new Button
            {
                Text = text,
                FontSize = ChipFontSize,
                HeightRequest = 30,
                Padding = new Thickness(12, 0),
                CornerRadius = 15,
            };

            void Paint()
            {
                bool active = isActive();
                chip.BackgroundColor = active ? ChipActiveBg : ChipBg;
                chip.TextColor = active ? Colors.White : Ink;
            }

            chip.Clicked += (_, _) =>
            {
                onPick();
                BuildChips();
                _ = LoadAsync();
            };

            Paint();
            _chipBuffer.Add(chip);
        }

        // ============================================================
        // 加载与过滤
        // ============================================================

        private async Task LoadAsync()
        {
            try
            {
                var cats = await _cache.GetCategoriesAsync();
                _categories = cats.Count > 0 ? cats : LocalCacheService.FallbackCategories;

                _all = (await _store.GetByRangeAsync(0, long.MaxValue))
                    .Where(r => !r.Deleted)
                    .ToList();

                BuildChips();

                var rows = Filter(_all);
                RenderRangeHeader(rows);
                RenderRows(rows);
            }
            catch (Exception ex)
            {
                StatusLabel.Text = "读取失败：" + ex.Message;
            }
        }

        private List<LocalRecord> Filter(List<LocalRecord> all)
        {
            _renderLimit = RenderPage;

            IEnumerable<LocalRecord> q = all;

            if (_range is not null)
            {
                q = q.Where(r => r.SpentAt >= _range.Value.From && r.SpentAt < _range.Value.To);
            }

            if (_direction is not null)
            {
                q = q.Where(r => r.Direction == _direction.Value);
            }

            if (_category is not null)
            {
                q = q.Where(r => r.CategoryId == _category.Value);
            }

            if (_searchTerms.Count > 0 || _searchExcludes.Count > 0)
            {
                q = q.Where(Matches);
            }

            return q.OrderByDescending(r => r.SpentAt).ToList();
        }

        private bool Matches(LocalRecord r)
        {
            string haystack = string.Concat(
                r.Note ?? string.Empty, "\n",
                CategoryName(r.CategoryId), "\n",
                r.PayMethod ?? string.Empty);

            foreach (string t in _searchTerms)
            {
                if (!haystack.Contains(t, StringComparison.OrdinalIgnoreCase)) return false;
            }
            foreach (string t in _searchExcludes)
            {
                if (haystack.Contains(t, StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }

        private string CategoryName(int id) =>
            _categories.FirstOrDefault(c => c.Id == id)?.Name ?? string.Empty;

        // ============================================================
        // 渲染
        // ============================================================

        private void RenderRangeHeader(List<LocalRecord> rows)
        {
            long expense = rows.Where(r => r.Direction == 0).Sum(r => ToCents(r.Amount));
            long income = rows.Where(r => r.Direction == 1).Sum(r => ToCents(r.Amount));

            RangeLabel.Text = _range?.Label ?? "全部";
            RangeSubLabel.Text = $"本地副本 · {rows.Count} 条"
                + (expense > 0 ? $" · 支出 {FmtCents(expense)}" : string.Empty)
                + (income > 0 ? $" · 收入 {FmtCents(income)}" : string.Empty);

            bool hasFilter = _range is not null || _direction is not null || _category is not null
                || _searchTerms.Count > 0 || _searchExcludes.Count > 0;
            ClearBtn.IsVisible = hasFilter;
        }

        private void RenderRows(List<LocalRecord> rows)
        {
            Rows.Children.Clear();
            _lastRows = rows;

            if (rows.Count == 0)
            {
                EmptyLabel.IsVisible = true;
                EmptyLabel.Text = _all.Count == 0
                    ? "本地还没有账单。记一笔或开通知抓取，就会出现在这里。"
                    : "这个筛选下没有流水。点右上「清筛选」回到全部。";
                return;
            }

            EmptyLabel.IsVisible = false;

            int shown = Math.Min(rows.Count, _renderLimit);

            long? lastDay = null;
            long dayExpense = 0;
            long dayIncome = 0;
            int dayCount = 0;

            foreach (var r in rows.Take(shown))
            {
                long day = Period.DayStartOf(r.SpentAt).ToUnixTimeMilliseconds();
                if (day != lastDay)
                {
                    if (lastDay is not null) AddDayHeader(lastDay.Value, dayCount, dayExpense, dayIncome);
                    lastDay = day;
                    dayCount = 0;
                    dayExpense = 0;
                    dayIncome = 0;
                }

                Rows.Children.Add(BuildRow(r));
                dayCount++;
                long cents = ToCents(r.Amount);
                if (r.Direction == 0) dayExpense += cents; else dayIncome += cents;
            }

            if (lastDay is not null) AddDayHeader(lastDay.Value, dayCount, dayExpense, dayIncome);

            if (rows.Count > shown)
            {
                Rows.Children.Add(BuildMoreButton(rows.Count - shown));
            }
        }

        private Button BuildMoreButton(int remaining)
        {
            var btn = new Button
            {
                Text = $"还有 {remaining} 条 · 显示更多",
                FontSize = 12,
                HeightRequest = 36,
                BackgroundColor = Colors.Transparent,
                TextColor = ChipActiveBg,
                BorderWidth = 1,
                BorderColor = Color.FromArgb("#DCEDF9"),
                Margin = new Thickness(0, 8, 0, 0),
            };

            btn.Clicked += (_, _) =>
            {
                try
                {
                    _renderLimit += RenderPage;
                    RenderRows(_lastRows);
                }
                catch (Exception ex)
                {
                    StatusLabel.Text = "展开失败：" + ex.Message;
                }
            };

            return btn;
        }

        private void AddDayHeader(long dayStart, int count, long expense, long income)
        {
            var day = DateTimeOffset.FromUnixTimeMilliseconds(dayStart).ToOffset(Period.Cst);
            string sum = expense > 0 ? $"支出 {FmtCents(expense)}" : string.Empty;
            if (income > 0)
            {
                sum = sum.Length > 0 ? sum + " · 收入 " + FmtCents(income) : "收入 " + FmtCents(income);
            }

            Rows.Children.Add(new Label
            {
                Text = $"{day:M月d日} {day:dddd} · {count} 条"
                    + (sum.Length > 0 ? $" · {sum}" : string.Empty),
                FontSize = 12,
                FontAttributes = FontAttributes.Bold,
                TextColor = Muted,
                Margin = new Thickness(0, 6, 0, 0),
            });
        }

        private Border BuildRow(LocalRecord r)
        {
            string id = r.Id;   // 闭包捕获（TapGestureRecognizer sender 归属的坑，闭包不赌版本）

            var info = new VerticalStackLayout { Spacing = 1 };
            var when = DateTimeOffset.FromUnixTimeMilliseconds(r.SpentAt).ToOffset(Period.Cst);

            string cat = CategoryName(r.CategoryId);
            info.Children.Add(new Label
            {
                Text = r.Direction == 0 ? cat : cat + "（收入）",
                FontSize = 14,
                TextColor = Ink,
            });

            string method = string.IsNullOrWhiteSpace(r.PayMethod) ? string.Empty : " · " + r.PayMethod;
            info.Children.Add(new Label
            {
                Text = $"{when:HH:mm}{method}"
                    + (!r.Synced ? " · 待同步" : string.Empty),
                FontSize = 11,
                TextColor = !r.Synced ? Color.FromArgb("#D97706") : Faint,
            });

            if (!string.IsNullOrWhiteSpace(r.Note))
            {
                info.Children.Add(new Label
                {
                    Text = r.Note,
                    FontSize = 11,
                    TextColor = Muted,
                    LineBreakMode = LineBreakMode.WordWrap,
                });
            }

            long cents = ToCents(r.Amount);
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
            grid.Add(new Label
            {
                Text = (r.Direction == 0 ? "-" : "+") + FmtCents(cents),
                FontSize = 14,
                FontAttributes = FontAttributes.Bold,
                TextColor = r.Direction == 0 ? Ink : IncomeClr,
                VerticalOptions = LayoutOptions.Center,
            }, 1);

            var card = new Border
            {
                Stroke = Color.FromArgb("#E3E0D8"),
                StrokeThickness = 1,
                Padding = new Thickness(12, 8),
                Content = grid,
            };

            var tap = new TapGestureRecognizer();
            tap.Tapped += async (_, _) =>
            {
                // async void 的坑：整个回调兜一层
                try
                {
                    await EditRowAsync(id);
                }
                catch (Exception ex)
                {
                    StatusLabel.Text = "打开失败：" + ex.Message;
                }
            };
            card.GestureRecognizers.Add(tap);

            return card;
        }

        private async Task EditRowAsync(string id)
        {
            var row = await _store.FindAsync(id);
            if (row is null)
            {
                StatusLabel.Text = "这条流水已经不在了。";
                await LoadAsync();
                return;
            }

            long cents = ToCents(row.Amount);
            string pick = await DisplayActionSheet(
                $"{CategoryName(row.CategoryId)}　{(row.Direction == 0 ? "-" : "+")}{FmtCents(cents)}",
                "取消", null, "改备注", "删除");

            string? result = pick switch
            {
                "改备注" => await ChangeNoteAsync(row),
                "删除" => await DeleteAsync(row),
                _ => null,
            };

            if (result is null) return;

            StatusLabel.Text = result;
            await LoadAsync();
        }

        private async Task<string?> ChangeNoteAsync(LocalRecord row)
        {
            string? input = await TextEditorPage.ShowAsync(
                $"备注 · {CategoryName(row.CategoryId)}",
                initial: row.Note,
                placeholder: "比如「午饭 · 和同事拼单」",
                compact: true,
                maxLength: 200);
            if (input is null) return null;

            row.Note = input;
            row.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            row.Synced = false;
            await _store.UpdateAsync(row);
            _ = _sync.SyncAsync();

            return string.IsNullOrEmpty(input) ? "已清空备注" : "已更新备注";
        }

        /// <summary>删除：已同步的留墓碑（同 BlockRowEditor.DeleteAsync 的口径），没上传过的直接物理删。</summary>
        private async Task<string?> DeleteAsync(LocalRecord row)
        {
            bool ok = await DisplayAlert("删除这条流水？",
                $"{CategoryName(row.CategoryId)} {(row.Direction == 0 ? "-" : "+")}{FmtCents(ToCents(row.Amount))}",
                "删除", "留着");

            if (!ok) return null;

            if (row.Synced)
            {
                row.Deleted = true;
                row.Synced = false;
                row.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                await _store.UpdateAsync(row);
            }
            else
            {
                await _store.DeleteAsync(row.Id);
            }
            _ = _sync.SyncAsync();

            return "已删除";
        }

        // ============================================================
        // 搜索 / 清筛选
        // ============================================================

        private async void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
        {
            int gen = ++_searchGen;
            await Task.Delay(150);
            if (gen != _searchGen) return;

            _search = e.NewTextValue ?? string.Empty;
            (_searchTerms, _searchExcludes) = ParseSearch(_search);

            var rows = Filter(_all);
            RenderRangeHeader(rows);
            RenderRows(rows);
        }

        private async void OnClearClicked(object? sender, EventArgs e)
        {
            try
            {
                _range = null;
                _direction = null;
                _category = null;

                if (!string.IsNullOrEmpty(SearchEntry.Text))
                {
                    SearchEntry.Text = string.Empty;   // 触发 TextChanged → 重新过滤
                    return;
                }

                _searchTerms.Clear();
                _searchExcludes.Clear();
                await LoadAsync();
            }
            catch (Exception ex)
            {
                StatusLabel.Text = "清筛选失败：" + ex.Message;
            }
        }

        /// <summary>与想法库/时间块库同一套语法：空格 AND、-排除、引号短语。</summary>
        private static (List<string> Terms, List<string> Excludes) ParseSearch(string raw)
        {
            var terms = new List<string>();
            var excludes = new List<string>();

            if (string.IsNullOrWhiteSpace(raw)) return (terms, excludes);

            var buffer = new StringBuilder();
            bool quoted = false;

            void Flush()
            {
                if (buffer.Length == 0) return;

                string token = buffer.ToString().Trim();
                buffer.Clear();
                if (token.Length == 0 || token == "-") return;

                if (token.Length > 1 && token[0] == '-') excludes.Add(token[1..].Trim());
                else terms.Add(token);
            }

            foreach (char ch in raw)
            {
                if (ch == '"' || ch == '“' || ch == '”')
                {
                    quoted = !quoted;
                    continue;
                }

                if (!quoted && char.IsWhiteSpace(ch))
                {
                    Flush();
                    continue;
                }

                buffer.Append(ch);
            }

            Flush();

            terms.RemoveAll(t => t.Length == 0);
            excludes.RemoveAll(t => t.Length == 0);

            return (terms, excludes);
        }

        // ============================================================
        // 金额
        // ============================================================

        private static long ToCents(double amount)
            => (long)Math.Round(amount * 100, MidpointRounding.AwayFromZero);

        /// <summary>分 → 元字符串，整数不带小数（¥12 而不是 ¥12.00）。</summary>
        private static string FmtCents(long cents)
        {
            bool negative = cents < 0;
            long abs = Math.Abs(cents);
            string s = abs % 100 == 0
                ? $"¥{abs / 100}"
                : $"¥{abs / 100}.{abs % 100:D2}";
            return negative ? "-" + s : s;
        }
    }
}
