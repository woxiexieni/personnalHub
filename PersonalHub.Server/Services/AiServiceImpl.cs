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
