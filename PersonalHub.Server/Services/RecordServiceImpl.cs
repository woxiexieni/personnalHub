using Grpc.Core;
using Microsoft.Extensions.Logging;
using Personalhub;
using personalHub.Server.Analysis;
using personalHub.Server.Data;

namespace personalHub.Server.Services
{
    /// <summary>
    /// 记账服务实现。
    ///
    /// 所有写操作（上传批次 / 管理端保存 / 删除）都过 <see cref="RecordStore.Save"/>，
    /// 校验只有一份 —— 手机不让存的数据，电脑也存不进去。
    /// 所有读操作的时间范围都来自 <see cref="Period"/>，不再出现 DateTime.Now。
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

        // ============================================================
        // 写
        // ============================================================

        /// <summary>
        /// 上传一批账单（手机端同步入口）。
        ///
        /// 与 R1 之前的行为差异：
        /// - 带 deleted = true 的记录按"软删"处理，**不校验金额/分类**（分类停用后也要删得掉）；
        /// - 逐条走 Save，失败原因不再只有"金额 <= 0"一种；
        /// - 出现版本冲突时返回 error_msg = "conflict"，客户端看到它应该停止重试并拉取覆盖。
        /// </summary>
        public override Task<UploadRecordsResponse> UploadRecords(
            UploadRecordsRequest request, ServerCallContext context)
        {
            int accepted = 0;
            bool conflict = false;
            var failedIds = new List<string>();

            foreach (var r in request.Records)
            {
                var result = _store.Save(r);

                if (result.Ok)
                {
                    accepted++;
                    continue;
                }

                if (result.Conflict) conflict = true;
                failedIds.Add(string.IsNullOrEmpty(r.Id) ? "(无 id)" : r.Id);

                _logger.LogWarning("UploadRecords: 拒收 {Id} —— {Reason}",
                    string.IsNullOrEmpty(r.Id) ? "(无 id)" : r.Id, result.Error);
            }

            var response = new UploadRecordsResponse { AcceptedCount = accepted };
            response.FailedIds.AddRange(failedIds);

            if (conflict)
            {
                // 固定小写关键字，客户端按 Contains("conflict") 判断，不要翻译成中文
                response.ErrorMsg = "conflict";
            }
            else if (failedIds.Count > 0)
            {
                response.ErrorMsg = "部分记录未通过校验";
            }

            _logger.LogInformation(
                "UploadRecords: 收到 {Total} 条，通过 {Ok} 条，失败 {Bad} 条{Conflict}",
                request.Records.Count, accepted, failedIds.Count, conflict ? "（含版本冲突）" : "");

            return Task.FromResult(response);
        }

        /// <summary>管理端：保存单条（有 id 则改，无 id 则补录）。校验失败时返回可展示的中文原因。</summary>
        public override Task<SaveRecordResponse> SaveRecord(
            SaveRecordRequest request, ServerCallContext context)
        {
            if (request.Record is null)
            {
                return Task.FromResult(new SaveRecordResponse { Ok = false, ErrorMsg = "缺少记录内容" });
            }

            var result = _store.Save(request.Record);

            if (!result.Ok)
            {
                _logger.LogInformation("SaveRecord: 拒绝 {Id} —— {Reason}", request.Record.Id, result.Error);
                return Task.FromResult(new SaveRecordResponse
                {
                    Ok = false,
                    ErrorMsg = result.Error ?? "保存失败",
                    Record = result.Record,
                });
            }

            return Task.FromResult(new SaveRecordResponse { Ok = true, Record = result.Record });
        }

        /// <summary>管理端：软删除单条。</summary>
        public override Task<OpResult> DeleteRecord(
            DeleteRecordRequest request, ServerCallContext context)
        {
            var result = _store.SoftDelete(request.Id);

            _logger.LogInformation("DeleteRecord: {Id} → {Ok} ({Reason})",
                request.Id, result.Ok, result.Error);

            return Task.FromResult(new OpResult { Ok = result.Ok, ErrorMsg = result.Error ?? string.Empty });
        }

        // ============================================================
        // 读
        // ============================================================

        /// <summary>
        /// 区间查询：手机端拿"最近 30 天"做镜像覆盖，管理端做列表筛选。
        /// 时间语义 [from, to)；from/to 为 0 时给一个安全的默认值，避免误查全表。
        /// </summary>
        public override Task<RecordList> ListRecords(
            ListRecordsRequest request, ServerCallContext context)
        {
            var now = DateTimeOffset.UtcNow;
            var today = Period.Today(now);

            long to = request.To > 0 ? request.To : today.To;
            long from = request.From > 0 ? request.From : today.From - 29L * 24 * 60 * 60 * 1000;

            if (to <= from)
            {
                _logger.LogWarning("ListRecords: 区间非法 from={From} to={To}，返回空", from, to);
                return Task.FromResult(new RecordList());
            }

            var records = _store.List(from, to, request.Direction, request.IncludeDeleted, request.Limit);

            var response = new RecordList();
            response.Records.AddRange(records);

            _logger.LogInformation(
                "ListRecords: [{From} → {To}) direction={Dir} deleted={Del} → {Count} 条",
                from, to, request.Direction, request.IncludeDeleted, records.Count);

            return Task.FromResult(response);
        }

        /// <summary>取「今天」的账单（固定 UTC+8）。保留该 RPC 以兼容旧客户端。</summary>
        public override Task<RecordList> GetTodayRecords(Empty request, ServerCallContext context)
        {
            var range = Period.Today(DateTimeOffset.UtcNow);
            var records = _store.List(range.From, range.To, direction: -1, includeDeleted: false);

            var list = new RecordList();
            list.Records.AddRange(records);

            _logger.LogInformation("GetTodayRecords: 返回 {Count} 条", records.Count);
            return Task.FromResult(list);
        }

        /// <summary>
        /// 支出趋势。request.Range 支持 "7d" / "30" 这类写法，默认 7 天。
        ///
        /// 注意：这里输出仍然用 double（老接口协议不变），但**分组的日界来自 Period**，
        /// 不再用 DateTime.Now —— 否则服务端一换时区，整张趋势图会平移 8 小时。
        /// </summary>
        public override Task<TrendResponse> GetExpenseTrend(
            TrendRequest request, ServerCallContext context)
        {
            int days = ParseDays(request.Range);
            var now = DateTimeOffset.UtcNow;
            var (from, to) = Period.LastDays(days, now);

            var rows = _store.ListRows(from, to, direction: 0, includeDeleted: false);
            var start = Period.DayStartOf(from);

            var response = new TrendResponse();
            double total = 0;

            for (int i = 0; i < days; i++)
            {
                var day = start.AddDays(i);
                long dayStart = day.ToUnixTimeMilliseconds();
                long dayEnd = day.AddDays(1).ToUnixTimeMilliseconds();

                double sum = rows
                    .Where(r => r.SpentAt >= dayStart && r.SpentAt < dayEnd)
                    .Sum(r => r.Amount);

                response.Points.Add(new TrendPoint
                {
                    Label = day.ToString("MM-dd"),
                    Amount = sum,
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
