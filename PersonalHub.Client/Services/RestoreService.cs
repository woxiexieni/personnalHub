using Personalhub;
using PersonalHub.Client.Data;

namespace PersonalHub.Client.Services
{
    /// <summary>一类记录的恢复结果（给界面显示一行）。</summary>
    public sealed record RestoreLine(string Name, RestoreStats? Stats, string? Error)
    {
        public bool Ok => Error is null;
    }

    /// <summary>一次完整恢复的结果。</summary>
    public sealed record RestoreReport(bool Ok, string Message, IReadOnlyList<RestoreLine> Lines);

    /// <summary>
    /// 「把服务端的数据搬回这台设备」。
    ///
    /// 存在的唯一理由：**卸载重装 / 换手机 / 手机丢了**。
    /// 本地库在 App 私有目录里，卸载就是删目录，这一点改不了（除非做云端），
    /// 所以数据安全的真正防线只能是服务端那一份 —— 而以前的同步链路只有"上传"，
    /// 没有任何一条路能把历史拉回来，等于备份存在但没法恢复。
    ///
    /// 与已有镜像服务（LocalCacheService / BlockMirrorService）的分工：
    ///   镜像  = 日常对齐「最近 30 天」，带墓碑，会清理窗口外的副本 → 越用越轻
    ///   恢复  = 一次性把「全部历史」搬回来，不带墓碑，绝不删除     → 只在新设备上用
    /// 两者的合并规则也不一样：恢复绝不删除本地任何东西，只补和覆盖。
    ///
    /// 并发保护：整个过程持一把信号量，且**不让两个恢复同时跑**
    /// （两个并发恢复会各自插入同一批行，凭 id 不会重复，但会白烧一倍流量）。
    /// </summary>
    public sealed class RestoreService
    {
        /// <summary>2000-01-01（UTC）。早于这个系统可能存在的任何一条数据。</summary>
        private const long EpochFrom = 946684800000L;

        /// <summary>单次列表调用的超时。恢复是"一次性搬完"，比日常镜像给得宽一些。</summary>
        private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(20);

        private readonly ServerConnection _conn;
        private readonly LocalRecordStore<LocalRecord> _records;
        private readonly LocalRecordStore<ThingRecord> _things;
        private readonly LocalRecordStore<LocalBlock> _blocks;
        private readonly LocalRecordStore<LocalGoal> _goals;
        private readonly LocalRecordStore<LocalPlan> _plans;
        private readonly LocalRecordStore<LocalPattern> _patterns;
        private readonly LocalRecordStore<LocalPractice> _practices;
        private readonly LocalRecordStore<LocalIdea> _ideas;
        private readonly SemaphoreSlim _gate = new(1, 1);

        public RestoreService(
            ServerConnection conn,
            LocalRecordStore<LocalRecord> records,
            LocalRecordStore<ThingRecord> things,
            LocalRecordStore<LocalBlock> blocks,
            LocalRecordStore<LocalGoal> goals,
            LocalRecordStore<LocalPlan> plans,
            LocalRecordStore<LocalPattern> patterns,
            LocalRecordStore<LocalPractice> practices,
            LocalRecordStore<LocalIdea> ideas)
        {
            _conn = conn;
            _records = records;
            _things = things;
            _blocks = blocks;
            _goals = goals;
            _plans = plans;
            _patterns = patterns;
            _practices = practices;
            _ideas = ideas;
        }

        /// <summary>本地这台设备一共存了多少条（八类合计）。0 = 全新的空设备。</summary>
        public async Task<int> CountLocalAsync()
        {
            return await _records.CountAllAsync().ConfigureAwait(false)
                 + await _things.CountAllAsync().ConfigureAwait(false)
                 + await _blocks.CountAllAsync().ConfigureAwait(false)
                 + await _goals.CountAllAsync().ConfigureAwait(false)
                 + await _plans.CountAllAsync().ConfigureAwait(false)
                 + await _patterns.CountAllAsync().ConfigureAwait(false)
                 + await _practices.CountAllAsync().ConfigureAwait(false)
                 + await _ideas.CountAllAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// 全量恢复。任何一类失败都不影响其它类继续 ——
        /// 半途网络断掉时，"账单回来了、想法没回来"远好过"什么都没回来"。
        /// </summary>
        public async Task<RestoreReport> RestoreAllAsync(
            IProgress<string>? progress = null,
            CancellationToken ct = default)
        {
            if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false))
            {
                return new RestoreReport(false, "已经在恢复了，等这一轮跑完。", Array.Empty<RestoreLine>());
            }

            try
            {
                progress?.Report("正在连服务端…");

                if (!await _conn.EnsureAsync().ConfigureAwait(false))
                {
                    return new RestoreReport(false,
                        "连不上服务端。恢复需要服务端在线 —— 先把手机和电脑连到同一个 Wi-Fi 再试。",
                        Array.Empty<RestoreLine>());
                }

                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                long to = now + 24L * 60 * 60 * 1000;   // 给"服务器时钟略快"留一天余量
                var lines = new List<RestoreLine>();

                // ---- 账单 ----
                lines.Add(await StepAsync("账单", _records, progress, async c =>
                {
                    var r = await _conn.NewRecordClient()
                        .ListRecordsAsync(new ListRecordsRequest
                        {
                            From = EpochFrom, To = to,
                            Direction = -1,
                            IncludeDeleted = false,
                        }, deadline: DateTime.UtcNow.Add(CallTimeout), cancellationToken: c)
                        .ConfigureAwait(false);

                    return r.Records.Select(x => x.ToLocalRecord()).ToList();
                }, now, ct).ConfigureAwait(false));

                // ---- 事件反思 ----
                lines.Add(await StepAsync("事件反思", _things, progress, async c =>
                {
                    var r = await _conn.NewThingClient()
                        .ListThingsAsync(new ListThingsRequest { From = EpochFrom, To = to },
                            deadline: DateTime.UtcNow.Add(CallTimeout), cancellationToken: c)
                        .ConfigureAwait(false);

                    return r.Things.Select(ToLocalThing).ToList();
                }, now, ct).ConfigureAwait(false));

                // ---- 时间块 ----
                lines.Add(await StepAsync("时间块", _blocks, progress, async c =>
                {
                    var r = await _conn.NewBlockClient()
                        .ListBlocksAsync(new ListBlocksRequest
                        {
                            From = EpochFrom, To = to,
                            IncludeDeleted = false,
                        }, deadline: DateTime.UtcNow.Add(CallTimeout), cancellationToken: c)
                        .ConfigureAwait(false);

                    return r.Blocks.Select(x => x.ToLocalBlock()).ToList();
                }, now, ct).ConfigureAwait(false));

                // ---- 目标 ----
                lines.Add(await StepAsync("目标", _goals, progress, async c =>
                {
                    var r = await _conn.NewGoalClient()
                        .ListGoalsAsync(new Empty(),
                            deadline: DateTime.UtcNow.Add(CallTimeout), cancellationToken: c)
                        .ConfigureAwait(false);

                    return r.Goals.Select(x => x.ToLocalGoal()).ToList();
                }, now, ct).ConfigureAwait(false));

                // ---- 每日计划 ----
                lines.Add(await StepAsync("计划", _plans, progress, async c =>
                {
                    var r = await _conn.NewPlanClient()
                        .ListPlansAsync(new ListPlansRequest { From = EpochFrom, To = to },
                            deadline: DateTime.UtcNow.Add(CallTimeout), cancellationToken: c)
                        .ConfigureAwait(false);

                    return r.Plans.Select(x => x.ToLocalPlan()).ToList();
                }, now, ct).ConfigureAwait(false));

                // ---- 行为模式 ----
                lines.Add(await StepAsync("模式", _patterns, progress, async c =>
                {
                    var r = await _conn.NewPatternClient()
                        .ListPatternsAsync(new Empty(),
                            deadline: DateTime.UtcNow.Add(CallTimeout), cancellationToken: c)
                        .ConfigureAwait(false);

                    return r.Patterns.Select(x => x.ToLocalPattern()).ToList();
                }, now, ct).ConfigureAwait(false));

                // ---- 练习记录 ----
                lines.Add(await StepAsync("练习", _practices, progress, async c =>
                {
                    var r = await _conn.NewPatternClient()
                        .ListPracticesAsync(new ListPracticesRequest { From = EpochFrom, To = to },
                            deadline: DateTime.UtcNow.Add(CallTimeout), cancellationToken: c)
                        .ConfigureAwait(false);

                    return r.Practices.Select(x => x.ToLocalPractice()).ToList();
                }, now, ct).ConfigureAwait(false));

                // ---- 想法 ----
                lines.Add(await StepAsync("想法", _ideas, progress, async c =>
                {
                    var r = await _conn.NewIdeaClient()
                        .ListIdeasAsync(new ListIdeasRequest
                        {
                            Kind = -1, Done = -1,
                            From = EpochFrom, To = to,
                            IncludeDeleted = false,
                        }, deadline: DateTime.UtcNow.Add(CallTimeout), cancellationToken: c)
                        .ConfigureAwait(false);

                    return r.Ideas.Select(x => x.ToLocalIdea()).ToList();
                }, now, ct).ConfigureAwait(false));

                int added = lines.Where(l => l.Stats is not null).Sum(l => l.Stats!.Added);
                int updated = lines.Where(l => l.Stats is not null).Sum(l => l.Stats!.Updated);
                int failed = lines.Count(l => !l.Ok);
                int remoteTotal = lines.Where(l => l.Stats is not null).Sum(l => l.Stats!.RemoteTotal);

                string msg = remoteTotal == 0
                    ? "服务端也没有数据。这台设备现在是干净的，开始记就行。"
                    : $"服务端共 {remoteTotal} 条：补进来 {added} 条，更新 {updated} 条"
                      + (failed > 0 ? $"，{failed} 类没成功（见下面的行）" : "。");

                progress?.Report("完成");
                return new RestoreReport(failed == 0, msg, lines);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// 单类恢复：拉取 → 合并。异常在这里被吃掉并折成一行错误，
        /// 不让"某一类查询报错"把整轮恢复打断。
        /// </summary>
        private static async Task<RestoreLine> StepAsync<T>(
            string name,
            LocalRecordStore<T> store,
            IProgress<string>? progress,
            Func<CancellationToken, Task<List<T>>> fetch,
            long nowMs,
            CancellationToken ct)
            where T : LocalRecordBase, IVersioned, new()
        {
            progress?.Report($"正在拉取{name}…");

            try
            {
                var remote = await fetch(ct).ConfigureAwait(false);
                var stats = await store.RestoreAsync(remote, nowMs).ConfigureAwait(false);
                return new RestoreLine(name, stats, null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new RestoreLine(name, null, ex.Message);
            }
        }

        /// <summary>
        /// proto Thing → 本地事件反思。
        ///
        /// 单独写而不是复用扩展方法：Thing 的本地映射原来只在编辑页用，
        /// 而且 ThingRecord 多一个 CreatedTimestamp 的映射（proto 里叫 created_at），
        /// 名字对不上时最容易写错，放这里一次写清。
        /// </summary>
        private static ThingRecord ToLocalThing(Thing p) => new()
        {
            Id = p.Id,
            Title = p.Title ?? string.Empty,
            Tag = p.Tag ?? string.Empty,
            Emotion = p.Emotion ?? string.Empty,
            OccurredAt = p.OccurredAt == 0 ? p.CreatedAt : p.OccurredAt,
            ObjectiveDescription = p.ObjectiveDescription ?? string.Empty,
            InitialThought = p.InitialThought ?? string.Empty,
            ActionTaken = p.ActionTaken ?? string.Empty,
            FinalResult = p.FinalResult ?? string.Empty,
            Reflection = p.Reflection ?? string.Empty,
            ActionPlan = p.ActionPlan ?? string.Empty,
            CreatedTimestamp = p.CreatedAt,
            UpdatedAt = p.CreatedAt,          // Thing 没有 updated_at，用创建时间当版本
            Deleted = false,
        };
    }
}
