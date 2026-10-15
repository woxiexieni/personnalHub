using Grpc.Core;
using Microsoft.Extensions.Logging;
using Personalhub;
using personalHub.Server.Data;

namespace personalHub.Server.Services
{
    /// <summary>
    /// 记账服务实现。
    /// 与服务端 proto 的 RecordService 对应：上传账单 / 取今日账单 / 支出趋势。
    /// 约定：Record.spent_at、Record.created_at 均为 Unix 毫秒时间戳。
    /// </summary>
    public class RecordServiceImpl : RecordService.RecordServiceBase
    {
        private readonly ILogger<RecordServiceImpl> _logger;
        private readonly RecordStore _store;

        public RecordServiceImpl(ILogger<RecordServiceImpl> logger, RecordStore store)
        {
            _logger = logger;
            _store = store;
        }

        /// <summary>上传一批账单。逐条做最小校验，金额 &lt;= 0 的记为失败。</summary>
        public override Task<UploadRecordsResponse> UploadRecords(
            UploadRecordsRequest request, ServerCallContext context)
        {
            var accepted = new List<Record>();
            var failedIds = new List<string>();
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            foreach (var r in request.Records)
            {
                if (r.Amount <= 0)
                {
                    failedIds.Add(string.IsNullOrEmpty(r.Id) ? "(无 id)" : r.Id);
                    continue;
                }

                // 补齐服务器负责的字段
                if (string.IsNullOrEmpty(r.Id))
                {
                    r.Id = Guid.NewGuid().ToString("N");
                }
                if (r.CreatedAt == 0)
                {
                    r.CreatedAt = nowMs;
                }
                if (r.SpentAt == 0)
                {
                    r.SpentAt = r.CreatedAt;
                }

                accepted.Add(r);
            }

            // 幂等写入：按 id 去重，重复上传（自动同步多次触发）不会导致账单翻倍。
            _store.Upsert(accepted);

            var response = new UploadRecordsResponse { AcceptedCount = accepted.Count };
            response.FailedIds.AddRange(failedIds);

            _logger.LogInformation(
                "UploadRecords: 收到 {Total} 条，通过 {Ok} 条，失败 {Bad} 条",
                request.Records.Count, accepted.Count, failedIds.Count);

            return Task.FromResult(response);
        }

        /// <summary>取服务器本地时区"今天"的账单。</summary>
        public override Task<RecordList> GetTodayRecords(Empty request, ServerCallContext context)
        {
            var list = new RecordList();
            list.Records.AddRange(_store.ByDay(DateTime.Now));

            _logger.LogInformation("GetTodayRecords: 返回 {Count} 条", list.Records.Count);
            return Task.FromResult(list);
        }

        /// <summary>
        /// 支出趋势。request.Range 支持 "7d" / "30" 这类写法，默认 7 天；
        /// 返回按天聚合的点、总额与日均。
        /// </summary>
        public override Task<TrendResponse> GetExpenseTrend(
            TrendRequest request, ServerCallContext context)
        {
            int days = ParseDays(request.Range);

            var today = DateTime.Now.Date;
            var start = today.AddDays(-(days - 1));
            long startMs = new DateTimeOffset(start).ToUnixTimeMilliseconds();

            var inRange = _store.All().Where(r => r.SpentAt >= startMs).ToList();

            var response = new TrendResponse();
            double total = 0;

            for (int i = 0; i < days; i++)
            {
                var day = start.AddDays(i);
                double sum = inRange
                    .Where(r => RecordStore.ToLocalTime(r.SpentAt).Date == day)
                    .Sum(r => r.Amount);

                response.Points.Add(new TrendPoint
                {
                    Label = day.ToString("MM-dd"),
                    Amount = sum
                });
                total += sum;
            }

            response.Total = total;
            response.DailyAvg = days > 0 ? total / days : 0;

            _logger.LogInformation(
                "GetExpenseTrend: range={Range} days={Days} total={Total}",
                request.Range, days, total);

            return Task.FromResult(response);
        }

        /// <summary>解析 "7d" / "7" / "" → 天数，兜底 7，上限 365。</summary>
        private static int ParseDays(string? range)
        {
            int days = 7;
            var text = (range ?? string.Empty).Trim().ToLowerInvariant();
            if (text.EndsWith("d"))
            {
                text = text[..^1];
            }
            if (int.TryParse(text, out int parsed) && parsed > 0 && parsed <= 365)
            {
                days = parsed;
            }
            return days;
        }
    }
}
