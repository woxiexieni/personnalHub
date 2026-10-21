using PersonalHub.Client.Data;

namespace PersonalHub.Client.Services
{
    /// <summary>
    /// 「概览」Tab 的目录：看什么。
    ///
    /// 三条并行：钱、时间、目标。
    /// 前两条是"这段时间花在哪"，第三条是"我正在推进哪几条线、还动不动"。
    /// 结构做成目录（而不是把某一页直接当 Tab）就是为了这个：加第三条（复盘、周报）
    /// 只是在这里多一行，导航和界面都不用动。
    /// </summary>
    public static class OverviewCatalog
    {
        /// <summary>支出概览页路由（MainPage）。</summary>
        public const string SpendingRoute = "view/spending";

        /// <summary>时间概览页路由（TimeOverviewPage）。</summary>
        public const string TimeRoute = "view/time";

        /// <summary>周回顾页路由（WeekReviewPage）：这周 vs 上周，四个数，0 输入。</summary>
        public const string WeekReviewRoute = "view/week";

        public static List<DirectoryItem> Build(
            LocalRecordStore<LocalRecord> expenses,
            LocalRecordStore<LocalBlock> blocks,
            LocalRecordStore<LocalGoal> goals)
        {
            return new List<DirectoryItem>
            {
                new(
                    title: "支出概览",
                    subtitle: "支出 / 收入 / 结余，分类与支付方式占比",
                    icon: "📊",
                    route: SpendingRoute,
                    summaryProvider: async () =>
                    {
                        // 只取"今天"的本地合计：本地只有 30 天副本，
                        // 拿它算本月/本年会在跨月时缺数据 —— 那种数字比不显示更糟。
                        var (from, to) = Period.Today();
                        long spent = await expenses.SumExpenseCentsAsync(from, to).ConfigureAwait(false);
                        return spent == 0 ? null : $"今日支出 {RecordCatalog.Money(spent)}";
                    }),

                new(
                    title: "时间概览",
                    subtitle: "时间去哪了、未记录多少、目标累计投入",
                    icon: "⏳",
                    route: TimeRoute,
                    summaryProvider: async () =>
                    {
                        var (from, to) = Period.Today();
                        var pieces = await blocks.AggregateByPieceAsync(from, to).ConfigureAwait(false);

                        int recorded = pieces.Sum(p => p.Minutes);
                        if (recorded == 0) return null;   // 今天还没记，不用一行"今日 0 分钟"占地方

                        int elapsed = (int)Math.Max(0,
                            (Math.Min(to, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) - from) / 60000L);
                        int unrecorded = Math.Max(0, elapsed - recorded);

                        return $"今日已记录 {BlockEntryPage.FormatMinutes(recorded)} · 未记录 {BlockEntryPage.FormatMinutes(unrecorded)}";
                    }),

                // 周回顾紧跟时间概览：它是"这段时间花在哪"的时间轴版本 ——
                // 时间概览回答"花在哪"，周回顾回答"比上周变了没有"。
                // 这一项存在的理由只有一个：让反馈周期从三个月缩到一周。
                new(
                    title: "这周回顾",
                    subtitle: "这周 vs 上周：投入 / 哪条线动了 / 反复说的话 / 时间挪去哪",
                    icon: "🔁",
                    route: WeekReviewRoute,
                    summaryProvider: async () =>
                    {
                        var (from, to) = Period.ThisWeek();
                        var blocksNow = await blocks.GetByRangeAsync(from, to).ConfigureAwait(false);

                        int nowMin = blocksNow.Sum(b => Math.Max(0, b.DurationMin));
                        if (nowMin == 0 && blocksNow.Count == 0) return "这周还没有记录";

                        return $"本周 {BlockEntryPage.FormatMinutes(nowMin)} · 记了 {blocksNow.Count} 条";
                    }),

                // 目标放概览里，不放「记录」里：它不是一次录入，是"我正在推进的几条线"。
                // 记录目录回答"刚刚发生了什么"，概览目录回答"我现在处在哪"。
                new(
                    title: "目标",
                    subtitle: "在推进的几条线：累计投入、最后活动、四周趋势",
                    icon: "🎯",
                    route: RecordCatalog.GoalRoute,
                    summaryProvider: async () =>
                    {
                        var all = await goals.GetByRangeAsync(0, long.MaxValue).ConfigureAwait(false);
                        var active = all.Where(g => g.Status == 0).ToList();
                        if (active.Count == 0) return null;

                        // 本周投入只算本地 30 天副本里的这些目标——够用，而且不会假装是全量
                        var (from, to) = Period.ThisWeek();
                        var weekBlocks = await blocks.GetByRangeAsync(from, to).ConfigureAwait(false);
                        var ids = active.Select(g => g.Id).ToHashSet(StringComparer.Ordinal);
                        int minutes = weekBlocks
                            .Where(b => ids.Contains(b.GoalId ?? string.Empty))
                            .Sum(b => Math.Max(0, b.DurationMin));

                        return minutes > 0
                            ? $"进行中 {active.Count} 个 · 本周 {BlockEntryPage.FormatMinutes(minutes)}"
                            : $"进行中 {active.Count} 个 · 本周还没动";
                    }),

            };
        }
    }
}
