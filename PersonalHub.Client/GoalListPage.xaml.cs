using PersonalHub.Client.Data;
using PersonalHub.Client.Services;

namespace PersonalHub.Client
{
    /// <summary>
    /// 目标总览：唯一的目标管理界面。展开目标后直接编辑小项、添加小项和计时。
    /// </summary>
    public partial class GoalListPage : ContentPage
    {
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

                var (from, to) = Period.LastDays(BlockMirrorService.WindowDays);
                var blocks = await _blocks.GetByRangeAsync(from, to);
                var totals = blocks
                    .Where(b => !string.IsNullOrEmpty(b.GoalId))
                    .GroupBy(b => b.GoalId, StringComparer.Ordinal)
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
            titleRow.Children.Add(new Label
            {
                Text = open ? "⌄" : "›",
                FontSize = 12,
                TextColor = Color.FromArgb("#78766E"),
                VerticalOptions = LayoutOptions.Center,
            });
            titleRow.Children.Add(title);
            if (item.Goal.IsCurrent) titleRow.Children.Add(Pill("当前", "#185FA5", "#FFFFFF"));
            goalCell.Children.Add(titleRow);
            goalCell.Children.Add(new Label
            {
                Text = string.IsNullOrWhiteSpace(item.SourceTitle)
                    ? "来源：未关联计划"
                    : "来源：" + Shorten(item.SourceTitle, 34),
                FontSize = 10,
                TextColor = Color.FromArgb("#78766E"),
                LineBreakMode = LineBreakMode.TailTruncation,
            });

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
            summary.Add(new Label
            {
                Text = current?.Title ?? "未选择",
                FontSize = 11,
                TextColor = Color.FromArgb("#5F5E5A"),
                LineBreakMode = LineBreakMode.TailTruncation,
                VerticalOptions = LayoutOptions.Center,
            }, 1);
            summary.Add(new Label
            {
                Text = $"{done}/{item.Steps.Count}",
                FontSize = 11,
                TextColor = Color.FromArgb("#5F5E5A"),
                VerticalOptions = LayoutOptions.Center,
            }, 2);
            summary.Add(Pill(item.Goal.Status == 1 ? "暂停" : "进行中",
                item.Goal.Status == 1 ? "#F1EFE8" : "#EAF3DE",
                item.Goal.Status == 1 ? "#78766E" : "#3B6D11"), 3);

            var stack = new VerticalStackLayout { Spacing = 8 };
            stack.Children.Add(summary);

            if (open)
            {
                stack.Children.Add(new BoxView
                {
                    HeightRequest = 1,
                    Color = Color.FromArgb("#E3E0D8"),
                });
                stack.Children.Add(new Label
                {
                    Text = $"累计投入 {BlockEntryPage.FormatMinutes(item.TotalMinutes)}",
                    FontSize = 10,
                    TextColor = Color.FromArgb("#78766E"),
                });
                stack.Children.Add(BuildGoalActions(item.Goal));

                var table = new VerticalStackLayout { WidthRequest = 760, Spacing = 0 };
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
                Stroke = Color.FromArgb(item.Goal.IsCurrent ? "#C9D8EA" : "#E3E0D8"),
                StrokeThickness = item.Goal.IsCurrent ? 2 : 1,
                Padding = new Thickness(10, 8),
                Content = stack,
            };

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

        private View BuildStepHeader() => new Border
        {
            Stroke = Color.FromArgb("#CFCBC0"),
            StrokeThickness = 1,
            BackgroundColor = Color.FromArgb("#F3F1EB"),
            Content = StepGrid(
                HeaderLabel("状态"),
                HeaderLabel("小项"),
                HeaderLabel("事情片"),
                HeaderLabel("预计"),
                HeaderLabel("实际"),
                HeaderLabel("操作")),
        };

        private View BuildStepRow(LocalGoal goal, LocalGoalStep step)
        {
            bool current = goal.CurrentStepId == step.Id;
            string status = step.Status switch
            {
                1 => "当前",
                2 => "完成",
                3 => "放弃",
                _ => "待开始",
            };

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
                Placeholder = "分钟",
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

            var actions = new HorizontalStackLayout { Spacing = 4 };
            var timer = SmallButton("计时", "#E7F0F9", "#185FA5");
            timer.Clicked += (_, _) => BeginTimer(goal, step);
            actions.Children.Add(timer);

            if (step.Status != 2)
            {
                var currentBtn = SmallButton(current ? "当前" : "设为当前", "#EEEDFE", "#534AB7");
                currentBtn.IsEnabled = !current;
                currentBtn.Clicked += async (_, _) => await SetCurrentStepAsync(step);
                actions.Children.Add(currentBtn);

                var done = SmallButton("完成", "#EAF3DE", "#3B6D11");
                done.Clicked += async (_, _) => await ToggleDoneAsync(step);
                actions.Children.Add(done);
            }
            else
            {
                var restore = SmallButton("恢复", "#F1EFE8", "#5F5E5A");
                restore.Clicked += async (_, _) => await ToggleDoneAsync(step);
                actions.Children.Add(restore);
            }

            var grid = StepGrid(
                new Label
                {
                    Text = status,
                    FontSize = 10,
                    TextColor = current ? Color.FromArgb("#185FA5") : Color.FromArgb("#78766E"),
                    VerticalOptions = LayoutOptions.Center,
                },
                title,
                piece,
                estimate,
                new Label { Text = "—", FontSize = 10, TextColor = Color.FromArgb("#78766E"), VerticalOptions = LayoutOptions.Center },
                actions);

            return new Border
            {
                Stroke = Color.FromArgb("#E3E0D8"),
                StrokeThickness = 1,
                BackgroundColor = current ? Color.FromArgb("#F2F7FC") : Colors.Transparent,
                Content = grid,
            };
        }

        private View BuildAddStepRow(GoalVm item)
        {
            var title = new Entry { Placeholder = "输入小项名称", FontSize = 11, HeightRequest = 32 };
            var piece = new Picker { FontSize = 10, HeightRequest = 32 };
            foreach (var p in Pieces.All) piece.Items.Add(_settings.DisplayName(p.Code));
            int work = Pieces.All.ToList().FindIndex(p => p.Code == "work");
            piece.SelectedIndex = work >= 0 ? work : 0;
            var estimate = new Entry
            {
                Placeholder = "分钟",
                Keyboard = Keyboard.Numeric,
                FontSize = 11,
                HeightRequest = 32,
            };
            var add = SmallButton("添加小项", "#185FA5", "#FFFFFF");
            add.Clicked += async (_, _) =>
            {
                string text = (title.Text ?? string.Empty).Trim();
                if (text.Length == 0)
                {
                    StatusLabel.Text = "先填写小项名称。";
                    return;
                }

                int.TryParse((estimate.Text ?? string.Empty).Trim(), out int minutes);
                minutes = Math.Clamp(minutes, 0, 1440);
                int idx = Math.Max(0, piece.SelectedIndex);
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var step = new LocalGoalStep
                {
                    Id = Guid.NewGuid().ToString("N"),
                    GoalId = item.Goal.Id,
                    Title = text,
                    Status = string.IsNullOrEmpty(item.Goal.CurrentStepId) ? 1 : 0,
                    PieceCode = Pieces.All[Math.Min(idx, Pieces.All.Count - 1)].Code,
                    EstimateMin = minutes,
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

            return new Border
            {
                Stroke = Color.FromArgb("#CFCBC0"),
                StrokeThickness = 1,
                BackgroundColor = Color.FromArgb("#FAF9F5"),
                Content = StepGrid(
                    new Label { Text = "新增", FontSize = 10, TextColor = Color.FromArgb("#185FA5") },
                    title, piece, estimate,
                    new Label { Text = "—", FontSize = 10, TextColor = Color.FromArgb("#78766E") },
                    add),
            };
        }

        private View BuildGoalActions(LocalGoal goal)
        {
            var row = new HorizontalStackLayout { Spacing = 6 };

            var current = SmallButton(goal.IsCurrent ? "当前目标" : "设为当前", "#F1EFE8", "#5F5E5A");
            current.IsEnabled = !goal.IsCurrent;
            current.Clicked += async (_, _) => await SetCurrentGoalAsync(goal);
            row.Children.Add(current);

            var pause = SmallButton(goal.Status == 1 ? "恢复" : "暂停", "#F1EFE8", "#5F5E5A");
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

            var complete = SmallButton("完成目标", "#EAF3DE", "#3B6D11");
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

        private static Label HeaderLabel(string text) => new()
        {
            Text = text,
            FontSize = 10,
            TextColor = Color.FromArgb("#78766E"),
        };

        private static Grid StepGrid(View status, View title, View piece, View estimate, View actual, View actions)
        {
            var grid = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(new GridLength(42)),
                    new ColumnDefinition(new GridLength(220)),
                    new ColumnDefinition(new GridLength(100)),
                    new ColumnDefinition(new GridLength(72)),
                    new ColumnDefinition(new GridLength(72)),
                    new ColumnDefinition(new GridLength(170)),
                },
                ColumnSpacing = 6,
                Padding = new Thickness(8, 5),
            };
            grid.Add(status, 0);
            grid.Add(title, 1);
            grid.Add(piece, 2);
            grid.Add(estimate, 3);
            grid.Add(actual, 4);
            grid.Add(actions, 5);
            return grid;
        }

        private async Task SetCurrentStepAsync(LocalGoalStep step)
        {
            var goal = _items.FirstOrDefault(x => x.Goal.Id == step.GoalId)?.Goal;
            if (goal is null || step.Status == 2) return;

            foreach (var other in _items.Where(x => x.Goal.Id == goal.Id)
                         .SelectMany(x => x.Steps).Where(s => s.Id != step.Id && s.Status == 1))
            {
                other.Status = 0;
                TouchStep(other);
                await _goalSteps.UpdateAsync(other);
            }

            step.Status = 1;
            TouchStep(step);
            await _goalSteps.UpdateAsync(step);

            goal.CurrentStepId = step.Id;
            TouchGoal(goal);
            await _goals.UpdateAsync(goal);
            _ = _goalSync.SyncAsync();
            _ = _goalStepSync.SyncAsync();
            await ReloadAsync();
        }

        private async Task ToggleDoneAsync(LocalGoalStep step)
        {
            var goal = _items.FirstOrDefault(x => x.Goal.Id == step.GoalId)?.Goal;
            if (goal is null) return;

            bool complete = step.Status != 2;
            step.Status = complete ? 2 : 0;
            TouchStep(step);
            await _goalSteps.UpdateAsync(step);

            if (complete && goal.CurrentStepId == step.Id)
            {
                goal.CurrentStepId = string.Empty;
                TouchGoal(goal);
                await _goals.UpdateAsync(goal);
                _ = _goalSync.SyncAsync();
            }
            _ = _goalStepSync.SyncAsync();
            await ReloadAsync();
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

        private static Label Pill(string text, string background, string foreground) => new()
        {
            Text = text,
            FontSize = 10,
            TextColor = Color.FromArgb(foreground),
            BackgroundColor = Color.FromArgb(background),
            Padding = new Thickness(6, 2),
            VerticalOptions = LayoutOptions.Center,
        };

        private static Button SmallButton(string text, string background, string foreground) => new()
        {
            Text = text,
            FontSize = 9,
            HeightRequest = 27,
            Padding = new Thickness(7, 0),
            CornerRadius = 6,
            BackgroundColor = Color.FromArgb(background),
            TextColor = Color.FromArgb(foreground),
        };

        private static string Shorten(string text, int max)
        {
            text = (text ?? string.Empty).Replace("\n", " ").Trim();
            return text.Length <= max ? text : text[..max] + "…";
        }
    }
}
