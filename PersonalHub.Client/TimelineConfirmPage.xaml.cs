using Microsoft.Maui.Layouts;
using PersonalHub.Client.Data;
using PersonalHub.Client.Services;

namespace PersonalHub.Client
{
    /// <summary>
    /// 时间线确认页（R13 初版，R17 改造：拖拽合并 + 已处理灰卡留显 + 归目标）。
    ///
    /// 设计立场（拍板时定的）：**确认节奏宁缺毋滥** —— 不实时打扰，
    /// 用户想看的时候进来，把今天的时间线一条条过一遍：
    ///   「记入」= 候选标签对，写正式时间块（StartedAt/EndedAt 是真值）；
    ///   「换片」= 猜错了，从 12 片里挑一个；
    ///   「忽略」= 不想要这段。
    /// 关页时没处理的段**按候选自动入库并打「自动」标记** ——
    /// 兜底路径必须存在，否则"没确认的段"会变成一种心理负担（确认疲劳的解药）。
    ///
    /// R17 三件事（需求 2026-11-27 当面拍板）：
    /// 1. **长按拖拽合并**：A 拖到 B 上 = A 并入 B，B 的标签说了算；
    ///    时长 = 各段分钟数相加（中间空隙不算 —— 合并的本意是"这几段是同一件事"，
    ///    而不是"这一整块都是"，切片不准的兜底手段）。合并是**纯前端状态**，
    ///    确认/自动入库那一刻才结清（写一块、物理删被并段），没入库前一切可拆。
    /// 2. **已处理不消失**：确认/自动入库的卡变灰留在时间线上（按时间混排），
    ///    隔天进页面不再显示 —— 纯显示层过滤，块库数据不动。
    /// 3. **归目标入口**：折叠区里手动选目标 → 小项，入库写 GoalId + GoalStepId；
    ///    不选走自动映射（PieceSettings 的片→目标表）。
    /// </summary>
    public partial class TimelineConfirmPage : ContentPage
    {
        private readonly LocalRecordStore<LocalPendingSegment> _pending;
        private readonly LocalRecordStore<LocalBlock> _blocks;
        private readonly LocalRecordStore<LocalGoal> _goals;
        private readonly LocalRecordStore<LocalGoalStep> _goalSteps;
        private readonly PieceSettings _settings;

        // ---- 合并状态（纯前端）：目标卡 Id → 被它吸掉的段 ----
        private readonly Dictionary<string, List<LocalPendingSegment>> _absorbed = new();

        // ---- 归目标的手动选择：段 Id → (GoalId, StepId) ----
        // 键不存在 = 走自动映射；("", "") = 明确不归；(goalId, stepId) = 手动指定。
        private readonly Dictionary<string, (string GoalId, string StepId)> _goalPicks = new();

        // ---- 当页缓存的目标/小项（LoadAsync 一次，卡片共享） ----
        private List<LocalGoal> _activeGoals = new();
        private Dictionary<string, List<LocalGoalStep>> _stepsByGoal = new();

        public TimelineConfirmPage(
            LocalRecordStore<LocalPendingSegment> pending,
            LocalRecordStore<LocalBlock> blocks,
            LocalRecordStore<LocalGoal> goals,
            LocalRecordStore<LocalGoalStep> goalSteps,
            PieceSettings settings)
        {
            InitializeComponent();
            _pending = pending;
            _blocks = blocks;
            _goals = goals;
            _goalSteps = goalSteps;
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
            // 显示范围：待确认（0）+ 今天处理过的灰卡（1 已确认 / 3 自动入库）。
            // 忽略（2）和隔天处理的不再显示 —— 纯显示层过滤，块库数据不动。
            long todayStart = new DateTimeOffset(DateTime.Today).ToUnixTimeMilliseconds();

            var segs = (await _pending.GetAllAsync())
                .Where(s => s.Status == 0
                            || ((s.Status == 1 || s.Status == 3) && s.ConfirmedAt >= todayStart))
                .OrderBy(s => s.StartedAt)
                .ToList();

            // 目标/小项缓存（灰卡和归目标面板共用）
            _activeGoals = (await _goals.GetAllAsync())
                .Where(g => !g.Deleted && g.Status != 2 && g.Status != 3)
                .ToList();
            _stepsByGoal = (await _goalSteps.GetAllAsync())
                .Where(s => !s.Deleted)
                .GroupBy(s => s.GoalId, StringComparer.Ordinal)
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderBy(s => s.SortOrder).ThenBy(s => s.CreatedAt).ToList(),
                    StringComparer.Ordinal);

            Rows.Children.Clear();

            int pendingCount = segs.Count(s => s.Status == 0);
            int doneCount = segs.Count - pendingCount;
            int unknown = segs.Count(s => s.Status == 0 && string.IsNullOrEmpty(s.PieceCode));

            HeadLabel.Text = pendingCount == 0
                ? (doneCount > 0 ? "今天的时间线都处理完了。" : "没有待确认的时间片。")
                : $"{pendingCount} 段待确认"
                    + (unknown > 0 ? $" · 其中 {unknown} 段没猜出标签，点「换片」自己挑" : "")
                    + (doneCount > 0 ? $" · 另有 {doneCount} 段已处理（灰卡，隔天自动收走）" : "");

            EmptyLabel.IsVisible = pendingCount == 0;
            ApplyAllBtn.IsVisible = segs.Any(s => s.Status == 0 && !string.IsNullOrEmpty(s.PieceCode));

            foreach (var seg in segs)
            {
                Rows.Children.Add(seg.Status == 0 ? BuildPendingCard(seg) : BuildDoneCard(seg));
            }
        }

        // ============================================================
        // 待确认卡
        // ============================================================

        private View BuildPendingCard(LocalPendingSegment seg)
        {
            _absorbed.TryGetValue(seg.Id, out var absorbed);
            absorbed ??= new List<LocalPendingSegment>();
            int absorbedMin = absorbed.Sum(a => Math.Max(0, a.Minutes));

            string timeText =
                $"{Period.ToLocalTime(seg.StartedAt):MM-dd HH:mm} – {Period.ToLocalTime(seg.EndedAt):HH:mm}"
                + $" · {FormatMin(Math.Max(1, seg.Minutes) + absorbedMin)}";

            string pieceText = seg.PieceCode.Length > 0
                ? _settings.DisplayName(seg.PieceCode)
                : "没猜出来";

            string appText = seg.SourceApp.Length > 0
                ? "证据：" + seg.SourceApp[(seg.SourceApp.LastIndexOf('.') + 1)..]
                : "证据：无";

            var info = new VerticalStackLayout { Spacing = 2 };
            info.Children.Add(new Label { Text = timeText, FontSize = 14 });
            var candidate = new Label
            {
                Text = $"候选：{pieceText} · {appText}",
                FontSize = 12,
            };
            candidate.SetAppThemeColor(Label.TextColorProperty,
                Color.FromArgb("#78766E"), Color.FromArgb("#A8A69E"));
            info.Children.Add(candidate);

            var stack = new VerticalStackLayout { Spacing = 8 };
            stack.Children.Add(info);

            // ---- 合并行：有被并进来的段才出现，附"拆开"（纯前端还原，没入库一切可逆）----
            if (absorbed.Count > 0)
            {
                var mergeRow = new HorizontalStackLayout { Spacing = 10 };
                var mergeLabel = new Label
                {
                    Text = $"已并入 {absorbed.Count} 段（时长按各段相加）",
                    FontSize = 12,
                };
                mergeLabel.SetAppThemeColor(Label.TextColorProperty,
                    Color.FromArgb("#78766E"), Color.FromArgb("#A8A69E"));
                mergeRow.Children.Add(mergeLabel);

                var unmerge = new Label { Text = "拆开", FontSize = 12 };
                unmerge.SetAppThemeColor(Label.TextColorProperty,
                    Color.FromArgb("#185FA5"), Color.FromArgb("#7FB4E8"));
                var unmergeTap = new TapGestureRecognizer();
                // 闭包捕获 seg（手势传参的坑见 memory，闭包不赌版本）
                unmergeTap.Tapped += async (_, _) =>
                {
                    _absorbed.Remove(seg.Id);
                    await LoadAsync();
                };
                unmerge.GestureRecognizers.Add(unmergeTap);
                mergeRow.Children.Add(unmerge);

                stack.Children.Add(mergeRow);
            }

            // ---- 折叠备注（R14.5）：点开才出现输入框，不写直接「记入」不受影响 ----
            var noteEditor = new Editor
            {
                Placeholder = "备注（可选）：这段在干嘛",
                FontSize = 12,
                HeightRequest = 60,
                IsVisible = false,
            };
            var noteToggle = new Label
            {
                Text = "＋ 备注",
                FontSize = 12,
                VerticalOptions = LayoutOptions.Center,
            };
            noteToggle.SetAppThemeColor(Label.TextColorProperty,
                Color.FromArgb("#185FA5"), Color.FromArgb("#7FB4E8"));
            var noteTap = new TapGestureRecognizer();
            noteTap.Tapped += (_, _) =>
            {
                noteEditor.IsVisible = !noteEditor.IsVisible;
                noteToggle.Text = noteEditor.IsVisible ? "－ 备注" : "＋ 备注";
            };
            noteToggle.GestureRecognizers.Add(noteTap);

            // ---- 换片网格：先建好藏起来，点「换片」再展开 ----
            var picker = BuildPiecePicker(seg, noteEditor);
            picker.IsVisible = false;

            // ---- 归目标：折叠区，自动映射为默认 ----
            var goalPanel = new VerticalStackLayout { Spacing = 6, IsVisible = false };
            var goalToggle = new Label
            {
                Text = GoalToggleText(seg),
                FontSize = 12,
                VerticalOptions = LayoutOptions.Center,
            };
            goalToggle.SetAppThemeColor(Label.TextColorProperty,
                Color.FromArgb("#185FA5"), Color.FromArgb("#7FB4E8"));
            var goalTap = new TapGestureRecognizer();
            goalTap.Tapped += (_, _) =>
            {
                if (goalPanel.IsVisible)
                {
                    goalPanel.IsVisible = false;
                }
                else
                {
                    BuildGoalPicker(seg, goalPanel);
                    goalPanel.IsVisible = true;
                }
                goalToggle.Text = GoalToggleText(seg);
            };
            goalToggle.GestureRecognizers.Add(goalTap);

            var btnApply = MakePill("记入", "#185FA5", "#FFFFFF", "#185FA5", "#FFFFFF");
            btnApply.Clicked += async (_, _) =>
            {
                if (string.IsNullOrEmpty(seg.PieceCode))
                {
                    picker.IsVisible = true;
                    return;
                }
                await ApplySegmentAsync(seg, seg.PieceCode, confirmed: true,
                    noteEditor.Text ?? string.Empty);
            };

            var btnChange = MakePill("换片", "#E6F1FB", "#185FA5", "#1E3550", "#7FB4E8");
            btnChange.Clicked += (_, _) => picker.IsVisible = !picker.IsVisible;

            var btnIgnore = MakePill("忽略", "#F1EFE8", "#78766E", "#2C2C2A", "#A8A69E");
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

            stack.Children.Add(buttons);
            stack.Children.Add(noteToggle);
            stack.Children.Add(noteEditor);
            stack.Children.Add(picker);
            stack.Children.Add(goalToggle);
            stack.Children.Add(goalPanel);

            var card = new Border
            {
                StrokeThickness = 1,
                Padding = new Thickness(12, 10),
                Content = stack,
            };
            card.SetAppThemeColor(Border.StrokeProperty,
                Color.FromArgb("#E3E0D8"), Color.FromArgb("#3A3A36"));

            AttachMergeGestures(card, seg);
            return card;
        }

        // ============================================================
        // 拖拽合并（照 QuadrantPage 的 DragGestureRecognizer/DropGestureRecognizer 模子）
        // ============================================================

        /// <summary>待确认卡既是拖拽源（Android 长按天然触发）也是放置目标（拖到谁上并入谁）。</summary>
        private void AttachMergeGestures(Border card, LocalPendingSegment seg)
        {
            var drag = new DragGestureRecognizer();
            drag.DragStarting += (_, e) =>
            {
                try { e.Data.Properties["segId"] = seg.Id; }
                catch { /* 个别平台不支持 Properties，拖不起来就拖不起来，不影响点 */ }
            };
            card.GestureRecognizers.Add(drag);

            var drop = new DropGestureRecognizer { AllowDrop = true };
            drop.DragOver += (_, e) =>
            {
                // 有东西在头顶时给个变淡反馈，不然不知道松手会落在哪
                card.Opacity = 0.6;
                e.AcceptedOperation = DataPackageOperation.Copy;
            };
            drop.DragLeave += (_, _) => card.Opacity = 1;
            drop.Drop += (_, e) =>
            {
                card.Opacity = 1;
                string? fromId = null;
                try
                {
                    if (e.Data?.Properties is { } props
                        && props.TryGetValue("segId", out var v)
                        && v is string s)
                    {
                        fromId = s;
                    }
                }
                catch { /* 拖拽通道不给数据就当没拖 */ }
                OnMergedOnto(fromId, seg.Id);
            };
            card.GestureRecognizers.Add(drop);
        }

        /// <summary>拖 from 段到 onto 段上：from 并入 onto，onto 的标签说了算。链式并（from 自己已有被并段）跟着一起搬。</summary>
        private async void OnMergedOnto(string? fromId, string ontoId)
        {
            if (string.IsNullOrEmpty(fromId) || fromId == ontoId) return;

            var all = await _pending.GetAllAsync();
            var from = all.FirstOrDefault(s => s.Id == fromId && s.Status == 0);
            if (from is null) return;

            // 已被并进别的卡的段不能再被拖（它已经不在列表上了，防御一下并发态）
            foreach (var list in _absorbed.Values)
            {
                if (list.Any(a => a.Id == fromId)) return;
            }

            if (!_absorbed.TryGetValue(ontoId, out var target))
            {
                target = new List<LocalPendingSegment>();
                _absorbed[ontoId] = target;
            }
            if (_absorbed.Remove(fromId, out var chain)) target.AddRange(chain);
            target.Add(from);

            await LoadAsync();
        }

        // ============================================================
        // 已处理灰卡
        // ============================================================

        private View BuildDoneCard(LocalPendingSegment seg)
        {
            string timeText =
                $"{Period.ToLocalTime(seg.StartedAt):MM-dd HH:mm} – {Period.ToLocalTime(seg.EndedAt):HH:mm}"
                + $" · {FormatMin(Math.Max(1, seg.Minutes))}";

            string pieceText = seg.PieceCode.Length > 0
                ? _settings.DisplayName(seg.PieceCode)
                : "未知片";
            string tag = seg.Status == 3 ? "自动入库" : "已记入";

            var info = new VerticalStackLayout { Spacing = 2 };
            var head = new Label { Text = $"✓ {tag} · {pieceText}", FontSize = 13 };
            head.SetAppThemeColor(Label.TextColorProperty,
                Color.FromArgb("#78766E"), Color.FromArgb("#A8A69E"));
            info.Children.Add(head);
            var time = new Label { Text = timeText, FontSize = 12 };
            time.SetAppThemeColor(Label.TextColorProperty,
                Color.FromArgb("#9A9890"), Color.FromArgb("#6E6C66"));
            info.Children.Add(time);

            var card = new Border
            {
                StrokeThickness = 1,
                Padding = new Thickness(12, 8),
                Opacity = 0.72,
                Content = info,
            };
            card.SetAppThemeColor(Border.StrokeProperty,
                Color.FromArgb("#E9E6DE"), Color.FromArgb("#34342F"));
            return card;
        }

        // ============================================================
        // 换片网格 / 归目标面板
        // ============================================================

        /// <summary>12 个片的胶囊网格，点一个 = 按这个片记入（confirmed 路径）。备注从 noteEditor 带走。</summary>
        private FlexLayout BuildPiecePicker(LocalPendingSegment seg, Editor noteEditor)
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
                };
                btn.SetAppThemeColor(Button.BackgroundColorProperty,
                    Color.FromArgb("#E6F1FB"), Color.FromArgb("#1E3550"));
                btn.SetAppThemeColor(Button.TextColorProperty,
                    Color.FromArgb("#185FA5"), Color.FromArgb("#7FB4E8"));
                // 闭包捕获 seg 和 piece.Code（Border/手势传参的坑见 memory，闭包不赌版本）
                btn.Clicked += async (_, _) =>
                    await ApplySegmentAsync(seg, piece.Code, confirmed: true,
                        noteEditor.Text ?? string.Empty);
                flex.Children.Add(btn);
            }

            return flex;
        }

        /// <summary>归目标折叠区：自动 / 不归 / 各目标，选中目标后再挑小项。选完重建一次以刷新选中态。</summary>
        private void BuildGoalPicker(LocalPendingSegment seg, VerticalStackLayout panel)
        {
            panel.Children.Clear();

            bool manual = _goalPicks.TryGetValue(seg.Id, out var pick);
            if (!manual) pick = ("", "");

            var flex = new FlexLayout
            {
                Wrap = FlexWrap.Wrap,
                JustifyContent = FlexJustify.Start,
            };

            flex.Children.Add(GoalChip("自动", !manual, () =>
            {
                _goalPicks.Remove(seg.Id);
                BuildGoalPicker(seg, panel);
            }));

            flex.Children.Add(GoalChip("不归目标", manual && pick.GoalId.Length == 0, () =>
            {
                _goalPicks[seg.Id] = ("", "");
                BuildGoalPicker(seg, panel);
            }));

            foreach (var goal in _activeGoals)
            {
                var goalId = goal.Id;
                flex.Children.Add(GoalChip(goal.Title, manual && pick.GoalId == goalId, () =>
                {
                    _goalPicks[seg.Id] = (goalId, string.Empty);
                    BuildGoalPicker(seg, panel);
                }));
            }

            panel.Children.Add(flex);

            // 小项行：只在手动选中某个目标时出现
            if (manual && pick.GoalId.Length > 0
                && _stepsByGoal.TryGetValue(pick.GoalId, out var steps)
                && steps.Count > 0)
            {
                var stepFlex = new FlexLayout
                {
                    Wrap = FlexWrap.Wrap,
                    JustifyContent = FlexJustify.Start,
                };
                foreach (var step in steps)
                {
                    var stepId = step.Id;
                    var goalId = pick.GoalId;
                    stepFlex.Children.Add(GoalChip(step.Title, pick.StepId == stepId, () =>
                    {
                        _goalPicks[seg.Id] = (goalId, stepId);
                        BuildGoalPicker(seg, panel);
                    }));
                }
                panel.Children.Add(stepFlex);
            }
        }

        /// <summary>归目标面板里的小胶囊：选中态填蓝，未选中态淡底。</summary>
        private Button GoalChip(string text, bool selected, Action onTap)
        {
            var btn = new Button
            {
                Text = text,
                FontSize = 12,
                HeightRequest = 30,
                Padding = new Thickness(10, 0),
                CornerRadius = 15,
            };

            if (selected)
            {
                btn.SetAppThemeColor(Button.BackgroundColorProperty,
                    Color.FromArgb("#185FA5"), Color.FromArgb("#185FA5"));
                btn.SetAppThemeColor(Button.TextColorProperty,
                    Color.FromArgb("#FFFFFF"), Color.FromArgb("#FFFFFF"));
            }
            else
            {
                btn.SetAppThemeColor(Button.BackgroundColorProperty,
                    Color.FromArgb("#E6F1FB"), Color.FromArgb("#1E3550"));
                btn.SetAppThemeColor(Button.TextColorProperty,
                    Color.FromArgb("#185FA5"), Color.FromArgb("#7FB4E8"));
            }

            btn.Clicked += (_, _) => onTap();
            return btn;
        }

        /// <summary>折叠行文案：自动映射的目标名 / 手动选择 / 不归。</summary>
        private string GoalToggleText(LocalPendingSegment seg)
        {
            if (!_goalPicks.TryGetValue(seg.Id, out var pick))
            {
                string autoId = _settings.HasGoalFor(seg.PieceCode) ? _settings.GoalFor(seg.PieceCode) : string.Empty;
                string autoName = _activeGoals.FirstOrDefault(g => g.Id == autoId)?.Title ?? string.Empty;
                return autoName.Length > 0 ? $"归目标：自动 →「{autoName}」" : "归目标";
            }
            if (pick.GoalId.Length == 0) return "归目标：不归";
            string name = _activeGoals.FirstOrDefault(g => g.Id == pick.GoalId)?.Title ?? "（已结束）";
            return $"归目标：手动 →「{name}」";
        }

        // ============================================================
        // 入库
        // ============================================================

        /// <summary>
        /// 把一段写进正式时间块。confirmed = 人点过（状态 1）；
        /// 否则走兜底（状态 3 + AutoApplied 标记，统计口径可区分）。
        /// 有被并段时：时长 = 各段相加，起止锚在目标卡（拖到谁上听谁的），
        /// 被并段物理删除（其信息已并入这一块）。
        /// </summary>
        private async Task ApplySegmentAsync(LocalPendingSegment seg, string pieceCode, bool confirmed,
            string note = "")
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            _absorbed.TryGetValue(seg.Id, out var absorbed);
            absorbed ??= new List<LocalPendingSegment>();
            int minutes = Math.Max(1, seg.Minutes) + absorbed.Sum(a => Math.Max(0, a.Minutes));

            // 归目标：手动选择优先（"" = 明确不归），否则走片→目标自动映射
            string goalId;
            string stepId;
            if (_goalPicks.TryGetValue(seg.Id, out var pick))
            {
                goalId = pick.GoalId;
                stepId = pick.StepId;
            }
            else
            {
                goalId = _settings.HasGoalFor(pieceCode) ? _settings.GoalFor(pieceCode) : string.Empty;
                stepId = string.Empty;
            }

            await _blocks.InsertAsync(new LocalBlock
            {
                Id = Guid.NewGuid().ToString("N"),
                PieceCode = pieceCode,
                OccurredAt = seg.StartedAt,
                DurationMin = minutes,
                GoalId = goalId,
                GoalStepId = stepId,
                Note = note ?? string.Empty,
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
            seg.PieceCode = pieceCode;   // 换片后灰卡显示真实片，不是旧候选
            seg.ConfirmedAt = now;
            await _pending.UpdateAsync(seg);

            // 被并段结清：物理删（pending_segments 本来就是"过期直接物理删"的口径）
            foreach (var a in absorbed)
            {
                await _pending.DeleteAsync(a.Id);
            }
            _absorbed.Remove(seg.Id);
            _goalPicks.Remove(seg.Id);

            await LoadAsync();
        }

        /// <summary>「剩下的按候选全部入库」：空候选段跳过（留着等人挑）。</summary>
        private async void OnApplyAllClicked(object? sender, EventArgs e)
        {
            await ApplyAllPendingAsync();
        }

        private async Task ApplyAllPendingAsync()
        {
            foreach (var seg in await PendingTargetsAsync())
            {
                await ApplySegmentAsync(seg, seg.PieceCode, confirmed: false);
            }

            await LoadAsync();
        }

        /// <summary>关页兜底：没处理的按候选自动入库（空候选的保留待确认）。</summary>
        private async Task AutoApplyRemainingAsync()
        {
            foreach (var seg in await PendingTargetsAsync())
            {
                await ApplySegmentAsync(seg, seg.PieceCode, confirmed: false);
            }
        }

        /// <summary>兜底入库的对象：所有待确认段，排除已被并进别的卡的（它们跟着目标卡一起结清）。</summary>
        private async Task<List<LocalPendingSegment>> PendingTargetsAsync()
        {
            var absorbedIds = new HashSet<string>(
                _absorbed.Values.SelectMany(x => x).Select(x => x.Id),
                StringComparer.Ordinal);

            return (await _pending.GetAllAsync())
                .Where(s => s.Status == 0 && s.PieceCode.Length > 0 && !absorbedIds.Contains(s.Id))
                .ToList();
        }

        // ============================================================
        // 小工具
        // ============================================================

        private static bool IsCrossDay(LocalPendingSegment seg)
            => Period.ToLocalTime(seg.StartedAt).Date != Period.ToLocalTime(seg.EndedAt).Date;

        private static string FormatMin(int minutes)
            => minutes >= 60 ? $"{minutes / 60} 小时 {minutes % 60} 分" : $"{minutes} 分钟";

        /// <summary>胶囊按钮。浅色/深色主题各给一对色（bg/fg 各两份），压住两套主题。</summary>
        private static Button MakePill(string text, string bgL, string fgL, string bgD, string fgD)
        {
            var btn = new Button
            {
                Text = text,
                FontSize = 12,
                HeightRequest = 32,
                Padding = new Thickness(12, 0),
                CornerRadius = 16,
            };
            btn.SetAppThemeColor(Button.BackgroundColorProperty,
                Color.FromArgb(bgL), Color.FromArgb(bgD));
            btn.SetAppThemeColor(Button.TextColorProperty,
                Color.FromArgb(fgL), Color.FromArgb(fgD));
            return btn;
        }
    }
}
