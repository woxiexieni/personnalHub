using System.ComponentModel;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Layouts;
using PersonalHub.Client.Data;
using PersonalHub.Client.Services;

namespace PersonalHub.Client
{
    /// <summary>
    /// 事件卡（原"事件反思"）—— R7 改版。
    ///
    /// 老版是 8 个必填字段的表单，结果谁都不填：复盘的入口太贵，不是不想复盘。
    /// 新流程只有两步：
    ///   1. 想到什么打什么（一段乱糟糟的话）→ AI 拆成最多 3 张事件卡草稿；
    ///   2. 逐张确认保存。草稿的字段都可以改，AI 只是起草人，签收的是用户。
    ///
    /// 数据仍然落在老的 ThingRecord 表（Synced=false → RecordSyncService 上传），
    /// 只是录入路径换了，老数据照常显示。
    ///
    /// AI 挂了 / 没网：原文可以先落一条"待整理"卡，不丢字。
    /// </summary>
    public partial class RecordDothingPage : ContentPage
    {
        private readonly LocalRecordStore<ThingRecord> _store;
        private readonly RecordSyncService<ThingRecord> _sync;
        private readonly LocalRecordStore<LocalPattern> _patterns;
        private readonly LocalRecordStore<LocalProblem> _problems;
        private readonly AiAssistService _ai;

        /// <summary>事件标签（顺序即 Picker 顺序）。词表与问题卡共用 RecordTags.All——不另起一套。</summary>
        private static readonly string[] Tags = RecordTags.All;

        /// <summary>事发时情绪。</summary>
        private static readonly string[] Emotions =
        {
            "平静", "焦虑", "烦躁", "激动", "委屈", "兴奋", "失落",
        };

        private static readonly Color PendingColor = Color.FromArgb("#D97706");
        private static readonly Color SyncedColor = Color.FromArgb("#8A8A8E");

        /// <summary>本次 AI 返回的路由（事件序号 → 问题/模式），存卡时写进 Reflection 留痕。</summary>
        private List<AiEventRoute> _routes = new();

        /// <summary>发给 AI 的原文。AI 失败时"先存原文"用的就是它。</summary>
        private string _lastRaw = string.Empty;

        /// <summary>
        /// 事件卡库 → 本页的交接：点某张待整理卡的「重新交给 AI」时由 EventListPage 塞进来，
        /// OnAppearing 消费一次（填回原文 + 删掉那张待整理卡，防重复堆积）。
        /// </summary>
        public static (string Id, string Raw)? PendingRefill { get; set; }

        /// <summary>
        /// 时间块页 → 本页的交接：评价攒够阈值后点提示过来，带一段预填原文。
        /// 预填的是「一段话」，不是一个表单 —— 用户可以在这段话上接着写，也可以直接交给 AI。
        /// </summary>
        public static string? PendingSeed { get; set; }

        public RecordDothingPage(
            LocalRecordStore<ThingRecord> store,
            RecordSyncService<ThingRecord> sync,
            LocalRecordStore<LocalPattern> patterns,
            LocalRecordStore<LocalProblem> problems,
            AiAssistService ai)
        {
            InitializeComponent();
            _store = store;
            _sync = sync;
            _patterns = patterns;
            _problems = problems;
            _ai = ai;

            var now = DateTime.Now;
            EventDatePicker.Date = now.Date;
            EventTimePicker.Time = now.TimeOfDay;
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();

            // 时间块页 → 「评价攒够了」带过来的预填原文。优先消费，和下面的 refill 互斥。
            if (PendingSeed is { Length: > 0 } seed)
            {
                PendingSeed = null;
                RawInput.Text = seed;
                StatusLabel.Text = "带着这段时间的记录过来的 —— 在上面补两句，或者直接交给 AI。";
            }
            // 事件卡库 → 「重新交给 AI」的交接：原文填回输入框，删掉那张待整理卡
            else if (PendingRefill is { } refill)
            {
                PendingRefill = null;
                RawInput.Text = refill.Raw;
                try
                {
                    await _store.DeleteAsync(refill.Id);
                }
                catch
                {
                    // 删不掉就留着，不影响重新整理
                }
                StatusLabel.Text = "原文已填回输入框（那张待整理卡已移除）。点「交给 AI 整理」再试一次。";
            }

            // 先展示本地数据（秒开，不等网络），再后台试一次同步
            await RefreshLocalAsync();

            _ = SyncInBackgroundAsync();
        }

        // ============ 交给 AI 整理 ============
        private async void OnExtractClicked(object sender, EventArgs e)
        {
            string raw = RawInput.Text?.Trim() ?? string.Empty;
            if (raw.Length < 10)
            {
                StatusLabel.Text = "多打两句——太短了整理不出东西。";
                return;
            }

            ExtractBtn.IsEnabled = false;
            SaveRawBtn.IsVisible = false;
            StatusLabel.Text = "AI 整理中……";
            _lastRaw = raw;

            try
            {
                // 已知清单随请求带给服务端（服务端无状态，v1 不存问题卡/模式）。
                // 问题卡功能还没做，先传空；模式取活跃的那些。
                var patternList = (await _patterns.GetAllAsync())
                    .Where(p => !p.Deleted && p.Status == 0 && !string.IsNullOrWhiteSpace(p.Title))
                    .Select(p => (p.Id, p.Title))
                    .ToList();

                // 路由提示里要显示模式名，留一份 id → 标题
                _patternTitles = new Dictionary<string, string>();
                foreach (var (id, title) in patternList)
                {
                    _patternTitles[id] = title;
                }

                // 进行中的问题卡随请求带给 AI（服务端无状态，只校验不存储）。
                // 已解决/放弃的不送：AI 不用再对照，清单也瘦 —— 成本涨不上去的关键。
                var problemList = (await _problems.GetAllAsync())
                    .Where(p => !p.Deleted && p.Status == 0 && !string.IsNullOrWhiteSpace(p.Title))
                    .Select(p => (p.Id, p.Title))
                    .ToList();

                // 路由提示里要显示问题卡名，留一份 id → 标题
                _problemTitles = new Dictionary<string, string>();
                foreach (var (id, title) in problemList)
                {
                    _problemTitles[id] = title;
                }

                var result = await _ai.ExtractEventsAsync(
                    raw,
                    problemList,
                    patternList,
                    Tags,
                    Emotions);

                if (!result.Ok || result.Events.Count == 0)
                {
                    StatusLabel.Text = result.Message;
                    SaveRawBtn.IsVisible = true;   // 给条后路：原文先落本地
                    return;
                }

                _routes = result.Routes;
                DraftCards.Children.Clear();

                int n = 1;
                foreach (var draft in result.Events)
                {
                    DraftCards.Children.Add(BuildDraftCard(draft, n++));
                }

                StatusLabel.Text = $"整理出 {result.Events.Count} 张卡。AI 起的稿，你逐张看一眼再存。";
                RawInput.Text = string.Empty;
            }
            catch (Exception ex)
            {
                StatusLabel.Text = "整理失败：" + ex.Message;
                SaveRawBtn.IsVisible = true;
            }
            finally
            {
                ExtractBtn.IsEnabled = true;
            }
        }

        // ============ AI 失败的兜底：原文先落本地 ============
        private async void OnSaveRawClicked(object sender, EventArgs e)
        {
            if (_lastRaw.Length < 10)
            {
                StatusLabel.Text = "没有可存的内容。";
                return;
            }

            SaveRawBtn.IsEnabled = false;
            try
            {
                await SaveThingAsync(new ThingRecord
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Title = "（待整理）",
                    Tag = "其他",
                    Emotion = string.Empty,          // 事实未知就留空，不编
                    OccurredAt = OccurredAtMs(),
                    ObjectiveDescription = _lastRaw,
                    CreatedTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Synced = false,
                    SyncedAt = 0,
                });

                StatusLabel.Text = "原文已存为待整理卡。联网后可在 AI 里重新整理。";
                RawInput.Text = string.Empty;
                _lastRaw = string.Empty;
                SaveRawBtn.IsVisible = false;

                await RefreshLocalAsync();
            }
            catch (Exception ex)
            {
                StatusLabel.Text = "保存失败：" + ex.Message;
            }
            finally
            {
                SaveRawBtn.IsEnabled = true;
            }
        }

        // ============ 草稿卡 UI ============
        private Border BuildDraftCard(AiEventDraft draft, int n)
        {
            var titleEditor = new Editor { Text = draft.Title, FontSize = 15, AutoSize = EditorAutoSizeOption.TextChanges };
            var factsEditor = new Editor
            {
                Text = draft.Facts,
                FontSize = 14,
                AutoSize = EditorAutoSizeOption.TextChanges,
                Placeholder = "客观事实（AI 搬的原文，可改）",
            };
            var thoughtEditor = new Editor
            {
                Text = draft.InitialThought,
                FontSize = 13,
                AutoSize = EditorAutoSizeOption.TextChanges,
                Placeholder = "当时的想法（可空，可改）",
            };
            var actionEditor = new Editor
            {
                Text = draft.Action,
                FontSize = 13,
                AutoSize = EditorAutoSizeOption.TextChanges,
                Placeholder = "当时做了什么（可空，可改）",
            };
            var resultEditor = new Editor
            {
                Text = draft.Result,
                FontSize = 13,
                AutoSize = EditorAutoSizeOption.TextChanges,
                Placeholder = "结果（可空，可改）",
            };

            // 情绪/标签：胶囊点选行（点一下选中，占的地方只有一行），替代原来的两个大下拉框
            var emotionState = new int[] { Array.IndexOf(Emotions, draft.Emotion) };
            var emotionRow = BuildChipRow(Emotions, emotionState);
            var tagState = new int[] { Array.IndexOf(Tags, draft.Tag) };
            var tagRow = BuildChipRow(Tags, tagState);

            // AI 的追问 + 你的回答（回答会存进"复盘反思"，这就是评估校验的那一格）
            var answerEditor = new Editor
            {
                FontSize = 13,
                AutoSize = EditorAutoSizeOption.TextChanges,
                Placeholder = "回答一句（可空）",
            };

            var saveBtn = new Button
            {
                Text = "保存这张",
                HorizontalOptions = LayoutOptions.Fill,
            };
            var rejectBtn = new Button
            {
                Text = "不要这张",
                HorizontalOptions = LayoutOptions.Fill,
            };

            // 保存/拒绝按钮的闭包里要移除整张卡；card 在方法尾部才构造，这里先占位声明
            Border card = null!;
            rejectBtn.Clicked += (_, _) => DraftCards.Children.Remove(card);

            // —— 折叠区：想法/行动/结果/AI 提示，默认收起 ——
            // AI 大部分时候写得对，默认只露"标题 + 事实 + 情绪/标签"，扫两行点保存就完事。
            var detailPanel = new VerticalStackLayout { Spacing = 8, IsVisible = false };
            var foldLabel = new Label
            {
                Text = "▸ 想法 / 行动 / 结果 / AI 提示（点开改）",
                FontSize = 13,
                TextColor = Color.FromArgb("#6E6E6E"),
            };
            var foldTap = new TapGestureRecognizer();
            foldTap.Tapped += (_, _) =>
            {
                bool show = !detailPanel.IsVisible;
                detailPanel.IsVisible = show;
                foldLabel.Text = (show ? "▾" : "▸") + " 想法 / 行动 / 结果 / AI 提示（点开改）";
            };
            foldLabel.GestureRecognizers.Add(foldTap);

            var rows = new List<IView>
            {
                new Label
                {
                    Text = $"草稿 {n}（AI 起的，可改）",
                    FontSize = 13,
                    TextColor = Color.FromArgb("#6E6E6E"),
                },
                titleEditor,
                factsEditor,
                new Label { Text = "情绪", FontSize = 12, TextColor = Color.FromArgb("#6E6E6E") },
                emotionRow,
                new Label { Text = "标签", FontSize = 12, TextColor = Color.FromArgb("#6E6E6E") },
                tagRow,
                foldLabel,
                detailPanel,
            };

            // AI 对这条事件的路由进折叠区：命中问题卡 → 保存时自动挂证据；
            // 新模式苗头 → 文字提示 + 一键「为它建一张问题卡」。
            detailPanel.Children.Add(BuildRouteArea(draft));
            detailPanel.Children.Add(thoughtEditor);
            detailPanel.Children.Add(actionEditor);
            detailPanel.Children.Add(resultEditor);

            if (!string.IsNullOrWhiteSpace(draft.Question))
            {
                rows.Add(new Label
                {
                    Text = "AI 想问你：" + draft.Question,
                    FontSize = 13,
                    FontAttributes = FontAttributes.Bold,
                    LineBreakMode = LineBreakMode.WordWrap,
                });
                rows.Add(answerEditor);
            }

            rows.Add(saveBtn);
            rows.Add(rejectBtn);

            saveBtn.Clicked += async (_, _) =>
            {
                if (emotionState[0] < 0 || tagState[0] < 0)
                {
                    StatusLabel.Text = "这张卡的情绪/标签还没选（点胶囊选一下）。";
                    return;
                }

                saveBtn.IsEnabled = false;
                try
                {
                    string reflection = ComposeReflection(draft.Question, answerEditor.Text);

                    await SaveThingAsync(new ThingRecord
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Title = string.IsNullOrWhiteSpace(titleEditor.Text) ? draft.Title : titleEditor.Text.Trim(),
                        Tag = Tags[tagState[0]],
                        Emotion = Emotions[emotionState[0]],
                        OccurredAt = OccurredAtMs(),
                        ObjectiveDescription = factsEditor.Text?.Trim() ?? draft.Facts,
                        InitialThought = thoughtEditor.Text?.Trim() ?? string.Empty,
                        ActionTaken = actionEditor.Text?.Trim() ?? string.Empty,
                        FinalResult = resultEditor.Text?.Trim() ?? string.Empty,
                        Reflection = reflection,
                        ActionPlan = string.Empty,   // 留给"问题卡"那一层，不在这里编计划
                        // AI 命中的问题卡：保存即挂证据（想取消就去问题卡列表里改）
                        ProblemId = _routes.FirstOrDefault(r => r.EventIndex == draft.Index)?.MatchProblemId ?? string.Empty,
                        CreatedTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        Synced = false,
                        SyncedAt = 0,
                    });

                    // 确认区拿掉这张卡（card 引用在下面闭包里）
                    DraftCards.Children.Remove(card);
                    StatusLabel.Text = "已保存。";

                    await RefreshLocalAsync();
                    _ = SyncInBackgroundAsync();
                }
                catch (Exception ex)
                {
                    StatusLabel.Text = "保存失败：" + ex.Message;
                    saveBtn.IsEnabled = true;
                }
            };

            var content = new VerticalStackLayout { Spacing = 8 };
            foreach (var row in rows)
            {
                content.Children.Add(row);
            }

            card = new Border
            {
                StrokeShape = new RoundRectangle { CornerRadius = 12 },
                Stroke = Color.FromArgb("#8E8E93"),
                StrokeThickness = 1,
                Padding = 14,
                // Windows 上窗口宽，卡片拉满 1000+ 像素一行字读不过来；限个宽
                MaximumWidthRequest = 720,
                Content = content,
            };
            return card;
        }

        /// <summary>
        /// 胶囊行：横向流式排列的小按钮，点中即选（选中下标记在 selected[0]）。
        /// 用数组存下标是因为闭包捕获局部变量会被 copy，数组是引用。
        /// </summary>
        private static FlexLayout BuildChipRow(string[] options, int[] selected)
        {
            var flex = new FlexLayout { Direction = FlexDirection.Row, Wrap = FlexWrap.Wrap };
            for (int i = 0; i < options.Length; i++)
            {
                int idx = i;
                var btn = new Button
                {
                    Text = options[idx],
                    FontSize = 12,
                    Padding = new Thickness(10, 2),
                    CornerRadius = 12,
                    MinimumHeightRequest = 30,
                    Margin = new Thickness(0, 0, 8, 8),
                };
                btn.Clicked += (_, _) =>
                {
                    selected[0] = idx;
                    RefreshChipRow(flex, options, selected);
                };
                flex.Children.Add(btn);
            }
            RefreshChipRow(flex, options, selected);
            return flex;
        }

        /// <summary>重刷一行胶囊的选中态（选中 = 实心强调色，未选中 = 浅底）。</summary>
        private static void RefreshChipRow(FlexLayout flex, string[] options, int[] selected)
        {
            bool dark = Application.Current?.RequestedTheme == AppTheme.Dark;
            int k = 0;
            foreach (var child in flex.Children)
            {
                if (child is Button b)
                {
                    bool on = k == selected[0];
                    b.BackgroundColor = on
                        ? Color.FromArgb("#4A6FA5")
                        : (dark ? Color.FromArgb("#3A3A3C") : Color.FromArgb("#E5E5EA"));
                    b.TextColor = on ? Colors.White : (dark ? Colors.White : Colors.Black);
                    k++;
                }
            }
        }

        /// <summary>
        /// 事件卡 → 问题/模式 的路由区（真关联，不再是纯文字）：
        ///   命中已有问题卡 → 保存即挂证据（ProblemId 写进 ThingRecord）；
        ///   命中已有模式   → 文字提示（模式证据关联 v2）；
        ///   新模式苗头     → 提示 + 一键「为它建一张问题卡」（标题/事实带过去，判据仍要你写——闸 1）。
        /// </summary>
        private VerticalStackLayout BuildRouteArea(AiEventDraft draft)
        {
            var area = new VerticalStackLayout { Spacing = 6 };
            var route = _routes.FirstOrDefault(r => r.EventIndex == draft.Index);
            if (route is null) return area;

            if (!string.IsNullOrEmpty(route.MatchProblemId))
            {
                string title = _problemTitles.TryGetValue(route.MatchProblemId, out var t) ? t : route.MatchProblemId;
                area.Children.Add(new Label
                {
                    Text = $"AI 提示：命中问题卡「{title}」——保存后这张卡会自动挂为它的证据。",
                    FontSize = 12,
                    TextColor = Color.FromArgb("#B45309"),
                    LineBreakMode = LineBreakMode.WordWrap,
                });

                // AI 建议试法：你判断行不行（判断比设计容易），行就一键填进那张卡
                if (!string.IsNullOrWhiteSpace(route.SuggestedAttempt))
                {
                    area.Children.Add(new Label
                    {
                        Text = "AI 建议试法：" + route.SuggestedAttempt,
                        FontSize = 12,
                        TextColor = Color.FromArgb("#B45309"),
                        LineBreakMode = LineBreakMode.WordWrap,
                    });

                    var adoptBtn = new Button
                    {
                        Text = "采纳为试法",
                        FontSize = 12,
                        Padding = new Thickness(10, 4),
                        HorizontalOptions = LayoutOptions.Start,
                    };
                    adoptBtn.Clicked += async (_, _) =>
                    {
                        var p = (await _problems.GetAllAsync()).FirstOrDefault(x => x.Id == route.MatchProblemId);
                        if (p is null) return;

                        bool replacing = !string.IsNullOrWhiteSpace(p.Attempt) && p.Attempt != route.SuggestedAttempt;
                        p.Attempt = route.SuggestedAttempt;
                        p.AttemptStartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        if (replacing) p.AttemptCount++;
                        p.UpdatedAt = p.AttemptStartedAt;
                        await _problems.UpdateAsync(p);

                        adoptBtn.Text = "已采纳 ✓";
                        adoptBtn.IsEnabled = false;
                    };
                    area.Children.Add(adoptBtn);
                }

                return area;
            }

            if (!string.IsNullOrEmpty(route.MatchPatternId))
            {
                string title = _patternTitles.TryGetValue(route.MatchPatternId, out var t) ? t : route.MatchPatternId;
                area.Children.Add(new Label
                {
                    Text = $"AI 提示：这件事像你已有的模式「{title}」。",
                    FontSize = 12,
                    TextColor = Color.FromArgb("#B45309"),
                    LineBreakMode = LineBreakMode.WordWrap,
                });
                return area;
            }

            if (route.NewPattern is { } np)
            {
                string extra = string.IsNullOrWhiteSpace(np.Reason) ? string.Empty : "——" + np.Reason;
                area.Children.Add(new Label
                {
                    Text = $"AI 提示：出现了新的重复苗头「{np.Title}」{extra}。",
                    FontSize = 12,
                    TextColor = Color.FromArgb("#B45309"),
                    LineBreakMode = LineBreakMode.WordWrap,
                });

                var suggestBtn = new Button
                {
                    Text = "为它建一张问题卡",
                    FontSize = 12,
                    Padding = new Thickness(10, 4),
                    HorizontalOptions = LayoutOptions.Start,
                };
                suggestBtn.Clicked += async (_, _) =>
                {
                    ProblemEditPage.PendingDraft = (np.Title, draft.Facts, string.Empty, string.Empty);
                    await Shell.Current.GoToAsync(RecordCatalog.ProblemEditRoute);
                };
                area.Children.Add(suggestBtn);
            }

            return area;
        }

        /// <summary>追问/回答 + AI 提示，合成一段写进 Reflection（评估校验留痕）。</summary>
        private static string ComposeReflection(string? question, string? answer)
        {
            var parts = new List<string>();
            string a = answer?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(question) && a.Length > 0)
            {
                parts.Add($"追问：{question.Trim()}");
                parts.Add($"回答：{a}");
            }
            return string.Join("\n", parts);
        }

        /// <summary>这批卡共用的时间 → Unix 毫秒（按本地时区，和记账页同一套算法）。</summary>
        private long OccurredAtMs()
        {
            var occurredLocal = DateTime.SpecifyKind(
                EventDatePicker.Date.Date + EventTimePicker.Time, DateTimeKind.Unspecified);
            return new DateTimeOffset(occurredLocal, TimeZoneInfo.Local.GetUtcOffset(occurredLocal))
                .ToUnixTimeMilliseconds();
        }

        private async Task SaveThingAsync(ThingRecord thing)
        {
            await _store.InsertAsync(thing);
        }

        // ============ 模式标题反查（路由提示显示用） ============
        private Dictionary<string, string> _patternTitles = new();

        // ============ 问题卡标题反查（路由提示 + 列表归属显示用） ============
        private Dictionary<string, string> _problemTitles = new();

        // ============ 从本地读今日事件 ============
        private async Task RefreshLocalAsync()
        {
            var local = await _store.GetByLocalDateAsync(DateTime.Now.Date);
            var problems = (await _problems.GetAllAsync())
                .Where(p => !p.Deleted)
                .GroupBy(p => p.Id)
                .ToDictionary(g => g.Key, g => g.First().Title);

            var rows = new List<ThingRow>();
            foreach (var r in local)
            {
                var localTime = DateTimeOffset.FromUnixTimeMilliseconds(r.OccurredAt).LocalDateTime;
                bool pending = string.Equals(r.Title, "（待整理）", StringComparison.Ordinal);

                var detail = new List<string>();
                if (pending)
                {
                    // 待整理卡：正文就是原文，展开就能看见
                    detail.Add(r.ObjectiveDescription ?? string.Empty);
                }
                else
                {
                    void Add(string label, string? v)
                    {
                        if (!string.IsNullOrWhiteSpace(v)) detail.Add(label + "：" + v.Trim());
                    }
                    Add("事实", r.ObjectiveDescription);
                    Add("想法", r.InitialThought);
                    Add("行动", r.ActionTaken);
                    Add("结果", r.FinalResult);
                    Add("复盘", r.Reflection);
                    if (!string.IsNullOrEmpty(r.ProblemId) &&
                        problems.TryGetValue(r.ProblemId, out var problemTitle))
                    {
                        Add("属于问题卡", problemTitle);
                    }
                }

                rows.Add(new ThingRow
                {
                    RecordId = r.Id,
                    Title = string.IsNullOrWhiteSpace(r.Title) ? "(无标题)" : r.Title,
                    EventTime = localTime.ToString("HH:mm"),
                    Tag = string.IsNullOrWhiteSpace(r.Tag) ? "未分类" : r.Tag,
                    SyncText = r.Synced ? "已同步" : "待同步",
                    SyncColor = r.Synced ? SyncedColor : PendingColor,
                    IsPending = pending,
                    RawText = r.ObjectiveDescription ?? string.Empty,
                    Detail = string.Join("\n", detail),
                });
            }

            TodayReflectList.ItemsSource = rows;
        }

        // ============ 今日列表：点行头展开/收起 ============
        private void OnThingTapped(object? sender, TappedEventArgs e)
        {
            if (e.Parameter is ThingRow row)
            {
                row.IsExpanded = !row.IsExpanded;
            }
        }

        // ============ 待整理卡：原文填回输入框重新整理 ============
        /// <summary>跳到事件卡库（全部历史）。</summary>
        private async void OnOpenEventListClicked(object? sender, EventArgs e)
            => await Shell.Current.GoToAsync(RecordCatalog.ThingListRoute);

        private async void OnRefillRawClicked(object? sender, EventArgs e)
        {
            if (sender is not Button btn || btn.BindingContext is not ThingRow row) return;

            RawInput.Text = row.RawText;
            try
            {
                // 原文已收回输入框，这张待整理卡就没存在的必要了（防重复堆积）
                await _store.DeleteAsync(row.RecordId);
            }
            catch
            {
                // 删不掉就留着，不影响重新整理
            }

            await RefreshLocalAsync();
            StatusLabel.Text = "原文已填回上面的输入框。点「交给 AI 整理」再试一次。";
        }

        /// <summary>后台静默同步：不打扰用户，只在成功时刷新列表与状态。</summary>
        private async Task SyncInBackgroundAsync()
        {
            try
            {
                var result = await _sync.SyncAsync();

                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    if (result.UploadedRecords > 0)
                    {
                        await RefreshLocalAsync();
                    }
                });
            }
            catch
            {
                // 后台同步失败不弹错，保持"待同步"状态等下次触发。
            }
        }
    }

    /// <summary>今日事件列表的展示行（只用于界面绑定）。点行头展开明细。</summary>
    public class ThingRow : INotifyPropertyChanged
    {
        public string RecordId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string EventTime { get; set; } = string.Empty;
        public string Tag { get; set; } = string.Empty;
        public string SyncText { get; set; } = string.Empty;
        public Color SyncColor { get; set; } = Colors.Transparent;

        /// <summary>待整理卡（原文兜底落的那张）——展开后有「重新交给 AI」按钮。</summary>
        public bool IsPending { get; set; }

        /// <summary>待整理卡的原文（填回输入框用）。</summary>
        public string RawText { get; set; } = string.Empty;

        /// <summary>展开后显示的正文（事实/想法/行动/结果/复盘 合成的一段）。</summary>
        public string Detail { get; set; } = string.Empty;

        private bool _isExpanded;
        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded == value) return;
                _isExpanded = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Arrow));
            }
        }

        /// <summary>行头箭头：▸ 收起 / ▾ 展开。</summary>
        public string Arrow => _isExpanded ? "▾" : "▸";

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
