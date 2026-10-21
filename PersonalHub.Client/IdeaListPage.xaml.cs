using PersonalHub.Client.Controls;
using PersonalHub.Client.Data;
using PersonalHub.Client.Services;

namespace PersonalHub.Client
{
    /// <summary>
    /// 想法库（R6）：看 + 整理 + 「转成目标」+「已落实」。
    ///
    /// 四条设计约束：
    ///  1. **不显示"未处理 N 条"**。想法库不是待办清单，一旦有积压计数，
    ///     人就会开始有"要清空它"的压力，然后放弃记录。
    ///  2. **不做自动生成目标**。只有用户自己点「转成目标」。
    ///     系统判断不了哪条想法值得做，判断错了代价是"多了一堆假目标"，比没有更糟。
    ///  3. **文字点开看全**（TextPeek），不进第二层详情页 —— 想法本身就只有一段话，
    ///     再包一层详情页等于让用户点两次才能看完一段话。
    ///  4. **「已落实」是回看的标记，不是流程状态**。它和「转成目标」互不推导：
    ///     转了目标的可能一直没做成，没转目标的小事也可能直接做完了。
    ///     所以列表里只有"已落实 N 条"这个正数，永远没有"还有 N 条没落实"。
    ///
    /// 只加载最近 200 条：想法是要长期攒的，全量渲染会在某天突然变卡。
    /// 需要更早的，用管理端的想法库页（那里有筛选和全文搜索）。
    /// </summary>
    public partial class IdeaListPage : ContentPage
    {
        /// <summary>一次最多渲染多少条。超过就提示去管理端看更早的。</summary>
        private const int MaxRender = 200;

        private static readonly Color ChipBg = Color.FromArgb("#F1EFE8");
        private static readonly Color ChipText = Color.FromArgb("#2C2C2A");
        private static readonly Color ChipActiveBg = Color.FromArgb("#185FA5");

        /// <summary>已落实用的绿。与「计划行动」同色：落实本来就是"计划那类想法"的终点。</summary>
        private static readonly Color DoneColor = Color.FromArgb("#3B6D11");

        private readonly LocalRecordStore<LocalIdea> _ideas;
        private readonly LocalRecordStore<LocalGoal> _goals;
        private readonly RecordSyncService<LocalIdea> _ideaSync;
        private readonly RecordSyncService<LocalGoal> _goalSync;

        /// <summary>-1 = 全部。</summary>
        private int _filter = -1;

        /// <summary>
        /// 只看已落实的。
        ///
        /// 它和「未处理」是两回事：这里只是"回看我做成了哪些"的视角开关，
        /// 关掉的时候不会告诉你"还有几条没落实" —— 想法库不给人积压感。
        /// </summary>
        private bool _doneOnly;

        public IdeaListPage(
            LocalRecordStore<LocalIdea> ideas,
            LocalRecordStore<LocalGoal> goals,
            RecordSyncService<LocalIdea> ideaSync,
            RecordSyncService<LocalGoal> goalSync)
        {
            InitializeComponent();

            _ideas = ideas;
            _goals = goals;
            _ideaSync = ideaSync;
            _goalSync = goalSync;

            BuildFilterChips();
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();
            await LoadAsync();
        }

        // ============================================================
        // 筛选
        // ============================================================

        private void BuildFilterChips()
        {
            FilterChips.Children.Clear();

            AddFilterChip(-1, "全部");
            foreach (var k in IdeaKinds.All)
            {
                AddFilterChip(k.Kind, k.Label);
            }

            AddDoneChip();
        }

        /// <summary>
        /// 「已落实」开关。它是**独立于类型**的一维筛选，不是第五种类型 ——
        /// 塞进类型 chips 里会让"未分类"和"已落实"看起来互斥，而它们根本不是一回事。
        /// </summary>
        private void AddDoneChip()
        {
            var chip = new Button
            {
                Text = "✓ 已落实",
                FontSize = 12,
                HeightRequest = 32,
                Padding = new Thickness(13, 0),
                CornerRadius = 16,
                Margin = new Thickness(0, 0, 8, 8),
            };

            StyleDoneChip(chip);
            chip.Clicked += OnDoneFilterClicked;
            FilterChips.Children.Add(chip);
        }

        private void StyleDoneChip(Button chip)
        {
            chip.BackgroundColor = _doneOnly ? DoneColor : ChipBg;
            chip.TextColor = _doneOnly ? Colors.White : ChipText;
            chip.BorderWidth = 0;
        }

        private async void OnDoneFilterClicked(object? sender, EventArgs e)
        {
            if (sender is not Button chip) return;

            _doneOnly = !_doneOnly;
            StyleDoneChip(chip);

            await LoadAsync();
        }

        private void AddFilterChip(int kind, string label)
        {
            var chip = new Button
            {
                Text = label,
                FontSize = 12,
                HeightRequest = 32,
                Padding = new Thickness(13, 0),
                CornerRadius = 16,
                Margin = new Thickness(0, 0, 8, 8),
                CommandParameter = kind,
            };

            StyleFilterChip(chip, kind);
            chip.Clicked += OnFilterClicked;
            FilterChips.Children.Add(chip);
        }

        private void StyleFilterChip(Button chip, int kind)
        {
            bool active = kind == _filter;
            chip.BackgroundColor = active ? ChipActiveBg : ChipBg;
            chip.TextColor = active ? Colors.White : ChipText;
            chip.BorderWidth = 0;
        }

        private async void OnFilterClicked(object? sender, EventArgs e)
        {
            if (sender is not Button b || b.CommandParameter is not int kind) return;

            _filter = kind;

            foreach (var child in FilterChips.Children)
            {
                if (child is Button c && c.CommandParameter is int k) StyleFilterChip(c, k);
            }

            await LoadAsync();
        }

        // ============================================================
        // 列表
        // ============================================================

        private async Task LoadAsync()
        {
            var all = await _ideas.GetByRangeAsync(0, long.MaxValue);

            var filtered = _filter < 0
                ? all
                : all.Where(x => x.Kind == _filter).ToList();

            if (_doneOnly) filtered = filtered.Where(x => x.Done).ToList();

            // 已按 CreatedAt 倒序（GetByRangeAsync 用 DayTimestamp 排序），这里再兜一次
            filtered = filtered.OrderByDescending(x => x.CreatedAt).ToList();

            var render = filtered.Take(MaxRender).ToList();

            IdeaRows.Children.Clear();
            foreach (var idea in render)
            {
                IdeaRows.Children.Add(BuildCard(idea));
            }

            EmptyLabel.IsVisible = render.Count == 0;
            EmptyLabel.Text = all.Count == 0
                ? "库里还是空的。回上一页写一句就有一条 —— 不需要组织语言。"
                : _doneOnly
                    ? "还没有标成「已落实」的想法。它不是待办，标不标都行。"
                    : "这个筛选下还没有想法。";

            CountLabel.Text = all.Count == 0
                ? string.Empty
                : BuildCountText(all, filtered, render);

            await RefreshStatusAsync();
        }

        private static string BuildCountText(List<LocalIdea> all, List<LocalIdea> filtered, List<LocalIdea> render)
        {
            int unclassified = all.Count(x => x.Kind == IdeaKinds.Unclassified);
            int converted = all.Count(x => !string.IsNullOrEmpty(x.GoalId));
            int done = all.Count(x => x.Done);

            string head = $"共 {all.Count} 条";
            if (filtered.Count != all.Count) head += $" · 当前筛选 {filtered.Count} 条";
            if (unclassified > 0) head += $" · 未分类 {unclassified} 条";
            if (converted > 0) head += $" · 已转成目标 {converted} 条";
            if (done > 0) head += $" · 已落实 {done} 条";
            if (render.Count < filtered.Count) head += $" · 只显示最近 {render.Count} 条";

            return head;
        }

        private View BuildCard(LocalIdea idea)
        {
            var info = new VerticalStackLayout { Spacing = 4 };

            // 点一下就地展开（短）或弹阅读卡（长）。想法没有详情页，所以这里是唯一读全文的地方。
            info.Children.Add(TextPeek.Attach(new Label
            {
                Text = idea.Text,
                FontSize = 14,
                LineBreakMode = LineBreakMode.TailTruncation,
                MaxLines = 4,
            }));

            string meta = $"{IdeaKinds.LabelOf(idea.Kind)} · {FormatWhen(idea.CreatedAt)}";
            if (!string.IsNullOrEmpty(idea.GoalId)) meta += " · 已转成目标";

            // 已落实单独占一行：混进上面那行灰字里，一眼扫过去根本看不见，
            // 而"这条我到底做没做成"恰恰是回头翻想法库时唯一想确认的事。
            if (idea.Done)
            {
                info.Children.Add(new Label
                {
                    Text = "✓ 已落实" + (idea.DoneAt > 0 ? " · " + FormatDay(idea.DoneAt) : string.Empty),
                    FontSize = 11,
                    TextColor = DoneColor,
                    FontAttributes = FontAttributes.Bold,
                });
            }

            info.Children.Add(new Label
            {
                Text = meta,
                FontSize = 11,
                TextColor = IdeaKinds.ColorOf(idea.Kind),
            });

            var actions = new VerticalStackLayout { Spacing = 6, VerticalOptions = LayoutOptions.Start };

            if (string.IsNullOrEmpty(idea.GoalId))
            {
                var convert = new Button
                {
                    Text = "转成目标",
                    FontSize = 11,
                    HeightRequest = 30,
                    Padding = new Thickness(10, 0),
                    CornerRadius = 8,
                    BackgroundColor = Color.FromArgb("#EEEDFE"),
                    TextColor = Color.FromArgb("#534AB7"),
                    CommandParameter = idea.Id,
                };
                convert.Clicked += OnConvertClicked;
                actions.Children.Add(convert);
            }

            var more = new Button
            {
                Text = "⋯",
                FontSize = 14,
                WidthRequest = 32,
                HeightRequest = 26,
                Padding = 0,
                CornerRadius = 8,
                BackgroundColor = Colors.Transparent,
                TextColor = Color.FromArgb("#8A8A8E"),
                CommandParameter = idea.Id,
            };
            more.Clicked += OnMoreClicked;
            actions.Children.Add(more);

            var grid = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(GridLength.Auto),
                },
                ColumnSpacing = 10,
            };
            grid.Add(info, 0);
            grid.Add(actions, 1);

            return new Border
            {
                Stroke = Color.FromArgb("#E3E0D8"),
                StrokeThickness = 1,
                Padding = new Thickness(14, 10),
                Content = grid,
            };
        }

        /// <summary>「10月21日」这种短日期，给"落实时间"用 —— 它只需要到天。</summary>
        private static string FormatDay(long ms)
        {
            if (ms <= 0) return string.Empty;
            return DateTimeOffset.FromUnixTimeMilliseconds(ms).ToOffset(Period.Cst).ToString("M月d日");
        }

        private static string FormatWhen(long ms)
        {
            if (ms <= 0) return "时间未知";

            var t = DateTimeOffset.FromUnixTimeMilliseconds(ms).ToOffset(Period.Cst);
            var today = DateTimeOffset.UtcNow.ToOffset(Period.Cst).Date;

            if (t.Date == today) return $"今天 {t:HH:mm}";
            if (t.Date == today.AddDays(-1)) return $"昨天 {t:HH:mm}";
            if (t.Year == today.Year) return t.ToString("M月d日");
            return t.ToString("yyyy年M月d日");
        }

        // ============================================================
        // 转成目标
        // ============================================================

        private async void OnConvertClicked(object? sender, EventArgs e)
        {
            if (sender is not Button b || b.CommandParameter is not string id) return;

            var idea = await _ideas.FindAsync(id);
            if (idea is null) return;

            // 「预填」而不是「自动生成」：想法是原材料，原文往往带着情绪和背景，
            // 直接当目标名会变成"我好像每次做完计划就松一口气，然后就不做了"这种没法判断做没做完的句子。
            // 先把它放进编辑框，让人自己把它改小成一句能验证的话。
            string? title = await TextEditorPage.ShowAsync("转成目标",
                initial: idea.Text,
                placeholder: "改成一句能判断「做没做完」的话",
                compact: false,
                maxLength: 120);

            if (title is null) return;
            if (title.Length == 0)
            {
                StatusLabel.Text = "目标名不能是空的。";
                return;
            }

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // 还没有"当前目标"的话，新目标顺手设成当前，省掉一次点击
            var goals = await _goals.GetByRangeAsync(0, long.MaxValue);
            bool hasCurrent = goals.Any(g => g.IsCurrent);

            var goal = new LocalGoal
            {
                Id = Guid.NewGuid().ToString("N"),
                Title = title,
                Status = 0,
                NextStep = string.Empty,
                IsCurrent = !hasCurrent,
                StartedAt = now,
                CreatedAt = now,
                UpdatedAt = now,
                Deleted = false,
                Synced = false,
                SyncedAt = 0,
            };

            await _goals.InsertAsync(goal);

            // 想法上只做标记，不改正文、不改类型：转化不等于想法消失。
            // 原文留着，才能回头看"我当时是怎么想的"。
            idea.GoalId = goal.Id;
            idea.UpdatedAt = now;
            idea.Synced = false;
            await _ideas.UpdateAsync(idea);

            await LoadAsync();
            StatusLabel.Text = $"已建目标「{title}」—— 去目标页给它配时间片，想法才算真的开始。";

            _ = _goalSync.SyncAsync();
            _ = _ideaSync.SyncAsync();
        }

        // ============================================================
        // 更多动作（改类型 / 删除）
        // ============================================================

        private async void OnMoreClicked(object? sender, EventArgs e)
        {
            if (sender is not Button b || b.CommandParameter is not string id) return;

            var idea = await _ideas.FindAsync(id);
            if (idea is null) return;

            // 菜单项跟着当前状态变：已经落实的给"取消已落实"，没落实的给"标记已落实"。
            // 用两个不同的词而不是一个开关项，是因为开关项要人先读一遍才知道现在是哪一边。
            string doneAction = idea.Done ? "取消已落实" : "标记已落实";

            string pick = await DisplayActionSheet(
                Shorten(idea.Text, 24), "取消", null, doneAction, "改类型", "删除");

            switch (pick)
            {
                case "标记已落实":
                case "取消已落实":
                    await ToggleDoneAsync(idea);
                    break;

                case "改类型":
                    await ChangeKindAsync(idea);
                    break;

                case "删除":
                    await DeleteAsync(idea);
                    break;
            }
        }

        // ============================================================
        // 已落实
        // ============================================================

        /// <summary>
        /// 打勾 / 取消「已落实」。
        ///
        /// 只改这一个标记，不动正文、不动类型、不动 GoalId ——
        /// "这条想法我做了"和"这条想法变成了目标"是两件独立的事，谁也不推导谁。
        ///
        /// 时间的口径：本地先记一个 now，服务端 <see cref="IdeaStore"/> 在 done=1 且
        /// done_at=0 时会再补一次，两端算出同一个值（差几十毫秒不影响按天显示）。
        /// </summary>
        private async Task ToggleDoneAsync(LocalIdea idea)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            idea.Done = !idea.Done;
            idea.DoneAt = idea.Done ? now : 0;
            idea.UpdatedAt = now;
            idea.Synced = false;

            await _ideas.UpdateAsync(idea);
            await LoadAsync();

            StatusLabel.Text = idea.Done
                ? "标成已落实了。回头翻库的时候一眼就能看见。"
                : "已取消落实标记。";

            _ = _ideaSync.SyncAsync();
        }

        private async Task ChangeKindAsync(LocalIdea idea)
        {
            var options = IdeaKinds.All.Select(k => k.Label).ToArray();
            string pick = await DisplayActionSheet("改类型", "取消", null, options);
            if (string.IsNullOrEmpty(pick) || pick == "取消") return;

            var match = IdeaKinds.All.FirstOrDefault(k => k.Label == pick);
            if (match.Label is null) return;

            idea.Kind = match.Kind;
            idea.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            idea.Synced = false;

            await _ideas.UpdateAsync(idea);
            await LoadAsync();

            StatusLabel.Text = $"已改成「{match.Label}」。";
            _ = _ideaSync.SyncAsync();
        }

        private async Task DeleteAsync(LocalIdea idea)
        {
            bool ok = await DisplayAlert("删掉这条想法？",
                Shorten(idea.Text, 80) + "\n\n删除是软删：服务端还留着墓碑，历史不会突然对不上。",
                "删掉", "取消");
            if (!ok) return;

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            if (idea.Synced)
            {
                // 已经上传过 → 走墓碑，让服务端也删
                idea.Deleted = true;
                idea.UpdatedAt = now;
                idea.Synced = false;
                await _ideas.UpdateAsync(idea);
            }
            else
            {
                // 从没上传过 → 物理删，不必在服务端留一条没人知道的墓碑
                await _ideas.DeleteAsync(idea.Id);
            }

            await LoadAsync();
            StatusLabel.Text = "已删掉。";
            _ = _ideaSync.SyncAsync();
        }

        private static string Shorten(string? text, int max)
        {
            string s = (text ?? string.Empty).Replace("\n", " ").Trim();
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        // ============================================================
        // 顶部小字
        // ============================================================

        private async Task RefreshStatusAsync()
        {
            try
            {
                int pending = await _ideas.CountUnsyncedAsync();
                if (pending > 0)
                {
                    StatusLabel.Text = $"{pending} 条还没同步，联网后会自动补传。";
                }
            }
            catch
            {
                // 读不到就当作"没有额外信息"
            }
        }
    }
}
