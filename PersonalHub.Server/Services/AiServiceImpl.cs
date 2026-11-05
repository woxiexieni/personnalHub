using Grpc.Core;
using Personalhub;
using personalHub.Server.Ai;
using personalHub.Server.Analysis;
using personalHub.Server.Data;

namespace personalHub.Server.Services
{
    /// <summary>
    /// AI 接口（§27）。四个方法，全是"用户点一下才发生"，没有任何后台自动调用。
    ///
    /// 失败一律返回 ok=false + 一句人话，**不抛 RpcException**。
    /// 理由：客户端在 ok=false 时会静默降级到本地规则版，用户根本不该看到这条错误；
    /// 但如果服务端抛异常，客户端那边会变成"整页报错"，性质就完全不同了。
    ///
    /// 隐私边界：发给模型的是**目标标题、片代号、用户自己写的一句话**，
    /// 不包含账单、人名、位置。这一点由这个方法自己控制 —— 它只挑需要的字段进 Prompt，
    /// 而不是"把整条记录序列化进去"。
    /// </summary>
    public sealed class AiServiceImpl : AiService.AiServiceBase
    {
        private readonly AiConfig _config;
        private readonly DeepSeekClient _llm;
        private readonly GoalStore _goals;
        private readonly BlockStore _blocks;
        private readonly IdeaStore _ideas;
        private readonly PlanStore _plans;

        public AiServiceImpl(
            AiConfig config, DeepSeekClient llm,
            GoalStore goals, BlockStore blocks, IdeaStore ideas, PlanStore plans)
        {
            _config = config;
            _llm = llm;
            _goals = goals;
            _blocks = blocks;
            _ideas = ideas;
            _plans = plans;
        }

        // ============================================================
        // 状态
        // ============================================================

        public override Task<AiStatusResponse> GetAiStatus(Empty request, ServerCallContext context)
        {
            var cfg = _config.Current;
            return Task.FromResult(new AiStatusResponse
            {
                Configured = cfg.Configured,
                Model = cfg.Model,
                Endpoint = cfg.Endpoint,
                Hint = cfg.Hint,
            });
        }

        // ============================================================
        // A. 提议下一步
        // ============================================================

        public override async Task<SuggestNextStepsResponse> SuggestNextSteps(
            SuggestNextStepsRequest request, ServerCallContext context)
        {
            var resp = new SuggestNextStepsResponse();

            var cfg = _config.Current;
            if (!cfg.Configured)
            {
                resp.ErrorMsg = cfg.Hint;
                return resp;
            }

            var goal = _goals.Get(request.GoalId ?? string.Empty);
            if (goal is null)
            {
                resp.ErrorMsg = "目标不在了";
                return resp;
            }

            var now = DateTimeOffset.UtcNow;
            var (from, to) = Period.LastDays(30, now);

            // 只取这个目标的块：**不**把整张 blocks 表交给模型
            var blocks = _blocks.List(from, to, goalId: goal.Id);

            // 「计划/行动」类想法里还没转成目标的那些 —— 这是最现成的下一步来源
            var (ideaFrom, ideaTo) = Period.LastDays(90, now);
            var planIdeas = _ideas.List(3, ideaFrom, ideaTo, includeDeleted: false)
                .Where(i => string.IsNullOrEmpty(i.GoalId) && !string.IsNullOrWhiteSpace(i.Text))
                .Take(10)
                .ToList();

            var todayPieces = _plans.ListDay(PlanStore.Today(), includeDeleted: false)
                .Select(p => p.PieceCode ?? string.Empty)
                .Where(c => c.Length > 0)
                .Distinct()
                .ToList();

            var (system, user) = AiPrompts.NextStep(goal, blocks, planIdeas, todayPieces);

            var reply = await _llm.ChatJsonAsync(cfg, system, user, context.CancellationToken)
                .ConfigureAwait(false);

            if (!reply.Ok)
            {
                resp.ErrorMsg = reply.Error;
                Log("下一步", goal.Id, reply);
                return resp;
            }

            var options = AiPrompts.ParseNextSteps(reply.Content);
            Log("下一步", goal.Id, reply, $"→ {options.Count} 条");

            if (options.Count == 0)
            {
                resp.ErrorMsg = "这次没想出合适的（返回不合规）";
                return resp;
            }

            resp.Ok = true;
            resp.Source = 1;
            foreach (var o in options)
            {
                resp.Options.Add(new NextStepOption
                {
                    Action = o.Action,
                    PieceCode = o.PieceCode,
                    Minutes = o.Minutes,
                });
            }

            return resp;
        }

        // ============================================================
        // B. 评价聚类 → 一个可练的模式
        // ============================================================

        public override async Task<PatternProposalList> FindRepeatingEvaluations(
            FindRepeatingRequest request, ServerCallContext context)
        {
            var resp = new PatternProposalList();

            var cfg = _config.Current;
            if (!cfg.Configured)
            {
                resp.ErrorMsg = cfg.Hint;
                return resp;
            }

            int days = request.Days is > 7 and <= 730 ? request.Days : 180;
            var (from, to) = Period.LastDays(days, DateTimeOffset.UtcNow);

            // kind = 2 评价类。「这件事我不行」「每次都是这样」这类句子都在这里。
            var ideas = _ideas.List(2, from, to, includeDeleted: false)
                .Where(i => !string.IsNullOrWhiteSpace(i.Text))
                .Take(120)              // 上限：再多的样本对找模式没有额外帮助，只会更贵
                .ToList();

            resp.Scanned = ideas.Count;

            if (ideas.Count < AiPrompts.MinIdeasForClustering)
            {
                resp.ErrorMsg = $"评价类想法只有 {ideas.Count} 条，攒到 {AiPrompts.MinIdeasForClustering} 条再看才有意义。";
                return resp;
            }

            var (system, user) = AiPrompts.Clustering(ideas);
            var reply = await _llm.ChatJsonAsync(cfg, system, user, context.CancellationToken)
                .ConfigureAwait(false);

            if (!reply.Ok)
            {
                resp.ErrorMsg = reply.Error;
                Log("聚类", $"{ideas.Count} 条", reply);
                return resp;
            }

            var proposals = AiPrompts.ParseProposals(reply.Content, ideas);
            Log("聚类", $"{ideas.Count} 条", reply, $"→ {proposals.Count} 个模式");

            if (proposals.Count == 0)
            {
                resp.ErrorMsg = "这批想法里没找到反复出现的模式。";
                return resp;
            }

            resp.Ok = true;
            foreach (var p in proposals)
            {
                var dto = new PatternProposal
                {
                    Title = p.Title,
                    Trigger = p.Trigger,
                    OldResponse = p.OldResponse,
                    NewResponse = p.NewResponse,
                    Count = p.Count,
                };
                dto.Evidence.AddRange(p.Evidence);
                resp.Proposals.Add(dto);
            }

            return resp;
        }

        // ============================================================
        // C. 周回顾一句话
        // ============================================================

        public override async Task<WeekComment> CommentWeek(WeekFacts request, ServerCallContext context)
        {
            var resp = new WeekComment();

            var cfg = _config.Current;
            if (!cfg.Configured)
            {
                resp.ErrorMsg = cfg.Hint;
                return resp;
            }

            // 数据太少就不说。这一条比"说得好"更重要：一页全是"以 — 为主"的空话，人就不看这一页了。
            if (request.Blocks < 3 && request.ThisMinutes == 0 && request.LastMinutes == 0)
            {
                resp.ErrorMsg = "这一周的数据还不够，不猜。下周再说。";
                return resp;
            }

            var (system, user) = AiPrompts.WeekComment(request);
            var reply = await _llm.ChatJsonAsync(cfg, system, user, context.CancellationToken)
                .ConfigureAwait(false);

            if (!reply.Ok)
            {
                resp.ErrorMsg = reply.Error;
                Log("周回顾", request.WeekLabel ?? "-", reply);
                return resp;
            }

            string text = AiPrompts.ParseWeekComment(reply.Content);
            Log("周回顾", request.WeekLabel ?? "-", reply, $"→ \"{text}\"");

            if (text.Length == 0)
            {
                resp.ErrorMsg = "模型认为这周没有明显差别，所以没说。";
                return resp;
            }

            resp.Ok = true;
            resp.Text = text;
            return resp;
        }

        // ============================================================
        // C2. 目标 30 天：归纳，不建议
        // ============================================================
        //
        // 和服务端其他 AI 方法一样：**无状态**。事实是客户端算好带来的，
        // 这里不查库、不存结果、不知道该用户的任何历史。

        public override async Task<GoalComment> CommentGoal(GoalFacts request, ServerCallContext context)
        {
            var resp = new GoalComment();

            var cfg = _config.Current;
            if (!cfg.Configured)
            {
                resp.ErrorMsg = cfg.Hint;
                return resp;
            }

            // 数据太少就不说。三两段记录凑不出"重复"，只会凑出空话 ——
            // 而这一块一旦开始说空话，用户就再也不点它了。
            if (request.Blocks < 3)
            {
                resp.ErrorMsg = "这个目标最近只有 " + request.Blocks + " 段记录，还看不出重复。攒到 3 段以上再看。";
                return resp;
            }

            var (system, user) = AiPrompts.GoalComment(request);
            var reply = await _llm.ChatJsonAsync(cfg, system, user, context.CancellationToken)
                .ConfigureAwait(false);

            if (!reply.Ok)
            {
                resp.ErrorMsg = reply.Error;
                Log("目标归纳", request.GoalTitle ?? "-", reply);
                return resp;
            }

            var lines = AiPrompts.ParseGoalComment(reply.Content);
            Log("目标归纳", request.GoalTitle ?? "-", reply, $"→ {lines.Count} 行");

            if (lines.Count == 0)
            {
                resp.ErrorMsg = "模型没看出重复，所以没说。";
                return resp;
            }

            resp.Ok = true;
            resp.Lines.AddRange(lines);
            return resp;
        }

        // ============================================================
        // D. 事件卡：自由文字 → 结构化事件草稿（R7）
        // ============================================================
        //
        // 与前三个方法最大的不同：**服务端无状态**。
        // 问题卡 / 模式清单是客户端从本地 SQLite 带来的，服务端不存也不查——
        // v1 不为此建表、不建同步链路。服务端在这里只是"读过上下文的整理员"。

        public override async Task<ExtractEventsReply> ExtractEvents(
            ExtractEventsRequest request, ServerCallContext context)
        {
            var resp = new ExtractEventsReply();

            var cfg = _config.Current;
            if (!cfg.Configured)
            {
                resp.ErrorMsg = cfg.Hint;
                return resp;
            }

            string rawText = (request.RawText ?? string.Empty).Trim();
            if (rawText.Length < 10)
            {
                resp.ErrorMsg = "写得太短了，多打两句才能整理出东西。";
                return resp;
            }

            var problems = request.KnownProblems
                .Where(p => !string.IsNullOrWhiteSpace(p.Id))
                .Select(p => (p.Id, p.Title ?? string.Empty))
                .ToList();
            var patterns = request.KnownPatterns
                .Where(p => !string.IsNullOrWhiteSpace(p.Id))
                .Select(p => (p.Id, p.Title ?? string.Empty))
                .ToList();
            var tags = request.TagOptions.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
            var emotions = request.EmotionOptions.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();

            if (tags.Count == 0 || emotions.Count == 0)
            {
                resp.ErrorMsg = "客户端没带标签/情绪选项，无法校验。";
                return resp;
            }

            var (system, user) = AiPrompts.ExtractEvents(rawText, problems, patterns, tags, emotions);

            var reply = await _llm.ChatJsonAsync(cfg, system, user, context.CancellationToken)
                .ConfigureAwait(false);

            if (!reply.Ok)
            {
                resp.ErrorMsg = reply.Error;
                Log("事件卡", $"{rawText.Length} 字", reply);
                return resp;
            }

            // 第一个参数是模型返回的 JSON（ParseEvents 内部 TryRoot 解析它），
            // 不是用户原文——传错的话校验永远 0 张卡。
            var (events, routes) = AiPrompts.ParseEvents(reply.Content, problems, patterns, tags, emotions);
            Log("事件卡", $"{rawText.Length} 字", reply, $"→ {events.Count} 张卡 / {routes.Count} 条路由");

            if (events.Count == 0)
            {
                resp.ErrorMsg = "这次没整理出事件卡（返回不合规）。原文可以先存着，回头再试。";
                return resp;
            }

            resp.Ok = true;
            foreach (var e in events)
            {
                resp.Events.Add(new EventDraft
                {
                    EventIndex = e.Index,
                    Title = e.Title,
                    Emotion = e.Emotion,
                    Tag = e.Tag,
                    Facts = e.Facts,
                    Action = e.Action,
                    InitialThought = e.InitialThought,
                    Result = e.Result,
                    Question = e.Question,
                });
            }

            foreach (var r in routes)
            {
                var dto = new EventRoute { EventIndex = r.EventIndex };
                if (r.MatchProblemId.Length > 0) dto.MatchProblemId = r.MatchProblemId;
                if (r.MatchPatternId.Length > 0) dto.MatchPatternId = r.MatchPatternId;
                if (r.NewPattern is { } np)
                {
                    dto.NewPattern = new PatternDraft { Title = np.Title, Reason = np.Reason };
                }
                if (r.SuggestedAttempt.Length > 0) dto.SuggestedAttempt = r.SuggestedAttempt;
                resp.Routes.Add(dto);
            }

            return resp;
        }

        /// <summary>
        /// AI 周回看：近 30 天事件卡 → 重复苗头。防盲区用的——用户自己意识不到
        /// "某些事在反复发生"，这是这个系统里唯一主动开口的功能（手动触发，一次调用）。
        /// 服务端照旧无状态：事件和已有问题/模式清单都由客户端带来。
        /// </summary>
        public override async Task<ReviewEventsReply> ReviewEvents(
            ReviewEventsRequest request, ServerCallContext context)
        {
            var resp = new ReviewEventsReply();

            var cfg = _config.Current;
            if (!cfg.Configured)
            {
                resp.ErrorMsg = cfg.Hint;
                return resp;
            }

            var events = request.Events
                .Where(e => !string.IsNullOrWhiteSpace(e.Facts))
                .Select(e => (e.Day ?? string.Empty, e.Title ?? string.Empty, e.Facts))
                .ToList();

            if (events.Count < 5)
            {
                resp.ErrorMsg = $"事件卡太少（{events.Count} 张，至少 5 张）。先照常打几天事件卡，攒够样本再来回看。";
                return resp;
            }

            var problems = request.KnownProblems
                .Where(p => !string.IsNullOrWhiteSpace(p.Id))
                .Select(p => (p.Id, p.Title ?? string.Empty))
                .ToList();
            var patterns = request.KnownPatterns
                .Where(p => !string.IsNullOrWhiteSpace(p.Id))
                .Select(p => (p.Id, p.Title ?? string.Empty))
                .ToList();

            var (system, user) = AiPrompts.ReviewEvents(events, problems, patterns);

            var reply = await _llm.ChatJsonAsync(cfg, system, user, context.CancellationToken)
                .ConfigureAwait(false);

            if (!reply.Ok)
            {
                resp.ErrorMsg = reply.Error;
                Log("周回看", $"{events.Count} 张事件卡", reply);
                return resp;
            }

            var findings = AiPrompts.ParseReview(reply.Content);
            Log("周回看", $"{events.Count} 张事件卡", reply, $"→ {findings.Count} 条发现");

            if (findings.Count == 0)
            {
                resp.ErrorMsg = "这批事件里没看出明显重复——不硬凑。继续攒，样本够了再回看。";
                return resp;
            }

            resp.Ok = true;
            foreach (var f in findings)
            {
                var dto = new ReviewFinding
                {
                    Title = f.Title,
                    Reason = f.Reason,
                    SuggestedCriteria = f.SuggestedCriteria,
                    SuggestedAttempt = f.SuggestedAttempt,
                };
                if (f.SuggestProblem) dto.SuggestProblem = true;
                resp.Findings.Add(dto);
            }

            return resp;
        }

        // ============================================================
        // 日志
        // ============================================================

        /// <summary>
        /// 一行一条，打在服务端控制台。这个项目没有日志框架，而 AI 这块"到底调没调、多少钱"
        /// 是最需要事后能看出来的地方 —— 一条 300ms 和一条 12s 的含义完全不同。
        /// </summary>
        private static void Log(string kind, string subject, LlmReply reply, string extra = "")
        {
            string status = reply.Ok ? "OK" : "FAIL";
            Console.WriteLine($"[AI] {kind} {status} {reply.ElapsedMs}ms · {subject}"
                + (reply.Ok ? extra : " · " + reply.Error));
        }
    }
}
