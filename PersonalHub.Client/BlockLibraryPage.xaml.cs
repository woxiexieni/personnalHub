using System.Text;
using PersonalHub.Client.Data;
using PersonalHub.Client.Services;

namespace PersonalHub.Client
{
    /// <summary>
    /// 时间块库（R10）：时间块第一个"随时可翻"的地方。
    ///
    /// 之前的现状：录入页只有今日列表，时间概览只有统计 —— 想翻"上周三下午干了什么"
    /// 没有任何入口。这一页照想法库的模子补上：全量（本地副本内）+ 按天倒序 + 筛选 chip + 搜索。
    ///
    /// 编辑口径（用户拍板）：**能改、不能删。** 行菜单只有改时长/换目标/备注 ——
    /// 库是回看用的，误删比误改伤；今天记错的在录入页当日就能删。
    /// 逻辑在 BlockRowEditor，录入页同一套，不各抄一份。
    ///
    /// 数据边界：手机本地只有 30 天镜像，更早的在电脑管理端看。空态照实说"本地没有"，
    /// 不假装是"你没记"。
    ///
    /// 跳转参数（这周回顾的数字都通到这里）：
    ///   ?range=today|week & offset=-1（往前几周） & piece=片code & goal=目标id & q=预填搜索词
    /// </summary>
    [QueryProperty(nameof(RangeKey), "range")]
    [QueryProperty(nameof(OffsetKey), "offset")]
    [QueryProperty(nameof(PieceKey), "piece")]
    [QueryProperty(nameof(GoalKey), "goal")]
    [QueryProperty(nameof(SearchKey), "q")]
    public partial class BlockLibraryPage : ContentPage
    {
        private static readonly Color Ink = Color.FromArgb("#2C2C2A");
        private static readonly Color Muted = Color.FromArgb("#78766E");
        private static readonly Color Faint = Color.FromArgb("#8A8A8E");
        private static readonly Color Up = Color.FromArgb("#185FA5");
        private static readonly Color PendingColor = Color.FromArgb("#D97706");

        private static readonly Color ChipBg = Color.FromArgb("#F1EFE8");
        private static readonly Color ChipActiveBg = Color.FromArgb("#185FA5");

        private readonly LocalRecordStore<LocalBlock> _blocks;
        private readonly LocalRecordStore<LocalGoal> _goals;
        private readonly RecordSyncService<LocalBlock> _blockSync;
        private readonly PieceSettings _settings;

        /// <summary>当前时间范围。null = 不限（全部 30 天）。</summary>
        private (long From, long To, string Label)? _range;

        /// <summary>筛选：片代号（Canonical 后）。null = 不限。</summary>
        private string? _filterPiece;

        /// <summary>筛选：目标 id。null = 不限。</summary>
        private string? _filterGoal;

        private bool _onlyPending;

        private string _search = string.Empty;
        private List<string> _searchTerms = new();
        private List<string> _searchExcludes = new();

        /// <summary>搜索防抖代数：只比较不释放，没有 CTS 那种竞态（想法库崩溃那次的教训）。</summary>
        private int _searchGen;

        /// <summary>全部块（已过滤 Deleted）。chips 和列表共用，避免查两遍。</summary>
        private List<LocalBlock> _all = new();

        private List<LocalGoal> _goalsAll = new();

        // ---- 路由参数（QueryProperty 回填，OnNavigatedTo 里统一消费）----
        public string RangeKey { get; set; } = string.Empty;
        public string OffsetKey { get; set; } = string.Empty;
        public string PieceKey { get; set; } = string.Empty;
        public string GoalKey { get; set; } = string.Empty;
        public string SearchKey { get; set; } = string.Empty;

        public BlockLibraryPage(
            LocalRecordStore<LocalBlock> blocks,
            LocalRecordStore<LocalGoal> goals,
            RecordSyncService<LocalBlock> blockSync,
            PieceSettings settings)
        {
            InitializeComponent();

            _blocks = blocks;
            _goals = goals;
            _blockSync = blockSync;
            _settings = settings;
        }

        protected override async void OnNavigatedTo(NavigatedToEventArgs args)
        {
            base.OnNavigatedTo(args);

            ApplyRouteParams();
            await LoadAsync();

            // 程序化带参跳转（从周回顾点进来）时，把预填搜索词放进输入框。
            // 赋值会触发 TextChanged → 150ms 后自动重过滤，不用手动再刷。
            if (!string.IsNullOrEmpty(SearchKey))
            {
                SearchEntry.Text = SearchKey;
            }
        }

        // ============================================================
        // 路由参数 → 初始筛选
        // ============================================================

        private void ApplyRouteParams()
        {
            // range=today / week；offset=-1 表示往前 N 周（配合 week）
            if (RangeKey == "today")
            {
                var (from, to) = Period.Today();
                _range = (from, to, "今天");
            }
            else if (RangeKey == "week")
            {
                int offset = int.TryParse(OffsetKey, out int o) ? Math.Clamp(o, -3, 0) : 0;
                var (from, to) = Period.ThisWeek();
                long shift = offset * 7L * 24 * 60 * 60 * 1000;
                from += shift;
                to += shift;
                _range = (from, to, offset == 0 ? "本周" : $"{DateTimeOffset.FromUnixTimeMilliseconds(from).ToOffset(Period.Cst):M月d日} 那一周");
            }

            if (!string.IsNullOrEmpty(PieceKey))
            {
                _filterPiece = Pieces.Canonical(PieceKey);
            }

            if (!string.IsNullOrEmpty(GoalKey))
            {
                _filterGoal = GoalKey;
            }
        }

        // ============================================================
        // 筛选 chips
        // ============================================================

        private void BuildChips()
        {
            ChipRow.Children.Clear();

            AddChip("全部", () => (_filterPiece is null && _filterGoal is null && !_onlyPending),
                () => { _filterPiece = null; _filterGoal = null; _onlyPending = false; });

            AddChip("待同步", () => _onlyPending,
                () => { _filterPiece = null; _filterGoal = null; _onlyPending = true; });

            // 片：只列数据里出现过的（Canonical 后），顺序照 Pieces.All，不摆没用的空格子
            var seenPieces = _all.Select(b => Pieces.Canonical(b.PieceCode))
                .Where(c => c.Length > 0)
                .Distinct().ToHashSet(StringComparer.Ordinal);
            foreach (var p in Pieces.All.Where(p => seenPieces.Contains(p.Code)))
            {
                AddChip($"{p.Emoji} {_settings.DisplayName(p.Code)}",
                    () => _filterPiece == p.Code,
                    () => { _filterPiece = p.Code; _filterGoal = null; _onlyPending = false; });
            }

            // 目标：只列有块挂着的目标，按标题排
            var goalNames = _goalsAll.ToDictionary(g => g.Id, g => g.Title);
            var seenGoals = _all.Select(b => b.GoalId ?? string.Empty)
                .Where(id => id.Length > 0 && goalNames.ContainsKey(id))
                .Distinct().ToList();
            foreach (string gid in seenGoals.OrderBy(id => goalNames[id], StringComparer.Ordinal))
            {
                string captured = gid;
                AddChip($"🎯 {goalNames[gid]}",
                    () => _filterGoal == captured,
                    () => { _filterGoal = captured; _filterPiece = null; _onlyPending = false; });
            }
        }

        /// <summary>一个 chip = 一个小按钮。选中态蓝底白字，未选米灰底深字（与录入页 chips 同款）。</summary>
        private void AddChip(string text, Func<bool> isActive, Action onPick)
        {
            var chip = new Button
            {
                Text = text,
                FontSize = 12,
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
                // 重画所有 chip 的选中态：重新构建最简单 —— chip 数量最多十几个
                BuildChips();
                _ = LoadAsync();
            };

            Paint();
            ChipRow.Children.Add(chip);
        }

        // ============================================================
        // 加载与过滤
        // ============================================================

        private async Task LoadAsync()
        {
            try
            {
                _all = (await _blocks.GetByRangeAsync(0, long.MaxValue))
                    .Where(b => !b.Deleted)
                    .ToList();
                _goalsAll = await _goals.GetByRangeAsync(0, long.MaxValue);

                // chips 只列"数据里出现过的片/目标"，数据变了就重排
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

        private List<LocalBlock> Filter(List<LocalBlock> all)
        {
            IEnumerable<LocalBlock> q = all;

            if (_range is not null)
            {
                q = q.Where(b => b.OccurredAt >= _range.Value.From && b.OccurredAt < _range.Value.To);
            }

            if (_filterPiece is not null)
            {
                q = q.Where(b => Pieces.Canonical(b.PieceCode) == _filterPiece);
            }

            if (_filterGoal is not null)
            {
                q = q.Where(b => b.GoalId == _filterGoal);
            }

            if (_onlyPending)
            {
                q = q.Where(b => !b.Synced);
            }

            if (_searchTerms.Count > 0 || _searchExcludes.Count > 0)
            {
                q = q.Where(Matches);
            }

            return q.OrderByDescending(b => b.OccurredAt).ToList();
        }

        /// <summary>一个块是否满足全部包含词、且不含任何排除词。搜备注 + 那句「下次怎么做」 + 片名 + 目标名。</summary>
        private bool Matches(LocalBlock b)
        {
            var goalName = _goalsAll.FirstOrDefault(g => g.Id == b.GoalId)?.Title ?? string.Empty;
            string haystack = string.Concat(
                b.Note ?? string.Empty, "\n",
                b.Lesson ?? string.Empty, "\n",
                _settings.DisplayName(b.PieceCode), "\n",
                goalName);

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

        // ============================================================
        // 渲染
        // ============================================================

        private void RenderRangeHeader(List<LocalBlock> rows)
        {
            int minutes = rows.Sum(b => Math.Max(0, b.DurationMin));
            int pending = rows.Count(b => !b.Synced);

            RangeLabel.Text = _range?.Label ?? "全部";
            RangeSubLabel.Text = (_range is null ? "最近 30 天 · 本地副本" : "本地副本内")
                + $" · {rows.Count} 条"
                + (minutes > 0 ? $" · {BlockRowEditor.FormatMinutes(minutes)}" : string.Empty)
                + (pending > 0 ? $" · {pending} 条待同步" : string.Empty);

            bool hasFilter = _filterPiece is not null || _filterGoal is not null
                || _onlyPending || _searchTerms.Count > 0 || _searchExcludes.Count > 0
                || _range is not null;
            ClearBtn.IsVisible = hasFilter;
        }

        private void RenderRows(List<LocalBlock> rows)
        {
            Rows.Children.Clear();

            if (rows.Count == 0)
            {
                EmptyLabel.IsVisible = true;
                EmptyLabel.Text = _range is null && _all.Count == 0
                    ? "本地还没有时间块。记一条就会出现在这里。"
                    : "这个筛选下没有块。点右上「清筛选」回到全部。";
                return;
            }

            EmptyLabel.IsVisible = false;

            long? lastDay = null;
            int dayCount = 0;
            int dayMinutes = 0;

            foreach (var b in rows)
            {
                long day = Period.DayStartOf(b.OccurredAt).ToUnixTimeMilliseconds();
                if (day != lastDay)
                {
                    if (lastDay is not null) AddDayHeader(lastDay.Value, dayCount, dayMinutes);
                    lastDay = day;
                    dayCount = 0;
                    dayMinutes = 0;
                }

                Rows.Children.Add(BuildRow(b));
                dayCount++;
                dayMinutes += Math.Max(0, b.DurationMin);
            }

            if (lastDay is not null) AddDayHeader(lastDay.Value, dayCount, dayMinutes);
        }

        private void AddDayHeader(long dayStart, int count, int minutes)
        {
            var day = DateTimeOffset.FromUnixTimeMilliseconds(dayStart).ToOffset(Period.Cst);
            Rows.Children.Add(new Label
            {
                Text = $"{day:M月d日} {day:dddd} · {count} 条"
                    + (minutes > 0 ? $" · {BlockRowEditor.FormatMinutes(minutes)}" : string.Empty),
                FontSize = 12,
                FontAttributes = FontAttributes.Bold,
                TextColor = Muted,
                Margin = new Thickness(0, 6, 0, 0),
            });
        }

        private Border BuildRow(LocalBlock b)
        {
            string id = b.Id;   // 闭包捕获：指针/点按手势的 sender 归属版本间有差异，闭包不赌版本

            var info = new VerticalStackLayout { Spacing = 1 };

            var piece = Pieces.ByCode(b.PieceCode);
            var when = DateTimeOffset.FromUnixTimeMilliseconds(b.OccurredAt).ToOffset(Period.Cst);

            info.Children.Add(new Label
            {
                Text = $"{piece?.Emoji} {_settings.DisplayName(b.PieceCode)}",
                FontSize = 14,
                TextColor = Ink,
            });

            string goalText = string.Empty;
            var goal = _goalsAll.FirstOrDefault(g => g.Id == b.GoalId);
            if (goal is not null) goalText = goal.Title;

            info.Children.Add(new Label
            {
                Text = $"{when:HH:mm}"
                    + (goalText.Length > 0 ? $" · 🎯 {goalText}" : string.Empty)
                    + (!b.Synced ? " · 待同步" : string.Empty),
                FontSize = 11,
                TextColor = !b.Synced ? PendingColor : Faint,
            });

            if (!string.IsNullOrWhiteSpace(b.Note))
            {
                info.Children.Add(new Label
                {
                    Text = b.Note,
                    FontSize = 11,
                    TextColor = Muted,
                    LineBreakMode = LineBreakMode.TailTruncation,
                    MaxLines = 2,
                });
            }

            if (!string.IsNullOrWhiteSpace(b.Lesson))
            {
                info.Children.Add(new Label
                {
                    Text = "↻ " + b.Lesson,
                    FontSize = 11,
                    TextColor = Color.FromArgb("#3B6D11"),
                    LineBreakMode = LineBreakMode.TailTruncation,
                    MaxLines = 2,
                });
            }

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
                Text = b.DurationMin > 0 ? BlockRowEditor.FormatMinutes(b.DurationMin) : "未填",
                FontSize = 13,
                TextColor = b.DurationMin > 0 ? Ink : Faint,
                VerticalOptions = LayoutOptions.Center,
            }, 1);

            var card = new Border
            {
                Stroke = Color.FromArgb("#E3E0D8"),
                StrokeThickness = 1,
                Padding = new Thickness(12, 8),
                Content = grid,
            };

            // 点行 → 轻编辑菜单（无删除）。整行可点，不用再放一个 ··· 按钮。
            var tap = new TapGestureRecognizer();
            tap.Tapped += async (_, _) => await EditRowAsync(id);
            card.GestureRecognizers.Add(tap);

            return card;
        }

        private async Task EditRowAsync(string id)
        {
            var row = await _blocks.FindAsync(id);
            if (row is null)
            {
                StatusLabel.Text = "这条块已经不在了。";
                await LoadAsync();
                return;
            }

            string? message = await BlockRowEditor.MenuAsync(
                this, row, _blocks, _goals, _settings, _blockSync, allowDelete: false);

            if (message is null) return;   // 取消 = 不动界面

            StatusLabel.Text = message;
            await LoadAsync();
        }

        // ============================================================
        // 搜索 / 清筛选
        // ============================================================

        private async void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
        {
            int gen = ++_searchGen;

            await Task.Delay(150);

            // 150ms 内又有新输入进来：这次是旧口径，放弃
            if (gen != _searchGen) return;

            _search = e.NewTextValue ?? string.Empty;
            (_searchTerms, _searchExcludes) = ParseSearch(_search);

            var rows = Filter(_all);
            RenderRangeHeader(rows);
            RenderRows(rows);
        }

        private async void OnClearClicked(object? sender, EventArgs e)
        {
            _range = null;
            _filterPiece = null;
            _filterGoal = null;
            _onlyPending = false;

            if (SearchEntry.Text.Length > 0)
            {
                SearchEntry.Text = string.Empty;   // 触发 TextChanged → 重新过滤
                return;
            }

            _searchTerms.Clear();
            _searchExcludes.Clear();
            await LoadAsync();
        }

        /// <summary>与想法库同一套语法：空格 AND、-排除、引号短语。引号只是分组，不做子串边界。</summary>
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

                // 光一个减号没有意义，别让它变成一个永远搜不到的词
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
    }
}
