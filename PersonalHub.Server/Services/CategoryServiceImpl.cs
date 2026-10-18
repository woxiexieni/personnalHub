using Grpc.Core;
using Microsoft.Extensions.Logging;
using Personalhub;
using personalHub.Server.Data;

namespace personalHub.Server.Services
{
    /// <summary>
    /// 分类服务实现。
    ///
    /// 手机端录入页的 chips 从这里来，不再硬编码 —— 这是"服务端知道 3 号叫什么"的起点。
    /// 返回全部分类（含已停用的），由客户端按 enabled 决定是否显示：
    /// 历史账单引用了停用分类时，统计和详情页仍然要能显示它的名字。
    /// </summary>
    public class CategoryServiceImpl : CategoryService.CategoryServiceBase
    {
        private readonly ILogger<CategoryServiceImpl> _logger;
        private readonly CategoryStore _store;

        public CategoryServiceImpl(ILogger<CategoryServiceImpl> logger, CategoryStore store)
        {
            _logger = logger;
            _store = store;
        }

        public override Task<CategoryList> GetCategories(Empty request, ServerCallContext context)
        {
            var response = new CategoryList { UpdatedAt = _store.LastUpdatedAt() };

            response.Categories.AddRange(_store.All().Select(c => new Category
            {
                Id = c.Id,
                Name = c.Name,
                Icon = c.Icon ?? string.Empty,
                SortOrder = c.SortOrder,
                Enabled = c.Enabled != 0,
                Direction = c.Direction,
            }));

            _logger.LogInformation("GetCategories: 返回 {Count} 个分类", response.Categories.Count);
            return Task.FromResult(response);
        }
    }
}
