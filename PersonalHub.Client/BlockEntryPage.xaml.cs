using PersonalHub.Client.Controls;
using PersonalHub.Client.Data;
using PersonalHub.Client.Services;

namespace PersonalHub.Client
{
    /// <summary>
    /// 时间块录入页（R2 的主入口）。
    ///
    /// 三条硬约束（全部来自需求 §4）：
    ///   1. **一次点击就能记完一条**：点片即存，用当前时长档，不弹确认、不跳页。
    ///   2. **打字永远是可选的**：唯一一个备注框平时空着；空着时点片仍是 1 次点击。
    ///   3. **允许半条记录**：只有片也能存，时长 0 也合法（服务端同样放行）。
    ///
    /// 数据流：
    ///   保存 → 只写本地 SQLite（Synced=false）→ 立刻刷新列表与合计 → 后台上传 + 拉镜像。
    /// 记完必须马上看到数字变了，否则人会以为没记上、再记一次。
    /// </summary>
    public partial class BlockEntryPage : ContentPage
    {
        private static readonly Color ChipBg = Color.FromArgb("#F1EFE8");
        private static readonly Color ChipText = Color.FromArgb("#2C2C2A");
        private static readonly Color ChipActiveBg = Color.FromArgb("#185FA5");
        private static readonly Color PendingColor = Color.FromArgb("#D97706");
        private static readonly Color SyncedColor = Color.FromArgb("#8A8A8E");

        private readonly LocalRecordStore<LocalBlock> _blocks;
        private readonly LocalRecordStore<LocalGoal> _goals;
        private readonly LocalRecordStore<LocalPlan> _plans;
        private readonly LocalRecordStore<LocalPattern> _patterns;
        private readonly LocalRecordStore<LocalPractice> _practices;
        private readonly RecordSyncService<LocalBlock> _blockSync;
        private readonly RecordSyncService<LocalGoal> _goalSync;
        private readonly RecordSyncService<LocalPlan> _planSync;
        private readonly RecordSyncService<LocalPattern> _patternSync;
        private readonly RecordSyncService<LocalPractice> _practiceSync;
        private readonly BlockMirrorService _mirror;
        private readonly PieceSettings _settings;

        private List<LocalGoal> _activeGoals = new();
        private List<LocalPlan> _todayPlans = new();

        /// <summary>前排片（钉子 + 频次补位），加计划时作为候选清单。</summary>
        private List<Piece> _frontPieces = new();

        /// <summary>当前时长档（分钟）。默认沿用上次保存的那一档 —— 多数时候连这一下都不用点。</summary>
        private int _duration = 30;

        /// <summary>统计区间：today / week。</summary>
        private string _period = "today";

        private bool _allPiecesOpen;
        private bool _busy;

        /// <summary>长按抑制窗口：长按后 800ms 内的 Clicked 一律忽略（见 OnPiecePressed）。</summary>
        private DateTime _suppressClickUntil = DateTime.MinValue;

        public BlockEntryPage(
            LocalRecordStore<LocalBlock> blocks,
            LocalRecordStore<LocalGoal> goals,
            LocalRecordStore<LocalPlan> plans,
            LocalRecordStore<LocalPattern> patterns,
            LocalRecordStore<LocalPractice> practices,
            RecordSyncService<LocalBlock> blockSync,
            RecordSyncService<LocalGoal> goalSync,
            RecordSyncService<LocalPlan> planSync,
            RecordSyncService<LocalPattern> patternSync,
            RecordSyncService<LocalPractice> practiceSync,
            BlockMirrorService mirror,
            PieceSettings settings)
        {
            InitializeComponent();

            _blocks = blocks;
            _goals = goals;
            _plans = plans;
            _patterns = patterns;
            _practices = practices;
            _blockSync = blockSync;
            _goalSync = goalSync;
            _planSync = planSync;
            _patternSync = patternSync;
            _practiceSync = practiceSync;
            _mirror = mirror;
            _settings = settings;

            _duration = Preferences.Default.Get("ph.blocks.lastDuration", 30);
            if (_duration <= 0) _duration = 30;

            BuildDurationChips();
            BuildAllPieces();
            ApplyPeriodStyle();
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();

            // 五个都在读同一个本地库、互不依赖 → 并行，别串着等。
            // 串行时每段各有一次 open/查询开销，叠起来就是"进页面要顿一下"。
            await Task.WhenAll(
                LoadGoalsAsync(),
                BuildFrontPiecesAsync(),
                RefreshTodayAsync(),
                RefreshPracticeAsync());

            await RefreshStatsAsync();

            _ = SyncAndRefreshAsync();
        }

        // ============================================================
        // 目标
        // ============================================================

        private async Task LoadGoalsAsync()
        {
            // 先读本地（离线也能显示），联网时再拉服务端覆盖
            var all = await _goals.GetByRangeAsync(0, long.MaxValue);
            _activeGoals = all.Where(g => g.Status == 0).ToList();

            ApplyGoalChip();
        }

        private void ApplyGoalChip()
        {
            var current = _activeGoals.FirstOrDefault(g => g.IsCurrent);

            GoalChipBtn.Text = current is null ? "未设置" : Short(current.Title);
            GoalChipBtn.TextColor = current is null ? ChipText : Colors.White;
            GoalChipBtn.BackgroundColor = current is null ? ChipBg : ChipActiveBg;

            // 一个目标都没有时才提示；有目标但没钉"当前"不打扰（块照样能记，只是不归目标）
            NoGoalCard.IsVisible = _activeGoals.Count == 0;
        }

        private static string Short(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "未设置";
            return s.Length <= 6 ? s : s.Substring(0, 6) + "…";
        }

        private async void OnGoalChipClicked(object sender, EventArgs e)
        {
            await Shell.Current.GoToAsync(RecordCatalog.GoalRoute);
        }

        private async void OnCreateGoalClicked(object sender, EventArgs e)
        {
            await Shell.Current.GoToAsync(RecordCatalog.GoalRoute);
        }

        private void OnSkipGoalClicked(object sender, EventArgs e)
        {
            // 不落任何偏好：下次进页面还会提示。这不是"稍后提醒"，是承认你可能就是不想要目标。
            NoGoalCard.IsVisible = false;
        }

        // ============================================================
        // 片
        // ============================================================

        private async Task BuildFrontPiecesAsync()
        {
            // 前排空位按"最近 7 天点得最多的片"补 —— 钉子优先，频次只填空
            var (from, to) = Period.LastDays(7);
            var freq = await _blocks.ClickFrequencyAsync(from, to);

            FrontPieces.Children.Clear();
            _frontPieces = _settings.FrontRow(freq).ToList();
            foreach (var p in _frontPieces)
            {
                FrontPieces.Children.Add(MakePieceButton(p, big: true));
            }
        }

        private void BuildAllPieces()
        {
            AllPiecesPanel.Children.Clear();

            foreach (var group in Pieces.GroupOrder)
            {
                var pieces = Pieces.All.Where(p => p.Group == group && !_settings.IsHidden(p.Code)).ToList();
                if (pieces.Count == 0) continue;

                AllPiecesPanel.Children.Add(new Label
                {
                    Text = group,
                    FontSize = 11,
                    TextColor = Color.FromArgb("#8A8A8E"),
                    Margin = new Thickness(0, 8, 0, 2),
                });

                var flex = new FlexLayout
                {
                    Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap,
                    Direction = Microsoft.Maui.Layouts.FlexDirection.Row,
                };

                foreach (var p in pieces)
                {
                    flex.Children.Add(MakePieceButton(p, big: false));
                }

                AllPiecesPanel.Children.Add(flex);
            }
        }

        private Button MakePieceButton(Piece p, bool big)
        {
            var btn = new Button
            {
                Text = _settings.DisplayName(p.Code),
                FontSize = big ? 14 : 13,
                HeightRequest = big ? 52 : 36,
                Padding = new Thickness(big ? 16 : 12, 0),
                CornerRadius = big ? 12 : 18,
                Margin = new Thickness(0, 0, 8, 8),
                BackgroundColor = ChipBg,
                TextColor = ChipText,
                CommandParameter = p.Code,
            };

            btn.Clicked += OnPieceClicked;
            btn.Pressed += OnPiecePressed;
            btn.Released += OnPieceReleased;

            return btn;
        }

        private void OnPiecePressed(object? sender, EventArgs e)
        {
            // 长按 = 打开片的菜单。MAUI 没有跨平台长按事件，用按下时长近似：
            // Released 里判断按住超过 600ms 就打开菜单，并把接下来 800ms 内的 Clicked 吞掉。
            // 用"时间窗"而不是布尔标记，是为了不依赖 Pressed/Released/Clicked 的触发顺序。
            _pressedAt = DateTime.UtcNow;
        }

        private DateTime _pressedAt = DateTime.MinValue;

        private async void OnPieceReleased(object? sender, EventArgs e)
        {
            if (sender is not Button b || b.CommandParameter is not string code) return;

            if ((DateTime.UtcNow - _pressedAt).TotalMilliseconds < 600) return;

            _suppressClickUntil = DateTime.UtcNow.AddMilliseconds(800);
            await ShowPieceMenuAsync(code);
        }

        private async void OnPieceClicked(object? sender, EventArgs e)
        {
            if (DateTime.UtcNow < _suppressClickUntil) return;
            if (sender is not Button b || b.CommandParameter is not string code) return;

            await SaveBlockAsync(code);
        }

        private async Task ShowPieceMenuAsync(string code)
        {
            string name = _settings.DisplayName(code);
            bool pinned = _settings.Pinned.Contains(code);

            string pick = await DisplayActionSheet(name, "取消", null,
                pinned ? "取消钉住" : "钉住到前排", "改名", "隐藏这个片");

            switch (pick)
            {
                case "取消钉住":
                case "钉住到前排":
                    if (!_settings.TogglePin(code))
                    {
                        StatusLabel.Text = "前排最多钉 6 个，先取消一个";
                    }
                    BuildAllPieces();
                    await BuildFrontPiecesAsync();
                    break;

                case "改名":
                    string? input = await TextEditorPage.ShowAsync("改名",
                        initial: name,
                        placeholder: "只改显示名，历史统计不受影响",
                        compact: true,
                        maxLength: 12);
                    if (!string.IsNullOrWhiteSpace(input))
                    {
                        _settings.Rename(code, input);
                        BuildAllPieces();
                        await BuildFrontPiecesAsync();
                    }
                    break;

                case "隐藏这个片":
                    _settings.SetHidden(code, true);
                    BuildAllPieces();
                    await BuildFrontPiecesAsync();
                    StatusLabel.Text = $"已隐藏「{name}」，历史记录仍在统计里";
                    break;
            }
        }

        private void OnToggleAllPiecesClicked(object sender, EventArgs e)
        {
            _allPiecesOpen = !_allPiecesOpen;
            AllPiecesPanel.IsVisible = _allPiecesOpen;
            MorePiecesBtn.Text = _allPiecesOpen ? "收起 ▴" : "全部事情片 ▾";
        }

        // ============================================================
        // 时长档
        // ============================================================

        /// <summary>自定义时长（不在固定档里，比如 37 分钟）。0 = 没设过。</summary>
        private int _customDuration;

        private void BuildDurationChips()
        {
            DurationChips.Children.Clear();

            foreach (int min in Pieces.Durations)
            {
                var btn = new Button
                {
                    Text = min >= 60 ? $"{min / 60}h" + (min % 60 == 0 ? "" : $"{min % 60}m") : $"{min}m",
                    FontSize = 13,
                    HeightRequest = 34,
                    Padding = new Thickness(13, 0),
                    CornerRadius = 17,
                    Margin = new Thickness(0, 0, 8, 8),
                    CommandParameter = min,
                    BackgroundColor = min == _duration ? ChipActiveBg : ChipBg,
                    TextColor = min == _duration ? Colors.White : ChipText,
                };
                btn.Clicked += OnDurationClicked;
                DurationChips.Children.Add(btn);
            }

            // 「自定义」：理发 25 分钟、路上 50 分钟这类不在档位上的真实时长，
            // 以前只能选 15 或 30（少算/多算），久了就不信这个数了。
            bool customActive = _customDuration > 0 && _duration == _customDuration;
            var customBtn = new Button
            {
                Text = _customDuration > 0 ? ShortMinutes(_customDuration) + " · 改" : "自定义",
                FontSize = 13,
                HeightRequest = 34,
                Padding = new Thickness(13, 0),
                CornerRadius = 17,
                Margin = new Thickness(0, 0, 8, 8),
                CommandParameter = -1,
                BackgroundColor = customActive ? ChipActiveBg : ChipBg,
                TextColor = customActive ? Colors.White : ChipText,
                BorderWidth = 1,
                BorderColor = Color.FromArgb("#D9D3E8"),
            };
            customBtn.Clicked += OnDurationClicked;
            DurationChips.Children.Add(customBtn);
        }

        private static string ShortMinutes(int min) =>
            min >= 60 ? $"{min / 60}h" + (min % 60 == 0 ? "" : $"{min % 60}m") : $"{min}m";

        private async void OnDurationClicked(object? sender, EventArgs e)
        {
            if (sender is not Button b || b.CommandParameter is not int min) return;

            if (min == -1)
            {
                string? input = await DisplayPromptAsync("自定义时长", "分钟数（1 ~ 1440）",
                    initialValue: _duration.ToString(), maxLength: 4, keyboard: Keyboard.Numeric);
                if (input is null) return;
                if (!int.TryParse(input, out int custom) || custom < 1 || custom > 1440)
                {
                    StatusLabel.Text = "时长要在 1 ~ 1440 分钟之间";
                    return;
                }

                _customDuration = custom;
                _duration = custom;
                Preferences.Default.Set("ph.blocks.lastDuration", custom);
                BuildDurationChips();
                StatusLabel.Text = $"下次点片按 {FormatMinutes(custom)} 记";
                return;
            }

            _duration = min;
            Preferences.Default.Set("ph.blocks.lastDuration", min);

            foreach (var child in DurationChips.Children)
            {
                if (child is not Button d || d.CommandParameter is not int v) continue;
                bool active = v == _duration;
                d.BackgroundColor = active ? ChipActiveBg : ChipBg;
                d.TextColor = active ? Colors.White : ChipText;
            }

            StatusLabel.Text = $"下次点片按 {FormatMinutes(min)} 记";
        }

        // ============================================================
        // 保存
        // ============================================================

        private async Task SaveBlockAsync(string code)
        {
            if (_busy) return;
            _busy = true;

            try
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var goal = _activeGoals.FirstOrDefault(g => g.IsCurrent);
                string note = (NoteEntry.Text ?? string.Empty).Trim();

                var block = new LocalBlock
                {
                    Id = Guid.NewGuid().ToString("N"),
                    PieceCode = code,
                    OccurredAt = now,
                    DurationMin = _duration,
                    GoalId = goal?.Id ?? string.Empty,
                    Note = note,
                    Worth = 0,
                    Anomaly = 0,
                    Mood = 0,
                    CreatedAt = now,
                    UpdatedAt = now,
                    Deleted = false,
                    Synced = false,
                    SyncedAt = 0,
                };

                await _blocks.InsertAsync(block);
                _settings.SetDuration(code, _duration);
                NoteEntry.Text = string.Empty;   // 备注随块存走，框回到空

                // 无弹窗、无 Toast：列表里立刻多一行，这就是唯一的反馈
                StatusLabel.Text = string.IsNullOrEmpty(note)
                    ? $"已记下 {_settings.DisplayName(code)} {FormatMinutes(_duration)}"
                    : $"已记下 {_settings.DisplayName(code)} {FormatMinutes(_duration)} · {note}";

                await RefreshTodayAsync();
                await RefreshStatsAsync();

                _ = SyncAndRefreshAsync();
            }
            catch (Exception ex)
            {
                StatusLabel.Text = "保存失败：" + ex.Message;
            }
            finally
            {
                _busy = false;
            }
        }

        // ============================================================
        // 今日列表
        // ============================================================

        private async Task RefreshTodayAsync()
        {
            var (from, to) = Period.Today();
            var rows = await _blocks.GetByRangeAsync(from, to);

            int totalMin = rows.Sum(r => Math.Max(0, r.DurationMin));
            int pending = rows.Count(r => !r.Synced);

            TodayTotalLabel.Text = FormatMinutes(totalMin);
            TodaySubLabel.Text = rows.Count == 0
                ? "点上面任意一片就能记一条"
                : $"{rows.Count} 条" + (pending > 0 ? $" · {pending} 条待同步" : " · 已全部同步");

            var goalNames = await GoalNameMapAsync();

            var vm = rows.Select(r => new BlockRowVm
            {
                Id = r.Id,
                Title = _settings.DisplayName(r.PieceCode),
                Time = Period.ToLocalTime(r.OccurredAt).ToString("HH:mm"),
                Minutes = r.DurationMin > 0 ? FormatMinutes(r.DurationMin) : "未填时长",
                GoalText = goalNames.TryGetValue(r.GoalId ?? string.Empty, out var gname) && !string.IsNullOrEmpty(gname)
                    ? "· " + gname
                    : string.Empty,
                NoteText = r.Note ?? string.Empty,
                SyncText = r.Synced ? "已同步" : "待同步",
                SyncColor = r.Synced ? SyncedColor : PendingColor,
            }).ToList();

            TodayList.ItemsSource = vm;

            // 列表高度跟着内容走：固定 320 会在只记了 3 条时留一大片空白（截图里那个空框），
            // 而记满一天时又要滚动两次——两头都不对。
            TodayList.HeightRequest = Math.Clamp(
                rows.Sum(r => string.IsNullOrEmpty(r.Note) ? 56 : 72) + 12, 76, 460);

            // 计划：从本地读今天这一组（按创建顺序），实际时长由今天的块现算
            var plans = await _plans.GetByRangeAsync(from, to);
            _todayPlans = plans.Where(p => !p.Deleted)
                .OrderBy(p => p.CreatedAt)
                .ToList();

            RenderPlans(rows);
            BuildReviewPanel(rows);
        }

        // ============================================================
        // R4 每日计划：今天做哪 3 件
        // ============================================================
        //
        // 三条边界（见需求 §18）：
        //   1. 最多 3 件，第 4 件直接加不进去；
        //   2. 只点选，0 打字（片名现成，预算从固定档里选）；
        //   3. 不做勾选：达成率 = 当天同片实际时长 ÷ 预算，≥80% 达成。

        private void RenderPlans(List<LocalBlock> todayBlocks)
        {
            PlanRows.Children.Clear();

            bool full = _todayPlans.Count >= 3;
            PlanAddBtn.IsVisible = !full;
            PlanTitle.Text = _todayPlans.Count == 0 ? "今天做哪 3 件？" : $"今天计划 {_todayPlans.Count} 件";
            PlanHint.Text = _todayPlans.Count == 0
                ? "只点选，不写字。不计划也行，不算漏。"
                : "晚上这里自动显示计划 vs 实际，不用勾任何东西。";

            foreach (var plan in _todayPlans)
            {
                int actual = todayBlocks
                    .Where(b => b.PieceCode == plan.PieceCode)
                    .Sum(b => Math.Max(0, b.DurationMin));

                var (status, color) = PlanStatus(plan.BudgetMin, actual);

                var info = new VerticalStackLayout { Spacing = 1 };
                info.Children.Add(TextPeek.Attach(new Label
                {
                    Text = _settings.DisplayName(plan.PieceCode),
                    FontSize = 13,
                    LineBreakMode = LineBreakMode.TailTruncation,
                }));
                info.Children.Add(new Label
                {
                    Text = plan.BudgetMin > 0
                        ? $"预算 {FormatMinutes(plan.BudgetMin)} · 实际 {FormatMinutes(actual)}"
                        : $"实际 {FormatMinutes(actual)}",
                    FontSize = 11,
                    TextColor = Color.FromArgb("#8A8A8E"),
                });

                var row = new Grid
                {
                    ColumnDefinitions =
                    {
                        new ColumnDefinition(GridLength.Star),
                        new ColumnDefinition(GridLength.Auto),
                        new ColumnDefinition(new GridLength(34)),
                    },
                    ColumnSpacing = 6,
                };
                row.Add(info, 0);
                row.Add(new Label
                {
                    Text = status,
                    FontSize = 12,
                    TextColor = color,
                    VerticalOptions = LayoutOptions.Center,
                }, 1);

                var remove = new Button
                {
                    Text = "×",
                    FontSize = 14,
                    Padding = 0,
                    WidthRequest = 34,
                    HeightRequest = 28,
                    BackgroundColor = Colors.Transparent,
                    TextColor = Color.FromArgb("#8A8A8E"),
                    CommandParameter = plan.Id,
                };
                remove.Clicked += OnRemovePlanClicked;
                row.Add(remove, 2);

                PlanRows.Children.Add(row);
            }
        }

        /// <summary>达成度：≥80% 达成 / 50~80% 部分 / &lt;50% 未达成。预算 0 时只报实际。</summary>
        private static (string Text, Color Color) PlanStatus(int budget, int actual)
        {
            if (budget <= 0) return ("已记 " + BlockEntryPage.FormatMinutes(actual), Color.FromArgb("#8A8A8E"));

            double ratio = actual / (double)budget;
            return ratio switch
            {
                >= 0.8 => ("达成", PlanOk),
                >= 0.5 => ("部分", PlanMid),
                _ => ("未达成", PlanBad),
            };
        }

        private async void OnAddPlanClicked(object? sender, EventArgs e)
        {
            if (_todayPlans.Count >= 3)
            {
                StatusLabel.Text = "一天最多计划 3 件。想换的话，先去掉一件。";
                return;
            }

            if (_frontPieces.Count == 0) await BuildFrontPiecesAsync();

            var names = _frontPieces.Select(p => _settings.DisplayName(p.Code)).ToArray();
            string pick = await DisplayActionSheet("今天做哪一件？", "取消", null, names);
            int idx = Array.IndexOf(names, pick);
            if (idx < 0) return;

            string code = _frontPieces[idx].Code;

            // 预算：从固定档里选，默认落在当前时长档上
            var labels = Pieces.Durations.Select(FormatMinutes).ToArray();
            string pickBudget = await DisplayActionSheet("打算花多久？", "取消", null, labels);
            int bIdx = Array.IndexOf(labels, pickBudget);
            if (bIdx < 0) return;

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var plan = new LocalPlan
            {
                Id = Guid.NewGuid().ToString("N"),
                Day = TodayDay(),
                PieceCode = code,
                BudgetMin = Pieces.Durations[bIdx],
                GoalId = _activeGoals.FirstOrDefault(g => g.IsCurrent)?.Id ?? string.Empty,
                CreatedAt = now,
                UpdatedAt = now,
                Deleted = false,
                Synced = false,
                SyncedAt = 0,
            };

            await _plans.InsertAsync(plan);
            StatusLabel.Text = $"今天加了「{plan.PieceCode}」，{FormatMinutes(plan.BudgetMin)}";

            await RefreshTodayAsync();
            _ = SyncAndRefreshAsync();
        }

        private async void OnRemovePlanClicked(object? sender, EventArgs e)
        {
            if (sender is not Button b || b.CommandParameter is not string id) return;

            var plan = await _plans.FindAsync(id);
            if (plan is null) return;

            if (plan.Synced)
            {
                plan.Deleted = true;
                plan.Synced = false;
                plan.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                await _plans.UpdateAsync(plan);
            }
            else
            {
                await _plans.DeleteAsync(id);
            }

            await RefreshTodayAsync();
            _ = SyncAndRefreshAsync();
        }

        /// <summary>今天的 yyyy-MM-dd（固定 UTC+8，与全项目口径一致）。</summary>
        private static string TodayDay() =>
            DateTimeOffset.UtcNow.ToOffset(Period.Cst).ToString("yyyy-MM-dd");

        // ============================================================
        // R5：今天在练什么（正向为主，1 次点击）
        // ============================================================
        //
        // 失败有痕迹（"又顶回去了"有具体场景，好记），成功没痕迹（"守住了"是没发生的事，想不起来点）。
        // 所以：**"守住了"是主按钮（大、在左），旧反应是次按钮（小、在右）**。
        // 不显示成功率、不显示连续天数：有比率就会开始不记失败那次。

        private async Task RefreshPracticeAsync()
        {
            var all = await _patterns.GetByRangeAsync(0, long.MaxValue);
            var active = all.Where(p => p.Status == 0).ToList();

            if (active.Count == 0)
            {
                PracticeCard.IsVisible = false;
                return;
            }

            var (from, to) = Period.Today();
            var today = await _practices.GetByRangeAsync(from, to);

            // 最近 14 天有练习的排前面；最多显示 2 个，3 个模式 6 个按钮会吃掉首屏
            var (f14, t14) = Period.LastDays(14);
            var recent = await _practices.GetByRangeAsync(f14, t14);

            var ordered = active
                .OrderByDescending(p => recent.Any(x => x.PatternId == p.Id))
                .ThenByDescending(p => p.UpdatedAt)
                .Take(2)
                .ToList();

            PracticeRows.Children.Clear();

            foreach (var p in ordered)
            {
                int good = today.Count(x => x.PatternId == p.Id && x.Result == 1);
                int back = today.Count(x => x.PatternId == p.Id && x.Result == 0);

                var info = new VerticalStackLayout { Spacing = 1 };
                info.Children.Add(TextPeek.Attach(new Label
                {
                    Text = p.Title,
                    FontSize = 13,
                    // 两行：模式名往往是一句完整行为（"被质疑时不立刻反驳"），
                    // 单行会被截成"被质疑时…"，等于没显示
                    LineBreakMode = LineBreakMode.TailTruncation,
                    MaxLines = 2,
                }));
                info.Children.Add(new Label
                {
                    Text = $"今天：守住 {good} · 又回去 {back}",
                    FontSize = 11,
                    TextColor = Color.FromArgb("#8A8A8E"),
                });

                var go = new Button
                {
                    Text = "守住了",
                    FontSize = 13,
                    HeightRequest = 40,
                    Padding = new Thickness(14, 0),
                    CornerRadius = 10,
                    BackgroundColor = Color.FromArgb("#3B6D11"),
                    TextColor = Colors.White,
                    CommandParameter = $"{p.Id}|1",
                };
                go.Clicked += OnPracticeClicked;

                var backBtn = new Button
                {
                    Text = "又回去了",
                    FontSize = 11,
                    HeightRequest = 30,
                    Padding = new Thickness(10, 0),
                    CornerRadius = 8,
                    BackgroundColor = Colors.Transparent,
                    TextColor = Color.FromArgb("#8A8A8E"),
                    BorderWidth = 1,
                    BorderColor = Color.FromArgb("#D9D3E8"),
                    CommandParameter = $"{p.Id}|0",
                };
                backBtn.Clicked += OnPracticeClicked;

                var grid = new Grid
                {
                    ColumnDefinitions =
                    {
                        new ColumnDefinition(GridLength.Star),
                        new ColumnDefinition(GridLength.Auto),
                        new ColumnDefinition(GridLength.Auto),
                    },
                    ColumnSpacing = 6,
                };
                grid.Add(info, 0);
                grid.Add(go, 1);
                grid.Add(backBtn, 2);

                PracticeRows.Children.Add(grid);
            }

            PracticeCard.IsVisible = true;
        }

        private async void OnPracticeClicked(object? sender, EventArgs e)
        {
            if (sender is not Button b || b.CommandParameter is not string param) return;

            var parts = param.Split('|');
            if (parts.Length != 2 || !int.TryParse(parts[1], out int result)) return;

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var practice = new LocalPractice
            {
                Id = Guid.NewGuid().ToString("N"),
                PatternId = parts[0],
                Result = result,
                OccurredAt = now,
                CreatedAt = now,
                UpdatedAt = now,
                Deleted = false,
                Synced = false,
                SyncedAt = 0,
            };

            await _practices.InsertAsync(practice);
            StatusLabel.Text = result == 1 ? "记了一次「守住了」" : "记了一次「又回去了」";

            await RefreshPracticeAsync();
            _ = SyncAndRefreshAsync();
        }

        private async Task<Dictionary<string, string>> GoalNameMapAsync()
        {
            var all = await _goals.GetByRangeAsync(0, long.MaxValue);
            return all
                .Where(g => !string.IsNullOrEmpty(g.Id))
                .ToDictionary(g => g.Id, g => Short(g.Title), StringComparer.Ordinal);
        }

        private async void OnRowMenuClicked(object? sender, EventArgs e)
        {
            if (sender is not Button b || b.CommandParameter is not string id) return;

            var row = await _blocks.FindAsync(id);
            if (row is null) return;

            string name = _settings.DisplayName(row.PieceCode);
            string pick = await DisplayActionSheet(
                $"{name}　{(row.DurationMin > 0 ? FormatMinutes(row.DurationMin) : "未填时长")}",
                "取消", null, "改时长", "换目标", "备注", "删除");

            switch (pick)
            {
                case "改时长":
                    await ChangeDurationAsync(row);
                    break;
                case "换目标":
                    await ChangeGoalAsync(row);
                    break;
                case "备注":
                    await ChangeNoteAsync(row);
                    break;
                case "删除":
                    await DeleteAsync(row);
                    break;
            }
        }

        private async Task ChangeNoteAsync(LocalBlock row)
        {
            // 换成卡片式多行编辑：原来的单行弹框只露出尾巴，改一段旧备注等于盲改
            string? input = await TextEditorPage.ShowAsync(
                $"备注 · {_settings.DisplayName(row.PieceCode)}",
                initial: row.Note,
                placeholder: "比如「理发」「路上顺便买了电池」",
                compact: true,
                maxLength: 200);
            if (input is null) return;   // 取消 = 不改

            row.Note = input;
            row.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            row.Synced = false;

            await _blocks.UpdateAsync(row);
            await RefreshTodayAsync();
            _ = SyncAndRefreshAsync();
        }

        private async Task ChangeDurationAsync(LocalBlock row)
        {
            var options = Pieces.Durations.Select(FormatMinutes).ToList();
            options.Add("自定义…");

            string pick = await DisplayActionSheet("改成多久", "取消", null, options.ToArray());
            if (string.IsNullOrEmpty(pick) || pick == "取消") return;

            int minutes;
            if (pick == "自定义…")
            {
                string? input = await DisplayPromptAsync("自定义时长", "分钟数（1 ~ 1440）",
                    initialValue: row.DurationMin > 0 ? row.DurationMin.ToString() : "30",
                    maxLength: 4, keyboard: Keyboard.Numeric);
                if (input is null) return;
                if (!int.TryParse(input, out minutes) || minutes < 1 || minutes > 1440)
                {
                    StatusLabel.Text = "时长要在 1 ~ 1440 分钟之间";
                    return;
                }
            }
            else
            {
                int idx = options.IndexOf(pick);
                if (idx < 0) return;
                minutes = Pieces.Durations[idx];
            }

            row.DurationMin = minutes;
            row.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            row.Synced = false;

            await _blocks.UpdateAsync(row);
            StatusLabel.Text = $"已改成 {FormatMinutes(row.DurationMin)}";

            await RefreshTodayAsync();
            await RefreshStatsAsync();
            _ = SyncAndRefreshAsync();
        }

        private async Task ChangeGoalAsync(LocalBlock row)
        {
            var names = new List<string> { "不归任何目标" };
            names.AddRange(_activeGoals.Select(g => g.Title));

            string? pick = await DisplayActionSheet("归到哪个目标", "取消", null, names.ToArray());
            if (string.IsNullOrEmpty(pick) || pick == "取消") return;

            int idx = names.IndexOf(pick);
            row.GoalId = idx <= 0 ? string.Empty : _activeGoals[idx - 1].Id;
            row.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            row.Synced = false;

            await _blocks.UpdateAsync(row);
            StatusLabel.Text = idx <= 0 ? "已取消目标归属" : $"已归到「{pick}」";

            await RefreshTodayAsync();
            _ = SyncAndRefreshAsync();
        }

        private async Task DeleteAsync(LocalBlock row)
        {
            bool ok = await DisplayAlert("删除这条？",
                $"{_settings.DisplayName(row.PieceCode)}　{(row.DurationMin > 0 ? FormatMinutes(row.DurationMin) : "未填时长")}",
                "删除", "取消");
            if (!ok) return;

            if (row.Synced)
            {
                // 已同步：留墓碑，否则下次镜像同步又会被拉回来
                row.Deleted = true;
                row.Synced = false;
                row.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                await _blocks.UpdateAsync(row);
            }
            else
            {
                await _blocks.DeleteAsync(row.Id);
            }

            StatusLabel.Text = "已删除";
            await RefreshTodayAsync();
            await RefreshStatsAsync();
            _ = SyncAndRefreshAsync();
        }

        // ============================================================
        // R3 日复盘：每条块三个按钮（值得 / 一般 / 别做了）
        // ============================================================

        private static readonly Color WorthOkBg = Color.FromArgb("#3B6D11");     // 值得（选中）
        private static readonly Color WorthMidBg = Color.FromArgb("#5F5E5A");    // 一般（选中）
        private static readonly Color WorthBadBg = Color.FromArgb("#A32D2D");    // 别做了（选中）

        // 计划达成度三档配色（与复盘评价无关，别混用）
        private static readonly Color PlanOk = Color.FromArgb("#3B6D11");
        private static readonly Color PlanMid = Color.FromArgb("#8A5B00");
        private static readonly Color PlanBad = Color.FromArgb("#A32D2D");

        private void BuildReviewPanel(List<LocalBlock> rows)
        {
            ReviewPanel.Children.Clear();

            var live = rows.Where(r => !r.Deleted).ToList();
            if (live.Count == 0)
            {
                ReviewPanel.Children.Add(new Label
                {
                    Text = "今天还没有记录，先在上面记几笔",
                    FontSize = 12,
                    TextColor = Color.FromArgb("#8A8A8E"),
                    Margin = new Thickness(0, 6),
                });
                return;
            }

            foreach (var r in live)
            {
                var info = new VerticalStackLayout { Spacing = 1 };

                string head = _settings.DisplayName(r.PieceCode)
                    + (r.DurationMin > 0 ? " · " + FormatMinutes(r.DurationMin) : " · 未填时长");
                info.Children.Add(TextPeek.Attach(new Label
                {
                    Text = head,
                    FontSize = 13,
                    LineBreakMode = LineBreakMode.TailTruncation,
                }));

                // 只在「值得」时写的那一句经验，跟着行显示
                if (!string.IsNullOrEmpty(r.Lesson))
                {
                    info.Children.Add(new Label
                    {
                        Text = "↳ " + r.Lesson,
                        FontSize = 11,
                        TextColor = WorthOkBg,
                        LineBreakMode = LineBreakMode.WordWrap,
                    });
                }

                var btns = new HorizontalStackLayout { Spacing = 6 };
                btns.Add(MakeWorthButton(r, 1, "值得"));
                btns.Add(MakeWorthButton(r, 2, "一般"));
                btns.Add(MakeWorthButton(r, 3, "别做了"));

                var grid = new Grid
                {
                    ColumnDefinitions =
                    {
                        new ColumnDefinition { Width = GridLength.Star },
                        new ColumnDefinition { Width = GridLength.Auto },
                    },
                    ColumnSpacing = 8,
                    Padding = new Thickness(0, 6),
                };
                grid.Add(info, 0);
                grid.Add(btns, 1);

                ReviewPanel.Children.Add(grid);
            }
        }

        private Button MakeWorthButton(LocalBlock row, int worth, string label)
        {
            bool active = row.Worth == worth;
            Color? activeBg = worth switch
            {
                1 => WorthOkBg,
                2 => WorthMidBg,
                _ => WorthBadBg,
            };

            var btn = new Button
            {
                Text = label,
                FontSize = 11,
                HeightRequest = 28,
                Padding = new Thickness(10, 0),
                CornerRadius = 14,
                BackgroundColor = active ? activeBg : ChipBg,
                TextColor = active ? Colors.White : ChipText,
                // 参数用字符串拼进 CommandParameter，避免闭包持有整行对象
                CommandParameter = $"{row.Id}|{worth}",
            };
            btn.Clicked += OnWorthClicked;
            return btn;
        }

        private async void OnWorthClicked(object? sender, EventArgs e)
        {
            if (sender is not Button b || b.CommandParameter is not string param) return;

            var parts = param.Split('|');
            var row = await _blocks.FindAsync(parts[0]);
            if (row is null || !int.TryParse(parts[1], out int worth)) return;

            if (worth == 1)
            {
                // 只有「值得」才问那一句：下次怎么做更快？一句话，可跳过。
                // 取消 = 这次不评，不落任何值。
                string? lesson = await TextEditorPage.ShowAsync("下次怎么做更快？",
                    initial: row.Lesson,
                    placeholder: "一句话，可跳过",
                    compact: true,
                    maxLength: 200);
                if (lesson is null) return;

                row.Worth = 1;
                row.Lesson = lesson;
            }
            else
            {
                if (row.Worth == worth) return;   // 重复点同一个不动作
                row.Worth = worth;
            }

            row.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            row.Synced = false;

            await _blocks.UpdateAsync(row);
            StatusLabel.Text = worth == 1 ? "已标值得" : worth == 2 ? "已标一般" : "已标「别做了」";

            await RefreshTodayAsync();     // 复盘区跟着重画（按钮选中态 + 那一句经验）
            _ = SyncAndRefreshAsync();
        }

        // ============================================================
        // 统计
        // ============================================================

        private void OnPeriodTodayClicked(object sender, EventArgs e) => SwitchPeriod("today");

        private void OnPeriodWeekClicked(object sender, EventArgs e) => SwitchPeriod("week");

        private async void SwitchPeriod(string period)
        {
            if (_period == period) return;
            _period = period;
            ApplyPeriodStyle();
            await RefreshStatsAsync();
        }

        private void ApplyPeriodStyle()
        {
            bool today = _period == "today";
            PeriodTodayBtn.BackgroundColor = today ? ChipActiveBg : ChipBg;
            PeriodTodayBtn.TextColor = today ? Colors.White : ChipText;
            PeriodWeekBtn.BackgroundColor = today ? ChipBg : ChipActiveBg;
            PeriodWeekBtn.TextColor = today ? ChipText : Colors.White;
        }

        private async Task RefreshStatsAsync()
        {
            var (from, to) = _period == "week" ? Period.ThisWeek() : Period.Today();

            var pieces = await _blocks.AggregateByPieceAsync(from, to);

            // 区间内"已经过去"的分钟数：未来的时间不算漏记，
            // 否则早上八点打开会看到"今天未记录 16 小时"，纯属制造焦虑。
            int total = (int)Math.Max(0, (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - from) / 60000);
            int covered = pieces.Sum(p => p.Minutes);
            int unrecorded = Math.Max(0, total - covered);

            RenderStats(pieces.Select(p => (p.PieceCode, p.Minutes, p.Count)).ToList(), total, unrecorded);
        }

        private void RenderStats(
            IReadOnlyList<(string PieceCode, int Minutes, int Count)> pieces,
            int totalMinutes,
            int unrecordedMinutes)
        {
            StatsPanel.Children.Clear();

            int max = pieces.Count == 0 ? 1 : Math.Max(1, pieces.Max(p => p.Minutes));

            foreach (var p in pieces.Where(x => x.Minutes > 0))
            {
                string name = _settings.DisplayName(p.PieceCode);
                double ratio = totalMinutes > 0 ? (double)p.Minutes / totalMinutes : 0;

                var grid = new Grid
                {
                    ColumnDefinitions =
                    {
                        new ColumnDefinition { Width = new GridLength(96) },
                        new ColumnDefinition { Width = GridLength.Star },
                        new ColumnDefinition { Width = new GridLength(96) },
                    },
                    ColumnSpacing = 8,
                };

                // 定宽 96px 的图表列：这里展开会把整行统计条撑歪，只能浮起来看
                grid.Add(TextPeek.Attach(new Label
                {
                    Text = name,
                    FontSize = 12,
                    VerticalOptions = LayoutOptions.Center,
                    LineBreakMode = LineBreakMode.TailTruncation,
                }, forceCard: true), 0);

                grid.Add(new ProgressBar
                {
                    Progress = (double)p.Minutes / max,
                    ProgressColor = Color.FromArgb("#185FA5"),
                    VerticalOptions = LayoutOptions.Center,
                }, 1);

                grid.Add(new Label
                {
                    Text = $"{FormatMinutes(p.Minutes)} · {ratio:P0}",
                    FontSize = 11,
                    TextColor = Color.FromArgb("#5F5E5A"),
                    HorizontalTextAlignment = TextAlignment.End,
                    VerticalOptions = LayoutOptions.Center,
                }, 2);

                StatsPanel.Children.Add(grid);
            }

            if (pieces.Count == 0)
            {
                StatsPanel.Children.Add(new Label
                {
                    Text = "这个区间还没有记录",
                    FontSize = 12,
                    TextColor = Color.FromArgb("#8A8A8E"),
                });
            }

            // 未记录是整页最值钱的一栏：一天 24 小时只记了 12 小时，
            // 剩下 12 小时必须显示出来，否则你会以为自己一天只活了 12 小时。
            double unRatio = totalMinutes > 0 ? (double)unrecordedMinutes / totalMinutes : 0;
            UnrecordedLabel.Text = totalMinutes > 0
                ? $"未记录 {FormatMinutes(unrecordedMinutes)}（占 {unRatio:P0}）"
                : string.Empty;
        }

        // ============================================================
        // 同步
        // ============================================================

        private async void OnSyncClicked(object sender, EventArgs e)
        {
            SyncBtn.IsEnabled = false;
            StatusLabel.Text = "正在同步……";
            try
            {
                var b = await _blockSync.SyncAsync();
                var g = await _goalSync.SyncAsync();
                var p = await _planSync.SyncAsync();
                await _patternSync.SyncAsync();
                await _practiceSync.SyncAsync();
                var m = await _mirror.MirrorAsync();
                await _mirror.FetchGoalsAsync();

                StatusLabel.Text = $"{b.Message}；{m.Message}";

                await LoadGoalsAsync();
                await RefreshTodayAsync();
                await RefreshStatsAsync();
            }
            catch (Exception ex)
            {
                StatusLabel.Text = "同步失败：" + ex.Message;
            }
            finally
            {
                SyncBtn.IsEnabled = true;
            }
        }

        private async Task SyncAndRefreshAsync()
        {
            try
            {
                await _blockSync.SyncAsync();
                await _goalSync.SyncAsync();
                await _planSync.SyncAsync();
                await _patternSync.SyncAsync();
                await _practiceSync.SyncAsync();
                await _mirror.MirrorAsync();

                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    await LoadGoalsAsync();
                    await RefreshTodayAsync();
                    await RefreshStatsAsync();
                });

                // 服务端校正（本地副本可能有别的设备刚记的）
                var server = await _mirror.GetPieceStatsAsync(_period);
                if (server is not null)
                {
                    await MainThread.InvokeOnMainThreadAsync(() =>
                        RenderStats(server.Pieces, server.TotalMinutes, server.UnrecordedMinutes));
                }
            }
            catch
            {
                // 后台失败不弹错：保持"待同步"，等下次进页面 / 网络恢复再试
            }
        }

        // ============================================================

        internal static string FormatMinutes(int minutes)
        {
            if (minutes <= 0) return "0 分钟";
            if (minutes < 60) return $"{minutes} 分钟";

            int h = minutes / 60;
            int m = minutes % 60;
            return m == 0 ? $"{h} 小时" : $"{h} 小时 {m} 分";
        }
    }

    /// <summary>今日列表的展示行。</summary>
    public class BlockRowVm
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Time { get; set; } = string.Empty;
        public string Minutes { get; set; } = string.Empty;
        public string GoalText { get; set; } = string.Empty;
        public string NoteText { get; set; } = string.Empty;
        public string SyncText { get; set; } = string.Empty;
        public Color SyncColor { get; set; } = Colors.Transparent;
    }
}
