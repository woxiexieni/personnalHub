using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Personalhub;
using personalHub.Server.Data;

namespace PersonalHub.Server.Pages.Goals
{
    /// <summary>
    /// 目标管理：全部目标 + 累计投入 / 最后活动 / 下一步，逐条进编辑页。
    ///
    /// 累计投入与最后活动是 <see cref="GoalStore.List"/> 现算的（全量，不受 30 天窗口限制），
    /// 这里只读不写 —— 写回去就错了，删一条块之后永远不会被纠正。
    /// 停滞（14 天无块）在这里只是加个标记，不做隐藏：管理端是核对数据的地方，该看到的都在。
    /// </summary>
    public class IndexModel : PageModel
    {
        private readonly GoalStore _goals;
        private readonly BlockStore _blocks;

        public IndexModel(GoalStore goals, BlockStore blocks)
        {
            _goals = goals;
            _blocks = blocks;
        }

        public IReadOnlyList<Goal> Items { get; private set; } = Array.Empty<Goal>();

        /// <summary>每个目标最近四周的分钟数（4 桶，左旧右新）。</summary>
        public IReadOnlyDictionary<string, int[]> Trends { get; private set; }
            = new Dictionary<string, int[]>(StringComparer.Ordinal);

        /// <summary>进行中数量（活跃上限 3）。</summary>
        public int ActiveCount { get; private set; }

        public void OnGet()
        {
            Items = _goals.List();
            ActiveCount = Items.Count(g => g.Status == 0);

            var now = DateTimeOffset.UtcNow;
            Trends = Items.ToDictionary(
                g => g.Id,
                g => _blocks.WeeklyMinutes(g.Id, now).ToArray(),
                StringComparer.Ordinal);
        }

        public int[] TrendOf(string goalId)
            => Trends.TryGetValue(goalId, out var t) ? t : new[] { 0, 0, 0, 0 };

        public string StatusText(int status) => status switch
        {
            1 => "已暂停",
            2 => "已完成",
            3 => "已放弃",
            _ => "进行中",
        };

        public string StatusClass(int status) => status switch
        {
            1 => "paused",
            2 => "done",
            3 => "deleted",
            _ => "active",
        };

        /// <summary>最后活动距今几天（0 = 今天，-1 = 还没动过）。</summary>
        public int DaysSince(long lastAt)
        {
            if (lastAt == 0) return -1;
            long days = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - lastAt) / (24L * 60 * 60 * 1000);
            return (int)days;
        }

        public string LastText(long lastAt)
        {
            int d = DaysSince(lastAt);
            return d < 0 ? "还没动过" : d == 0 ? "今天" : $"{d} 天前";
        }

        /// <summary>进行中且超过 14 天没动：加个"停滞"标记（不隐藏，管理端要看到全部）。</summary>
        public bool IsStalled(Goal g)
        {
            if (g.Status != 0) return false;
            long marker = g.LastActivityAt != 0 ? g.LastActivityAt : g.StartedAt;
            if (marker == 0) return false;
            return marker < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 14L * 24 * 60 * 60 * 1000;
        }
    }
}
