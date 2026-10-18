using Grpc.Core;
using Microsoft.Extensions.Logging;
using Personalhub;
using personalHub.Server.Data;

namespace personalHub.Server.Services
{
    /// <summary>
    /// 事件反思服务实现。与 proto 的 ThingService 对应。
    /// 结构与 <see cref="RecordServiceImpl"/> 对称：上传 / 取今日。
    /// 约定：Thing.occurred_at、Thing.created_at 均为 Unix 毫秒时间戳。
    /// </summary>
    public class ThingServiceImpl : ThingService.ThingServiceBase
    {
        private readonly ILogger<ThingServiceImpl> _logger;
        private readonly ThingStore _store;

        public ThingServiceImpl(ILogger<ThingServiceImpl> logger, ThingStore store)
        {
            _logger = logger;
            _store = store;
        }

        /// <summary>
        /// 上传一批事件反思。最小校验：标题不能为空（其余长文本允许留空，
        /// 客户端已经强制必填，服务端不重复做严格校验，避免两端规则不一致把历史数据卡住）。
        /// </summary>
        public override Task<UploadThingsResponse> UploadThings(
            UploadThingsRequest request, ServerCallContext context)
        {
            var accepted = new List<Thing>();
            var failedIds = new List<string>();
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            foreach (var t in request.Things)
            {
                if (string.IsNullOrWhiteSpace(t.Title))
                {
                    failedIds.Add(string.IsNullOrEmpty(t.Id) ? "(无 id)" : t.Id);
                    continue;
                }

                // 补齐服务器负责的字段
                if (string.IsNullOrEmpty(t.Id))
                {
                    t.Id = Guid.NewGuid().ToString("N");
                }
                if (t.CreatedAt == 0)
                {
                    t.CreatedAt = nowMs;
                }
                if (t.OccurredAt == 0)
                {
                    t.OccurredAt = t.CreatedAt;
                }

                accepted.Add(t);
            }

            // 幂等写入：按 id 去重，重复同步不会产生重复记录。
            _store.Upsert(accepted);

            var response = new UploadThingsResponse { AcceptedCount = accepted.Count };
            response.FailedIds.AddRange(failedIds);

            _logger.LogInformation(
                "UploadThings: 收到 {Total} 条，通过 {Ok} 条，失败 {Bad} 条",
                request.Things.Count, accepted.Count, failedIds.Count);

            return Task.FromResult(response);
        }

        /// <summary>取「今天」的事件反思（固定 UTC+8，与统计口径一致）。</summary>
        public override Task<ThingList> GetTodayThings(Empty request, ServerCallContext context)
        {
            var list = new ThingList();
            list.Things.AddRange(_store.ByDay(DateTimeOffset.UtcNow));

            _logger.LogInformation("GetTodayThings: 返回 {Count} 条", list.Things.Count);
            return Task.FromResult(list);
        }
    }
}
