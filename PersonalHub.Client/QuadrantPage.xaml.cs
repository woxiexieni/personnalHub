using Microsoft.Maui.Controls.Shapes;
using PersonalHub.Client.Controls;
using PersonalHub.Client.Data;
using PersonalHub.Client.Services;

namespace PersonalHub.Client
{
    /// <summary>
    /// 「计划行动」的四象限视图（R11）。
    ///
    /// 定位：这一页不是待办清单，是**一张作战地图**。它只回答一个问题 ——
    /// "我现在到底有几件事是活的，哪件先动"，不回答"我还有多少没做"（没有这种计数）。
    ///
    /// 三条规则决定了它的形状：
    ///
    /// 1. **「重要」不由人选，而是算出来的。** 新条目默认落 Ⅱ；Ⅱ 里 30 天没人碰过 → 自动落 Ⅳ。
    ///    这样 Ⅱ 里剩下的每一条都是最近真的在惦记的，不用每天重排一次四象限。
    ///    手工提供的是**例外通道**：长按拖走 / 拖回来。
    ///
    /// 2. **「紧急」只认"会过期"。** 不是"今天必须做" —— 他没有老板和交付日，
    ///    按"今天必须做"算，这一列会永远是空的，而空着又不解释就会让人以为功能坏了。
    ///
    /// 3. **已落实当天留在原位划线，跨 0 点就不再出现在格子里。**
    ///    不删数据，只是不占位置 —— 否则干完几十条之后，格子里全是历史，地图就没用了。
    ///    历史去下面的「已落实」折叠带翻。
    ///
    /// 四个格子是固定高度、各自滚动。整页能滚的话格子会被内容撑开，"四个格子"就不成立了。
    /// 两个折叠带**原地展开**（顶掉四象限的位置），不弹层 —— 弹层每次都要多点一下才关得掉，
    /// 而回看是"看一眼就回去"的动作，多一步就是每次多一步。
    /// </summary>
    public partial class QuadrantPage : ContentPage
    {
        /// <summary>Ⅱ 里躺这么多天没人碰，就自动落 Ⅳ。</summary>
        private const int StaleDays = 30;

        private static readonly Color Ink = Color.FromArgb("#2C2C2A");
        private static readonly Color DeepInk = Color.FromArgb("#2C2C2A");
        private static readonly Color Muted = Color.FromArgb("#888780");
        private static readonly Color Faint = Color.FromArgb("#B4B2A9");
        private static readonly Color CardStroke = Color.FromArgb("#E3E0D8");
        private static readonly Color CircleIdle = Color.FromArgb("#B4B2A9");
        private static readonly Color LinkBlue = Color.FromArgb("#185FA5");
        private static readonly Color DangerRed = Color.FromArgb("#A32D2D");
        private static readonly Color DoneInk = Color.FromArgb("#5F5E5A");

        private readonly LocalRecordStore<LocalIdea> _ideas;
        private readonly RecordSyncService<LocalIdea> _ideaSync;
        private readonly LocalRecordStore<LocalGoal> _goals;
        private readonly RecordSyncService<LocalGoal> _goalSync;

        /// <summary>要进四格的：未落实的 + 今天刚落实的。</summary>
        private List<LocalIdea> _active = new();

        /// <summary>还没形状的（plan_kind = 1），只在折叠带里。</summary>
        private List<LocalIdea> _plans = new();

        /// <summary>全部已落实，只在折叠带里。</summary>
        private List<LocalIdea> _done = new();

        /// <summary>
        /// 每条卡片的操作条，key 是想法 id。
        ///
        /// 注意：**只能在 RenderAll 开头清一次**。之前在 RenderCell 里清，
        /// 每渲染一个格子就把上一格的条目全删了，于是点第一格的卡片永远找不到自己的操作条，
        /// 表现就是"点了没反应"。四个格子共用这一个字典，就必须跨格子保存。
        /// </summary>
        private readonly Dictionary<string, View> _actionBars = new();

        private string? _expandedId;

        /// <summary>哪条折叠带是展开的。同一时间只开一条，收起时四象限回来。</summary>
        private Band _openBand = Band.None;

        private enum Band { None, Plan, Done }

        private bool _loading;

        public QuadrantPage(
            LocalRecordStore<LocalIdea> ideas,
            RecordSyncService<LocalIdea> ideaSync,
            LocalRecordStore<LocalGoal> goals,
            RecordSyncService<LocalGoal> goalSync)
        {
            InitializeComponent();

            _ideas = ideas;
            _ideaSync = ideaSync;
            _goals = goals;
            _goalSync = goalSync;

            // 拖放的目标必须常驻挂一次：每格知道自己代表"急/重"的哪种组合，
            // 卡片拖进来就按它改这两个布尔值。重渲染不会重建格子的手势。
            AttachDrop(Cell1, urgent: true, important: true);
            AttachDrop(Cell2, urgent: false, important: true);
            AttachDrop(Cell3, urgent: true, important: false);
            AttachDrop(Cell4, urgent: false, important: false);
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();
            await LoadAsync();
        }

        // ============================================================
        // 读
        // ============================================================

        private async Task LoadAsync()
        {
            if (_loading) return;
            _loading = true;

            try
            {
                var all = await _ideas.GetAllAsync();

                // 作废的不进地图：废案不该继续占位置，也不该继续产生动作。
                var play = all
                    .Where(x => !x.Deleted && !x.IsVoided && x.Kind == 3)
                    .ToList();

                var canDo = play.Where(x => x.PlanKind == 0).ToList();

                await DowngradeStaleAsync(canDo);

                long todayFrom = Period.Today().From;

                _active = canDo
                    .Where(x => !x.Done || x.DoneAt >= todayFrom)
                    .ToList();

                _plans = play.Where(x => x.PlanKind == 1 && !x.Done).ToList();
                _done = play.Where(x => x.Done).ToList();

                RenderAll();
            }
            catch (Exception ex)
            {
                ShowStatus("读取失败：" + ex.Message);
            }
            finally
            {
                _loading = false;
            }
        }

        /// <summary>
        /// 30 天没人碰的 Ⅱ 自动落 Ⅳ。
        ///
        /// 这是整页最关键的一条"算"：它让「重要」不需要人维护。
        /// 静默执行 —— 不弹提示、不发通知、不在格子里留痕迹；
        /// 你下次打开只会看到 Ⅱ 变短了，而 Ⅳ 长了一点，这本身就是要传达的信息。
        ///
        /// 不同步改 UpdatedAt：它只是显示归类的变化，不是内容变更。
        /// 服务端在"上传版本更旧"时才拒绝，相等会直接覆盖，所以只置 Synced=false 就够。
        /// </summary>
        private async Task DowngradeStaleAsync(List<LocalIdea> candidates)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long cut = now - StaleDays * 24L * 60 * 60 * 1000;

            var stale = candidates
                .Where(x => x.Important && LastTouch(x) < cut)
                .ToList();

            if (stale.Count == 0) return;

            foreach (var idea in stale)
            {
                idea.Important = false;
                idea.Synced = false;
                await _ideas.UpdateAsync(idea);
            }

            _ = _ideaSync.SyncAsync();
        }

        /// <summary>你最后一次真的动过它的时刻。老数据没有 TouchedAt，退回创建时间。</summary>
        private static long LastTouch(LocalIdea idea)
            => idea.TouchedAt > 0 ? idea.TouchedAt : idea.CreatedAt;

        // ============================================================
        // 渲染
        // ============================================================

        private void RenderAll()
        {
            // 四格共用这一个字典，所以清在这里、而且只清一次
            _actionBars.Clear();

            RenderCell(Cell1Body, Cell1Scroll, Cell1Empty, Cell1Count,
                _active.Where(x => x.Urgent && x.Important), "还没有会过期的事");
            RenderCell(Cell2Body, Cell2Scroll, Cell2Empty, Cell2Count,
                _active.Where(x => !x.Urgent && x.Important), "当前暂无内容");
            RenderCell(Cell3Body, Cell3Scroll, Cell3Empty, Cell3Count,
                _active.Where(x => x.Urgent && !x.Important), "当前暂无内容");
            RenderCell(Cell4Body, Cell4Scroll, Cell4Empty, Cell4Count,
                _active.Where(x => !x.Urgent && !x.Important), "当前暂无内容");

            ApplyFold();                    // 展开状态跟着新数据走
            if (_openBand != Band.None) RenderBand();

            PlanBandCount.Text = _plans.Count > 0 ? $"{_plans.Count} 条" : "空";
            DoneBandCount.Text = _done.Count > 0 ? $"{_done.Count} 条" : "空";
        }

        private void RenderCell(
            VerticalStackLayout body, ScrollView scroll, Label empty, Label count,
            IEnumerable<LocalIdea> items, string emptyHint)
        {
            // 新的在上：滚在最上面的永远是你最近真的在想的。
            var list = items
                .OrderByDescending(LastTouch)
                .ThenByDescending(x => x.CreatedAt)
                .ToList();

            body.Children.Clear();

            count.Text = list.Count > 0 ? list.Count.ToString() : string.Empty;

            empty.IsVisible = list.Count == 0;
            empty.Text = emptyHint;

            if (list.Count == 0) return;

            foreach (var idea in list)
                body.Children.Add(BuildCard(idea));
        }

        private View BuildCard(LocalIdea idea)
        {
            bool done = idea.Done;
            bool hasArtifact = !string.IsNullOrEmpty(idea.ArtifactUri);

            var circle = new Button
            {
                Text = done ? "✓" : string.Empty,
                FontSize = 11,
                WidthRequest = 20,
                HeightRequest = 20,
                Padding = 0,
                CornerRadius = 10,
                BorderWidth = done ? 0 : 1,
                BorderColor = CircleIdle,
                BackgroundColor = done ? LinkBlue : Colors.Transparent,
                TextColor = Colors.White,
                VerticalOptions = LayoutOptions.Start,
            };
            circle.Clicked += async (_, _) => await ToggleDoneAsync(idea);

            var title = new Label
            {
                Text = idea.Text ?? string.Empty,
                FontSize = 12,
                LineHeight = 1.3,
                LineBreakMode = LineBreakMode.WordWrap,
                TextColor = done ? Faint : Ink,
                TextDecorations = done ? TextDecorations.Strikethrough : TextDecorations.None,
            };

            var meta = new Label
            {
                Text = MetaText(idea),
                FontSize = 10,
                // 有成果时这一行是可点的：格子里最窄，再放一个按钮会把标题压成一列字，
                // 所以把「去成果」挂在它身上 —— 看见"有成果"三个字就会想去点。
                TextColor = hasArtifact ? LinkBlue : Faint,
            };

            if (hasArtifact)
            {
                var openTap = new TapGestureRecognizer();
                openTap.Tapped += async (_, _) => await OpenArtifactAsync(idea);
                meta.GestureRecognizers.Add(openTap);
            }

            var head = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Star),
                },
                ColumnSpacing = 7,
            };
            head.Add(circle, 0);
            head.Add(new VerticalStackLayout { Spacing = 1, Children = { title, meta } }, 1);

            var bar = BuildActionBar(idea);
            _actionBars[idea.Id] = bar;

            // 重画之后保持原来那条的展开状态：否则点完「已落实」再回来，操作条自己收起来了，
            // 想接着留成果还得重新点一次卡。
            if (idea.Id == _expandedId) bar.IsVisible = true;

            var stack = new VerticalStackLayout
            {
                Spacing = 0,
                Children = { head, bar },
            };

            var card = new Border
            {
                Stroke = CardStroke,
                StrokeThickness = 1,
                BackgroundColor = Colors.White,
                Padding = new Thickness(8),
                StrokeShape = new RoundRectangle { CornerRadius = 8 },
                Content = stack,
            };

            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => ToggleExpand(idea.Id);
            card.GestureRecognizers.Add(tap);

            // 拖拽源。Android 上 DragGestureRecognizer 天然由长按触发，
            // 所以「长按拖动 = 换格」是系统行为，不需要额外判断长按时长。
            var drag = new DragGestureRecognizer();
            drag.DragStarting += (_, e) =>
            {
                try { e.Data.Properties["ideaId"] = idea.Id; }
                catch { /* 个别平台不支持 Properties，拖不起来就拖不起来，不影响点 */ }
            };
            card.GestureRecognizers.Add(drag);

            return card;
        }

        /// <summary>卡片第二行：已落实显示"已落实 · 日期"，有成果再挂一句。</summary>
        private static string MetaText(LocalIdea idea)
        {
            long at = idea.Done && idea.DoneAt > 0 ? idea.DoneAt : idea.CreatedAt;
            string day = Period.DayStartOf(at).ToString("MM-dd");

            // 成果只在落实之后才算成果（和想法库一个口径）：
            // 没落实就贴了链接的，不该在格子里冒充"已完成"。
            if (idea.Done)
                return string.IsNullOrEmpty(idea.ArtifactUri)
                    ? "已落实 · " + day
                    : "已落实 · " + day + " · 有成果";

            return day;
        }

        /// <summary>
        /// 展开后才出现的一行：留成果 / 改成果 / 删除。
        ///
        /// 为什么这两个藏起来、而「已落实」放在外面：
        /// 已落实一天点 1~3 次，留成果一条想法一生一次，删除一周 0~1 次。
        /// 按频率分配显性程度 —— 高频用零成本的动作，低频藏一层，误触代价也就不存在了。
        /// </summary>
        private View BuildActionBar(LocalIdea idea)
        {
            var bar = new HorizontalStackLayout
            {
                Spacing = 14,
                HorizontalOptions = LayoutOptions.End,
                IsVisible = false,
                Margin = new Thickness(0, 6, 0, 0),
            };

            bool hasArtifact = !string.IsNullOrEmpty(idea.ArtifactUri);

            if (hasArtifact)
            {
                var open = new Label { Text = "打开", FontSize = 10, TextColor = LinkBlue };
                var openTap = new TapGestureRecognizer();
                openTap.Tapped += async (_, _) => await OpenArtifactAsync(idea);
                open.GestureRecognizers.Add(openTap);
                bar.Children.Add(open);

                var edit = new Label { Text = "改", FontSize = 10, TextColor = Muted };
                var editTap = new TapGestureRecognizer();
                editTap.Tapped += async (_, _) => await EditArtifactAsync(idea);
                edit.GestureRecognizers.Add(editTap);
                bar.Children.Add(edit);
            }
            else
            {
                var add = new Label { Text = "留成果", FontSize = 10, TextColor = LinkBlue };
                var addTap = new TapGestureRecognizer();
                addTap.Tapped += async (_, _) => await EditArtifactAsync(idea);
                add.GestureRecognizers.Add(addTap);
                bar.Children.Add(add);
            }

            var del = new Label
            {
                Text = "删除",
                FontSize = 10,
                TextColor = DangerRed,
            };
            var delTap = new TapGestureRecognizer();
            delTap.Tapped += async (_, _) => await DeleteAsync(idea);
            del.GestureRecognizers.Add(delTap);
            bar.Children.Add(del);

            return bar;
        }

        /// <summary>一屏只展开一条：第二条点开时第一条自动收起。</summary>
        private void ToggleExpand(string id)
        {
            _expandedId = _expandedId == id ? null : id;

            foreach (var (key, bar) in _actionBars)
                bar.IsVisible = key == _expandedId;
        }

        // ============================================================
        // 拖放
        // ============================================================

        private void AttachDrop(View cell, bool urgent, bool important)
        {
            var drop = new DropGestureRecognizer { AllowDrop = true };

            drop.DragOver += (_, e) =>
            {
                // 有东西在头顶时给一格淡描边，不然不知道松手会落在哪
                if (cell is Grid g) g.Opacity = 0.72;
                e.AcceptedOperation = DataPackageOperation.Copy;
            };

            drop.DragLeave += (_, _) =>
            {
                if (cell is Grid g) g.Opacity = 1;
            };

            drop.Drop += async (_, e) =>
            {
                if (cell is Grid g) g.Opacity = 1;
                await OnDroppedAsync(e, urgent, important);
            };

            cell.GestureRecognizers.Add(drop);
        }

        private async Task OnDroppedAsync(DropEventArgs e, bool urgent, bool important)
        {
            string? id = ReadDraggedId(e);
            if (string.IsNullOrEmpty(id)) return;

            var idea = _active.FirstOrDefault(x => x.Id == id);
            if (idea is null) return;

            if (idea.Urgent == urgent && idea.Important == important) return;

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            idea.Urgent = urgent;
            idea.Important = important;
            idea.TouchedAt = now;
            idea.UpdatedAt = now;
            idea.Synced = false;

            await _ideas.UpdateAsync(idea);
            await LoadAsync();

            ShowStatus($"已放到「{QuadrantName(urgent, important)}」。");
            _ = _ideaSync.SyncAsync();
        }

        /// <summary>
        /// 从拖放数据里取想法 id。
        ///
        /// 各平台给的容器不一样（Android 走 Properties，iOS 走 NSItemProvider），
        /// 这里只认 Properties —— 目标是 Android，其它平台取不到就静默不动，
        /// 不影响点击和勾选。
        /// </summary>
        private static string? ReadDraggedId(DropEventArgs e)
        {
            try
            {
                if (e.Data?.Properties is not null &&
                    e.Data.Properties.TryGetValue("ideaId", out object? v) &&
                    v is string s)
                {
                    return s;
                }
            }
            catch
            {
                // 平台不支持就算了
            }

            return null;
        }

        private static string QuadrantName(bool urgent, bool important) =>
            (urgent, important) switch
            {
                (true, true) => "Ⅰ 重要且紧急",
                (false, true) => "Ⅱ 重要不紧急",
                (true, false) => "Ⅲ 不重要且紧急",
                (false, false) => "Ⅳ 不重要不紧急",
            };

        // ============================================================
        // 已落实 / 成果 / 删除
        // ============================================================

        private async Task ToggleDoneAsync(LocalIdea idea)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            idea.Done = !idea.Done;
            idea.DoneAt = idea.Done ? now : 0;
            idea.UpdatedAt = now;
            idea.TouchedAt = now;
            idea.Synced = false;

            await _ideas.UpdateAsync(idea);
            await LoadAsync();

            // 落实后不再弹任何框问"要不要留成果"：那会把 1 秒的动作变成 10 秒。
            // 只在状态行里提一句，想要的人自己点卡片。
            if (!idea.Done)
                ShowStatus("已取消落实标记。");
            else if (!string.IsNullOrEmpty(idea.ArtifactUri))
                ShowStatus("已落实。卡片上的「有成果」可以直达成果。");
            else
                ShowStatus("已落实。今天留在格子里划线，明天就不占位置了 —— 有成果的话点卡片留一条链接。");

            _ = _ideaSync.SyncAsync();
        }

        /// <summary>
        /// 打开成果：obsidian:// 在装了 Obsidian 的机器上直达那篇笔记；
        /// 打不开（没装 app / 路径失效）就把链接复制到剪贴板，不弹错误框。
        /// 和想法库同一套兜底 —— 跳转失败是常态，不是错误。
        /// </summary>
        private async Task OpenArtifactAsync(LocalIdea idea)
        {
            if (string.IsNullOrEmpty(idea.ArtifactUri)) return;

            bool opened = false;
            try
            {
                opened = await Launcher.TryOpenAsync(idea.ArtifactUri);
            }
            catch
            {
                // URI 格式坏 / 平台不认这个 scheme：按"没打开"处理，走复制兜底
            }

            if (!opened)
            {
                await Clipboard.SetTextAsync(idea.ArtifactUri);
                ShowStatus("没能直接打开，链接已复制 —— 粘到浏览器或 Obsidian 里。");
            }
        }

        private async Task EditArtifactAsync(LocalIdea idea)
        {
            string? uri = await DisplayPromptAsync(
                "成果链接",
                "贴 obsidian:// 链接或文件路径。清空保存 = 移除链接。",
                "保存", "取消",
                placeholder: "obsidian://open?vault=…",
                maxLength: 500,
                keyboard: Keyboard.Url,
                initialValue: idea.ArtifactUri);

            if (uri is null) return;

            uri = uri.Trim();
            if (uri == idea.ArtifactUri) return;

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            idea.ArtifactUri = uri;
            idea.UpdatedAt = now;
            idea.TouchedAt = now;
            idea.Synced = false;

            await _ideas.UpdateAsync(idea);
            await LoadAsync();

            ShowStatus(uri.Length == 0 ? "已移除成果链接。" : "已保存成果链接。");
            _ = _ideaSync.SyncAsync();
        }

        private async Task DeleteAsync(LocalIdea idea)
        {
            // 不弹确认框：确认框每次都要点，误删却只有百分之一。
            // 删除后给一条可撤销提示，代价落在真的手滑那一次。
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            bool wasSynced = idea.Synced;

            if (wasSynced)
            {
                idea.Deleted = true;
                idea.UpdatedAt = now;
                idea.Synced = false;
                await _ideas.UpdateAsync(idea);
            }
            else
            {
                await _ideas.DeleteAsync(idea.Id);
            }

            await LoadAsync();
            ShowUndo(idea, wasSynced);
        }

        /// <summary>
        /// 撤销条：直接在状态行上做，不另造控件。
        ///
        /// 状态行本来就是这页唯一的"刚才发生了什么"的位置，撤销放这儿不用额外学。
        /// 8 秒后自动恢复成普通状态；期间点一下「撤销」就把它放回来。
        /// </summary>
        private void ShowUndo(LocalIdea idea, bool wasSynced)
        {
            StatusLabel.IsVisible = true;
            StatusLabel.Text = "已删掉。「撤销」";

            var tap = new TapGestureRecognizer();
            tap.Tapped += async (_, _) =>
            {
                idea.Deleted = false;
                idea.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                idea.Synced = false;

                if (wasSynced) await _ideas.UpdateAsync(idea);
                else await _ideas.InsertAsync(idea);

                await LoadAsync();
                ShowStatus("已放回来。");
                _ = _ideaSync.SyncAsync();
            };

            StatusLabel.GestureRecognizers.Clear();
            StatusLabel.GestureRecognizers.Add(tap);

            _ = Task.Delay(8000).ContinueWith(_ =>
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    if (StatusLabel.Text?.StartsWith("已删掉") == true)
                        StatusLabel.IsVisible = false;
                }));
        }

        private void ShowStatus(string text)
        {
            StatusLabel.GestureRecognizers.Clear();
            StatusLabel.IsVisible = true;
            StatusLabel.Text = text;
        }

        // ============================================================
        // 折叠带（计划 / 已落实）—— 原地展开，不弹层
        // ============================================================

        private void OnPlanBandTapped(object? sender, TappedEventArgs e)
            => SetBand(_openBand == Band.Plan ? Band.None : Band.Plan);

        private void OnDoneBandTapped(object? sender, TappedEventArgs e)
            => SetBand(_openBand == Band.Done ? Band.None : Band.Done);

        private void SetBand(Band band)
        {
            _openBand = band;

            if (band != Band.None)
            {
                // 展开即重画：数据可能在这期间变过（勾了、转了目标、删了）
                RenderBand();
            }

            ApplyFold();
        }

        /// <summary>
        /// 折叠 = 四象限让位给列表，两者共用同一块高度。
        /// 用 IsVisible 切换而不是挪动布局，这样格子高度不变、列表也能拿到整屏。
        /// </summary>
        private void ApplyFold()
        {
            bool quad = _openBand == Band.None;

            QuadGrid.IsVisible = quad;
            PlanScroll.IsVisible = _openBand == Band.Plan;
            DoneScroll.IsVisible = _openBand == Band.Done;

            PlanChevron.Text = _openBand == Band.Plan ? "⌄" : "›";
            DoneChevron.Text = _openBand == Band.Done ? "⌄" : "›";

            // 展开的那条给一点底色：不然两条长得一样，看不出现在在看哪条
            PlanBand.BackgroundColor = _openBand == Band.Plan ? Color.FromArgb("#F1EFE8") : Colors.Transparent;
            DoneBand.BackgroundColor = _openBand == Band.Done ? Color.FromArgb("#F1EFE8") : Colors.Transparent;

            HeaderLabel.Text = quad
                ? "点一下卡片展开「留成果 / 删除」；长按拖到别的格子 = 改它有多着急、多重要。"
                : "再点一次底部这条标题，回到四象限。";
        }

        private void RenderBand()
        {
            if (_openBand == Band.Plan)
                Fill(PlanBody, _plans, isPlanBand: true, emptyHint: "没有还没形状的计划。");
            else if (_openBand == Band.Done)
                Fill(DoneBody, _done, isPlanBand: false, emptyHint: "还没有落实过的想法。");
        }

        private void Fill(VerticalStackLayout body, List<LocalIdea> items, bool isPlanBand, string emptyHint)
        {
            body.Children.Clear();

            var list = items
                .OrderByDescending(x => isPlanBand ? x.CreatedAt : (x.DoneAt > 0 ? x.DoneAt : x.CreatedAt))
                .ToList();

            if (list.Count == 0)
            {
                body.Children.Add(new Label
                {
                    Text = emptyHint,
                    FontSize = 13,
                    TextColor = Muted,
                    Margin = new Thickness(2, 6, 0, 0),
                });
                return;
            }

            foreach (var idea in list)
                body.Children.Add(BuildListCard(idea, isPlanBand));
        }

        /// <summary>
        /// 折叠带里的卡片：跟想法库同款（细描边 + 左正文右按钮）。
        ///
        /// 为什么抄那个样式、不自己设计：同一批数据在两页长成两个样子，
        /// 会让人以为是两种东西。这页只是换了个摆放位置，不是换了个物种。
        /// </summary>
        private View BuildListCard(LocalIdea idea, bool isPlanBand)
        {
            var info = new VerticalStackLayout { Spacing = 4 };

            var body = new Label
            {
                Text = idea.Text ?? string.Empty,
                FontSize = 14,
                LineBreakMode = LineBreakMode.WordWrap,
                TextColor = isPlanBand ? DeepInk : DoneInk,
            };

            if (!isPlanBand) body.TextDecorations = TextDecorations.Strikethrough;

            info.Children.Add(body);

            string day = Period.DayStartOf(isPlanBand
                    ? idea.CreatedAt
                    : (idea.DoneAt > 0 ? idea.DoneAt : idea.CreatedAt))
                .ToString("MM-dd");

            string meta = isPlanBand
                ? day + (string.IsNullOrEmpty(idea.GoalId) ? string.Empty : " · 已转目标")
                : "已落实 · " + day;

            info.Children.Add(new Label
            {
                Text = meta,
                FontSize = 11,
                TextColor = isPlanBand ? IdeaKinds.ColorOf(idea.Kind) : Faint,
            });

            var actions = new VerticalStackLayout { Spacing = 6, VerticalOptions = LayoutOptions.Start };

            if (isPlanBand)
            {
                if (string.IsNullOrEmpty(idea.GoalId))
                {
                    var convert = SmallButton("转成目标", "#EEEDFE", "#534AB7");
                    convert.Clicked += async (_, _) => await ConvertToGoalAsync(idea);
                    actions.Children.Add(convert);

                    // 第二个出口：已经想清楚能动手了 → 进四象限。
                    // 只给出口不给入口 —— 反方向（能干的 → 计划）没有入口，那是把答应自己的事降级。
                    var canDo = new Label
                    {
                        Text = "能动手 ›",
                        FontSize = 11,
                        TextColor = Muted,
                        HorizontalOptions = LayoutOptions.End,
                    };
                    var canDoTap = new TapGestureRecognizer();
                    canDoTap.Tapped += async (_, _) => await PromoteToCanDoAsync(idea);
                    canDo.GestureRecognizers.Add(canDoTap);
                    actions.Children.Add(canDo);
                }
            }
            else if (!string.IsNullOrEmpty(idea.ArtifactUri))
            {
                // 落实过又留了成果 —— 这里就是那个"导航到成果"的入口
                var open = SmallButton("📄 成果", "#E8F1EA", "#2F6B3C");
                open.Clicked += async (_, _) => await OpenArtifactAsync(idea);
                actions.Children.Add(open);
            }
            else
            {
                var add = new Label
                {
                    Text = "留成果 ›",
                    FontSize = 11,
                    TextColor = LinkBlue,
                    HorizontalOptions = LayoutOptions.End,
                };
                var addTap = new TapGestureRecognizer();
                addTap.Tapped += async (_, _) => await EditArtifactAsync(idea);
                add.GestureRecognizers.Add(addTap);
                actions.Children.Add(add);
            }

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
                Stroke = CardStroke,
                StrokeThickness = 1,
                Padding = new Thickness(14, 10),
                Content = grid,
            };
        }

        private static Button SmallButton(string text, string background, string foreground) => new()
        {
            Text = text,
            FontSize = 11,
            HeightRequest = 30,
            Padding = new Thickness(10, 0),
            CornerRadius = 8,
            BackgroundColor = Color.FromArgb(background),
            TextColor = Color.FromArgb(foreground),
        };

        /// <summary>
        /// 计划 → 能干的。**只允许这一个方向**：想清楚了能进四象限；
        /// 反方向（能干的 → 计划）在界面上没有入口 —— 那是把答应自己的事降级，等于逃避。
        /// </summary>
        private async Task PromoteToCanDoAsync(LocalIdea idea)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            idea.PlanKind = 0;
            idea.TouchedAt = now;
            idea.UpdatedAt = now;
            idea.Synced = false;

            await _ideas.UpdateAsync(idea);
            await LoadAsync();

            // 刚升级的那条落在 Ⅱ，不额外标记 —— 它自己会浮到格子上面（按最后被碰过的时间排）。
            ShowStatus("已放进四象限（Ⅱ 重要不紧急）。");
            _ = _ideaSync.SyncAsync();
        }

        /// <summary>
        /// 转成目标。和想法库同一套做法：**预填**原文让人改成一句能判断做没做完的话，
        /// 不自动生成目标名 —— 想法原文往往带着情绪，直接当目标名永远不会有"做完"的一天。
        /// </summary>
        private async Task ConvertToGoalAsync(LocalIdea idea)
        {
            string? title = await TextEditorPage.ShowAsync("转成目标",
                initial: idea.Text,
                placeholder: "改成一句能判断「做没做完」的话",
                compact: false,
                maxLength: 120);

            if (title is null) return;
            if (title.Length == 0) return;

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var goals = await _goals.GetByRangeAsync(0, long.MaxValue);
            bool hasCurrent = goals.Any(g => g.IsCurrent);

            var goal = new LocalGoal
            {
                Id = Guid.NewGuid().ToString("N"),
                Title = title,
                Status = 0,
                NextStep = string.Empty,
                SourceIdeaId = idea.Id,
                CurrentStepId = string.Empty,
                IsCurrent = !hasCurrent,
                StartedAt = now,
                CreatedAt = now,
                UpdatedAt = now,
                Deleted = false,
                Synced = false,
                SyncedAt = 0,
            };

            await _goals.InsertAsync(goal);

            idea.GoalId = goal.Id;
            idea.UpdatedAt = now;
            idea.TouchedAt = now;
            idea.Synced = false;
            await _ideas.UpdateAsync(idea);

            await LoadAsync();

            ShowStatus($"已建目标「{title}」—— 去目标页给它配时间片，想法才算真的开始。");

            _ = _goalSync.SyncAsync();
            _ = _ideaSync.SyncAsync();
        }

        private async void OnRefreshClicked(object? sender, EventArgs e)
        {
            await LoadAsync();
            ShowStatus("已刷新。");
        }
    }
}
