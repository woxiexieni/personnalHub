using Grpc.Core;
using Microsoft.Extensions.Logging;
using Personalhub;
using personalHub.Server.Analysis;
using personalHub.Server.Data;

namespace personalHub.Server.Services
{
    /// <summary>
    /// 分析服务实现。只做三件事：定范围 → 取数 → 转 proto。
    /// 计算本身在 <see cref="SpendingAnalyzer"/>，口径在 <see cref="Period"/> / <see cref="PeriodResolver"/>，
    /// 这里不写任何 if/else 业务规则。
    /// </summary>
    public class AnalysisServiceImpl : AnalysisService.AnalysisServiceBase
    {
        private readonly ILogger<AnalysisServiceImpl> _logger;
        private readonly RecordStore _store;
        private readonly CategoryStore _categories;

        public AnalysisServiceImpl(ILogger<AnalysisServiceImpl> logger, RecordStore store, CategoryStore categories)
        {
            _logger = logger;
            _store = store;
            _categories = categories;
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

        /// <summary>
        /// 结构分析 —— 手机「状态」页 / 网页概览共用。
        ///
        /// 只多取一次"上一周期同期"的明细：一个月的量级最多几百条，一次多查一遍
        /// 远比在 SQL 里写两套聚合（还要保证口径一致）安全。
        /// </summary>
        public override Task<StructureResponse> GetSpendingStructure(StructureRequest request, ServerCallContext context)
        {
            var now = DateTimeOffset.UtcNow;
            var kind = PeriodResolver.Parse(request.Period);
            var range = PeriodResolver.Resolve(kind, now);

            var current = _store.ListRows(range.From, range.To, direction: -1, includeDeleted: false);
            var previous = _store.ListRows(range.PrevFrom, range.PrevTo, direction: -1, includeDeleted: false);

            // 分类名一次查全，避免在循环里反复 Find（一条记录一次查询，几万条时会很明显）
            var lookup = _categories.All().ToDictionary(c => c.Id, c => (c.Name, Icon: c.Icon ?? string.Empty));
            (string Name, string Icon) Resolve(int id) =>
                lookup.TryGetValue(id, out var c) ? c : ("未分类", string.Empty);

            var result = SpendingAnalyzer.BuildStructure(current, previous, range, Resolve);

            var resp = new StructureResponse
            {
                Period = kind.ToString().ToLowerInvariant(),
                RangeStart = result.Range.From,
                RangeEnd = result.Range.To,
                ExpenseCents = result.ExpenseCents,
                IncomeCents = result.IncomeCents,
                ExpenseCount = result.ExpenseCount,
                DailyAvgCents = result.DailyAvgCents,
                PrevExpenseCents = result.PrevExpenseCents,
                DeltaRatio = result.DeltaRatio,
                HasPrev = result.HasPrev,
                PrevRangeStart = result.Range.PrevFrom,
                PrevRangeEnd = result.Range.PrevTo,
                GeneratedAt = now.ToUnixTimeMilliseconds(),
            };

            resp.Categories.AddRange(result.Categories.Select(s => new CategorySlice
            {
                CategoryId = s.Key,
                Name = s.Name,
                Icon = s.Icon,
                AmountCents = s.Cents,
                Ratio = result.ExpenseCents > 0 ? s.Cents / (double)result.ExpenseCents : 0,
                Count = s.Count,
                PrevAmountCents = s.PrevCents,
                DeltaRatio = s.DeltaRatio,
                HasPrev = s.HasPrev,
            }));

            resp.PayMethods.AddRange(result.PayMethods.Select(s => new PayMethodSlice
            {
                Name = s.Name,
                AmountCents = s.Cents,
                Ratio = result.ExpenseCents > 0 ? s.Cents / (double)result.ExpenseCents : 0,
                Count = s.Count,
            }));

            _logger.LogInformation(
                "GetSpendingStructure({Period}): 支出 {Expense} 分 / {Count} 笔，上期 {Prev} 分，分类 {Cat} 个",
                resp.Period, result.ExpenseCents, result.ExpenseCount, result.PrevExpenseCents, resp.Categories.Count);

            return Task.FromResult(resp);
        }
    }
}
