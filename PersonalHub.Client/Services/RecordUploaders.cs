using Personalhub;
using PersonalHub.Client.Data;

namespace PersonalHub.Client.Services
{
    /// <summary>
    /// 账单上传器：LocalRecord 列表 → UploadRecordsRequest → RecordService.UploadRecords。
    ///
    /// 这里就是「不同 proto 定义带来的差异」的全部所在地。
    /// 再加一类记录，只是再写一个长得差不多的类，同步流程完全不动。
    /// </summary>
    public sealed class RecordUploader : IRecordUploader<LocalRecord>
    {
        /// <summary>单次上传的超时。局域网内正常是毫秒级，10 秒足够兜住"Wi-Fi 假连通"。</summary>
        private static readonly TimeSpan UploadTimeout = TimeSpan.FromSeconds(10);

        private readonly ServerConnection _conn;

        public RecordUploader(ServerConnection conn) => _conn = conn;

        public string DisplayName => "账单";

        public async Task<UploadOutcome> UploadAsync(IReadOnlyList<LocalRecord> items, CancellationToken ct = default)
        {
            if (items.Count == 0) return UploadOutcome.Empty;

            // 通道可能因为换网络失效，这里确保一下（EnsureAsync 内部有锁，重复调用很便宜）。
            await _conn.EnsureAsync().ConfigureAwait(false);

            var request = new UploadRecordsRequest();
            request.Records.AddRange(items.Select(x => x.ToProto()));

            // gRPC 客户端必须**每次从当前通道新建**，不能缓存单例（通道换了客户端就指向旧地址）。
            var resp = await _conn.NewRecordClient()
                .UploadRecordsAsync(
                    request,
                    deadline: DateTime.UtcNow.Add(UploadTimeout),
                    cancellationToken: ct)
                .ConfigureAwait(false);

            return new UploadOutcome
            {
                Accepted = resp.AcceptedCount,
                FailedIds = resp.FailedIds.ToArray(),
                ErrorMsg = string.IsNullOrEmpty(resp.ErrorMsg) ? null : resp.ErrorMsg,
            };
        }
    }

    /// <summary>
    /// 事件反思上传器：ThingRecord 列表 → UploadThingsRequest → ThingService.UploadThings。
    /// 与 <see cref="RecordUploader"/> 结构完全对称，只有字段和 RPC 名字不同。
    /// </summary>
    public sealed class ThingUploader : IRecordUploader<ThingRecord>
    {
        private static readonly TimeSpan UploadTimeout = TimeSpan.FromSeconds(10);

        private readonly ServerConnection _conn;

        public ThingUploader(ServerConnection conn) => _conn = conn;

        public string DisplayName => "事件";

        public async Task<UploadOutcome> UploadAsync(IReadOnlyList<ThingRecord> items, CancellationToken ct = default)
        {
            if (items.Count == 0) return UploadOutcome.Empty;

            await _conn.EnsureAsync().ConfigureAwait(false);

            var request = new UploadThingsRequest();
            request.Things.AddRange(items.Select(x => x.ToProto()));

            var resp = await _conn.NewThingClient()
                .UploadThingsAsync(
                    request,
                    deadline: DateTime.UtcNow.Add(UploadTimeout),
                    cancellationToken: ct)
                .ConfigureAwait(false);

            return new UploadOutcome
            {
                Accepted = resp.AcceptedCount,
                FailedIds = resp.FailedIds.ToArray(),
                ErrorMsg = string.IsNullOrEmpty(resp.ErrorMsg) ? null : resp.ErrorMsg,
            };
        }
    }

    /// <summary>
    /// 时间块上传器：LocalBlock 列表 → UploadBlocksRequest → BlockService.UploadBlocks。
    /// 与上面两个结构完全对称 —— 这就是把 proto 差异关进 UploadAsync 的好处。
    /// </summary>
    public sealed class BlockUploader : IRecordUploader<LocalBlock>
    {
        private static readonly TimeSpan UploadTimeout = TimeSpan.FromSeconds(10);

        private readonly ServerConnection _conn;

        public BlockUploader(ServerConnection conn) => _conn = conn;

        public string DisplayName => "时间块";

        public async Task<UploadOutcome> UploadAsync(IReadOnlyList<LocalBlock> items, CancellationToken ct = default)
        {
            if (items.Count == 0) return UploadOutcome.Empty;

            await _conn.EnsureAsync().ConfigureAwait(false);

            var request = new UploadBlocksRequest();
            request.Blocks.AddRange(items.Select(x => x.ToProto()));

            var resp = await _conn.NewBlockClient()
                .UploadBlocksAsync(
                    request,
                    deadline: DateTime.UtcNow.Add(UploadTimeout),
                    cancellationToken: ct)
                .ConfigureAwait(false);

            return new UploadOutcome
            {
                Accepted = resp.AcceptedCount,
                FailedIds = resp.FailedIds.ToArray(),
                ErrorMsg = string.IsNullOrEmpty(resp.ErrorMsg) ? null : resp.ErrorMsg,
            };
        }
    }

    /// <summary>计划上传器：LocalPlan 列表 → UploadPlansRequest → PlanService.UploadPlans。</summary>
    public sealed class PlanUploader : IRecordUploader<LocalPlan>
    {
        private static readonly TimeSpan UploadTimeout = TimeSpan.FromSeconds(10);

        private readonly ServerConnection _conn;

        public PlanUploader(ServerConnection conn) => _conn = conn;

        public string DisplayName => "计划";

        public async Task<UploadOutcome> UploadAsync(IReadOnlyList<LocalPlan> items, CancellationToken ct = default)
        {
            if (items.Count == 0) return UploadOutcome.Empty;

            await _conn.EnsureAsync().ConfigureAwait(false);

            var request = new UploadPlansRequest();
            request.Plans.AddRange(items.Select(x => x.ToProto()));

            var resp = await _conn.NewPlanClient()
                .UploadPlansAsync(
                    request,
                    deadline: DateTime.UtcNow.Add(UploadTimeout),
                    cancellationToken: ct)
                .ConfigureAwait(false);

            return new UploadOutcome
            {
                Accepted = resp.AcceptedCount,
                FailedIds = resp.FailedIds.ToArray(),
                ErrorMsg = string.IsNullOrEmpty(resp.ErrorMsg) ? null : resp.ErrorMsg,
            };
        }
    }

    /// <summary>目标上传器：LocalGoal 列表 → UploadGoalsRequest → GoalService.UploadGoals。</summary>
    public sealed class GoalUploader : IRecordUploader<LocalGoal>
    {
        private static readonly TimeSpan UploadTimeout = TimeSpan.FromSeconds(10);

        private readonly ServerConnection _conn;

        public GoalUploader(ServerConnection conn) => _conn = conn;

        public string DisplayName => "目标";

        public async Task<UploadOutcome> UploadAsync(IReadOnlyList<LocalGoal> items, CancellationToken ct = default)
        {
            if (items.Count == 0) return UploadOutcome.Empty;

            await _conn.EnsureAsync().ConfigureAwait(false);

            var request = new UploadGoalsRequest();
            request.Goals.AddRange(items.Select(x => x.ToProto()));

            var resp = await _conn.NewGoalClient()
                .UploadGoalsAsync(
                    request,
                    deadline: DateTime.UtcNow.Add(UploadTimeout),
                    cancellationToken: ct)
                .ConfigureAwait(false);

            return new UploadOutcome
            {
                Accepted = resp.AcceptedCount,
                FailedIds = resp.FailedIds.ToArray(),
                ErrorMsg = string.IsNullOrEmpty(resp.ErrorMsg) ? null : resp.ErrorMsg,
            };
        }
    }

    /// <summary>模式上传器：LocalPattern 列表 → UploadPatternsRequest → PatternService.UploadPatterns。</summary>
    public sealed class PatternUploader : IRecordUploader<LocalPattern>
    {
        private static readonly TimeSpan UploadTimeout = TimeSpan.FromSeconds(10);

        private readonly ServerConnection _conn;

        public PatternUploader(ServerConnection conn) => _conn = conn;

        public string DisplayName => "模式";

        public async Task<UploadOutcome> UploadAsync(IReadOnlyList<LocalPattern> items, CancellationToken ct = default)
        {
            if (items.Count == 0) return UploadOutcome.Empty;

            await _conn.EnsureAsync().ConfigureAwait(false);

            var request = new UploadPatternsRequest();
            request.Patterns.AddRange(items.Select(x => x.ToProto()));

            var resp = await _conn.NewPatternClient()
                .UploadPatternsAsync(request,
                    deadline: DateTime.UtcNow.Add(UploadTimeout),
                    cancellationToken: ct)
                .ConfigureAwait(false);

            return new UploadOutcome
            {
                Accepted = resp.AcceptedCount,
                FailedIds = resp.FailedIds.ToArray(),
                ErrorMsg = string.IsNullOrEmpty(resp.ErrorMsg) ? null : resp.ErrorMsg,
            };
        }
    }

    /// <summary>练习上传器：LocalPractice 列表 → UploadPracticesRequest → PatternService.UploadPractices。</summary>
    public sealed class PracticeUploader : IRecordUploader<LocalPractice>
    {
        private static readonly TimeSpan UploadTimeout = TimeSpan.FromSeconds(10);

        private readonly ServerConnection _conn;

        public PracticeUploader(ServerConnection conn) => _conn = conn;

        public string DisplayName => "练习";

        public async Task<UploadOutcome> UploadAsync(IReadOnlyList<LocalPractice> items, CancellationToken ct = default)
        {
            if (items.Count == 0) return UploadOutcome.Empty;

            await _conn.EnsureAsync().ConfigureAwait(false);

            var request = new UploadPracticesRequest();
            request.Practices.AddRange(items.Select(x => x.ToProto()));

            var resp = await _conn.NewPatternClient()
                .UploadPracticesAsync(request,
                    deadline: DateTime.UtcNow.Add(UploadTimeout),
                    cancellationToken: ct)
                .ConfigureAwait(false);

            return new UploadOutcome
            {
                Accepted = resp.AcceptedCount,
                FailedIds = resp.FailedIds.ToArray(),
                ErrorMsg = string.IsNullOrEmpty(resp.ErrorMsg) ? null : resp.ErrorMsg,
            };
        }
    }

    /// <summary>想法上传器：LocalIdea 列表 → UploadIdeasRequest → IdeaService.UploadIdeas。</summary>
    public sealed class IdeaUploader : IRecordUploader<LocalIdea>
    {
        private static readonly TimeSpan UploadTimeout = TimeSpan.FromSeconds(10);

        private readonly ServerConnection _conn;

        public IdeaUploader(ServerConnection conn) => _conn = conn;

        public string DisplayName => "想法";

        public async Task<UploadOutcome> UploadAsync(IReadOnlyList<LocalIdea> items, CancellationToken ct = default)
        {
            if (items.Count == 0) return UploadOutcome.Empty;

            await _conn.EnsureAsync().ConfigureAwait(false);

            var request = new UploadIdeasRequest();
            request.Ideas.AddRange(items.Select(x => x.ToProto()));

            var resp = await _conn.NewIdeaClient()
                .UploadIdeasAsync(request,
                    deadline: DateTime.UtcNow.Add(UploadTimeout),
                    cancellationToken: ct)
                .ConfigureAwait(false);

            return new UploadOutcome
            {
                Accepted = resp.AcceptedCount,
                FailedIds = resp.FailedIds.ToArray(),
                ErrorMsg = string.IsNullOrEmpty(resp.ErrorMsg) ? null : resp.ErrorMsg,
            };
        }
    }
}
