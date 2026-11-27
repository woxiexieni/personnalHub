using PersonalHub.Client.Data;
using PersonalHub.Client.Services;

namespace PersonalHub.Client
{
    /// <summary>
    /// 目标总览：唯一的目标管理界面。展开目标后直接编辑小项、添加小项和计时。
    /// R18：小项表格收敛为 3 列（小项名称 / 状态 / 操作），状态列按状态变身，
    /// 事情片/预计/截止/成果收进「⋯」折叠区；阻塞/放弃必填原因。
    /// </summary>
    public partial class GoalListPage : ContentPage
    {
        // ---- 双主题成对色（浅 / 深），新画的控件一律走这对 ----
        private const string StrokeL = "#E3E0D8";       private const string StrokeD = "#3A3A36";
        private const string StrokeStrongL = "#CFCBC0"; private const string StrokeStrongD = "#4A4A44";
        private const string MutedL = "#78766E";        private const string MutedD = "#9C9A92";
        private const string TextL = "#5F5E5A";         private const string TextD = "#C9C7BF";
        private const string HeadBgL = "#F3F1EB";       private const string HeadBgD = "#2A2A27";
        private const string FoldBgL = "#FAF9F5";       private const string FoldBgD = "#262624";
        private const string BlueBgL = "#E7F0F9";       private const string BlueBgD = "#16283A";
        private const string BlueFgL = "#185FA5";       private const string BlueFgD = "#7FB3E3";
        private const string GreenBgL = "#EAF3DE";      private const string GreenBgD = "#1D3313";
        private const string GreenFgL = "#3B6D11";      private const string GreenFgD = "#9FDB8B";
        private const string PurpleBgL = "#EEEDFE";     private const string PurpleBgD = "#241F45";
        private const string PurpleFgL = "#534AB7";     private const string PurpleFgD = "#B7AFFF";
        private const string RedBgL = "#F9E7E7";        private const string RedBgD = "#3A211E";
        private const string RedFgL = "#B3261E";        private const string RedFgD = "#E28C84";
        private const string GrayBgL = "#F1EFE8";       private const string GrayBgD = "#333330";
        private const string GrayFgL = "#5F5E5A";       private const string GrayFgD = "#C9C7BF";

        private sealed record GoalVm(
            LocalGoal Goal,
            string SourceTitle,
            int TotalMinutes,
            List<LocalGoalStep> Steps);

        private readonly LocalRecordStore<LocalGoal> _goals;
        private readonly LocalRecordStore<LocalBlock> _blocks;
        private readonly LocalRecordStore<LocalIdea> _ideas;
        private readonly LocalRecordStore<LocalGoalStep> _goalSteps;
        private readonly RecordSyncService<LocalGoal> _goalSync;
        private readonly RecordSyncService<LocalGoalStep> _goalStepSync;
        private readonly BlockMirrorService _mirror;
        private readonly PieceSettings _settings;

        private List<GoalVm> _items = new();
        private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
        private readonly HashSet<string> _stepFolds = new(StringComparer.Ordinal);
        private Dictionary<string, int> _stepActualMin = new(StringComparer.Ordinal);
        private bool _busy;

        private string _timerGoalId = string.Empty;
        private string _timerStepId = string.Empty;
        private string _timerStepTitle = string.Empty;
        private PomodoroMode _timerMode = PomodoroMode.Countdown;

        public GoalListPage(
            LocalRecordStore<LocalGoal> goals,
            LocalRecordStore<LocalBlock> blocks,
            LocalRecordStore<LocalIdea> ideas,
            LocalRecordStore<LocalGoalStep> goalSteps,
            RecordSyncService<LocalGoal> goalSync,
            RecordSyncService<LocalGoalStep> goalStepSync,
            BlockMirrorService mirror,
            PieceSettings settings)
        {
            InitializeComponent();
            _goals = goals;
            _blocks = blocks;
            _ideas = ideas;
            _goalSteps = goalSteps;
            _goalSync = goalSync;
            _goalStepSync = goalStepSync;
            _mirror = mirror;
            _settings = settings;

            TimerMinutesEntry.Text = "25";
            StyleTimerModeButtons();
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();
            PomodoroState.Changed += OnPomodoroChanged;
            PomodoroState.Finished += OnPomodoroFinished;
            await ReloadAsync();
            RestoreTimerUiFromState();
            _ = SyncAndReloadAsync();
        }

        protected override void OnDisappearing()
        {
            base.OnDisappearing();
            PomodoroState.Changed -= OnPomodoroChanged;
            PomodoroState.Finished -= OnPomodoroFinished;
        }

        private async Task ReloadAsync()
        {
            if (_busy) return;
            _busy = true;

            try
            {
                var goals = (await _goals.GetByRangeAsync(0, long.MaxValue))
                    .Where(g => !g.Deleted && g.Status != 2 && g.Status != 3)
                    .ToList();
                var ideas = (await _ideas.GetAllAsync())
                    .Where(i => !i.Deleted)
                    .GroupBy(i => i.Id, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
                var steps = (await _goalSteps.GetAllAsync())
                    .Where(s => !s.Deleted)
                    .GroupBy(s => s.GoalId, StringComparer.Ordinal)
                    .ToDictionary(
                        g => g.Key,
                        g => g.OrderBy(s => s.SortOrder).ThenBy(s => s.CreatedAt).ToList(),
                        StringComparer.Ordinal);

                // R18：实际/累计口径统一为「全部时间块之和」（手动块 + 番茄钟 + 自动入库都算）。
                // 之前目标累计只算镜像窗口，跨月目标会越看越少；小项实际时间按 GoalStepId 汇总。
                var blocks = await _blocks.GetAllAsync();
                var totals = blocks
                    .Where(b => !string.IsNullOrEmpty(b.GoalId))
                    .GroupBy(b => b.GoalId, StringComparer.Ordinal)
                    .ToDictionary(
                        g => g.Key,
                        g => g.Sum(x => Math.Max(0, x.DurationMin)),
                        StringComparer.Ordinal);
                _stepActualMin = blocks
                    .Where(b => !string.IsNullOrEmpty(b.GoalStepId))
                    .GroupBy(b => b.GoalStepId, StringComparer.Ordinal)
                    .ToDictionary(
                        g => g.Key,
                        g => g.Sum(x => Math.Max(0, x.DurationMin)),
                        StringComparer.Ordinal);

                _items = goals.Select(goal =>
                {
                    totals.TryGetValue(goal.Id, out int total);
                    steps.TryGetValue(goal.Id, out var mine);
                    mine ??= new List<LocalGoalStep>();

                    string source = string.Empty;
                    if (!string.IsNullOrEmpty(goal.SourceIdeaId)
                        && ideas.TryGetValue(goal.SourceIdeaId, out var idea))
                    {
                        source = idea.Text ?? string.Empty;
                    }

                    return new GoalVm(goal, source, total, mine);
                })
                .OrderByDescending(x => x.Goal.IsCurrent)
                .ThenByDescending(x => x.Goal.StartedAt)
                .ToList();

                Render();
            }
            catch (Exception ex)
            {
                StatusLabel.Text = "读取失败：" + ex.Message;
            }
            finally
            {
                _busy = false;
            }
        }

        private async Task SyncAndReloadAsync()
        {
            try
            {
                await _goalSync.SyncAsync();
                await _goalStepSync.SyncAsync();
                _ = _mirror.FetchGoalsAsync();
                await MainThread.InvokeOnMainThreadAsync(ReloadAsync);
            }
            catch
            {
                // 离线时保留本地表格
            }
        }

        private void Render()
        {
            GoalRows.Children.Clear();

            foreach (var item in _items)
            {
                GoalRows.Children.Add(BuildGoalTableRow(item));
            }

            EmptyLabel.IsVisible = _items.Count == 0;
            EmptyLabel.Text = _items.Count == 0
                ? "还没有目标。请从「计划 · 还没形状」转入一条目标。"
                : string.Empty;
        }

        private View BuildGoalTableRow(GoalVm item)
        {
            bool open = _expanded.Contains(item.Goal.Id);
            int done = item.Steps.Count(s => s.Status == 2);
            var current = item.Steps.FirstOrDefault(s => s.Id == item.Goal.CurrentStepId)
                          ?? item.Steps.FirstOrDefault(s => s.Status == 1);

            var title = new Entry
            {
                Text = item.Goal.Title,
                FontSize = 13,
                FontAttributes = FontAttributes.Bold,
                BackgroundColor = Colors.Transparent,
                HeightRequest = 34,
            };
            title.Unfocused += async (_, _) =>
            {
                string next = (title.Text ?? string.Empty).Trim();
                if (next.Length == 0 || next == item.Goal.Title) return;
                item.Goal.Title = next;
                TouchGoal(item.Goal);
                await _goals.UpdateAsync(item.Goal);
                _ = _goalSync.SyncAsync();
            };

            var goalCell = new VerticalStackLayout { Spacing = 1 };
            var titleRow = new HorizontalStackLayout { Spacing = 5 };
            var chevron = new Label
            {
                Text = open ? "⌄" : "›",
                FontSize = 12,
                VerticalOptions = LayoutOptions.Center,
            };
            ThemeLabel(chevron, MutedL, MutedD);
            titleRow.Children.Add(chevron);
            titleRow.Children.Add(title);
            if (item.Goal.IsCurrent) titleRow.Children.Add(Pill("当前", BlueBgL, "#FFFFFF", BlueBgD, BlueFgD));
            goalCell.Children.Add(titleRow);
            var sourceLabel = new Label
            {
                Text = string.IsNullOrWhiteSpace(item.SourceTitle)
                    ? "来源：未关联计划"
                    : "来源：" + Shorten(item.SourceTitle, 34),
                FontSize = 10,
                LineBreakMode = LineBreakMode.TailTruncation,
            };
            ThemeLabel(sourceLabel, MutedL, MutedD);
            goalCell.Children.Add(sourceLabel);

            var summary = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(new GridLength(3, GridUnitType.Star)),
                    new ColumnDefinition(new GridLength(2, GridUnitType.Star)),
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Auto),
                },
                ColumnSpacing = 8,
            };
            summary.Add(goalCell, 0);
            var currentLabel = new Label
            {
                Text = current?.Title ?? "未选择",
                FontSize = 11,
                LineBreakMode = LineBreakMode.TailTruncation,
                VerticalOptions = LayoutOptions.Center,
            };
            ThemeLabel(currentLabel, TextL, TextD);
            summary.Add(currentLabel, 1);
            var doneLabel = new Label
            {
                Text = $"{done}/{item.Steps.Count}",
                FontSize = 11,
                VerticalOptions = LayoutOptions.Center,
            };
            ThemeLabel(doneLabel, TextL, TextD);
            summary.Add(doneLabel, 2);
            summary.Add(Pill(item.Goal.Status == 1 ? "暂停" : "进行中",
                item.Goal.Status == 1 ? GrayBgL : GreenBgL,
                item.Goal.Status == 1 ? GrayFgL : GreenFgL,
                item.Goal.Status == 1 ? GrayBgD : GreenBgD,
                item.Goal.Status == 1 ? GrayFgD : GreenFgD), 3);

            var stack = new VerticalStackLayout { Spacing = 8 };
            stack.Children.Add(summary);

            if (open)
            {
                var line1 = new BoxView { HeightRequest = 1 };
                line1.SetAppThemeColor(BoxView.ColorProperty, Color.FromArgb(StrokeL), Color.FromArgb(StrokeD));
                stack.Children.Add(line1);
                var invested = new Label
                {
                    Text = $"累计投入 {BlockEntryPage.FormatMinutes(item.TotalMinutes)}",
                    FontSize = 10,
                };
                ThemeLabel(invested, MutedL, MutedD);
                stack.Children.Add(invested);
                stack.Children.Add(BuildGoalActions(item.Goal));

                var table = new VerticalStackLayout { WidthRequest = 660, Spacing = 0 };
                table.Children.Add(BuildStepHeader());
                foreach (var step in item.Steps)
                {
                    table.Children.Add(BuildStepRow(item.Goal, step));
                }
                table.Children.Add(BuildAddStepRow(item));
                stack.Children.Add(new ScrollView
                {
                    Orientation = ScrollOrientation.Horizontal,
                    Content = table,
                });
            }

            var border = new Border
            {
                StrokeThickness = item.Goal.IsCurrent ? 2 : 1,
                Padding = new Thickness(10, 8),
                Content = stack,
            };
            border.SetAppThemeColor(Border.StrokeProperty,
                Color.FromArgb(item.Goal.IsCurrent ? "#C9D8EA" : StrokeL),
                Color.FromArgb(item.Goal.IsCurrent ? "#3D5875" : StrokeD));

            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) =>
            {
                if (_expanded.Contains(item.Goal.Id)) _expanded.Remove(item.Goal.Id);
                else _expanded.Add(item.Goal.Id);
                Render();
            };
            border.GestureRecognizers.Add(tap);

            return border;
        }

        private View BuildStepHeader()
        {
            var header = new Border
            {
                StrokeThickness = 1,
                Content = StepGrid(
                    HeaderLabel("小项名称"),
                    HeaderLabel("状态"),
                    HeaderLabel("操作")),
            };
            header.SetAppThemeColor(Border.StrokeProperty,
                Color.FromArgb(StrokeStrongL), Color.FromArgb(StrokeStrongD));
            header.SetAppThemeColor(Border.BackgroundColorProperty,
                Color.FromArgb(HeadBgL), Color.FromArgb(HeadBgD));
            return header;
        }

        private View BuildStepRow(LocalGoal goal, LocalGoalStep step)
        {
            bool current = goal.CurrentStepId == step.Id;
            bool folded = _stepFolds.Contains(step.Id);

            var title = new Entry { Text = step.Title, FontSize = 11, HeightRequest = 32 };
            title.Unfocused += async (_, _) =>
            {
                string next = (title.Text ?? string.Empty).Trim();
                if (next.Length == 0 || next == step.Title) return;
                step.Title = next;
                TouchStep(step);
                await _goalSteps.UpdateAsync(step);
                _ = _goalStepSync.SyncAsync();
                if (_timerStepId == step.Id)
                {
                    _timerStepTitle = next;
                    TimerStepLabel.Text = next;
                }
            };

            var actions = new HorizontalStackLayout { Spacing = 4 };
            var change = SmallButton("改状态", BlueBgL, BlueFgL, BlueBgD, BlueFgD);
            change.Clicked += async (_, _) => await ChangeStepStatusAsync(goal, step);
            actions.Children.Add(change);

            var timer = SmallButton("计时", PurpleBgL, PurpleFgL, PurpleBgD, PurpleFgD);
            timer.Clicked += (_, _) => BeginTimer(goal, step);
            actions.Children.Add(timer);

            var more = SmallButton(folded ? "收起" : "⋯", GrayBgL, GrayFgL, GrayBgD, GrayFgD);
            more.Clicked += (_, _) =>
            {
                if (folded) _stepFolds.Remove(step.Id);
                else _stepFolds.Add(step.Id);
                Render();
            };
            actions.Children.Add(more);

            var stack = new VerticalStackLayout { Spacing = 4 };
            stack.Children.Add(StepGrid(title, BuildStatusCell(step), actions));
            if (folded) stack.Children.Add(BuildStepFold(goal, step));

            var border = new Border
            {
                StrokeThickness = 1,
                Content = stack,
            };
            border.SetAppThemeColor(Border.StrokeProperty, Color.FromArgb(StrokeL), Color.FromArgb(StrokeD));
            if (current)
                border.SetAppThemeColor(Border.BackgroundColorProperty,
                    Color.FromArgb("#F2F7FC"), Color.FromArgb("#16222E"));
            return border;
        }

        /// <summary>
        /// 状态列的"变身"工厂：不同状态返回不同的内容与配色。
        /// 待办=预计；进行中=实际/预计（绿）；阻塞=原因（红）；完成=✓+成果按钮（灰）；放弃=原因（灰）。
        /// deadline 设了就显示，超期且未完成标红（只提醒，不拦操作）。
        /// </summary>
        private View BuildStatusCell(LocalGoalStep step)
        {
            var stack = new VerticalStackLayout { Spacing = 2, VerticalOptions = LayoutOptions.Center };

            string main;
            string bgL, bgD, fgL, fgD;
            switch (step.Status)
            {
                case 1:
                    int actual = _stepActualMin.TryGetValue(step.Id, out int a) ? a : 0;
                    main = step.EstimateMin > 0
                        ? $"实际 {actual} / 预计 {step.EstimateMin} 分"
                        : $"实际 {actual} 分";
                    bgL = GreenBgL; bgD = GreenBgD; fgL = GreenFgL; fgD = GreenFgD;
                    break;
                case 2:
                    main = "✓ 已完成";
                    bgL = "#EFEDE6"; bgD = "#333330"; fgL = "#78766E"; fgD = "#C9C7BF";
                    break;
                case 3:
                    main = "放弃：" + Shorten(step.Reason, 18);
                    bgL = GrayBgL; bgD = GrayBgD; fgL = GrayFgL; fgD = GrayFgD;
                    break;
                case 4:
                    main = "阻塞：" + Shorten(step.Reason, 18);
                    bgL = RedBgL; bgD = RedBgD; fgL = RedFgL; fgD = RedFgD;
                    break;
                default:
                    main = step.EstimateMin > 0 ? $"预计 {step.EstimateMin} 分" : "待办";
                    bgL = "transparent"; bgD = "transparent"; fgL = TextL; fgD = TextD;
                    break;
            }

            var mainLabel = new Label
            {
                Text = main,
                FontSize = 10,
                Padding = new Thickness(5, 3),
                VerticalOptions = LayoutOptions.Center,
                LineBreakMode = LineBreakMode.TailTruncation,
            };
            if (bgL != "transparent")
            {
                mainLabel.SetAppThemeColor(Label.BackgroundColorProperty,
                    Color.FromArgb(bgL), Color.FromArgb(bgD));
            }
            mainLabel.SetAppThemeColor(Label.TextColorProperty,
                Color.FromArgb(fgL), Color.FromArgb(fgD));
            stack.Children.Add(mainLabel);

            if (step.Deadline > 0)
            {
                var d = DateTimeOffset.FromUnixTimeMilliseconds(step.Deadline).LocalDateTime;
                bool overdue = step.Status is 0 or 1 or 4 && d.Date < DateTime.Today;
                var dl = new Label
                {
                    Text = "截止 " + d.ToString("MM-dd"),
                    FontSize = 9,
                    Padding = new Thickness(5, 0),
                };
                dl.SetAppThemeColor(Label.TextColorProperty,
                    Color.FromArgb(overdue ? RedFgL : MutedL),
                    Color.FromArgb(overdue ? RedFgD : MutedD));
                stack.Children.Add(dl);
            }

            if (step.Status == 2 && !string.IsNullOrEmpty(step.ArtifactUri))
            {
                var open = SmallButton("打开成果", BlueBgL, BlueFgL, BlueBgD, BlueFgD);
                open.Clicked += async (_, _) =>
                {
                    try { await Launcher.OpenAsync(step.ArtifactUri); }
                    catch (Exception ex) { StatusLabel.Text = "打不开成果链接：" + ex.Message; }
                };
                stack.Children.Add(open);
            }

            return stack;
        }

        /// <summary>折叠区：事情片 / 预计 / 截止 / 成果链接 / 删除，细节全收这里。</summary>
        private View BuildStepFold(LocalGoal goal, LocalGoalStep step)
        {
            var piece = new Picker { FontSize = 10, HeightRequest = 32 };
            foreach (var p in Pieces.All) piece.Items.Add(_settings.DisplayName(p.Code));
            int pieceIndex = Pieces.All.ToList().FindIndex(p =>
                string.Equals(p.Code, Pieces.Canonical(step.PieceCode), StringComparison.Ordinal));
            piece.SelectedIndex = pieceIndex >= 0 ? pieceIndex : 0;
            piece.SelectedIndexChanged += async (_, _) =>
            {
                if (piece.SelectedIndex < 0) return;
                string code = Pieces.All[piece.SelectedIndex].Code;
                if (code == step.PieceCode) return;
                step.PieceCode = code;
                TouchStep(step);
                await _goalSteps.UpdateAsync(step);
                _ = _goalStepSync.SyncAsync();
            };

            var estimate = new Entry
            {
                Text = step.EstimateMin > 0 ? step.EstimateMin.ToString() : string.Empty,
                Placeholder = "预计分钟",
                Keyboard = Keyboard.Numeric,
                FontSize = 11,
                HeightRequest = 32,
            };
            estimate.Unfocused += async (_, _) =>
            {
                int.TryParse((estimate.Text ?? string.Empty).Trim(), out int minutes);
                minutes = Math.Clamp(minutes, 0, 1440);
                if (minutes == step.EstimateMin) return;
                step.EstimateMin = minutes;
                TouchStep(step);
                await _goalSteps.UpdateAsync(step);
                _ = _goalStepSync.SyncAsync();
            };

            var deadline = new Entry
            {
                Text = step.Deadline > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(step.Deadline).LocalDateTime.ToString("yyyy-MM-dd")
                    : string.Empty,
                Placeholder = "截止 2026-12-03（可空）",
                FontSize = 11,
                HeightRequest = 32,
            };
            deadline.Unfocused += async (_, _) =>
            {
                string text = (deadline.Text ?? string.Empty).Trim();
                long next = 0;
                if (text.Length > 0)
                {
                    if (!DateTime.TryParseExact(text, "yyyy-MM-dd", null,
                            System.Globalization.DateTimeStyles.None, out var d))
                    {
                        StatusLabel.Text = "截止日期格式：yyyy-MM-dd";
                        return;
                    }
                    next = ((DateTimeOffset)new DateTime(d.Year, d.Month, d.Day, 0, 0, 0, DateTimeKind.Local))
                        .ToUnixTimeMilliseconds();
                }
                if (next == step.Deadline) return;
                step.Deadline = next;
                TouchStep(step);
                await _goalSteps.UpdateAsync(step);
                _ = _goalStepSync.SyncAsync();
            };

            var artifact = new Entry
            {
                Text = step.ArtifactUri,
                Placeholder = "成果链接 obsidian:// …（可空）",
                FontSize = 11,
                HeightRequest = 32,
            };
            artifact.Unfocused += async (_, _) =>
            {
                string next = (artifact.Text ?? string.Empty).Trim();
                if (next == step.ArtifactUri) return;
                step.ArtifactUri = next;
                TouchStep(step);
                await _goalSteps.UpdateAsync(step);
                _ = _goalStepSync.SyncAsync();
            };

            var del = SmallButton("删除小项", RedBgL, RedFgL, RedBgD, RedFgD);
            del.Clicked += async (_, _) =>
            {
                bool ok = await DisplayAlert("删除小项",
                    "删除「" + Shorten(step.Title, 20) + "」？记录软删并同步，表格里消失。",
                    "删除", "取消");
                if (!ok) return;
                step.Deleted = true;
                TouchStep(step);
                await _goalSteps.UpdateAsync(step);
                _ = _goalStepSync.SyncAsync();
                if (goal.CurrentStepId == step.Id)
                {
                    goal.CurrentStepId = string.Empty;
                    TouchGoal(goal);
                    await _goals.UpdateAsync(goal);
                    _ = _goalSync.SyncAsync();
                }
                await ReloadAsync();
            };

            var fold = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(new GridLength(120)),
                    new ColumnDefinition(new GridLength(110)),
                    new ColumnDefinition(new GridLength(150)),
                    new ColumnDefinition(new GridLength(170)),
                    new ColumnDefinition(GridLength.Auto),
                },
                ColumnSpacing = 6,
                Padding = new Thickness(8, 6),
            };
            fold.Add(piece, 0);
            fold.Add(estimate, 1);
            fold.Add(deadline, 2);
            fold.Add(artifact, 3);
            fold.Add(del, 4);

            var border = new Border
            {
                StrokeThickness = 1,
                Content = fold,
            };
            border.SetAppThemeColor(Border.StrokeProperty, Color.FromArgb(StrokeStrongL), Color.FromArgb(StrokeStrongD));
            border.SetAppThemeColor(Border.BackgroundColorProperty, Color.FromArgb(FoldBgL), Color.FromArgb(FoldBgD));
            return border;
        }

        private View BuildAddStepRow(GoalVm item)
        {
            var title = new Entry { Placeholder = "输入小项名称", FontSize = 11, HeightRequest = 32 };
            var hint = new Label
            {
                Text = "细节（事情片/预计/截止）加完在 ⋯ 里改",
                FontSize = 10,
                VerticalOptions = LayoutOptions.Center,
            };
            ThemeLabel(hint, MutedL, MutedD);

            var add = SmallButton("添加小项", BlueBgL, "#FFFFFF", BlueBgD, BlueFgD);
            add.Clicked += async (_, _) =>
            {
                string text = (title.Text ?? string.Empty).Trim();
                if (text.Length == 0)
                {
                    StatusLabel.Text = "先填写小项名称。";
                    return;
                }

                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var step = new LocalGoalStep
                {
                    Id = Guid.NewGuid().ToString("N"),
                    GoalId = item.Goal.Id,
                    Title = text,
                    Status = string.IsNullOrEmpty(item.Goal.CurrentStepId) ? 1 : 0,
                    PieceCode = "work",
                    EstimateMin = 0,
                    SortOrder = item.Steps.Count == 0 ? 10 : item.Steps.Max(s => s.SortOrder) + 10,
                    CreatedAt = now,
                    UpdatedAt = now,
                    Deleted = false,
                    Synced = false,
                };
                await _goalSteps.InsertAsync(step);

                if (string.IsNullOrEmpty(item.Goal.CurrentStepId))
                {
                    item.Goal.CurrentStepId = step.Id;
                    TouchGoal(item.Goal);
                    await _goals.UpdateAsync(item.Goal);
                    _ = _goalSync.SyncAsync();
                }

                _ = _goalStepSync.SyncAsync();
                await ReloadAsync();
            };

            var addBorder = new Border
            {
                StrokeThickness = 1,
                Content = StepGrid(title, hint, add),
            };
            addBorder.SetAppThemeColor(Border.StrokeProperty,
                Color.FromArgb(StrokeStrongL), Color.FromArgb(StrokeStrongD));
            addBorder.SetAppThemeColor(Border.BackgroundColorProperty,
                Color.FromArgb(FoldBgL), Color.FromArgb(FoldBgD));
            return addBorder;
        }

        /// <summary>改状态入口：弹选择列表；阻塞/放弃必填原因；完成时可选录成果链接。</summary>
        private async Task ChangeStepStatusAsync(LocalGoal goal, LocalGoalStep step)
        {
            string? pick = await DisplayActionSheet("改状态 · " + Shorten(step.Title, 14), "取消", null,
                "待办", "进行中", "阻塞", "完成", "放弃");
            if (string.IsNullOrEmpty(pick) || pick == "取消") return;

            switch (pick)
            {
                case "待办":
                    step.Status = 0;
                    step.Reason = string.Empty;
                    step.CompletedAt = 0;
                    break;

                case "进行中":
                    step.Status = 1;
                    step.CompletedAt = 0;
                    foreach (var other in _items.SelectMany(x => x.Steps)
                                 .Where(s => s.GoalId == goal.Id && s.Id != step.Id && s.Status == 1))
                    {
                        other.Status = 0;
                        TouchStep(other);
                        await _goalSteps.UpdateAsync(other);
                    }
                    goal.CurrentStepId = step.Id;
                    TouchGoal(goal);
                    await _goals.UpdateAsync(goal);
                    _ = _goalSync.SyncAsync();
                    break;

                case "阻塞":
                    string? reason = await DisplayPromptAsync("阻塞原因", "卡在哪？（必填）",
                        "确定", "取消", maxLength: 60,
                        initialValue: step.Status == 4 ? step.Reason : string.Empty);
                    if (string.IsNullOrWhiteSpace(reason))
                    {
                        StatusLabel.Text = "转阻塞必须填原因。";
                        return;
                    }
                    step.Status = 4;
                    step.Reason = reason.Trim();
                    break;

                case "完成":
                    string? link = await DisplayPromptAsync("成果链接",
                        "成果的 URI（可留空，以后在 ⋯ 里补）",
                        "确定", "跳过", maxLength: 500, initialValue: step.ArtifactUri);
                    step.Status = 2;
                    step.CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    if (!string.IsNullOrWhiteSpace(link)) step.ArtifactUri = link.Trim();
                    if (goal.CurrentStepId == step.Id)
                    {
                        goal.CurrentStepId = string.Empty;
                        TouchGoal(goal);
                        await _goals.UpdateAsync(goal);
                        _ = _goalSync.SyncAsync();
                    }
                    break;

                case "放弃":
                    string? giveUp = await DisplayPromptAsync("放弃原因", "为什么放弃？（必填）",
                        "确定", "取消", maxLength: 60);
                    if (string.IsNullOrWhiteSpace(giveUp))
                    {
                        StatusLabel.Text = "放弃必须填原因。";
                        return;
                    }
                    step.Status = 3;
                    step.Reason = giveUp.Trim();
                    if (goal.CurrentStepId == step.Id)
                    {
                        goal.CurrentStepId = string.Empty;
                        TouchGoal(goal);
                        await _goals.UpdateAsync(goal);
                        _ = _goalSync.SyncAsync();
                    }
                    break;
            }

            TouchStep(step);
            await _goalSteps.UpdateAsync(step);
            _ = _goalStepSync.SyncAsync();
            await ReloadAsync();
        }

        private View BuildGoalActions(LocalGoal goal)
        {
            var row = new HorizontalStackLayout { Spacing = 6 };

            var current = SmallButton(goal.IsCurrent ? "当前目标" : "设为当前", GrayBgL, GrayFgL, GrayBgD, GrayFgD);
            current.IsEnabled = !goal.IsCurrent;
            current.Clicked += async (_, _) => await SetCurrentGoalAsync(goal);
            row.Children.Add(current);

            var pause = SmallButton(goal.Status == 1 ? "恢复" : "暂停", GrayBgL, GrayFgL, GrayBgD, GrayFgD);
            pause.Clicked += async (_, _) =>
            {
                goal.Status = goal.Status == 1 ? 0 : 1;
                if (goal.Status != 0) goal.IsCurrent = false;
                TouchGoal(goal);
                await _goals.UpdateAsync(goal);
                _ = _goalSync.SyncAsync();
                await ReloadAsync();
            };
            row.Children.Add(pause);

            var complete = SmallButton("完成目标", GreenBgL, GreenFgL, GreenBgD, GreenFgD);
            complete.Clicked += async (_, _) =>
            {
                goal.Status = 2;
                goal.IsCurrent = false;
                TouchGoal(goal);
                await _goals.UpdateAsync(goal);
                _ = _goalSync.SyncAsync();
                await ReloadAsync();
            };
            row.Children.Add(complete);

            return row;
        }

        private static Label HeaderLabel(string text)
        {
            var l = new Label
            {
                Text = text,
                FontSize = 10,
            };
            l.SetAppThemeColor(Label.TextColorProperty,
                Color.FromArgb(MutedL), Color.FromArgb(MutedD));
            return l;
        }

        private static Grid StepGrid(View title, View status, View actions)
        {
            var grid = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(new GridLength(220)),
                    new ColumnDefinition(new GridLength(250)),
                    new ColumnDefinition(new GridLength(160)),
                },
                ColumnSpacing = 6,
                Padding = new Thickness(8, 5),
            };
            grid.Add(title, 0);
            grid.Add(status, 1);
            grid.Add(actions, 2);
            return grid;
        }

        private async Task SetCurrentGoalAsync(LocalGoal goal)
        {
            var all = await _goals.GetByRangeAsync(0, long.MaxValue);
            foreach (var item in all)
            {
                bool on = item.Id == goal.Id;
                if (item.IsCurrent == on) continue;
                item.IsCurrent = on;
                TouchGoal(item);
                await _goals.UpdateAsync(item);
            }
            _ = _goalSync.SyncAsync();
            await ReloadAsync();
        }

        private void BeginTimer(LocalGoal goal, LocalGoalStep step)
        {
            if (PomodoroState.Running) return;

            _timerGoalId = goal.Id;
            _timerStepId = step.Id;
            _timerStepTitle = step.Title;
            _timerMode = PomodoroMode.Countdown;
            TimerMinutesEntry.Text = step.EstimateMin > 0 ? step.EstimateMin.ToString() : "25";
            TimerPanel.IsVisible = true;
            TimerStepLabel.Text = step.Title;
            TimerStateLabel.Text = "可以开始";
            StyleTimerModeButtons();
            UpdateTimerUi();
        }

        private void OnCountdownModeClicked(object? sender, EventArgs e)
        {
            if (PomodoroState.Running) return;
            _timerMode = PomodoroMode.Countdown;
            StyleTimerModeButtons();
        }

        private void OnStopwatchModeClicked(object? sender, EventArgs e)
        {
            if (PomodoroState.Running) return;
            _timerMode = PomodoroMode.Stopwatch;
            StyleTimerModeButtons();
        }

        private void StyleTimerModeButtons()
        {
            bool countdown = _timerMode == PomodoroMode.Countdown;
            CountdownModeBtn.BackgroundColor = Color.FromArgb(countdown ? "#185FA5" : "#F1EFE8");
            CountdownModeBtn.TextColor = countdown ? Colors.White : Color.FromArgb("#2C2C2A");
            StopwatchModeBtn.BackgroundColor = Color.FromArgb(countdown ? "#F1EFE8" : "#185FA5");
            StopwatchModeBtn.TextColor = countdown ? Color.FromArgb("#2C2C2A") : Colors.White;
            TimerMinutesEntry.IsEnabled = countdown;
            TimerMinutesEntry.Opacity = countdown ? 1 : 0.45;
        }

        private void OnTimerStartClicked(object? sender, EventArgs e)
        {
            if (PomodoroState.Running || string.IsNullOrEmpty(_timerStepId)) return;

            int minutes = 0;
            if (_timerMode == PomodoroMode.Countdown)
            {
                if (!int.TryParse((TimerMinutesEntry.Text ?? string.Empty).Trim(), out minutes)
                    || minutes <= 0)
                {
                    TimerStateLabel.Text = "请输入分钟数";
                    return;
                }
            }

            string piece = _items.SelectMany(x => x.Steps)
                .FirstOrDefault(s => s.Id == _timerStepId)?.PieceCode ?? "work";
            if (string.IsNullOrEmpty(piece)) piece = "work";

            PomodoroState.Start(
                piece,
                string.Empty,
                minutes,
                _timerMode,
                _timerGoalId,
                _timerStepId,
                _timerStepTitle);

#if ANDROID
            Platforms.Android.PomodoroServiceHelper.StartService();
#endif
            UpdateTimerUi();
        }

        private void OnTimerPauseClicked(object? sender, EventArgs e)
        {
            if (!PomodoroState.Running) return;
            if (PomodoroState.PausedPhase) PomodoroState.ResumePhase();
            else PomodoroState.PausePhase();
            UpdateTimerUi();
        }

        private void OnTimerStopClicked(object? sender, EventArgs e)
        {
            if (PomodoroState.Running) PomodoroState.StopEarly();
        }

        private void OnPomodoroChanged()
        {
            Dispatcher.Dispatch(UpdateTimerUi);
        }

        private void OnPomodoroFinished(int minutes)
        {
            Dispatcher.Dispatch(async () =>
            {
                TimerStateLabel.Text = $"已记入 {minutes} 分钟";
                UpdateTimerUi();
                await ReloadAsync();
            });
        }

        private void RestoreTimerUiFromState()
        {
            if (!PomodoroState.Running) return;
            TimerPanel.IsVisible = true;
            _timerGoalId = PomodoroState.GoalId;
            _timerStepId = PomodoroState.GoalStepId;
            _timerStepTitle = PomodoroState.StepTitle;
            TimerStepLabel.Text = string.IsNullOrEmpty(PomodoroState.StepTitle)
                ? "正在计时"
                : PomodoroState.StepTitle;
            UpdateTimerUi();
        }

        private void UpdateTimerUi()
        {
            bool running = PomodoroState.Running;
            TimerIdlePanel.IsVisible = !running;
            TimerRunningPanel.IsVisible = running;

            if (!running)
            {
                if (string.IsNullOrWhiteSpace(TimerStateLabel.Text))
                    TimerStateLabel.Text = "可以开始";
                return;
            }

            TimerPanel.IsVisible = true;
            _timerMode = PomodoroState.Mode;
            _timerStepTitle = PomodoroState.StepTitle;
            TimerStepLabel.Text = string.IsNullOrEmpty(PomodoroState.StepTitle)
                ? PomodoroState.PieceDisplay
                : PomodoroState.StepTitle;
            TimerStateLabel.Text = PomodoroState.PausedPhase
                ? "已暂停"
                : PomodoroState.Mode == PomodoroMode.Stopwatch ? "正计时" : "倒计时";

            long ms = PomodoroState.Mode == PomodoroMode.Stopwatch
                ? PomodoroState.ElapsedMs
                : PomodoroState.RemainingMs;
            var t = TimeSpan.FromMilliseconds(ms);
            TimerClockLabel.Text = t.TotalHours >= 1
                ? $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2}"
                : $"{t.Minutes:D2}:{t.Seconds:D2}";
            TimerPauseBtn.Text = PomodoroState.PausedPhase ? "继续" : "暂停";
        }

        private static void TouchGoal(LocalGoal goal)
        {
            goal.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            goal.Synced = false;
        }

        private static void TouchStep(LocalGoalStep step)
        {
            step.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            step.Synced = false;
        }

        private static void ThemeLabel(Label label, string colorL, string colorD)
        {
            label.SetAppThemeColor(Label.TextColorProperty,
                Color.FromArgb(colorL), Color.FromArgb(colorD));
        }

        private static Label Pill(string text, string bgL, string fgL, string bgD, string fgD)
        {
            var p = new Label
            {
                Text = text,
                FontSize = 10,
                Padding = new Thickness(6, 2),
                VerticalOptions = LayoutOptions.Center,
            };
            p.SetAppThemeColor(Label.BackgroundColorProperty,
                Color.FromArgb(bgL), Color.FromArgb(bgD));
            p.SetAppThemeColor(Label.TextColorProperty,
                Color.FromArgb(fgL), Color.FromArgb(fgD));
            return p;
        }

        private static Button SmallButton(string text, string bgL, string fgL, string bgD, string fgD)
        {
            var b = new Button
            {
                Text = text,
                FontSize = 9,
                HeightRequest = 27,
                Padding = new Thickness(7, 0),
                CornerRadius = 6,
            };
            b.SetAppThemeColor(Button.BackgroundColorProperty,
                Color.FromArgb(bgL), Color.FromArgb(bgD));
            b.SetAppThemeColor(Button.TextColorProperty,
                Color.FromArgb(fgL), Color.FromArgb(fgD));
            return b;
        }

        private static string Shorten(string text, int max)
        {
            text = (text ?? string.Empty).Replace("\n", " ").Trim();
            return text.Length <= max ? text : text[..max] + "…";
        }
    }
}
