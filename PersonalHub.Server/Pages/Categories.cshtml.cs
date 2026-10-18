using Microsoft.AspNetCore.Mvc.RazorPages;
using personalHub.Server.Data;

namespace PersonalHub.Server.Pages
{
    /// <summary>
    /// 分类只读页。
    ///
    /// 本轮不做增删改：分类一改，历史统计的口径就会跟着变（改名会让"餐饮"和"吃饭"在图上分家），
    /// 所以维护功能要配"改名前合并历史"的策略，属于 P1。现在先把现状摊开给你看。
    /// </summary>
    public class CategoriesModel : PageModel
    {
        private readonly CategoryStore _categories;
        private readonly RecordStore _records;

        public CategoriesModel(CategoryStore categories, RecordStore records)
        {
            _categories = categories;
            _records = records;
        }

        public IReadOnlyList<CategoryRow> Categories { get; private set; } = Array.Empty<CategoryRow>();
        public IReadOnlyDictionary<int, int> Usage { get; private set; } = new Dictionary<int, int>();

        public void OnGet()
        {
            Categories = _categories.All();
            Usage = _records.CountByCategory();
        }

        public int CountOf(int id) => Usage.TryGetValue(id, out int n) ? n : 0;
    }
}
