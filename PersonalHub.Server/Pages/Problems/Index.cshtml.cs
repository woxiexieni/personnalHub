using Microsoft.AspNetCore.Mvc.RazorPages;
using Personalhub;
using personalHub.Server.Data;

namespace PersonalHub.Server.Pages.Problems
{
    /// <summary>
    /// 问题卡库（管理端，R7.2）。
    ///
    /// 手机上这一页是"每天要看见的债"，电脑上这一页是"翻账本"：
    /// 判据（怎么算还完）、试法（现在用的招跑几天了）、证据（事件卡反挂了几条）。
    ///
    /// 刻意不做的事：
    ///   1. 不做编辑 / 不做"标记解决"。评估必须在手机上点 ——
    ///      那张卡是你在现实里试出来的，坐在电脑前点"解决了"没有任何分量。
    ///   2. 不做完成率、不做"提醒你已经 N 天没评估"。这一页只呈现事实，不催人。
    ///   3. 不显示"证据太多 = 问题严重"这种暗示。证据多是好事：说明你真的在记。
    /// </summary>
    public class IndexModel : PageModel
    {
        private const int PageSize = 300;

        private readonly ProblemStore _problems;
        private readonly ThingStore _things;

        public IndexModel(ProblemStore problems, ThingStore things)
        {
            _problems = problems;
            _things = things;
        }

        /// <summary>-1 全部 / 0 进行中 / 1 已解决 / 2 放弃。</summary>
        [Microsoft.AspNetCore.Mvc.BindProperty(SupportsGet = true)]
        public int Status { get; set; } = -1;

        public IReadOnlyList<Problem> Items { get; private set; } = Array.Empty<Problem>();

        /// <summary>problem_id → 证据条数。</summary>
        public IReadOnlyDictionary<string, int> Evidence { get; private set; }
            = new Dictionary<string, int>();

        public int CountActive { get; private set; }
        public int CountSolved { get; private set; }
        public int CountDropped { get; private set; }
        public int EvidenceTotal { get; private set; }
        public int Hidden { get; private set; }

        public void OnGet()
        {
            var counts = _problems.CountByStatus();
            CountActive = counts.TryGetValue(0, out int a) ? a : 0;
            CountSolved = counts.TryGetValue(1, out int s) ? s : 0;
            CountDropped = counts.TryGetValue(2, out int d) ? d : 0;

            Evidence = _things.CountByProblem();
            EvidenceTotal = Evidence.Values.Sum();

            var rows = _problems.List(Status, 0, 0, includeDeleted: false);
            Items = rows.Take(PageSize).ToList();
            Hidden = Math.Max(0, rows.Count - Items.Count);
        }

        public int EvidenceOf(Problem p)
            => !string.IsNullOrEmpty(p.Id) && Evidence.TryGetValue(p.Id, out int n) ? n : 0;

        /// <summary>试法那一列的文案：观察中 / 跑了几天 / 换过几次招。</summary>
        public string AttemptText(Problem p)
        {
            if (string.IsNullOrWhiteSpace(p.Attempt))
            {
                return p.Status == 0 ? "观察中（还没定试法）" : "—";
            }

            var parts = new List<string> { p.Attempt };

            // 「跑了几天」只对进行中的卡有意义：关卡之后这个数会一直涨，
            // 看着像"还在跑"，其实早就停了。
            if (p.Status == 0 && p.AttemptStartedAt > 0)
            {
                int days = Math.Max(1, (int)((DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - p.AttemptStartedAt)
                                             / 86400000L));
                parts.Add($"跑了 {days} 天");
            }

            if (p.AttemptCount > 0) parts.Add($"换过 {p.AttemptCount} 次招");

            return string.Join(" · ", parts);
        }

        public string StatusText(Problem p) => p.Status switch
        {
            1 => "已解决",
            2 => "放弃",
            _ => "进行中",
        };

        public string StatusClass(Problem p) => p.Status switch
        {
            1 => "tag done",
            2 => "tag",
            _ => "tag active",
        };

        /// <summary>长文本预览：判据通常一句话，但有人会写很长。</summary>
        public static string Preview(string? text, int max = 160)
        {
            string t = (text ?? string.Empty).Replace("\r", " ").Replace("\n", " ");
            return t.Length <= max ? t : t.Substring(0, max) + "…";
        }
    }
}
