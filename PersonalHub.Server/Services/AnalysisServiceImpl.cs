using Grpc.Core;
using Microsoft.Extensions.Logging;
using Personalhub;
using personalHub.Server.Analysis;
using personalHub.Server.Data;

namespace personalHub.Server.Services
{
    /// <summary>
    /// 分析服务实现。只做三件事：定范围 → 取数 → 转 proto。
    /// 计算本身在 <see cref="SpendingAnalyzer"/>，口径在 <see cref="Period"/>，
    /// 这里不写任何 if/else 业务规则。
    /// </summary>
    public class AnalysisServiceImpl : AnalysisService.AnalysisServiceBase
    {
        private readonly ILogger<AnalysisServiceImpl> _logger;
        private readonly RecordStore _store;

        public AnalysisServiceImpl(ILogger<AnalysisServiceImpl> logger, RecordStore store)
        {
            _logger = logger;
            _store = store;
        }

        /// <summary>
        /// 今日汇总 —— 手机端「今日总额」的校正值。
        ///
        /// 手机端先用本地 SQL 渲染即时值（0ms、含未同步、断网可用），
        /// 这个接口回来后再覆盖一次，补上"电脑端补录 / 另一台设备刚记的"。
        /// 两边口径完全一致（都是 [今天00:00, 明天00:00) + UTC+8 + 分累加）。
        /// </summary>
        public override Task<TodaySummaryResponse> GetTodaySummary(Empty request, ServerCallContext context)
        {
            var now = DateTimeOffset.UtcNow;
            var range = Period.Today(now);

            // 一天的账单最多几十条，直接取行比在 SQL 里再写一遍聚合逻辑更不容易走样
            var rows = _store.ListRows(range.From, range.To, direction: -1, includeDeleted: false);
            var summary = SpendingAnalyzer.Summarize(rows, range, now);

            _logger.LogInformation(
                "GetTodaySummary: 支出 {Expense} 分 / {Count} 笔，收入 {Income} 分",
                summary.ExpenseCents, summary.ExpenseCount, summary.IncomeCents);

            return Task.FromResult(new TodaySummaryResponse
            {
                RangeStart = summary.From,
                RangeEnd = summary.To,
                ExpenseCents = summary.ExpenseCents,
                IncomeCents = summary.IncomeCents,
                ExpenseCount = summary.ExpenseCount,
                GeneratedAt = summary.GeneratedAt.ToUnixTimeMilliseconds(),
            });
        }
    }
}
