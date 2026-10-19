using Grpc.Core;
using Microsoft.Extensions.Logging;
using Personalhub;
using personalHub.Server.Analysis;
using personalHub.Server.Data;

namespace personalHub.Server.Services
{
    /// <summary>
    /// 时间块服务。与 proto 的 BlockService 对应。
    ///
    /// 三个 RPC 的分工：
    ///   UploadBlocks → 手机端离线队列上来的（幂等，按 id 覆盖）
    ///   ListBlocks   → 手机端 30 天镜像 + 管理端区间查询
    ///   GetPieceStats→ 统计页整块数据，**区间由服务端算**，避免两端各算一遍
    ///
    /// 这里没有"保存成功"的提示语义：手机端存完立刻回列表，服务端接受与否不打断用户。
    /// </summary>
    public class BlockServiceImpl : BlockService.BlockServiceBase
    {
        private readonly ILogger<BlockServiceImpl> _logger;
        private readonly BlockStore _store;

        public BlockServiceImpl(ILogger<BlockServiceImpl> logger, BlockStore store)
        {
            _logger = logger;
            _store = store;
        }

        public override Task<UploadBlocksResponse> UploadBlocks(
            UploadBlocksRequest request, ServerCallContext context)
        {
            var failed = new List<string>();
            int accepted = 0;
            string? firstError = null;

            foreach (var b in request.Blocks)
            {
                var result = _store.Save(b);

                if (result.Ok)
                {
                    accepted++;
                    continue;
                }

                // 冲突也算"拒收但不要重试"：客户端会把它标记为已同步，随后镜像拉服务端版本。
                failed.Add(string.IsNullOrEmpty(b.Id) ? "(无 id)" : b.Id);
                firstError ??= result.Error;
            }

            var resp = new UploadBlocksResponse { AcceptedCount = accepted };
            resp.FailedIds.AddRange(failed);
            if (firstError is not null) resp.ErrorMsg = firstError;

            _logger.LogInformation(
                "UploadBlocks: 收到 {Total} 条，通过 {Ok} 条，失败 {Bad} 条",
                request.Blocks.Count, accepted, failed.Count);

            return Task.FromResult(resp);
        }

        public override Task<BlockList> ListBlocks(ListBlocksRequest request, ServerCallContext context)
        {
            // 没传区间时给"最近 30 天"，避免一次全表扫（手机镜像就是 30 天）
            long to = request.To != 0 ? request.To : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long from = request.From != 0 ? request.From : to - 30L * 24 * 60 * 60 * 1000;

            var list = new BlockList();
            list.Blocks.AddRange(_store.List(
                from, to,
                goalId: string.IsNullOrWhiteSpace(request.GoalId) ? null : request.GoalId,
                pieceCode: string.IsNullOrWhiteSpace(request.PieceCode) ? null : request.PieceCode,
                includeDeleted: request.IncludeDeleted,
                limit: request.Limit));

            return Task.FromResult(list);
        }

        public override Task<PieceStatsResponse> GetPieceStats(
            PieceStatsRequest request, ServerCallContext context)
        {
            var now = DateTimeOffset.UtcNow;
            var kind = PeriodResolver.Parse(request.Period);
            var range = PeriodResolver.Resolve(kind, now);

            var pieces = _store.AggregateByPiece(range.From, range.To);

            int covered = pieces.Sum(p => p.Minutes);

            // 区间内"已经过去"的分钟数。已过才叫"未记录"，未来的时间不算漏记 ——
            // 否则早上八点打开会看到"今天未记录 16 小时"，纯属制造焦虑。
            double elapsedMin = (now.ToUnixTimeMilliseconds() - range.From) / 60000.0;
            if (elapsedMin < 0) elapsedMin = 0;
            double periodMin = TimeSpan.FromMilliseconds(range.To - range.From).TotalMinutes;
            if (elapsedMin > periodMin) elapsedMin = periodMin;

            int total = (int)Math.Floor(elapsedMin);
            int unrecorded = total - covered;
            if (unrecorded < 0) unrecorded = 0;   // 记超了（重叠/估多）不倒扣，只钳到 0

            var resp = new PieceStatsResponse
            {
                Period = kind.ToString().ToLowerInvariant(),
                RangeStart = range.From,
                RangeEnd = range.To,
                TotalMinutes = total,
                CoveredMinutes = covered,
                UnrecordedMinutes = unrecorded,
                GeneratedAt = now.ToUnixTimeMilliseconds(),
            };

            resp.Pieces.AddRange(pieces.Select(p => new PieceSlice
            {
                PieceCode = p.PieceCode,
                Minutes = p.Minutes,
                Count = p.Count,
            }));

            return Task.FromResult(resp);
        }
    }
}
