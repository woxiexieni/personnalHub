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
    ///   range=today 或 week，配 offset=-1 表示往前几周；piece=片code；goal=目标id；q=预填搜索词
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

        /// <summary>chip 的字号。宽度估算和实际按钮共用这一个值，改字号只要改这里。</summary>
        private const double ChipFontSize = 12;

        private readonly LocalRecordStore<LocalBlock> _blocks;
        private readonly LocalRecordStore<LocalGoal> _goals;
        private readonly RecordSyncService<LocalBlock> _blockSync;
        private readonly PieceSettings _settings;
        private readonly BlockMirrorService _mirror;

        /// <summary>一次渲染多少行。超过就先停在这里，底部给一个「显示更多」。</summary>
        private const int RenderPage = 200;

        /// <summary>一次向服务端要多少条。3000 条 ≈ 一年的记录量级，够翻很久。</summary>
        private const int RemoteLimit = 3000;

        /// <summary>当前数据源是不是服务器。false = 本地 30 天镜像。</summary>
        private bool _remote;

        /// <summary>当前已渲染到多少行（点「显示更多」往上加）。</summary>
        private int _renderLimit = RenderPage;

        /// <summary>上一次过滤出来的结果，供「显示更多」直接复用，不重新算。</summary>
        private List<LocalBlock> _lastRows = new();

        /// <summary>服务端返回的条数是否触到了上限（用来在标题上说明"只到这儿"）。</summary>
        private bool _remoteTruncated;

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

        /// <summary>本轮要铺的 chip，先攒起来再一次折行。BuildChips → AddChip → PackChips。</summary>
        private readonly List<Button> _chipBuffer = new();

        /// <summary>ChipRow 的可用宽度（已扣掉页面左右 20 的内边距）。-1 = 还没量过。</summary>
        private double _chipAvail = -1;

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
            PieceSettings settings,
            BlockMirrorService mirror)
        {
            InitializeComponent();

            _blocks = blocks;
            _goals = goals;
            _blockSync = blockSync;
            _settings = settings;
            _mirror = mirror;

            PaintSourceButton();
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
            // 先把 chip 全部造出来攒进 buffer，再一次性按宽度折行（见 PackChips）。
            // 直接往 ChipRow 里塞的话，中途每次 Add 都会触发一轮布局。
            _chipBuffer.Clear();

            AddChip("全部", () => (_filterPiece is null && _filterGoal is null && !_onlyPending),
                () => { _filterPiece = null; _filterGoal = null; _onlyPending = false; });

            // 待同步只对本地镜像有意义：服务器上的数据本来就已经在服务器上了
            if (!_remote)
            {
                AddChip("待同步", () => _onlyPending,
                    () => { _filterPiece = null; _filterGoal = null; _onlyPending = true; });
            }

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

            PackChips();
        }

        // ============================================================
        // chip 折行（不用 FlexLayout，原因见 xaml 里的注释）
        // ============================================================

        /// <summary>
        /// 按可用宽度把 _chipBuffer 里的 chip 贪心排成若干行。
        /// 每行是一个 HorizontalStackLayout，行与行之间靠 ChipRow.Spacing。
        ///
        /// 宽度用估算而不是真实测量：MAUI 里在 handler 挂上之前拿不到准确宽度，
        /// 而这一段可能发生在 OnNavigatedTo（此时还没布局完）。估算故意偏大一点，
        /// 宁可多折一行，也绝不横向溢出 —— 溢出就是用户现在遇到的"要往右划很久"。
        /// </summary>
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

                // 一行一个放不下（超长的目标名）：自己独占一行，并硬压到屏幕宽度内
                if (w > avail)
                {
                    chip.MaximumWidthRequest = avail;
                    w = avail;
                }

                // 该换行了：本行已有内容、再放一个就超宽
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

        /// <summary>一个 chip 折行后占的水平宽度：文字估算 + 左右 padding 12 + 一点余量。</summary>
        private static double EstimateChipWidth(string? text)
        {
            const double padding = 26;   // 左右各 12，另加 2 的边框/取整余量

            if (string.IsNullOrEmpty(text)) return padding;

            double w = 0;
            foreach (var rune in text.EnumerateRunes())
            {
                int v = rune.Value;

                // 变体选择符 / 零宽连接符：emoji 的一部分，不单独占宽
                if (v == 0xFE0F || v == 0xFE0E || v == 0x200D) continue;

                if (v >= 0x1F000) w += ChipFontSize * 1.25;       // emoji（🍽 🎯）
                else if (v >= 0x1100) w += ChipFontSize * 1.0;    // 中日韩 / 全角标点
                else w += ChipFontSize * 0.56;                    // 拉丁、数字、空格
            }

            return w + padding;
        }

        /// <summary>页面还没布局完时的兜底宽度：按屏幕宽度算。</summary>
        private static double FallbackChipWidth()
        {
            var info = DeviceDisplay.Current.MainDisplayInfo;
            double dp = info.Width / Math.Max(1, info.Density);
            return Math.Max(120, dp - 40);   // 40 = 页面 Padding 左右各 20
        }

        /// <summary>
        /// 页面宽度变了（首次布局、或转屏）就重新折行。
        /// 只在宽度真的变化时动手，否则每次高度变化都会重排一遍，容易来回抖。
        /// </summary>
        protected override void OnSizeAllocated(double width, double height)
        {
            base.OnSizeAllocated(width, height);

            if (width <= 0) return;

            double avail = width - 40;
            if (Math.Abs(avail - _chipAvail) <= 1) return;

            _chipAvail = avail;

            // 数据还没读进来时不排，等 LoadAsync 里的 BuildChips 一次到位
            if (_chipBuffer.Count > 0) PackChips();
        }

        /// <summary>
        /// 一个 chip = 一个小按钮。选中态蓝底白字，未选米灰底深字（与录入页 chips 同款）。
        /// 只造出来放进 buffer，位置由 PackChips 决定；间距也由行容器给，这里不挂 Margin。
        /// </summary>
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
                // 重画所有 chip 的选中态：重新构建最简单 —— chip 数量最多十几个
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
                // 目标名两边都要用（服务器回来的块只有 goal_id），本地 goals 表本身是全量的
                _goalsAll = await _goals.GetByRangeAsync(0, long.MaxValue);

                if (_remote)
                {
                    await LoadRemoteAsync();
                    return;
                }

                _all = (await _blocks.GetByRangeAsync(0, long.MaxValue))
                    .Where(b => !b.Deleted)
                    .ToList();

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

        /// <summary>
        /// 从服务器拉历史块。本地只有 30 天镜像，更早的只能在这儿看。
        ///
        /// 连不上不弹框、不报错：静默退回本地，只在状态行写一句实话。
        /// 这是全项目的老规矩 —— 连不上服务端是常态，不是错误。
        /// </summary>
        private async Task LoadRemoteAsync()
        {
            long to = _range?.To ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // 没限定范围就往前给足：服务端的 ListBlocks 在 from=0 时会默认只给 30 天，
            // 所以这里显式给一个很早的时间点，让它"从头给"。
            long from = _range?.From
                ?? DateTimeOffset.UtcNow.AddYears(-10).ToUnixTimeMilliseconds();

            var list = await _mirror.FetchBlocksAsync(from, to, RemoteLimit);

            if (list is null)
            {
                _remote = false;
                _remoteTruncated = false;
                PaintSourceButton();

                _all = (await _blocks.GetByRangeAsync(0, long.MaxValue))
                    .Where(b => !b.Deleted)
                    .ToList();

                BuildChips();
                var localRows = Filter(_all);
                RenderRangeHeader(localRows);
                RenderRows(localRows);
                StatusLabel.Text = "连不上服务器，先看本地这 30 天。";
                return;
            }

            _remoteTruncated = list.Count >= RemoteLimit;
            _all = list.Where(b => !b.Deleted).ToList();

            // 服务端回来的数据本来就都在服务端了，标成已同步 ——
            // 否则每行都会挂一条"待同步"红字（ToLocalBlock 故意不设这个标记）。
            foreach (var b in _all) b.Synced = true;

            BuildChips();
            var rows = Filter(_all);
            RenderRangeHeader(rows);
            RenderRows(rows);
            StatusLabel.Text = string.Empty;
        }

        private List<LocalBlock> Filter(List<LocalBlock> all)
        {
            // 筛选条件一变就回到第一页：不然换了个片还停在第 600 行，用户会以为没结果
            _renderLimit = RenderPage;

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

            RangeLabel.Text = _range?.Label ?? (_remote ? "服务器" : "全部");

            if (_remote)
            {
                RangeSubLabel.Text = "服务器数据库"
                    + (_remoteTruncated ? $" · 最近 {RemoteLimit} 条" : $" · 共 {rows.Count} 条")
                    + (minutes > 0 ? $" · {BlockRowEditor.FormatMinutes(minutes)}" : string.Empty);
            }
            else
            {
                RangeSubLabel.Text = (_range is null ? "最近 30 天 · 本地副本" : "本地副本内")
                    + $" · {rows.Count} 条"
                    + (minutes > 0 ? $" · {BlockRowEditor.FormatMinutes(minutes)}" : string.Empty)
                    + (pending > 0 ? $" · {pending} 条待同步" : string.Empty);
            }

            bool hasFilter = _filterPiece is not null || _filterGoal is not null
                || _onlyPending || _searchTerms.Count > 0 || _searchExcludes.Count > 0
                || _range is not null;
            ClearBtn.IsVisible = hasFilter;
        }

        private void RenderRows(List<LocalBlock> rows)
        {
            Rows.Children.Clear();
            _lastRows = rows;

            if (rows.Count == 0)
            {
                EmptyLabel.IsVisible = true;
                EmptyLabel.Text = _all.Count == 0
                    ? (_remote ? "服务器上还没有时间块。" : "本地还没有时间块。记一条就会出现在这里。")
                    : "这个筛选下没有块。点右上「清筛选」回到全部。";
                return;
            }

            EmptyLabel.IsVisible = false;

            // 一次最多铺 RenderPage 行：MAUI 的列表没有虚拟化，上千行会明显卡。
            // 多出来的放在底部一个「显示更多」，点一下加一页。
            int shown = Math.Min(rows.Count, _renderLimit);

            long? lastDay = null;
            int dayCount = 0;
            int dayMinutes = 0;

            foreach (var b in rows.Take(shown))
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

            if (rows.Count > shown)
            {
                Rows.Children.Add(BuildMoreButton(rows.Count - shown));
            }
        }

        /// <summary>底部「显示更多」：一次加一页，不自动滚到底就狂加载（那样反而更卡）。</summary>
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
                    // async 无关的同步点击，但兜一层总没坏处：宁可写一行字也不要让页面炸
                    StatusLabel.Text = "展开失败：" + ex.Message;
                }
            };

            return btn;
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

            // 备注要能看全：这是回看页，省略号等于把内容藏了。
            // 卡片高度随内容长（Border + VerticalStackLayout 自适应），不设 MaxLines。
            if (!string.IsNullOrWhiteSpace(b.Note))
            {
                info.Children.Add(new Label
                {
                    Text = b.Note,
                    FontSize = 11,
                    TextColor = Muted,
                    LineBreakMode = LineBreakMode.WordWrap,
                });
            }

            if (!string.IsNullOrWhiteSpace(b.Lesson))
            {
                info.Children.Add(new Label
                {
                    Text = "↻ " + b.Lesson,
                    FontSize = 11,
                    TextColor = Color.FromArgb("#3B6D11"),
                    LineBreakMode = LineBreakMode.WordWrap,
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

            // 点行：本地 → 轻编辑菜单；服务器 → 只读详情（改旧数据要连带同步、版本冲突，代价太大）
            var tap = new TapGestureRecognizer();
            tap.Tapped += async (_, _) =>
            {
                // TapGestureRecognizer 的 async 回调是 async void：异常没人接，安卓上直接闪退
                try
                {
                    if (_remote) await ShowRemoteRowAsync(id);
                    else await EditRowAsync(id);
                }
                catch (Exception ex)
                {
                    StatusLabel.Text = "打开失败：" + ex.Message;
                }
            };
            card.GestureRecognizers.Add(tap);

            return card;
        }

        /// <summary>服务器上的行只读：把这条的完整内容摊开看，不提供改动。</summary>
        private Task ShowRemoteRowAsync(string id)
        {
            var b = _all.FirstOrDefault(x => x.Id == id);
            if (b is null) return Task.CompletedTask;

            var when = DateTimeOffset.FromUnixTimeMilliseconds(b.OccurredAt).ToOffset(Period.Cst);
            var piece = Pieces.ByCode(b.PieceCode);
            string goalName = _goalsAll.FirstOrDefault(g => g.Id == b.GoalId)?.Title ?? string.Empty;

            var sb = new StringBuilder();
            sb.AppendLine($"{when:yyyy年M月d日 dddd HH:mm}");
            sb.AppendLine($"{piece?.Emoji} {_settings.DisplayName(b.PieceCode)} · "
                + (b.DurationMin > 0 ? BlockRowEditor.FormatMinutes(b.DurationMin) : "时长未填"));

            if (goalName.Length > 0) sb.AppendLine("🎯 " + goalName);
            if (!string.IsNullOrWhiteSpace(b.Note)) sb.AppendLine().AppendLine(b.Note);
            if (!string.IsNullOrWhiteSpace(b.Lesson)) sb.AppendLine().AppendLine("↻ " + b.Lesson);

            string worth = b.Worth switch { 1 => "值得", 2 => "一般", 3 => "别做了", _ => string.Empty };
            if (worth.Length > 0) sb.AppendLine("评价：" + worth);

            return DisplayAlert("服务器上的记录", sb.ToString().TrimEnd(), "知道了");
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
        // 数据源：本地 30 天 ⇄ 服务器
        // ============================================================

        /// <summary>按钮显示的是"现在在看哪里"：本地 30 天（灰）/ 服务器（蓝）。点一下切过去。</summary>
        private void PaintSourceButton()
        {
            if (_remote)
            {
                SourceBtn.Text = "服务器";
                SourceBtn.BackgroundColor = ChipActiveBg;
                SourceBtn.TextColor = Colors.White;
                SourceBtn.BorderColor = ChipActiveBg;
            }
            else
            {
                SourceBtn.Text = "本地 30 天";
                SourceBtn.BackgroundColor = Colors.Transparent;
                SourceBtn.TextColor = Muted;
                SourceBtn.BorderColor = Color.FromArgb("#D9D6CC");
            }
        }

        private async void OnSourceClicked(object? sender, EventArgs e)
        {
            // async void 的惯例：整段兜一层，出错只写一行字，不让 App 消失
            try
            {
                _remote = !_remote;

                if (_remote)
                {
                    // 「待同步」只对本地镜像有意义
                    _onlyPending = false;
                    StatusLabel.Text = "正在查服务器…";
                }
                else
                {
                    StatusLabel.Text = string.Empty;
                }

                _renderLimit = RenderPage;
                PaintSourceButton();
                await LoadAsync();
            }
            catch (Exception ex)
            {
                StatusLabel.Text = "切换数据源失败：" + ex.Message;
            }
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
            // async void：这里抛出去的异常没有任何人接，在安卓上就是直接闪退。
            // 所以整个方法兜一层，宁可留一行错误字，也不要让 App 消失。
            try
            {
                _range = null;
                _filterPiece = null;
                _filterGoal = null;
                _onlyPending = false;

                // Entry.Text 的默认值是 null，不是空串 —— 直接 .Length 会 NullReferenceException。
                // 用户没在搜索框打过字就点「清筛选」时必踩，这就是之前闪退的原因。
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
