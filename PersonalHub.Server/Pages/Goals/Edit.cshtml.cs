using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Personalhub;
using personalHub.Server.Data;

namespace PersonalHub.Server.Pages.Goals
{
    /// <summary>
    /// 目标编辑 / 新建：标题、一条下一步、状态、是否为当前目标。
    ///
    /// 字段刻意只有这四个 —— 表里就这四样（见 <see cref="GoalRow"/>），
    /// 累计投入之类的是查询结果，不存在"编辑"一说。
    /// 保存走 <see cref="GoalStore.Save"/>，与手机端同一套校验（活跃上限 / 当前唯一 / 版本冲突）。
    /// </summary>
    public class EditModel : PageModel
    {
        private readonly GoalStore _goals;

        public EditModel(GoalStore goals)
        {
            _goals = goals;
        }

        [BindProperty] public string? Id { get; set; }
        [BindProperty] public string? Title { get; set; }
        [BindProperty] public string? NextStep { get; set; }
        [BindProperty] public int Status { get; set; }
        [BindProperty] public bool IsCurrent { get; set; }

        /// <summary>编辑时的原 UpdatedAt，透传给 Save 过版本检查。</summary>
        [BindProperty] public long RowUpdatedAt { get; set; }

        public bool IsNew => string.IsNullOrEmpty(Id);

        public string Error { get; private set; } = string.Empty;

        public IActionResult OnGet(string? id)
        {
            if (string.IsNullOrEmpty(id)) return Page();

            var g = _goals.Get(id);
            if (g is null)
            {
                Error = "这个目标不存在（可能已被删除）";
                return Page();
            }

            Id = g.Id;
            Title = g.Title;
            NextStep = g.NextStep;
            Status = g.Status;
            IsCurrent = g.IsCurrent;
            RowUpdatedAt = g.UpdatedAt;
            return Page();
        }

        public IActionResult OnPost()
        {
            var title = (Title ?? string.Empty).Trim();
            if (title.Length == 0)
            {
                Error = "目标名称不能为空";
                return Page();
            }

            // 只有进行中的目标能当"当前目标"（与手机端一致：暂停/完成自动让出）
            bool current = IsCurrent && Status == 0;

            var g = new Goal
            {
                Id = Id ?? string.Empty,
                Title = title,
                NextStep = (NextStep ?? string.Empty).Trim(),
                Status = Status is < 0 or > 3 ? 0 : Status,
                IsCurrent = current,
                UpdatedAt = RowUpdatedAt,
            };

            var r = _goals.Save(g);
            if (!r.Ok)
            {
                Error = r.Error ?? "保存失败";
                return Page();
            }

            TempData["Ok"] = IsNew ? $"已创建「{title}」" : $"已保存「{title}」";
            return Redirect("/goals");
        }
    }
}
