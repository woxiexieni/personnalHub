using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Personalhub;
using personalHub.Server.Analysis;
using personalHub.Server.Data;

namespace PersonalHub.Server.Pages.Records
{
    /// <summary>
    /// 账单列表：区间 / 方向 / 分类 / 关键字筛选 + 分页，可软删。
    ///
    /// 这里是「改错」的主入口 —— 复利闭环的第 2 步（数据可信）靠它兜底，
    /// 所以有两点不能省：已删记录能查出来（否则误删无从恢复）、
    /// 每条都能直接跳到编辑页。
    /// </summary>
    public class IndexModel : PageModel
    {
        private const int PageSize = 50;

        private readonly RecordStore _store;
        private readonly CategoryStore _categories;

        public IndexModel(RecordStore store, CategoryStore categories)
        {
            _store = store;
            _categories = categories;
        }

        [BindProperty(SupportsGet = true)] public string? From { get; set; }
        [BindProperty(SupportsGet = true)] public string? To { get; set; }
        [BindProperty(SupportsGet = true)] public int Direction { get; set; } = -1;
        [BindProperty(SupportsGet = true)] public int CategoryId { get; set; } = -1;
        [BindProperty(SupportsGet = true)] public string? Q { get; set; }
        [BindProperty(SupportsGet = true)] public bool Deleted { get; set; }
        [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;

        public IReadOnlyList<CategoryRow> Categories { get; private set; } = Array.Empty<CategoryRow>();
        public IReadOnlyList<Record> Items { get; private set; } = Array.Empty<Record>();
        public int Total { get; private set; }
        public int TotalPages => Math.Max(1, (int)Math.Ceiling(Total / (double)PageSize));

        public string CategoryName(int id)
            => Categories.FirstOrDefault(c => c.Id == id)?.Name ?? "未分类";

        public void OnGet()
        {
            var now = DateTimeOffset.UtcNow;

            // 默认看最近 30 天：既能覆盖"前几天记错了"，又不会一上来就把几年的数据全倒出来
            long from = Fmt.ParseLocal(From, dateOnly: true);
            long to = Fmt.ParseLocal(To, dateOnly: true);

            if (from == 0) from = Period.StartOfDay(now).AddDays(-29).ToUnixTimeMilliseconds();
            if (to == 0) to = Period.StartOfDay(now).AddDays(1).ToUnixTimeMilliseconds();
            else to = to + 86_400_000L;   // date 输入是"那一天"，查询要含当天 → 右端 +1 天

            if (to <= from) to = from + 86_400_000L;

            From = Fmt.ToDateInput(from);
            To = Fmt.ToDateInput(to - 1);   // 回显成"含当天"的那一天

            Categories = _categories.All();
            var result = _store.Search(from, to, Direction, CategoryId, Q, Deleted, Math.Max(1, PageNumber), PageSize);

            Items = result.Items;
            Total = result.Total;
        }

        /// <summary>软删（不物理删，随时能从"显示已删除"里找回来）。</summary>
        public IActionResult OnPostDelete(string id, string? returnUrl)
        {
            if (!string.IsNullOrEmpty(id))
            {
                var r = _store.SoftDelete(id);
                if (!r.Ok && r.Error != null) TempData["Err"] = r.Error;
                else TempData["Ok"] = "已删除（软删，勾选「显示已删除」还能看到）";
            }

            return Redirect(string.IsNullOrEmpty(returnUrl) ? "/records" : returnUrl);
        }

        /// <summary>分页链接：保留当前所有筛选条件，只换页码。</summary>
        public string PageUrl(int page)
        {
            var parts = new List<string>
            {
                "from=" + Uri.EscapeDataString(From ?? string.Empty),
                "to=" + Uri.EscapeDataString(To ?? string.Empty),
                "direction=" + Direction,
                "categoryId=" + CategoryId,
                "pageNumber=" + page,
            };
            if (!string.IsNullOrWhiteSpace(Q)) parts.Add("q=" + Uri.EscapeDataString(Q));
            if (Deleted) parts.Add("deleted=true");
            return "/records?" + string.Join("&", parts);
        }
    }
}
