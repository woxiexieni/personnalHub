using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Personalhub;
using personalHub.Server.Data;

namespace PersonalHub.Server.Pages.Records
{
    /// <summary>
    /// 单条账单的新增 / 编辑。
    ///
    /// **所有校验都落在 <see cref="RecordStore.Save"/>**，这里只做"早一点给出人话提示"：
    /// 页面自己再写一套 if，就迟早会出现"网页能存、手机不让存"，统计口径当场破。
    /// 保存失败时把 <see cref="SaveResult.Error"/> 原样回显到表单上，不吞掉原因。
    /// </summary>
    public class EditModel : PageModel
    {
        private readonly RecordStore _store;
        private readonly CategoryStore _categories;

        public EditModel(RecordStore store, CategoryStore categories)
        {
            _store = store;
            _categories = categories;
        }

        [BindProperty] public InputModel Input { get; set; } = new();

        public IReadOnlyList<CategoryRow> Categories { get; private set; } = Array.Empty<CategoryRow>();

        public bool IsNew => string.IsNullOrEmpty(Input.Id);

        public class InputModel
        {
            public string? Id { get; set; }
            public int Direction { get; set; } = 0;
            public double Amount { get; set; }
            public int CategoryId { get; set; }
            /// <summary>datetime-local 的字符串，按 UTC+8 解释。</summary>
            public string? SpentAt { get; set; }
            public string? PayMethod { get; set; }
            public string? Note { get; set; }
        }

        public IActionResult OnGet(string? id)
        {
            Categories = _categories.All();

            if (string.IsNullOrEmpty(id))
            {
                var now = DateTimeOffset.UtcNow;
                Input = new InputModel
                {
                    Direction = 0,
                    SpentAt = Fmt.ToLocalInput(now.ToUnixTimeMilliseconds()),
                    CategoryId = Categories.FirstOrDefault(c => c.Direction == 0)?.Id ?? 1,
                    PayMethod = "微信",
                };
                return Page();
            }

            var r = _store.Get(id);
            if (r is null)
            {
                TempData["Err"] = "找不到这条记录（可能已被删除）";
                return RedirectToPage("/Records/Index");
            }
            if (r.Deleted)
            {
                // 不允许从"已删除"直接编辑：那等于用编辑悄悄复活一条记录，
                // 而客户端的镜像逻辑认为 deleted=1 就该消失，会来回打架。
                TempData["Err"] = "这条记录已被删除，无法编辑。需要恢复请先在列表里勾选「显示已删除」确认。";
                return RedirectToPage("/Records/Index");
            }

            Input = new InputModel
            {
                Id = r.Id,
                Direction = r.Direction,
                Amount = r.Amount,
                CategoryId = r.CategoryId,
                SpentAt = Fmt.ToLocalInput(r.SpentAt),
                PayMethod = r.PayMethod,
                Note = r.Note,
            };
            return Page();
        }

        public IActionResult OnPost()
        {
            Categories = _categories.All();

            long spentAt = Fmt.ParseLocal(Input.SpentAt);
            if (spentAt == 0)
                ModelState.AddModelError("Input.SpentAt", "时间格式不对，请用 2026-10-18 21:30 这种格式");

            if (Input.Amount <= 0)
                ModelState.AddModelError("Input.Amount", "金额必须大于 0");

            var existing = string.IsNullOrEmpty(Input.Id) ? null : _store.Get(Input.Id);
            if (existing is { Deleted: true })
            {
                ModelState.AddModelError(string.Empty, "这条记录已被删除，不能改。");
                return Page();
            }

            if (!ModelState.IsValid) return Page();

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var record = new Record
            {
                Id = Input.Id ?? string.Empty,
                Amount = Input.Amount,
                CategoryId = Input.CategoryId,
                Direction = Input.Direction,
                SpentAt = spentAt,
                PayMethod = Input.PayMethod ?? string.Empty,
                Note = Input.Note ?? string.Empty,
                CreatedAt = existing?.CreatedAt ?? now,
                // 管理端修改 = 新版本。服务端会拿它和库里的 updated_at 比，
                // 手机若还持有更旧的副本，下次上传会被判 conflict（不重试，直接拉取覆盖）。
                UpdatedAt = now,
            };

            var result = _store.Save(record);

            if (!result.Ok)
            {
                // 服务端的错误本来就是人话（"分类已停用"/"分类与收支方向不匹配"），直接展示
                ModelState.AddModelError(string.Empty, result.Error ?? "保存失败");
                return Page();
            }

            TempData["Ok"] = IsNew ? "已补录一条" : "已保存修改";
            return RedirectToPage("/Records/Index");
        }
    }
}
