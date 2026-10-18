using Microsoft.AspNetCore.Mvc.RazorPages;
using Personalhub;
using personalHub.Server.Analysis;
using personalHub.Server.Data;

namespace PersonalHub.Server.Pages
{
    /// <summary>
    /// 概览页：今日 / 本月 / 最近记录。
    ///
    /// 只调 Store 和 Analyzer，不写裸 SQL —— 否则会出现"手机不让存 0 元、网页能存进去"，
    /// 统计当场就错，而且错得很隐蔽。
    /// </summary>
    public class IndexModel : PageModel
    {
        private readonly RecordStore _store;
        private readonly CategoryStore _categories;

        public IndexModel(RecordStore store, CategoryStore categories)
        {
            _store = store;
            _categories = categories;
        }

        public TodaySummary Today { get; private set; } = default!;
        public StructureResult Month { get; private set; } = default!;
        public IReadOnlyList<Record> Recent { get; private set; } = Array.Empty<Record>();
        public IReadOnlyDictionary<int, (string Name, string Icon)> CategoryMap { get; private set; }
            = new Dictionary<int, (string, string)>();

        public string CategoryName(int id) =>
            CategoryMap.TryGetValue(id, out var c) ? c.Name : "未分类";

        public void OnGet()
        {
            var now = DateTimeOffset.UtcNow;

            var todayRange = Period.Today(now);
            var todayRows = _store.ListRows(todayRange.From, todayRange.To);
            Today = SpendingAnalyzer.Summarize(todayRows, todayRange, now);

            CategoryMap = _categories.All().ToDictionary(c => c.Id, c => (c.Name, c.Icon ?? string.Empty));
            (string Name, string Icon) Lookup(int id) =>
                CategoryMap.TryGetValue(id, out var c) ? c : ("未分类", string.Empty);

            var monthRange = PeriodResolver.Resolve(PeriodKind.Month, now);
            var monthRows = _store.ListRows(monthRange.From, monthRange.To);
            var prevRows = _store.ListRows(monthRange.PrevFrom, monthRange.PrevTo);
            Month = SpendingAnalyzer.BuildStructure(monthRows, prevRows, monthRange, Lookup);

            // 不限区间的最近 6 条（列表页显示 5 条，最后一条用来判断"还有更多"）
            Recent = _store.Search(0, now.ToUnixTimeMilliseconds() + 86_400_000L,
                direction: -1, categoryId: -1, keyword: null, includeDeleted: false,
                page: 1, pageSize: 6).Items;
        }
    }
}
