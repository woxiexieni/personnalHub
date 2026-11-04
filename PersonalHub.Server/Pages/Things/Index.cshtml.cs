using Microsoft.AspNetCore.Mvc.RazorPages;
using Personalhub;
using personalHub.Server.Analysis;
using personalHub.Server.Data;

namespace PersonalHub.Server.Pages.Things
{
    /// <summary>
    /// 事件卡库（管理端，R7.2）。
    ///
    /// 这个页面存在的理由只有一条：**事件卡是这套系统里唯一"事实"层的数据**，
    /// 而手机上它只能按「今天 / 最近 30 天」切片看，想回看"这件事我到底写过几遍"
    /// 就得在电脑上翻。所以这里给全量 + 按标签筛 + 按是否挂了问题卡筛。
    ///
    /// 刻意不做的事：
    ///   1. 不做编辑。事件卡是"当时那一刻的记录"，事后改事实会让复盘失真；
    ///      要补想法就再打一条新卡（AI 会把它并到同一个问题下）。
    ///   2. 不做删除。删事实没有意义，不想看就筛掉。
    ///   3. 不做统计图表。这一页的用途是"翻"，不是"看趋势"（趋势在概览）。
    ///
    /// 渲染上限 <see cref="PageSize"/>：事件卡会天天长，DOM 一次性铺几万行浏览器会卡。
    /// 更早的内容用标签筛或"只看未挂卡"收窄，不做分页器。
    /// </summary>
    public class IndexModel : PageModel
    {
        private const int PageSize = 300;

        /// <summary>"待整理"卡片的标题。与客户端 <c>RecordTags.PendingTitle</c> 保持一致。</summary>
        private const string PendingTitle = "（待整理）";

        private readonly ThingStore _things;
        private readonly ProblemStore _problems;

        public IndexModel(ThingStore things, ProblemStore problems)
        {
            _things = things;
            _problems = problems;
        }

        /// <summary>空 = 全部标签。</summary>
        [Microsoft.AspNetCore.Mvc.BindProperty(SupportsGet = true)]
        public string Tag { get; set; } = string.Empty;

        /// <summary>-1 全部 / 0 未挂问题卡 / 1 已挂问题卡。</summary>
        [Microsoft.AspNetCore.Mvc.BindProperty(SupportsGet = true)]
        public int Linked { get; set; } = -1;

        /// <summary>只看某张问题卡的证据（问题卡页的「证据 ×N」点进来）。空 = 不限。</summary>
        [Microsoft.AspNetCore.Mvc.BindProperty(SupportsGet = true)]
        public string Problem { get; set; } = string.Empty;

        public IReadOnlyList<Thing> Items { get; private set; } = Array.Empty<Thing>();

        /// <summary>problem_id → 问题卡标题。查不到的（卡被删了）按"已删"显示，不隐藏事实。</summary>
        public IReadOnlyDictionary<string, string> ProblemTitles { get; private set; }
            = new Dictionary<string, string>();

        public IReadOnlyList<string> Tags { get; private set; } = Array.Empty<string>();

        public int Total { get; private set; }
        public int ThisWeek { get; private set; }
        public int LinkedCount { get; private set; }
        public int PendingCount { get; private set; }
        public int Hidden { get; private set; }

        /// <summary>正在按某张问题卡筛证据时，返回那张卡的标题（空 = 没筛）。</summary>
        public string FilterTitle => string.IsNullOrEmpty(Problem)
            ? string.Empty
            : ProblemTitles.TryGetValue(Problem, out var title) ? title : "（问题卡已删）";

        public void OnGet()
        {
            // 事件卡体量比想法小得多（一周几条到几十条），这里直接读全量再在内存里筛，
            // 换来实现最短、口径最清楚；真到上万条再改成 SQL 分页。
            var all = _things.All();
            Total = all.Count;
            LinkedCount = all.Count(t => !string.IsNullOrEmpty(t.ProblemId));
            PendingCount = all.Count(t => t.Title == PendingTitle);

            long weekStart = WeekStartMs();
            ThisWeek = all.Count(t => t.CreatedAt >= weekStart);

            Tags = _things.Tags();
            ProblemTitles = _problems.Titles();

            IEnumerable<Thing> q = all;

            if (!string.IsNullOrEmpty(Tag))
            {
                q = q.Where(t => string.Equals(t.Tag, Tag, StringComparison.Ordinal));
            }

            if (Linked == 1) q = q.Where(t => !string.IsNullOrEmpty(t.ProblemId));
            else if (Linked == 0) q = q.Where(t => string.IsNullOrEmpty(t.ProblemId));

            if (!string.IsNullOrEmpty(Problem))
            {
                q = q.Where(t => string.Equals(t.ProblemId, Problem, StringComparison.Ordinal));
            }

            var rows = q.OrderByDescending(t => t.OccurredAt).ToList();
            Items = rows.Take(PageSize).ToList();
            Hidden = Math.Max(0, rows.Count - Items.Count);
        }

        /// <summary>正文预览：表格里放全文会把行撑到几屏。</summary>
        public static string Preview(string? text, int max = 120)
        {
            string t = (text ?? string.Empty).Replace("\r", " ").Replace("\n", " ");
            return t.Length <= max ? t : t.Substring(0, max) + "…";
        }

        /// <summary>挂了卡的显示问题卡标题；卡被删了也要说出来，不能假装没挂过。</summary>
        public string ProblemText(Thing t)
        {
            if (string.IsNullOrEmpty(t.ProblemId)) return string.Empty;
            return ProblemTitles.TryGetValue(t.ProblemId, out var title) ? title : "（问题卡已删）";
        }

        /// <summary>本周一 00:00（固定 UTC+8），Unix 毫秒。</summary>
        private static long WeekStartMs()
        {
            var now = DateTimeOffset.UtcNow.ToOffset(Period.Cst);
            int back = ((int)now.DayOfWeek + 6) % 7;   // 周一 = 0
            var monday = now.Date.AddDays(-back);
            return new DateTimeOffset(monday, Period.Cst).ToUnixTimeMilliseconds();
        }
    }
}
