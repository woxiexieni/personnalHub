using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Personalhub;
using personalHub.Server.Analysis;
using personalHub.Server.Data;

namespace PersonalHub.Server.Pages.Ideas
{
    /// <summary>
    /// 想法库（R6）：只读浏览 + 按类型筛选。
    ///
    /// 这里刻意不做的事（与需求文档 §22.3 一致）：
    ///   1. 没有「未处理 / 已处理」状态，也没有「待办」口径的计数 ——
    ///      想法库一旦有积压感，就会变成第二个待办清单，然后被放弃。
    ///   2. 没有完成率、没有转化率。KPI 只放三个中性的数：全部 / 本周新增 / 已转目标。
    ///   3. 不提供"批量删除"之类的高效清理入口 —— 删东西太顺手，人会忍不住删掉
    ///      "看起来没用"的想法，而那些往往是一年后最值钱的。
    ///
    /// 渲染上限 <see cref="PageSize"/>：管理端是浏览器，几万行 DOM 会卡；
    /// 更早的内容用类型筛选或时间范围收窄，不做分页器（分页器会让人以为"要翻完"）。
    /// </summary>
    public class IndexModel : PageModel
    {
        private const int PageSize = 300;

        private readonly IdeaStore _ideas;

        public IndexModel(IdeaStore ideas)
        {
            _ideas = ideas;
        }

        /// <summary>-1 = 全部，0..4 = 对应类型。</summary>
        [BindProperty(SupportsGet = true)]
        public int Kind { get; set; } = -1;

        public IReadOnlyList<Idea> Items { get; private set; } = Array.Empty<Idea>();

        public IReadOnlyDictionary<int, int> Counts { get; private set; }
            = new Dictionary<int, int>();

        public int Total { get; private set; }

        /// <summary>本周（周一 00:00 起，UTC+8）新增条数。</summary>
        public int ThisWeek { get; private set; }

        /// <summary>已经转成目标的条数。</summary>
        public int Linked { get; private set; }

        /// <summary>当前筛选下还有多少条没渲染出来。</summary>
        public int Hidden { get; private set; }

        public void OnGet()
        {
            Counts = _ideas.CountByKind();
            Total = Counts.Values.Sum();
            Linked = _ideas.CountLinked();

            // 本周新增用时间窗查，不读全表：想法库会越来越长。
            ThisWeek = _ideas.List(-1, WeekStartMs(), 0, false).Count;

            // List 已按 created_at 倒序，这里只截前 PageSize 条。
            var page = _ideas.List(Kind, 0, 0, false);
            Items = page.Take(PageSize).ToList();
            Hidden = Math.Max(0, page.Count - Items.Count);
        }

        public int CountOf(int kind)
            => Counts.TryGetValue(kind, out int n) ? n : 0;

        public string KindText(int kind) => kind switch
        {
            1 => "事实",
            2 => "评价",
            3 => "计划行动",
            4 => "脑补",
            _ => "未分类",
        };

        /// <summary>复用 site.css 里现成的 tag 配色，不新增样式。</summary>
        public string KindClass(int kind) => kind switch
        {
            1 => "tag active",   // 蓝
            2 => "tag paused",   // 黄
            3 => "tag done",     // 绿
            4 => "tag income",   // 淡绿
            _ => "tag",          // 灰
        };

        /// <summary>正文预览：表格里放全文会把行撑到几屏，超出的用 title 兜。</summary>
        public static string Preview(Idea i)
        {
            string t = (i.Text ?? string.Empty).Replace("\r", " ").Replace("\n", " ");
            return t.Length <= 140 ? t : t.Substring(0, 140) + "…";
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
