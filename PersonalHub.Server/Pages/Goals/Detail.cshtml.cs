using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Personalhub;
using personalHub.Server.Analysis;
using personalHub.Server.Data;

namespace PersonalHub.Server.Pages.Goals
{
    /// <summary>
    /// 目标演进：这个目标由哪一长串时间片组成。
    ///
    /// 目标不是一个「完成度」，而是一条线 —— 线就是它下面所有时间块按时间排下来。
    /// 这里只呈现痕迹，不呈现百分比：
    ///   累计投入、最后活动、四周趋势、以及逐日的块（片 / 时长 / 备注 / 评价 / 一句话经验）。
    ///
    /// 这一页手机上也有对应版本，但手机只镜像 30 天，所以「全量演进」只能在网页看。
    /// </summary>
    public class DetailModel : PageModel
    {
        private readonly GoalStore _goals;
        private readonly BlockStore _blocks;

        public DetailModel(GoalStore goals, BlockStore blocks)
        {
            _goals = goals;
            _blocks = blocks;
        }

        public Goal? Goal { get; private set; }

        /// <summary>按天分组的时间块（新 → 旧），每组是一个「节点串」。</summary>
        public IReadOnlyList<(string DayLabel, int Minutes, IReadOnlyList<Block> Blocks)> Days { get; private set; }
            = Array.Empty<(string, int, IReadOnlyList<Block>)>();

        public IReadOnlyList<int> Weeks { get; private set; } = new[] { 0, 0, 0, 0 };

        public int TotalCount { get; private set; }

        public void OnGet(string? id)
        {
            if (string.IsNullOrEmpty(id)) return;

            Goal = _goals.Get(id);
            if (Goal is null) return;

            // 全量（不设区间上限），但最多取 500 条：单目标的演进看最近 500 个节点够了
            var all = _blocks.List(0, long.MaxValue, goalId: Goal.Id, limit: 500);
            TotalCount = all.Count;

            Days = all
                .GroupBy(b => Period.StartOfDay(DateTimeOffset.FromUnixTimeMilliseconds(b.OccurredAt)))
                .OrderByDescending(g => g.Key)
                .Select(g => (
                    DayLabel: g.Key.ToOffset(Period.Cst).ToString("yyyy-MM-dd") + " " + WeekdayName(g.Key),
                    Minutes: g.Sum(x => Math.Max(0, x.DurationMin)),
                    Blocks: (IReadOnlyList<Block>)g.OrderByDescending(x => x.OccurredAt).ToList()))
                .ToList();

            Weeks = _blocks.WeeklyMinutes(Goal.Id, DateTimeOffset.UtcNow);
        }

        private static string WeekdayName(DateTimeOffset local)
        {
            // 固定 UTC+8，避免服务器本地时区影响"周几"
            var d = Period.StartOfDay(local).ToOffset(Period.Cst).DayOfWeek;
            return d switch
            {
                DayOfWeek.Monday => "周一",
                DayOfWeek.Tuesday => "周二",
                DayOfWeek.Wednesday => "周三",
                DayOfWeek.Thursday => "周四",
                DayOfWeek.Friday => "周五",
                DayOfWeek.Saturday => "周六",
                _ => "周日",
            };
        }

        public string PieceName(string? code) => Pieces.Name(code);

        public string TimeText(long ms)
            => DateTimeOffset.FromUnixTimeMilliseconds(ms).ToOffset(Period.Cst).ToString("HH:mm");

        public string WorthText(int worth) => worth switch
        {
            1 => "值得",
            2 => "一般",
            3 => "别做了",
            _ => "",
        };

        public string WorthClass(int worth) => worth switch
        {
            1 => "done",
            2 => "paused",
            3 => "deleted",
            _ => "",
        };

        public string MinText(int minutes) => Pieces.Minutes(minutes);

        public string GainedText(int minutes)
            => Goal is not null && Goal.TotalMinutes > 0
                ? Fmt.Pct(minutes / (double)Goal.TotalMinutes)
                : "0";
    }
}
