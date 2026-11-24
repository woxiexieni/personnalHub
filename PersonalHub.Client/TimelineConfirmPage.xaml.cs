using Microsoft.Maui.Layouts;
using PersonalHub.Client.Data;
using PersonalHub.Client.Services;

namespace PersonalHub.Client
{
    /// <summary>
    /// 时间线确认页（R13）：自动切片的"晚间过一遍"入口。
    ///
    /// 设计立场（拍板时定的）：**确认节奏宁缺毋滥** —— 不实时打扰，
    /// 用户想看的时候进来，把今天的时间线一条条过一遍：
    ///   「记入」= 候选标签对，写正式时间块（StartedAt/EndedAt 是真值）；
    ///   「换片」= 猜错了，从 12 片里挑一个；
    ///   「忽略」= 不想要这段。
    /// 关页时没处理的段**按候选自动入库并打「自动」标记** ——
    /// 兜底路径必须存在，否则"没确认的段"会变成一种心理负担（确认疲劳的解药）。
    /// </summary>
    public partial class TimelineConfirmPage : ContentPage
    {
        private readonly LocalRecordStore<LocalPendingSegment> _pending;
        private readonly LocalRecordStore<LocalBlock> _blocks;
        private readonly PieceSettings _settings;

        public TimelineConfirmPage(
            LocalRecordStore<LocalPendingSegment> pending,
            LocalRecordStore<LocalBlock> blocks,
            PieceSettings settings)
        {
            InitializeComponent();
            _pending = pending;
            _blocks = blocks;
            _settings = settings;
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();
            await LoadAsync();
        }

        protected override async void OnDisappearing()
        {
            base.OnDisappearing();

            // 兜底：没处理的段按候选自动入库（空候选的留着，下次手动挑）。
            // 不弹任何提示 —— 自动入库本来就该是"无声的默认"。
            await AutoApplyRemainingAsync();
        }

        // ============================================================
        // 加载
        // ============================================================

        private async Task LoadAsync()
        {
            var segs = (await _pending.GetAllAsync())
                .Where(s => s.Status == 0)
                .OrderBy(s => s.StartedAt)
                .ToList();

            Rows.Children.Clear();
            EmptyLabel.IsVisible = segs.Count == 0;
            ApplyAllBtn.IsVisible = segs.Any(s => !string.IsNullOrEmpty(s.PieceCode));

            int unknown = segs.Count(s => string.IsNullOrEmpty(s.PieceCode));
            HeadLabel.Text = segs.Count == 0
                ? "没有待确认的时间片。"
                : $"{segs.Count} 段待确认"
                    + (unknown > 0 ? $" · 其中 {unknown} 段没猜出标签，点「换片」自己挑" : "");

            foreach (var seg in segs)
            {
                Rows.Children.Add(BuildCard(seg));
            }
        }

        private View BuildCard(LocalPendingSegment seg)
        {
            string timeText =
                $"{Period.ToLocalTime(seg.StartedAt):MM-dd HH:mm} – {Period.ToLocalTime(seg.EndedAt):HH:mm}"
                + $" · {FormatMin(Math.Max(1, seg.Minutes))}";

            string pieceText = seg.PieceCode.Length > 0
                ? _settings.DisplayName(seg.PieceCode)
                : "没猜出来";

            string appText = seg.SourceApp.Length > 0
                ? "证据：" + seg.SourceApp[(seg.SourceApp.LastIndexOf('.') + 1)..]
                : "证据：无";

            var info = new VerticalStackLayout { Spacing = 2 };
            info.Children.Add(new Label { Text = timeText, FontSize = 14 });
            info.Children.Add(new Label
            {
                Text = $"候选：{pieceText} · {appText}",
                FontSize = 12,
                TextColor = Color.FromArgb("#78766E"),
            });

            // ---- 换片网格：先建好藏起来，点「换片」再展开（折叠套路：切 IsVisible）----
            var picker = BuildPiecePicker(seg);
            picker.IsVisible = false;

            var btnApply = MakePill("记入", "#185FA5", "#FFFFFF");
            btnApply.Clicked += async (_, _) =>
            {
                if (string.IsNullOrEmpty(seg.PieceCode))
                {
                    picker.IsVisible = true;
                    return;
                }
                await ApplySegmentAsync(seg, seg.PieceCode, confirmed: true);
            };

            var btnChange = MakePill("换片", "#E6F1FB", "#185FA5");
            btnChange.Clicked += (_, _) => picker.IsVisible = !picker.IsVisible;

            var btnIgnore = MakePill("忽略", "#F1EFE8", "#78766E");
            btnIgnore.Clicked += async (_, _) =>
            {
                seg.Status = 2;
                await _pending.UpdateAsync(seg);
                await LoadAsync();
            };

            var buttons = new HorizontalStackLayout { Spacing = 8 };
            buttons.Children.Add(btnApply);
            buttons.Children.Add(btnChange);
            buttons.Children.Add(btnIgnore);

            var stack = new VerticalStackLayout { Spacing = 8 };
            stack.Children.Add(info);
            stack.Children.Add(buttons);
            stack.Children.Add(picker);

            return new Border
            {
                Stroke = Color.FromArgb("#E3E0D8"),
                StrokeThickness = 1,
                Padding = new Thickness(12, 10),
                Content = stack,
            };
        }

        /// <summary>12 个片的胶囊网格，点一个 = 按这个片记入（confirmed 路径）。</summary>
        private FlexLayout BuildPiecePicker(LocalPendingSegment seg)
        {
            var flex = new FlexLayout
            {
                Wrap = FlexWrap.Wrap,
                JustifyContent = FlexJustify.Start,
            };

            foreach (var piece in Pieces.All)
            {
                var btn = new Button
                {
                    Text = $"{piece.Emoji} {_settings.DisplayName(piece.Code)}",
                    FontSize = 12,
                    HeightRequest = 32,
                    Padding = new Thickness(10, 0),
                    CornerRadius = 16,
                    BackgroundColor = Color.FromArgb("#E6F1FB"),
                    TextColor = Color.FromArgb("#185FA5"),
                };
                // 闭包捕获 seg 和 piece.Code（Border/手势传参的坑见 memory，闭包不赌版本）
                btn.Clicked += async (_, _) =>
                    await ApplySegmentAsync(seg, piece.Code, confirmed: true);
                flex.Children.Add(btn);
            }

            return flex;
        }

        // ============================================================
        // 入库
        // ============================================================

        /// <summary>
        /// 把一段写进正式时间块。confirmed = 人点过（状态 1）；
        /// 否则走兜底（状态 3 + AutoApplied 标记，统计口径可区分）。
        /// </summary>
        private async Task ApplySegmentAsync(LocalPendingSegment seg, string pieceCode, bool confirmed)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            int minutes = Math.Max(1, seg.Minutes);

            await _blocks.InsertAsync(new LocalBlock
            {
                Id = Guid.NewGuid().ToString("N"),
                PieceCode = pieceCode,
                OccurredAt = seg.StartedAt,
                DurationMin = minutes,
                GoalId = _settings.HasGoalFor(pieceCode) ? _settings.GoalFor(pieceCode) : string.Empty,
                Note = string.Empty,
                Worth = 0,
                Lesson = string.Empty,
                PlanId = string.Empty,
                Anomaly = IsCrossDay(seg) ? 1 : 0,
                Mood = 0,
                // 重点：从这里开始，时间块终于有真实的起止时刻了
                StartedAt = seg.StartedAt,
                EndedAt = seg.EndedAt,
                CreatedAt = now,
                UpdatedAt = now,
            });

            seg.Status = confirmed ? 1 : 3;
            seg.AutoApplied = !confirmed;
            await _pending.UpdateAsync(seg);

            await LoadAsync();
        }

        /// <summary>「剩下的按候选全部入库」：空候选段跳过（留着等人挑）。</summary>
        private async void OnApplyAllClicked(object? sender, EventArgs e)
        {
            await ApplyAllPendingAsync();
        }

        private async Task ApplyAllPendingAsync()
        {
            var segs = (await _pending.GetAllAsync())
                .Where(s => s.Status == 0 && s.PieceCode.Length > 0)
                .ToList();

            foreach (var seg in segs)
            {
                await ApplySegmentAsync(seg, seg.PieceCode, confirmed: false);
            }

            await LoadAsync();
        }

        /// <summary>关页兜底：没处理的按候选自动入库（空候选的保留待确认）。</summary>
        private async Task AutoApplyRemainingAsync()
        {
            var segs = (await _pending.GetAllAsync())
                .Where(s => s.Status == 0 && s.PieceCode.Length > 0)
                .ToList();

            foreach (var seg in segs)
            {
                await ApplySegmentAsync(seg, seg.PieceCode, confirmed: false);
            }
        }

        // ============================================================
        // 小工具
        // ============================================================

        private static bool IsCrossDay(LocalPendingSegment seg)
            => Period.ToLocalTime(seg.StartedAt).Date != Period.ToLocalTime(seg.EndedAt).Date;

        private static string FormatMin(int minutes)
            => minutes >= 60 ? $"{minutes / 60} 小时 {minutes % 60} 分" : $"{minutes} 分钟";

        private static Button MakePill(string text, string bg, string fg)
            => new()
            {
                Text = text,
                FontSize = 12,
                HeightRequest = 32,
                Padding = new Thickness(12, 0),
                CornerRadius = 16,
                BackgroundColor = Color.FromArgb(bg),
                TextColor = Color.FromArgb(fg),
            };
    }
}
