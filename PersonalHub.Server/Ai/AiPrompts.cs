using System.Text;
using System.Text.Json;
using Personalhub;
using personalHub.Server.Data;

namespace personalHub.Server.Ai
{
    /// <summary>模型返回且**已通过校验**的一条下一步建议。</summary>
    public sealed record StepOption(string Action, string PieceCode, int Minutes);

    /// <summary>已通过校验的一个模式提议（evidence 是原文回填，不是模型写的）。</summary>
    public sealed record Proposal(
        string Title, string Trigger, string OldResponse, string NewResponse,
        IReadOnlyList<string> Evidence, int Count);

    /// <summary>
    /// 提示词 + **结果校验**。这个文件是整套 AI 接入里唯一重要的地方。
    ///
    /// 核心原则：模型只负责"想"，不负责"事实"。
    ///   - 片 code、时长档位、证据原句，全部由代码生成/回填，模型只能从给定选项里挑。
    ///   - 任何一条不合规就丢掉那一条；全都不合规就当这次没生成（客户端走规则版）。
    ///
    /// 为什么这么严：这个项目的 AI 不是"聊天"，它要把结果直接填进一个真实输入框。
    /// 一个编出来的片 code（"reading"）会让用户点「排进今天」时无声失败，
    /// 而用户以为"AI 帮我排好了"——这比不给建议差得多。
    /// </summary>
    public static class AiPrompts
    {
        /// <summary>目标动作的字数上限。超过就说明它不是一个"今天能做完"的动作。</summary>
        private const int MaxActionLen = 24;

        /// <summary>固定时长档（与客户端 Pieces.Durations 一致）。</summary>
        private static readonly int[] Durations = { 5, 15, 30, 45, 60, 90, 120, 180 };

        // ============================================================
        // A. 提议下一步
        // ============================================================

        public static (string System, string User) NextStep(
            Goal goal,
            IReadOnlyList<Block> blocks30d,
            IReadOnlyList<Idea> planIdeas,
            IReadOnlyList<string> todayPieces)
        {
            const string system = """
                你是一个记录整理助手，不是教练。

                任务：根据材料，提出 1~3 个「今天就能做完」的下一步。

                硬性要求：
                1. action 不超过 20 个字，必须是一个**能判断做没做完**的动作。
                   好："读第 3 章，写 3 条笔记"；坏："多读书"、"提升效率"、"继续努力"。
                2. piece 只能从材料给出的片里选一个 code，不许自己发明。
                3. minutes 只能是 5/15/30/45/60/90/120/180 之一。
                4. 不写理由、不写鼓励、不问问题、不解释。只给动作本身。
                5. 材料里有「上次的下一步」时，优先把它拆小或接着做；材料里有「上次学到的」时，优先按它做。
                6. 材料不足（比如没有任何历史）就返回空数组，不要硬编。

                只输出 JSON，格式：
                {"options":[{"action":"读第 3 章，写 3 条笔记","piece":"learn","minutes":30}]}
                """;

            var sb = new StringBuilder();
            sb.AppendLine($"目标：{goal.Title}");

            if (!string.IsNullOrWhiteSpace(goal.NextStep))
                sb.AppendLine($"上次的下一步（可能没做完）：{goal.NextStep}");

            if (blocks30d.Count == 0)
            {
                sb.AppendLine("最近 30 天这个目标没有任何时间块。");
            }
            else
            {
                sb.AppendLine("最近 30 天这个目标的片（次数 / 分钟）：");
                foreach (var g in blocks30d
                    .Where(b => !string.IsNullOrEmpty(b.PieceCode))
                    .GroupBy(b => b.PieceCode!)
                    .OrderByDescending(g => g.Count())
                    .Take(6))
                {
                    sb.AppendLine($"- {g.Key}：{g.Count()} 次 / {g.Sum(b => Math.Max(0, b.DurationMin))} 分钟");
                }
            }

            var lessons = blocks30d
                .Where(b => !string.IsNullOrWhiteSpace(b.Lesson))
                .OrderByDescending(b => b.OccurredAt)
                .Select(b => b.Lesson!.Trim())
                .Distinct()
                .Take(5)
                .ToList();

            if (lessons.Count > 0)
            {
                sb.AppendLine("上次学到的（用户自己写的）：");
                foreach (var l in lessons) sb.AppendLine("- " + l);
            }

            if (planIdeas.Count > 0)
            {
                sb.AppendLine("用户记下但还没做的「计划/行动」类想法（挑一条搬过来也可以）：");
                foreach (var i in planIdeas.Take(10)) sb.AppendLine("- " + OneLine(i.Text, 80));
            }

            if (todayPieces.Count > 0)
            {
                sb.AppendLine("今天已经排过的片（不要再排同样的）：" + string.Join("、", todayPieces));
            }

            sb.AppendLine("可用的片（code 必须从这里取）：" + PieceList());

            return (system, sb.ToString());
        }

        /// <summary>
        /// 校验并清洗模型输出。返回空列表 = 这次没生成，调用方应返回 ok=false。
        /// </summary>
        public static List<StepOption> ParseNextSteps(string raw)
        {
            var list = new List<StepOption>();
            if (!TryRoot(raw, out var root)) return list;
            if (!root.TryGetProperty("options", out var arr) || arr.ValueKind != JsonValueKind.Array) return list;

            var valid = new HashSet<string>(Pieces.All.Select(p => p.Code), StringComparer.Ordinal);

            foreach (var el in arr.EnumerateArray())
            {
                string action = OneLine(Str(el, "action"), MaxActionLen + 8);
                string piece = Str(el, "piece").Trim().ToLowerInvariant();
                int minutes = Int(el, "minutes");

                if (action.Length == 0 || action.Length > MaxActionLen) continue;
                if (!valid.Contains(piece)) continue;           // 编出来的片直接丢，不"猜他想说哪个"

                // 时长不合规不丢整条：吸到最近的固定档（动作本身还是有用的）
                list.Add(new StepOption(action, piece, NearestDuration(minutes)));

                if (list.Count >= 3) break;
            }

            // 同一个动作说两遍没有意义（模型有时会换个说法给两条一样的）
            return list
                .GroupBy(o => o.Action, StringComparer.Ordinal)
                .Select(g => g.First())
                .ToList();
        }

        // ============================================================
        // B. 评价聚类 → 一个可练的模式
        // ============================================================

        /// <summary>低于这个条数不调模型：样本不够时它一定会编。</summary>
        public const int MinIdeasForClustering = 12;

        /// <summary>低于这个条数不调模型：样本不够时它一定会编。</summary>
        public const int MinOccurrences = 3;

        public static (string System, string User) Clustering(IReadOnlyList<Idea> ideas)
        {
            const string system = """
                你是一个记录整理员，不是心理咨询师。

                材料是用户自己写下的「评价」类想法（对某件事的看法、对自己的判断）。
                任务：找出**反复出现**的反应模式，整理成一条可以练的行为模式。

                硬性要求：
                1. 只提至少出现 3 次的模式。宁可一条都不提，也不要凑。
                2. 不要评价用户、不要用「总是」「从不」「你不该」「问题在于」这类词。
                3. new_response 必须是一个具体的动作（下次那种情况发生时，做什么），不是心态。
                   好："先复述对方观点，再回答"；坏："保持冷静"。
                4. evidence 填材料的序号（方括号里的数字），2~4 个，**必须是原文里真实存在的**。
                   不要改写原句，不要自己造句。
                5. 最多 2 条。一条都没有就返回空数组。

                只输出 JSON，格式：
                {"proposals":[{"title":"被质疑时先不反驳","trigger":"...","old_response":"...","new_response":"...","evidence":[3,7,11]}]}
                """;

            var sb = new StringBuilder("想法原文（序号｜内容）：\n");
            for (int i = 0; i < ideas.Count; i++)
            {
                sb.Append(i).Append("｜").AppendLine(OneLine(ideas[i].Text, 100));
            }

            return (system, sb.ToString());
        }

        /// <summary>
        /// 校验聚类结果。evidence 用序号回填原文 —— 模型碰不到那几行字，编不出来。
        /// </summary>
        public static List<Proposal> ParseProposals(string raw, IReadOnlyList<Idea> ideas)
        {
            var list = new List<Proposal>();
            if (!TryRoot(raw, out var root)) return list;
            if (!root.TryGetProperty("proposals", out var arr) || arr.ValueKind != JsonValueKind.Array) return list;

            foreach (var el in arr.EnumerateArray())
            {
                string title = OneLine(Str(el, "title"), 30);
                string newResp = OneLine(Str(el, "new_response"), 120);
                if (title.Length == 0 || newResp.Length == 0) continue;

                var evidence = new List<string>();
                if (el.TryGetProperty("evidence", out var ev) && ev.ValueKind == JsonValueKind.Array)
                {
                    foreach (var idEl in ev.EnumerateArray())
                    {
                        if (idEl.ValueKind != JsonValueKind.Number) continue;
                        int idx = idEl.GetInt32();
                        if (idx < 0 || idx >= ideas.Count) continue;   // 越界序号一律丢

                        string text = OneLine(ideas[idx].Text, 80);
                        if (!evidence.Contains(text)) evidence.Add(text);
                        if (evidence.Count >= 4) break;
                    }
                }

                // 没有一条能对上原文 = 这条是编的，整条丢掉
                if (evidence.Count < 2) continue;

                // 模型给的次数不可信，用证据实际覆盖的条数当次数
                list.Add(new Proposal(
                    title,
                    OneLine(Str(el, "trigger"), 60),
                    OneLine(Str(el, "old_response"), 60),
                    newResp,
                    evidence,
                    evidence.Count));

                if (list.Count >= 2) break;
            }

            return list;
        }

        // ============================================================
        // C. 周回顾的一句话
        // ============================================================

        public static (string System, string User) WeekComment(WeekFacts f)
        {
            const string system = """
                你是一个记录员，只做一件事：把这个星期和上个星期的**数字差别**写成一句话。

                硬性要求：
                1. 一句话，不超过 40 个字。只陈述差别，不评论。
                2. 不说好坏、不给建议、不鼓励、不预测、不用"应该"。
                3. 只使用材料里的数字，不要推算材料里没有的东西。
                4. 如果数字之间看不出明显差别，就返回空字符串 —— 什么都不说，比编一句强。

                只输出 JSON：{"text":"..."}
                """;

            var sb = new StringBuilder();
            sb.AppendLine($"区间：{f.WeekLabel}");
            sb.AppendLine($"本周投入：{f.ThisMinutes} 分钟；上周：{f.LastMinutes} 分钟");
            sb.AppendLine($"本周留记录的天数：{f.Days} 天，共 {f.Blocks} 条");
            sb.AppendLine($"进行中的目标 {f.ActiveGoals} 条，本周动过 {f.MovedGoals} 条");

            if (!string.IsNullOrWhiteSpace(f.TopShift)) sb.AppendLine("变化最大的片：" + f.TopShift);
            if (!string.IsNullOrWhiteSpace(f.Repeat)) sb.AppendLine("反复说过的话：" + f.Repeat);

            return (system, sb.ToString());
        }

        public static string ParseWeekComment(string raw)
        {
            if (!TryRoot(raw, out var root)) return string.Empty;
            string text = OneLine(Str(root, "text"), 60);
            return text;
        }

        // ============================================================
        // D. 事件卡：自由文字 → 结构化事件草稿（R7）
        // ============================================================

        /// <summary>一次最多拆出几张事件卡。再多说明用户把一周都倒进来了，
        /// 应该让他分几次打——一张超长确认页没人看得完。</summary>
        public const int MaxEventsPerCall = 3;

        /// <summary>事件卡提取的校验结果行（已通过校验，可直接给客户端）。</summary>
        public sealed record EventDraftRow(
            int Index, string Title, string Emotion, string Tag,
            string Facts, string Action, string InitialThought, string Result, string Question);

        /// <summary>新模式草案（只提示，不自动建）。</summary>
        public sealed record PatternDraftRow(string Title, string Reason);

        /// <summary>事件卡 → 问题/模式 的路由（id 已对照请求清单校验过）。</summary>
        public sealed record EventRouteRow(
            int EventIndex, string MatchProblemId, string MatchPatternId, PatternDraftRow? NewPattern,
            string SuggestedAttempt = "");

        public static (string System, string User) ExtractEvents(
            string rawText,
            IReadOnlyList<(string Id, string Title)> problems,
            IReadOnlyList<(string Id, string Title)> patterns,
            IReadOnlyList<string> tagOptions,
            IReadOnlyList<string> emotionOptions)
        {
            const string system = """
                你是一个记录整理员，不是心理咨询师。用户会随手打一段话——乱序、带情绪、几句抱怨混在一起。
                任务：把这段话**拆成最多 3 张事件卡**，每张卡描述一件真实发生的事。

                硬性要求：
                1. 只整理用户写出来的内容。facts 只准搬运/缩写原文，一个字都不许编。
                   原文没写的字段留空字符串，不要猜、不要补全。
                2. 一段话里可能是多件事，按"不同时间/不同对象"拆开；也可能只是一件事。
                3. 别人的评价、自己的总结（"我总是……"这种没有具体时间地点的话）不是事件，
                   不要做成事件卡。
                4. emotion 必须从给定列表里挑；tag 必须从给定列表里挑。
                5. question 每张卡最多一个，不超过 20 个字，只问"当时"的事实（帮助分清
                   是没注意到还是不会应对），不问感受、不给建议。没有值得问的就留空。
                6. 最多 3 张。拆不出来就返回空数组。
                7. 命中 match_problem_id 时，可以给一条 suggested_attempt（≤30字）：
                   针对"这一件事"给一个具体可做的应对动作，不是人生建议。
                   说不出具体动作就留空，宁缺毋滥。

                只输出 JSON，格式：
                {"events":[{"event_index":0,"title":"...","emotion":"...","tag":"...","facts":"...","action":"...","initial_thought":"...","result":"...","question":"..."}],
                 "routes":[{"event_index":0,"match_problem_id":"","match_pattern_id":"","suggested_attempt":"","new_pattern":{"title":"","reason":""}}]}
                """;

            var sb = new StringBuilder();
            sb.AppendLine("固定标签（tag 只准从这里挑）：");
            sb.AppendLine(string.Join("、", tagOptions));
            sb.AppendLine("固定情绪（emotion 只准从这里挑）：");
            sb.AppendLine(string.Join("、", emotionOptions));

            if (problems.Count > 0)
            {
                sb.AppendLine("用户已经在跟进的「问题卡」（如果某件事明显属于其中之一，把它的 id 填进 match_problem_id）：");
                foreach (var p in problems) sb.AppendLine($"- {p.Id}｜{OneLine(p.Title, 40)}");
            }

            if (patterns.Count > 0)
            {
                sb.AppendLine("用户已有的「行为模式」（明显像其中之一就填 match_pattern_id）：");
                foreach (var p in patterns) sb.AppendLine($"- {p.Id}｜{OneLine(p.Title, 40)}");
            }

            sb.AppendLine();
            sb.AppendLine("用户打的原文：");
            sb.AppendLine(OneLine(rawText, 2000));

            return (system, sb.ToString());
        }

        /// <summary>
        /// 校验提取结果。纪律与 ParseNextSteps 相同：
        ///   - facts 为空的整张丢（说明在编）；id 对不上请求清单的整条丢；
        ///   - emotion/tag 不在固定列表里的整张丢（不确定的归类宁可不给）；
        ///   - 路由三选一，都不沾边就丢弃这条路由（事件卡本身仍然有效）。
        /// 返回空 events = 这次没生成，调用方返回 ok=false。
        /// </summary>
        public static (List<EventDraftRow> Events, List<EventRouteRow> Routes) ParseEvents(
            string raw,
            IReadOnlyList<(string Id, string Title)> problems,
            IReadOnlyList<(string Id, string Title)> patterns,
            IReadOnlyList<string> tagOptions,
            IReadOnlyList<string> emotionOptions)
        {
            var events = new List<EventDraftRow>();
            var routes = new List<EventRouteRow>();
            if (!TryRoot(raw, out var root)) return (events, routes);

            var tags = new HashSet<string>(tagOptions, StringComparer.Ordinal);
            var emotions = new HashSet<string>(emotionOptions, StringComparer.Ordinal);
            var problemIds = new HashSet<string>(problems.Select(p => p.Id), StringComparer.Ordinal);
            var patternIds = new HashSet<string>(patterns.Select(p => p.Id), StringComparer.Ordinal);

            if (root.TryGetProperty("events", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray())
                {
                    string facts = OneLine(Str(el, "facts"), 300);
                    if (facts.Length == 0) continue;                 // 没有事实 = 在编，整张丢

                    string emotion = Str(el, "emotion").Trim();
                    string tag = Str(el, "tag").Trim();
                    if (!emotions.Contains(emotion)) continue;       // 编出来的情绪整张丢
                    if (!tags.Contains(tag)) continue;

                    string title = OneLine(Str(el, "title"), 20);
                    if (title.Length == 0) title = OneLine(facts, 15);

                    events.Add(new EventDraftRow(
                        Int(el, "event_index"),
                        title,
                        emotion,
                        tag,
                        facts,
                        OneLine(Str(el, "action"), 120),
                        OneLine(Str(el, "initial_thought"), 120),
                        OneLine(Str(el, "result"), 120),
                        OneLine(Str(el, "question"), 40)));

                    if (events.Count >= MaxEventsPerCall) break;
                }
            }

            if (events.Count == 0) return (events, routes);

            var validIndexes = events.Select(e => e.Index).ToHashSet();

            if (root.TryGetProperty("routes", out var rArr) && rArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in rArr.EnumerateArray())
                {
                    int idx = Int(el, "event_index");
                    if (!validIndexes.Contains(idx)) continue;       // 对不上事件序号的路由丢

                    string problemId = Str(el, "match_problem_id").Trim();
                    string patternId = Str(el, "match_pattern_id").Trim();
                    if (!problemIds.Contains(problemId)) problemId = string.Empty;
                    if (!patternIds.Contains(patternId)) patternId = string.Empty;

                    PatternDraftRow? draft = null;
                    if (el.TryGetProperty("new_pattern", out var np) && np.ValueKind == JsonValueKind.Object)
                    {
                        string npTitle = OneLine(Str(np, "title"), 12);
                        string npReason = OneLine(Str(np, "reason"), 40);
                        if (npTitle.Length > 0) draft = new PatternDraftRow(npTitle, npReason);
                    }

                    // 三选一都不沾边 → 这条路由没有信息量，丢弃
                    if (problemId.Length == 0 && patternId.Length == 0 && draft is null) continue;

                    // 建议试法只对命中的问题卡有意义（模式不需要"试法"）
                    string suggestedAttempt = problemId.Length > 0 ? OneLine(Str(el, "suggested_attempt"), 30) : string.Empty;

                    routes.Add(new EventRouteRow(idx, problemId, patternId, draft, suggestedAttempt));
                }
            }

            return (events, routes);
        }

        // ============================================================
        // E. AI 周回看：扫近期事件卡 → 主动发现用户自己意识不到的重复苗头
        // ============================================================

        /// <summary>一次回看最多给几条发现。多了没人看，和事件卡一个道理。</summary>
        public const int MaxFindingsPerReview = 3;

        /// <summary>回看结果行（已通过校验）。</summary>
        public sealed record ReviewFindingRow(
            string Title, string Reason, bool SuggestProblem, string SuggestedCriteria, string SuggestedAttempt);

        public static (string System, string User) ReviewEvents(
            IReadOnlyList<(string Day, string Title, string Facts)> events,
            IReadOnlyList<(string Id, string Title)> problems,
            IReadOnlyList<(string Id, string Title)> patterns)
        {
            const string system = """
                你是一个记录分析员。用户记录了一段时间的事件卡，但他自己经常意识不到
                "某些事在反复发生"——人对自己盲区里的事是看不见的，这正是你的任务。

                任务：从这批事件里找出**重复出现的苗头**。注意：
                1. 只依据给出的 facts，一个字都不许编。事件不够多（少于 5 条）或
                   看不出任何重复，就返回空数组——不要硬凑发现。
                2. 已经在"用户跟进的问题卡"清单里的事，不算新发现，不要重复报。
                3. 每条发现给 title（≤12字，说现象不说评价，例如"和熟人说话不过脑"，
                   不说"情商低"）和 reason（≤60字，点出是哪几天、哪几件事像）。
                4. suggest_problem=true 只给"正在造成实际损失、值得马上改"的；
                   只是性格特点或样本还少的，给 false（先进模式页攒样本）。
                5. suggested_criteria（≤40字）：如果建议立卡，给一个"怎么算解决"的
                   草案——必须是可验证的行为标准，不是"变得更好"这种空话。
                6. suggested_attempt（≤30字，可空）：给一个具体可做的应对动作。
                7. 最多 3 条。宁缺毋滥。

                只输出 JSON，格式：
                {"findings":[{"title":"...","reason":"...","suggest_problem":true,"suggested_criteria":"...","suggested_attempt":"..."}]}
                """;

            var sb = new StringBuilder();

            if (problems.Count > 0)
            {
                sb.AppendLine("用户已在跟进的问题卡（这些不算新发现）：");
                foreach (var p in problems) sb.AppendLine($"- {OneLine(p.Title, 40)}");
            }
            if (patterns.Count > 0)
            {
                sb.AppendLine("用户已有的行为模式：");
                foreach (var p in patterns) sb.AppendLine($"- {OneLine(p.Title, 40)}");
            }

            sb.AppendLine();
            sb.AppendLine("近期事件卡：");
            foreach (var e in events)
            {
                sb.AppendLine($"- {e.Day}｜{OneLine(e.Title, 20)}｜{OneLine(e.Facts, 150)}");
            }

            return (system, sb.ToString());
        }

        /// <summary>校验回看结果：title/reason 为空丢、清单外的事不报、最多 3 条。</summary>
        public static List<ReviewFindingRow> ParseReview(string raw)
        {
            var findings = new List<ReviewFindingRow>();
            if (!TryRoot(raw, out var root)) return findings;
            if (!root.TryGetProperty("findings", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return findings;

            foreach (var el in arr.EnumerateArray())
            {
                string title = OneLine(Str(el, "title"), 12);
                string reason = OneLine(Str(el, "reason"), 60);
                if (title.Length == 0 || reason.Length == 0) continue;   // 缺名字或缺依据 = 没信息量

                findings.Add(new ReviewFindingRow(
                    title,
                    reason,
                    Bool(el, "suggest_problem"),
                    OneLine(Str(el, "suggested_criteria"), 40),
                    OneLine(Str(el, "suggested_attempt"), 30)));

                if (findings.Count >= MaxFindingsPerReview) break;
            }

            return findings;
        }

        // ============================================================
        // 小工具
        // ============================================================

        /// <summary>从可能带 ``` 围栏 / 前后废话的回复里抠出 JSON 根对象。</summary>
        private static bool TryRoot(string raw, out JsonElement root)
        {
            root = default;
            if (string.IsNullOrWhiteSpace(raw)) return false;

            int start = raw.IndexOf('{');
            int end = raw.LastIndexOf('}');
            if (start < 0 || end <= start) return false;

            try
            {
                using var doc = JsonDocument.Parse(raw.Substring(start, end - start + 1));
                root = doc.RootElement.Clone();
                return root.ValueKind == JsonValueKind.Object;
            }
            catch
            {
                return false;
            }
        }

        private static string Str(JsonElement el, string name)
        {
            if (el.ValueKind != JsonValueKind.Object) return string.Empty;
            if (!el.TryGetProperty(name, out var v)) return string.Empty;
            return v.ValueKind == JsonValueKind.String ? (v.GetString() ?? string.Empty) : string.Empty;
        }

        private static int Int(JsonElement el, string name)
        {
            if (el.ValueKind != JsonValueKind.Object) return 0;
            if (!el.TryGetProperty(name, out var v)) return 0;
            return v.ValueKind switch
            {
                JsonValueKind.Number => v.GetInt32(),
                JsonValueKind.String => int.TryParse(v.GetString(), out int n) ? n : 0,
                _ => 0,
            };
        }

        /// <summary>模型对布尔字段可能给 true/"true"/1，统一兜住。</summary>
        private static bool Bool(JsonElement el, string name)
        {
            if (el.ValueKind != JsonValueKind.Object) return false;
            if (!el.TryGetProperty(name, out var v)) return false;
            return v.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.Number => v.GetInt32() != 0,
                JsonValueKind.String => bool.TryParse(v.GetString(), out var b) && b,
                _ => false,
            };
        }

        /// <summary>压成一行：模型很爱在 JSON 字符串里塞 \n，那会让卡片突然变很高。</summary>
        private static string OneLine(string? s, int max)
        {
            s = (s ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            while (s.Contains("  ")) s = s.Replace("  ", " ");
            if (s.Length > max) s = s.Substring(0, max).TrimEnd('，', '。', ',', ' ');
            return s;
        }

        private static int NearestDuration(int minutes)
        {
            if (minutes <= 0) return 30;
            int best = Durations[0];
            foreach (int d in Durations)
            {
                if (Math.Abs(d - minutes) < Math.Abs(best - minutes)) best = d;
            }
            return best;
        }

        private static string PieceList() =>
            string.Join("、", Pieces.All.Select(p => $"{p.Code}（{p.Name}）"));
    }
}
