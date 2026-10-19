using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Personalhub;
using personalHub.Server.Analysis;
using personalHub.Server.Data;

namespace PersonalHub.Server.Pages.Blocks
{
    /// <summary>
    /// 时间块管理：区间 / 片 / 目标筛选 + 时长统计 + 逐条改删。
    ///
    /// 这一页存在的理由和账单页一样：**手机只能改今天**，昨天及更早的写错只能到电脑上改。
    /// 没有这一页，时间块就是"只能记、不能修"的数据，而修不了的数据没人会长期记。
    ///
    /// 统计口径全在服务端算（与手机同一套 <see cref="Period"/>），页面不写裸 SQL ——
    /// 否则会出现"手机说本周 12 小时、网页说 11 小时"，两个数字各自都说得通，但你会开始不信它。
    /// </summary>
    public class IndexModel : PageModel
    {
        private const int PageSize = 50;

        private readonly BlockStore _blocks;
        private readonly GoalStore _goals;
        private readonly PlanStore _plans;

        public IndexModel(BlockStore blocks, GoalStore goals, PlanStore plans)
        {
            _blocks = blocks;
            _goals = goals;
            _plans = plans;
        }

        [BindProperty(SupportsGet = true)] public string? From { get; set; }
        [BindProperty(SupportsGet = true)] public string? To { get; set; }
        [BindProperty(SupportsGet = true)] public string? PieceCode { get; set; }
        [BindProperty(SupportsGet = true)] public string? GoalId { get; set; }
        [BindProperty(SupportsGet = true)] public bool Deleted { get; set; }
        [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;

        public IReadOnlyList<Block> Items { get; private set; } = Array.Empty<Block>();
        public IReadOnlyList<Goal> Goals { get; private set; } = Array.Empty<Goal>();

        /// <summary>按片聚合（分钟降序），只统计未删除的。</summary>
        public IReadOnlyList<(string Code, int Minutes, int Count)> ByPiece { get; private set; }
            = Array.Empty<(string, int, int)>();

        /// <summary>按目标聚合（分钟降序）。空目标归到 "（未归目标）"。</summary>
        public IReadOnlyList<(string Title, int Minutes, int Count)> ByGoal { get; private set; }
            = Array.Empty<(string, int, int)>();

        public int Total { get; private set; }
        public int TotalPages => Math.Max(1, (int)Math.Ceiling(Total / (double)PageSize));

        /// <summary>区间内已记录的分钟数。</summary>
        public int RecordedMinutes { get; private set; }

        /// <summary>区间内"已经过去"的分钟数：未来的时间不算漏记。</summary>
        public int ElapsedMinutes { get; private set; }

        public int UnrecordedMinutes { get; private set; }

        /// <summary>已过天数，用于日均。</summary>
        public int ElapsedDays { get; private set; }

        public int DailyAvgMinutes => ElapsedDays > 0 ? RecordedMinutes / ElapsedDays : 0;

        /// <summary>区间内未删除的条数（不是当前页的条数）。</summary>
        public int LiveCount { get; private set; }

        /// <summary>今天计划 vs 实际（与区间筛选无关，永远看今天）。</summary>
        public IReadOnlyList<PlanLine> TodayPlans { get; private set; } = Array.Empty<PlanLine>();

        public sealed record PlanLine(string PieceCode, int Budget, int Actual)
        {
            public double Ratio => Budget > 0 ? Actual / (double)Budget : 0;

            public string Status => Budget <= 0 ? "已记" : Ratio >= 0.8 ? "达成" : Ratio >= 0.5 ? "部分" : "未达成";

            public string StatusClass => Budget <= 0 ? "" : Ratio >= 0.8 ? "done" : Ratio >= 0.5 ? "paused" : "deleted";
        }

        /// <summary>占"已过去时间"的百分比，给 CSS 宽度用。</summary>
        public string PctOfElapsed(int minutes)
            => ElapsedMinutes > 0 ? Fmt.Pct(minutes / (double)ElapsedMinutes) : "0";

        public string GoalTitle(string? id)
        {
            if (string.IsNullOrEmpty(id)) return "未归目标";
            return Goals.FirstOrDefault(g => g.Id == id)?.Title ?? "（已删除的目标）";
        }

        public string PieceName(string? code) => Pieces.Name(code);

        public void OnGet()
        {
            var now = DateTimeOffset.UtcNow;

            long from = Fmt.ParseLocal(From, dateOnly: true);
            long to = Fmt.ParseLocal(To, dateOnly: true);

            // 默认最近 7 天：时间块一天 6~10 条，一周正好一屏能看完的量
            if (from == 0) from = Period.StartOfDay(now).AddDays(-6).ToUnixTimeMilliseconds();
            if (to == 0) to = Period.StartOfDay(now).AddDays(1).ToUnixTimeMilliseconds();
            else to += 86_400_000L;   // date 输入是"那一天"，查询要含当天

            if (to <= from) to = from + 86_400_000L;

            From = Fmt.ToDateInput(from);
            To = Fmt.ToDateInput(to - 1);

            Goals = _goals.List();

            var all = _blocks.List(
                from, to,
                goalId: string.IsNullOrWhiteSpace(GoalId) ? null : GoalId,
                pieceCode: string.IsNullOrWhiteSpace(PieceCode) ? null : PieceCode,
                includeDeleted: Deleted);

            // 统计只算未删除的：勾了"显示已删除"是为了找回来核对，不该把删掉的时长算进总量
            var live = all.Where(b => !b.Deleted).ToList();

            RecordedMinutes = live.Sum(b => Math.Max(0, b.DurationMin));
            LiveCount = live.Count;
            ElapsedMinutes = Elapsed(from, to, now);
            UnrecordedMinutes = Math.Max(0, ElapsedMinutes - RecordedMinutes);

            // 已过天数：右端钳到今天此刻，向上取整（今天过了一半也算一天）
            long nowMs = now.ToUnixTimeMilliseconds();
            long effectiveEnd = Math.Min(to, nowMs);
            ElapsedDays = effectiveEnd <= from
                ? 0
                : (int)Math.Ceiling((effectiveEnd - from) / 86_400_000.0);

            ByPiece = live
                .GroupBy(b => b.PieceCode ?? string.Empty, StringComparer.Ordinal)
                .Select(g => (Code: g.Key, Minutes: g.Sum(x => Math.Max(0, x.DurationMin)), Count: g.Count()))
                .OrderByDescending(x => x.Minutes)
                .ThenByDescending(x => x.Count)
                .ToList();

            ByGoal = live
                .GroupBy(b => b.GoalId ?? string.Empty, StringComparer.Ordinal)
                .Select(g => (Title: GoalTitle(g.Key), Minutes: g.Sum(x => Math.Max(0, x.DurationMin)), Count: g.Count()))
                .OrderByDescending(x => x.Minutes)
                .ToList();

            Total = all.Count;
            int page = Math.Max(1, PageNumber);
            Items = all.Skip((page - 1) * PageSize).Take(PageSize).ToList();
            PageNumber = page;

            // 今天的计划 vs 实际：实际按"今天 + 同片"求和，不要求绑定到具体某条块
            var todayRange = Period.Today(now);
            var todayBlocks = _blocks.List(todayRange.From, todayRange.To);
            TodayPlans = _plans.ListDay(PlanStore.Today())
                .Select(p => new PlanLine(
                    p.PieceCode,
                    p.BudgetMin,
                    todayBlocks.Where(b => b.PieceCode == p.PieceCode)
                        .Sum(b => Math.Max(0, b.DurationMin))))
                .ToList();
        }

        /// <summary>软删。删不存在的 id 视为成功（幂等），与手机端语义一致。</summary>
        public IActionResult OnPostDelete(string id, string? returnUrl)
        {
            if (!string.IsNullOrEmpty(id))
            {
                var r = _blocks.SoftDelete(id);
                TempData[r.Ok ? "Ok" : "Err"] = r.Ok
                    ? "已删除（软删，勾选「显示已删除」还能看到）"
                    : (r.Error ?? "删除失败");
            }

            return Redirect(string.IsNullOrEmpty(returnUrl) ? "/blocks" : returnUrl);
        }

        /// <summary>区间内已过分钟数（右端钳到今天此刻，未来的时间不算漏记）。</summary>
        private static int Elapsed(long from, long to, DateTimeOffset now)
        {
            long nowMs = now.ToUnixTimeMilliseconds();
            long end = Math.Min(to, nowMs);
            if (end <= from) return 0;

            return (int)((end - from) / 60000L);
        }

        /// <summary>分页链接：保留全部筛选条件，只换页码。</summary>
        public string PageUrl(int page)
        {
            var parts = new List<string>
            {
                "from=" + Uri.EscapeDataString(From ?? string.Empty),
                "to=" + Uri.EscapeDataString(To ?? string.Empty),
                "pageNumber=" + page,
            };
            if (!string.IsNullOrWhiteSpace(PieceCode)) parts.Add("pieceCode=" + Uri.EscapeDataString(PieceCode));
            if (!string.IsNullOrWhiteSpace(GoalId)) parts.Add("goalId=" + Uri.EscapeDataString(GoalId));
            if (Deleted) parts.Add("deleted=true");
            return "/blocks?" + string.Join("&", parts);
        }
    }
}
