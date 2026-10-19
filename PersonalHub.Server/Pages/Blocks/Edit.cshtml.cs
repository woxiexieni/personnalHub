using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Personalhub;
using personalHub.Server.Data;

namespace PersonalHub.Server.Pages.Blocks
{
    /// <summary>
    /// 单条时间块的补录 / 编辑。
    ///
    /// 校验全部落在 <see cref="BlockStore.Save"/>，这里只做"早一点给人话"。
    /// 页面自己再写一套 if，就会出现"网页能存、手机不让存"这种只在统计上炸、不报错的问题。
    ///
    /// 一条刻意的约束：**删除不在这里恢复**。已删的块打开只会提示，不给编辑框 ——
    /// 否则"编辑"就成了偷偷复活一条记录的后门，而手机端镜像认为 deleted=1 就该消失，两边会打架。
    /// </summary>
    public class EditModel : PageModel
    {
        private readonly BlockStore _blocks;
        private readonly GoalStore _goals;

        public EditModel(BlockStore blocks, GoalStore goals)
        {
            _blocks = blocks;
            _goals = goals;
        }

        [BindProperty] public InputModel Input { get; set; } = new();

        public IReadOnlyList<Goal> Goals { get; private set; } = Array.Empty<Goal>();

        public bool IsNew => string.IsNullOrEmpty(Input.Id);

        public class InputModel
        {
            public string? Id { get; set; }
            public string? PieceCode { get; set; }
            /// <summary>datetime-local 字符串，按 UTC+8 解释。</summary>
            public string? OccurredAt { get; set; }
            /// <summary>分钟。0 = 没填，合法（半条记录）。</summary>
            public int DurationMin { get; set; }
            public string? GoalId { get; set; }
            public string? Note { get; set; }
        }

        public IActionResult OnGet(string? id, string? goalId)
        {
            Goals = _goals.List();

            if (string.IsNullOrEmpty(id))
            {
                var now = DateTimeOffset.UtcNow;
                Input = new InputModel
                {
                    OccurredAt = Fmt.ToLocalInput(now.ToUnixTimeMilliseconds()),
                    DurationMin = 30,
                    PieceCode = "code",
                    // 默认继承「当前目标」：和手机端录入一致，否则电脑补录的块全成了孤儿。
                    // 从目标演进页点「补录」进来时，直接归到那个目标。
                    GoalId = !string.IsNullOrEmpty(goalId)
                        ? goalId
                        : Goals.FirstOrDefault(g => g.IsCurrent && g.Status == 0)?.Id,
                };
                return Page();
            }

            var b = Find(id);
            if (b is null)
            {
                TempData["Err"] = "找不到这条时间块（可能已被删除）";
                return RedirectToPage("/Blocks/Index");
            }
            if (b.Deleted)
            {
                TempData["Err"] = "这条时间块已被删除，无法编辑。需要核对请勾选「显示已删除」。";
                return RedirectToPage("/Blocks/Index");
            }

            Input = new InputModel
            {
                Id = b.Id,
                PieceCode = b.PieceCode,
                OccurredAt = Fmt.ToLocalInput(b.OccurredAt),
                DurationMin = b.DurationMin,
                GoalId = string.IsNullOrEmpty(b.GoalId) ? null : b.GoalId,
                Note = b.Note,
            };
            return Page();
        }

        public IActionResult OnPost()
        {
            Goals = _goals.List();

            long occurred = Fmt.ParseLocal(Input.OccurredAt);
            if (occurred == 0)
                ModelState.AddModelError("Input.OccurredAt", "时间格式不对，请用 2026-10-18 21:30 这种格式");

            if (string.IsNullOrWhiteSpace(Input.PieceCode))
                ModelState.AddModelError("Input.PieceCode", "必须选一个事情片");

            if (Input.DurationMin < 0 || Input.DurationMin > 1440)
                ModelState.AddModelError("Input.DurationMin", "时长要在 0 ~ 1440 分钟之间（0 表示先不填）");

            var existing = string.IsNullOrEmpty(Input.Id) ? null : Find(Input.Id);
            if (existing is { Deleted: true })
            {
                ModelState.AddModelError(string.Empty, "这条时间块已被删除，不能改。");
                return Page();
            }

            if (!ModelState.IsValid) return Page();

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var block = new Block
            {
                Id = Input.Id ?? string.Empty,
                PieceCode = Input.PieceCode ?? string.Empty,
                OccurredAt = occurred,
                DurationMin = Input.DurationMin,
                GoalId = Input.GoalId ?? string.Empty,
                Note = Input.Note ?? string.Empty,
                CreatedAt = existing?.CreatedAt ?? now,
                // 管理端修改 = 新版本：手机若还持有更旧的副本，下次上传会被判冲突（不重试，直接拉取覆盖）
                UpdatedAt = now,
            };

            var result = _blocks.Save(block);
            if (!result.Ok)
            {
                ModelState.AddModelError(string.Empty, result.Error ?? "保存失败");
                return Page();
            }

            TempData["Ok"] = IsNew ? "已补录一条时间块" : "已保存修改";
            return RedirectToPage("/Blocks/Index");
        }

        /// <summary>按 id 找一条（含已删除的，好给出"已删除"的明确提示）。</summary>
        private Block? Find(string id) => _blocks.Get(id);
    }
}
