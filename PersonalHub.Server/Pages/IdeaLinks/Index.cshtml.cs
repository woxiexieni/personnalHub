using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Personalhub;
using personalHub.Server.Data;

namespace PersonalHub.Server.Pages.IdeaLinks
{
    /// <summary>
    /// 想法关系（R9）：只读浏览衍生 / 矛盾边。
    ///
    /// 管理端只负责"看"，不提供建边/删边 —— 关系在手机上跟着想法走，一屏之内建完；
    /// 网页端建边要翻 id，是给机器用的交互，不是给人用的。
    ///
    /// 边的两端是软引用：目标被删/还没同步过来时显示「（已不在库）」，而不是裸 id。
    /// </summary>
    public class IndexModel : PageModel
    {
        private const int PageSize = 300;

        private readonly IdeaLinkStore _links;
        private readonly IdeaStore _ideas;

        public IndexModel(IdeaLinkStore links, IdeaStore ideas)
        {
            _links = links;
            _ideas = ideas;
        }

        /// <summary>-1 = 全部，0 衍生，1 矛盾。</summary>
        [BindProperty(SupportsGet = true)]
        public int Kind { get; set; } = -1;

        public IReadOnlyList<IdeaLink> Items { get; private set; } = Array.Empty<IdeaLink>();

        public IReadOnlyDictionary<int, int> Counts { get; private set; }
            = new Dictionary<int, int>();

        public int Total { get; private set; }

        /// <summary>当前筛选下还有多少条没渲染出来。</summary>
        public int Hidden { get; private set; }

        /// <summary>id → 正文前 40 字（含作废/删除的行：目标没了也要能标"已不在库"）。</summary>
        public IReadOnlyDictionary<string, string> Titles { get; private set; }
            = new Dictionary<string, string>();

        public void OnGet()
        {
            Counts = _links.CountByKind();
            Total = Counts.Values.Sum();
            Titles = _ideas.Titles();

            var page = _links.List(Kind, false);
            Items = page.Take(PageSize).ToList();
            Hidden = Math.Max(0, page.Count - Items.Count);
        }

        public int CountOf(int kind)
            => Counts.TryGetValue(kind, out int n) ? n : 0;

        public string KindText(int kind) => kind switch
        {
            0 => "衍生",
            1 => "矛盾",
            _ => "未知",
        };

        /// <summary>复用 site.css 里现成的 tag 配色：衍生用蓝（tag active），矛盾用红（tag deleted）。</summary>
        public string KindClass(int kind) => kind switch
        {
            0 => "tag active",
            1 => "tag deleted",
            _ => "tag",
        };

        /// <summary>端点显示：翻得动人话就翻，翻不动（没同步过来/被删）就明说。</summary>
        public string EndpointText(string id)
            => Titles.TryGetValue(id, out string? t) && !string.IsNullOrEmpty(t) ? t : "（已不在库）";

        /// <summary>关系建立时间。用 created_at（边建成的那一刻），updated_at 会跟着墓碑变。</summary>
        public static string TimeText(IdeaLink l)
            => l.CreatedAt > 0 ? Fmt.ShortDate(l.CreatedAt) : "—";
    }
}
