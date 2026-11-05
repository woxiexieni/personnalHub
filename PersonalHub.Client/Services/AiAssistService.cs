using System.Text.Json;
using Personalhub;

namespace PersonalHub.Client.Services
{
    // ============================================================
    // 客户端侧的 AI 数据类型
    // ============================================================
    //
    // 刻意不用生成的 proto 类型直接当界面模型：
    //  1. proto 类型是 mutable + 带一堆 Has 属性，绑定到界面容易出"改了一个字段但界面没刷"；
    //  2. 缓存要把它们塞进 Preferences 的 JSON 里，自己这几个 record 的字段名更短、更稳。

    public sealed record AiStepOption(string Action, string PieceCode, int Minutes);

    /// <summary>
    /// 「这 30 天」的归纳输入：全部由客户端本地算好（数据本来就在 SQLite 里）。
    /// 不直接用 proto 的 GoalFacts —— 同下面的理由，界面层不碰生成类型。
    /// </summary>
    public sealed record AiGoalDigest(
        string GoalTitle, int Days, int Blocks, int Minutes,
        int WorthGood, int WorthMid, int WorthBad,
        List<string> Lessons, List<string> BadNotes, string TopPiece);

    public sealed record AiPattern(
        string Title, string Trigger, string OldResponse, string NewResponse,
        List<string> Evidence, int Count);

    public sealed record AiStepResult(bool Ok, bool FromCache, string Message, List<AiStepOption> Options);

    public sealed record AiPatternResult(bool Ok, string Message, List<AiPattern> Items, int Scanned);

    public sealed record AiTextResult(bool Ok, string Message, string Text);

    /// <summary>一张事件卡草稿（服务端已校验，但用户没确认前不算数）。</summary>
    public sealed record AiEventDraft(
        int Index, string Title, string Emotion, string Tag,
        string Facts, string Action, string InitialThought, string Result, string Question);

    /// <summary>新模式草案（只提示，不自动建）。</summary>
    public sealed record AiPatternDraft(string Title, string Reason);

    /// <summary>事件卡 → 问题/模式 的路由（id 服务端已对照请求清单校验，标题由页面按 id 反查）。</summary>
    public sealed record AiEventRoute(
        int EventIndex, string MatchProblemId, string MatchPatternId, AiPatternDraft? NewPattern,
        string SuggestedAttempt = "");

    /// <summary>AI 周回看的一条发现（服务端已校验：title/reason 非空）。</summary>
    public sealed record AiReviewFinding(
        string Title, string Reason, bool SuggestProblem, string SuggestedCriteria, string SuggestedAttempt);

    public sealed record AiEventResult(bool Ok, string Message, List<AiEventDraft> Events, List<AiEventRoute> Routes);

    /// <summary>
    /// AI 助手的客户端入口：gRPC → 服务端 → DeepSeek，外加上「当天缓存」与「失败静默」。
    ///
    /// 三条规矩（§27）：
    ///  1. **只有用户点了才会调用**，没有任何后台定时任务。
    ///  2. **当天同一件事只算一次**：结果缓存在本机偏好里，反复点不重复烧 token
    ///     （长按/多点/来回切页都会让同一个按钮被点很多次，没有缓存的话这是纯粹的钱）。
    ///  3. **失败不弹错**：返回 Ok=false，调用方降级到本地规则版。
    ///     用户看到的应该是"这次没想出更好的"，而不是"网络错误"。
    ///
    /// key 不在这里 —— 它在服务端的 ai.json 里，客户端连它的影子都看不到。
    /// </summary>
    public sealed class AiAssistService
    {
        /// <summary>整条链路（手机 → 服务端 → 模型）的超时。比服务端自己的 25 秒稍宽一点。</summary>
        private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(32);

        private const string KeyPrefix = "ph.ai.v1.";

        private readonly ServerConnection _conn;

        public AiAssistService(ServerConnection conn) => _conn = conn;

        /// <summary>当天日期（UTC+8）—— 缓存是按天失效的。</summary>
        private static string Today => DateTimeOffset.UtcNow.ToOffset(Period.Cst).ToString("yyyyMMdd");

        // ============================================================
        // 状态（管理端/页面可以显示"服务端到底配没配"）
        // ============================================================

        public async Task<AiStatusResponse?> GetStatusAsync(CancellationToken ct = default)
        {
            try
            {
                if (!await _conn.EnsureAsync().ConfigureAwait(false)) return null;
                return await _conn.NewAiClient()
                    .GetAiStatusAsync(new Empty(), deadline: DateTime.UtcNow.AddSeconds(5), cancellationToken: ct)
                    .ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
        }

        // ============================================================
        // A. 提议下一步
        // ============================================================

        /// <summary>
        /// 帮这个目标想 1~3 个"今天能做完"的下一步。
        /// force=false 时先看当天的缓存；force=true 强制重新生成（"都不对，再想一次"）。
        /// </summary>
        public async Task<AiStepResult> SuggestNextStepsAsync(
            string goalId, bool force = false, CancellationToken ct = default)
        {
            string cacheKey = KeyPrefix + "next." + goalId + "." + Today;

            if (!force)
            {
                var cached = ReadCache<List<AiStepOption>>(cacheKey);
                if (cached is { Count: > 0 })
                {
                    return new AiStepResult(true, true, "今天已经想过了（缓存）", cached);
                }
            }

            try
            {
                if (!await _conn.EnsureAsync().ConfigureAwait(false))
                {
                    return new AiStepResult(false, false, "暂时连不上服务端", new());
                }

                var resp = await _conn.NewAiClient()
                    .SuggestNextStepsAsync(
                        new SuggestNextStepsRequest { GoalId = goalId },
                        deadline: DateTime.UtcNow.Add(CallTimeout),
                        cancellationToken: ct)
                    .ConfigureAwait(false);

                if (!resp.Ok || resp.Options.Count == 0)
                {
                    return new AiStepResult(false, false, resp.ErrorMsg ?? "这次没想出来", new());
                }

                var options = resp.Options
                    .Select(o => new AiStepOption(o.Action, o.PieceCode, o.Minutes))
                    .Where(o => !string.IsNullOrWhiteSpace(o.Action))
                    .ToList();

                if (options.Count == 0)
                {
                    return new AiStepResult(false, false, "返回里没有可用的一条", new());
                }

                WriteCache(cacheKey, options);
                return new AiStepResult(true, false, string.Empty, options);
            }
            catch (Exception ex)
            {
                return new AiStepResult(false, false, "调用失败：" + ex.Message, new());
            }
        }

        // ============================================================
        // B. 评价聚类 → 模式提议
        // ============================================================

        public async Task<AiPatternResult> FindPatternsAsync(
            int days = 180, bool force = false, CancellationToken ct = default)
        {
            string cacheKey = KeyPrefix + "patterns." + Today;

            if (!force)
            {
                var cached = ReadCache<List<AiPattern>>(cacheKey);
                if (cached is { Count: > 0 })
                {
                    return new AiPatternResult(true, "今天已经看过了（缓存）", cached, 0);
                }
            }

            try
            {
                if (!await _conn.EnsureAsync().ConfigureAwait(false))
                {
                    return new AiPatternResult(false, "暂时连不上服务端", new(), 0);
                }

                var resp = await _conn.NewAiClient()
                    .FindRepeatingEvaluationsAsync(
                        new FindRepeatingRequest { Days = days },
                        deadline: DateTime.UtcNow.Add(CallTimeout),
                        cancellationToken: ct)
                    .ConfigureAwait(false);

                if (!resp.Ok || resp.Proposals.Count == 0)
                {
                    return new AiPatternResult(false, resp.ErrorMsg ?? "没找到反复出现的", new(), resp.Scanned);
                }

                var items = resp.Proposals
                    .Select(p => new AiPattern(
                        p.Title, p.Trigger, p.OldResponse, p.NewResponse,
                        p.Evidence.ToList(), p.Count))
                    .ToList();

                WriteCache(cacheKey, items);
                return new AiPatternResult(true, string.Empty, items, resp.Scanned);
            }
            catch (Exception ex)
            {
                return new AiPatternResult(false, "调用失败：" + ex.Message, new(), 0);
            }
        }

        // ============================================================
        // C. 周回顾一句话
        // ============================================================

        /// <summary>weekKey 用这一周的起始日（yyyyMMdd）—— 同一周只看一次，翻回来不该再调一次。</summary>
        public async Task<AiTextResult> CommentWeekAsync(
            WeekFacts facts, string weekKey, bool force = false, CancellationToken ct = default)
        {
            string cacheKey = KeyPrefix + "week." + weekKey;

            if (!force)
            {
                string cached = Preferences.Default.Get(cacheKey, string.Empty);
                if (!string.IsNullOrWhiteSpace(cached))
                {
                    return new AiTextResult(true, string.Empty, cached);
                }
            }

            try
            {
                if (!await _conn.EnsureAsync().ConfigureAwait(false))
                {
                    return new AiTextResult(false, "暂时连不上服务端", string.Empty);
                }

                var resp = await _conn.NewAiClient()
                    .CommentWeekAsync(facts,
                        deadline: DateTime.UtcNow.Add(CallTimeout),
                        cancellationToken: ct)
                    .ConfigureAwait(false);

                if (!resp.Ok || string.IsNullOrWhiteSpace(resp.Text))
                {
                    return new AiTextResult(false, resp.ErrorMsg ?? "这次没说", string.Empty);
                }

                try { Preferences.Default.Set(cacheKey, resp.Text); } catch { /* 缓存失败不影响显示 */ }

                return new AiTextResult(true, string.Empty, resp.Text);
            }
            catch (Exception ex)
            {
                return new AiTextResult(false, "调用失败：" + ex.Message, string.Empty);
            }
        }

        // ============================================================
        // C2. 目标 30 天：归纳，不建议
        // ============================================================
        //
        // **不做缓存**：输入是"截至现在的 30 天"，每记一块就变一次。
        // 缓存在这里会在最需要新数据的时候把旧结论端上来。按钮低频，不缓存更诚实。

        public async Task<(bool Ok, string Message, List<string> Lines)> CommentGoalAsync(
            AiGoalDigest digest, CancellationToken ct = default)
        {
            try
            {
                if (!await _conn.EnsureAsync().ConfigureAwait(false))
                {
                    return (false, "暂时连不上服务端", new List<string>());
                }

                var facts = new GoalFacts
                {
                    GoalTitle = digest.GoalTitle,
                    Days = digest.Days,
                    Blocks = digest.Blocks,
                    Minutes = digest.Minutes,
                    WorthGood = digest.WorthGood,
                    WorthMid = digest.WorthMid,
                    WorthBad = digest.WorthBad,
                    TopPiece = digest.TopPiece,
                };
                facts.Lessons.AddRange(digest.Lessons);
                facts.BadNotes.AddRange(digest.BadNotes);

                var resp = await _conn.NewAiClient()
                    .CommentGoalAsync(facts,
                        deadline: DateTime.UtcNow.Add(CallTimeout),
                        cancellationToken: ct)
                    .ConfigureAwait(false);

                if (!resp.Ok || resp.Lines.Count == 0)
                {
                    return (false, resp.ErrorMsg ?? "这次没说", new List<string>());
                }

                return (true, string.Empty, resp.Lines.ToList());
            }
            catch (Exception ex)
            {
                return (false, "调用失败：" + ex.Message, new List<string>());
            }
        }

        // ============================================================
        // D. 事件卡：自由文字 → 结构化事件草稿（R7）
        // ============================================================
        //
        // 与上面三个方法不同：**不做当天缓存**。缓存的意义是"同一件事反复点"，
        // 而这里的输入是用户现打的文字，每次都不一样，没有可缓存的东西。

        public async Task<AiEventResult> ExtractEventsAsync(
            string rawText,
            IReadOnlyList<(string Id, string Title)> problems,
            IReadOnlyList<(string Id, string Title)> patterns,
            IReadOnlyList<string> tagOptions,
            IReadOnlyList<string> emotionOptions,
            CancellationToken ct = default)
        {
            try
            {
                if (!await _conn.EnsureAsync().ConfigureAwait(false))
                {
                    return new AiEventResult(false, "暂时连不上服务端（原文可以先存着）", new(), new());
                }

                var req = new ExtractEventsRequest { RawText = rawText };
                foreach (var (id, title) in problems)
                {
                    req.KnownProblems.Add(new KnownRef { Id = id, Title = title });
                }
                foreach (var (id, title) in patterns)
                {
                    req.KnownPatterns.Add(new KnownRef { Id = id, Title = title });
                }
                req.TagOptions.AddRange(tagOptions);
                req.EmotionOptions.AddRange(emotionOptions);

                var resp = await _conn.NewAiClient()
                    .ExtractEventsAsync(req,
                        deadline: DateTime.UtcNow.Add(CallTimeout),
                        cancellationToken: ct)
                    .ConfigureAwait(false);

                if (!resp.Ok || resp.Events.Count == 0)
                {
                    return new AiEventResult(false, resp.ErrorMsg ?? "这次没整理出来", new(), new());
                }

                var events = resp.Events
                    .Where(e => !string.IsNullOrWhiteSpace(e.Facts))
                    .Select(e => new AiEventDraft(
                        e.EventIndex, e.Title ?? string.Empty, e.Emotion ?? string.Empty,
                        e.Tag ?? string.Empty, e.Facts, e.Action ?? string.Empty,
                        e.InitialThought ?? string.Empty, e.Result ?? string.Empty,
                        e.Question ?? string.Empty))
                    .ToList();

                var routes = resp.Routes
                    .Select(r => new AiEventRoute(
                        r.EventIndex, r.MatchProblemId ?? string.Empty, r.MatchPatternId ?? string.Empty,
                        r.NewPattern is null
                            ? null
                            : new AiPatternDraft(r.NewPattern.Title ?? string.Empty, r.NewPattern.Reason ?? string.Empty),
                        r.SuggestedAttempt ?? string.Empty))
                    .ToList();

                if (events.Count == 0)
                {
                    return new AiEventResult(false, "返回里没有可用的一张卡", new(), new());
                }

                return new AiEventResult(true, string.Empty, events, routes);
            }
            catch (Exception ex)
            {
                return new AiEventResult(false, "调用失败：" + ex.Message, new(), new());
            }
        }

        // ============================================================
        // D2. AI 周回看：近 30 天事件卡 → 重复苗头（防盲区，手动触发）
        // ============================================================

        /// <summary>
        /// 让 AI 扫一遍近期事件卡，主动说"我注意到这几件事在重复"。
        /// 不做当天缓存——事件列表每次都在变，缓存反而是误导。
        /// </summary>
        public async Task<(bool Ok, string Message, List<AiReviewFinding> Findings)> ReviewEventsAsync(
            IReadOnlyList<(string Day, string Title, string Facts)> events,
            IReadOnlyList<(string Id, string Title)> problems,
            IReadOnlyList<(string Id, string Title)> patterns,
            CancellationToken ct = default)
        {
            try
            {
                if (!await _conn.EnsureAsync().ConfigureAwait(false))
                {
                    return (false, "暂时连不上服务端", new());
                }

                var req = new ReviewEventsRequest();
                foreach (var (day, title, facts) in events)
                {
                    req.Events.Add(new ReviewEvent { Day = day, Title = title, Facts = facts });
                }
                foreach (var (id, title) in problems)
                {
                    req.KnownProblems.Add(new KnownRef { Id = id, Title = title });
                }
                foreach (var (id, title) in patterns)
                {
                    req.KnownPatterns.Add(new KnownRef { Id = id, Title = title });
                }

                var resp = await _conn.NewAiClient()
                    .ReviewEventsAsync(req,
                        deadline: DateTime.UtcNow.Add(CallTimeout),
                        cancellationToken: ct)
                    .ConfigureAwait(false);

                if (!resp.Ok || resp.Findings.Count == 0)
                {
                    return (false, resp.ErrorMsg ?? "这次没看出苗头", new());
                }

                var findings = resp.Findings
                    .Where(f => !string.IsNullOrWhiteSpace(f.Title) && !string.IsNullOrWhiteSpace(f.Reason))
                    .Select(f => new AiReviewFinding(
                        f.Title, f.Reason, f.SuggestProblem,
                        f.SuggestedCriteria ?? string.Empty, f.SuggestedAttempt ?? string.Empty))
                    .ToList();

                if (findings.Count == 0)
                {
                    return (false, "返回里没有可用的一条发现", new());
                }

                return (true, string.Empty, findings);
            }
            catch (Exception ex)
            {
                return (false, "调用失败：" + ex.Message, new());
            }
        }

        // ============================================================
        // 缓存（本机偏好，不同步；按天失效，不需要清理）
        // ============================================================

        private static T? ReadCache<T>(string key) where T : class
        {
            try
            {
                string json = Preferences.Default.Get(key, string.Empty);
                if (string.IsNullOrWhiteSpace(json)) return null;
                return JsonSerializer.Deserialize<T>(json);
            }
            catch
            {
                return null;
            }
        }

        private static void WriteCache<T>(string key, T value)
        {
            try
            {
                Preferences.Default.Set(key, JsonSerializer.Serialize(value));
            }
            catch
            {
                // 写不进去只影响"会不会重复调用一次"，不影响这次结果
            }
        }
    }
}
