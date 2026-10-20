using PersonalHub.Client.Data;

namespace PersonalHub.Client.Services
{
    /// <summary>
    /// 「记录」Tab 的目录：往库里写什么。
    ///
    /// 新增一种记录类型，只改这里（+ AppShell 一行路由 + MauiProgram 一行注册 + 一个录入页）。
    /// 底部导航永远不用动 —— 这也是为什么 Tab 只有「概览 / 记录」两项。
    ///
    /// R2 加了第三项「时间块」。三项的动机刻意不重叠，用户不会犹豫该点哪个：
    ///   账单     → 钱出去了
    ///   时间块   → 刚才那段时间去哪了
    ///   事件反思 → 有一件事值得深挖
    /// 目标不在这里：它不是"记录"，是归属容器，入口在
    ///   ① 时间块页顶部（快速切当前目标）
    ///   ② 概览目录（看"我在推进哪几条线"）
    /// </summary>
    public static class RecordCatalog
    {
        /// <summary>账单录入页路由。</summary>
        public const string ExpenseRoute = "entry/expense";

        /// <summary>事件反思录入页路由（9 段，低频深写）。</summary>
        public const string ThingRoute = "entry/thing";

        /// <summary>时间块录入页路由（R2 主入口）。</summary>
        public const string BlockRoute = "entry/block";

        /// <summary>行为模式列表路由（R5）。</summary>
        public const string PatternRoute = "pattern/list";

        /// <summary>行为模式详情路由（带 ?id=）。</summary>
        public const string PatternDetailRoute = "pattern/detail";

        /// <summary>目标列表路由。</summary>
        public const string GoalRoute = "goal/list";

        /// <summary>目标详情路由（带 ?id=）。</summary>
        public const string GoalDetailRoute = "goal/detail";

        /// <summary>回顾页（三个数 + 停滞目标 + 奖励自决）。</summary>
        public const string QuarterReviewRoute = "goal/review";

        /// <summary>想法录入路由（R6）：一个大框 + 四个类型，默认未分类。</summary>
        public const string IdeaRoute = "idea/entry";

        /// <summary>想法库路由（列表 + 转成目标）。</summary>
        public const string IdeaListRoute = "idea/list";

        public static List<DirectoryItem> Build(
            LocalRecordStore<LocalRecord> expenses,
            LocalRecordStore<ThingRecord> things,
            LocalRecordStore<LocalBlock> blocks,
            LocalRecordStore<LocalIdea> ideas)
        {
            return new List<DirectoryItem>
            {
                new(
                    title: "账单",
                    subtitle: "支出 / 收入，随手记一笔",
                    icon: "💰",
                    route: ExpenseRoute,
                    summaryProvider: async () =>
                    {
                        var (from, to) = Period.Today();
                        long spent = await expenses.SumExpenseCentsAsync(from, to).ConfigureAwait(false);
                        long income = await expenses.SumIncomeCentsAsync(from, to).ConfigureAwait(false);

                        if (spent == 0 && income == 0) return null;
                        if (income == 0) return $"今日支出 {Money(spent)}";
                        if (spent == 0) return $"今日收入 {Money(income)}";

                        return $"今日支出 {Money(spent)} · 收入 {Money(income)}";
                    },
                    pendingProvider: expenses.CountUnsyncedAsync),

                // 时间块放第二项：它是唯一一个"一天点 6~10 次"的入口，
                // 位置越靠上，越不用找。
                new(
                    title: "时间块",
                    subtitle: "刚才那段时间去哪了",
                    icon: "⏱",
                    route: BlockRoute,
                    summaryProvider: async () =>
                    {
                        var (from, to) = Period.Today();
                        var rows = await blocks.GetByRangeAsync(from, to).ConfigureAwait(false);
                        if (rows.Count == 0) return null;

                        int minutes = rows.Sum(r => Math.Max(0, r.DurationMin));
                        return $"今天已记 {rows.Count} 条 · 共 {BlockEntryPage.FormatMinutes(minutes)}";
                    },
                    pendingProvider: blocks.CountUnsyncedAsync),

                new(
                    title: "事件反思",
                    subtitle: "想深挖一件事时用（9 段，不是日常入口）",
                    icon: "📝",
                    route: ThingRoute,
                    summaryProvider: async () =>
                    {
                        var (from, to) = Period.Today();
                        var rows = await things.GetByRangeAsync(from, to).ConfigureAwait(false);
                        return rows.Count == 0 ? null : $"今日已记 {rows.Count} 条";
                    },
                    pendingProvider: things.CountUnsyncedAsync),

                new(
                    title: "行为模式",
                    subtitle: "在练什么：触发 / 旧反应 / 新反应（一季度两三个）",
                    icon: "🌱",
                    route: PatternRoute),

                // R6：想法放最后一项。
                // 位置说明：前四项都是"已经发生、有结构"的事（钱 / 时间 / 事件 / 反应），
                // 想法是唯一"还没成形"的东西 —— 放在最后，既不会抢主入口，也随时找得到。
                // 它和三、四项的关系是：想法是原材料，反思和模式是加工后的产物。
                new(
                    title: "想法",
                    subtitle: "冒出来就记：事实 / 评价 / 计划 / 脑补，不分类也行",
                    icon: "💡",
                    route: IdeaRoute,
                    summaryProvider: async () =>
                    {
                        var (from, to) = Period.Today();
                        var todayRows = await ideas.GetByRangeAsync(from, to).ConfigureAwait(false);
                        var all = await ideas.GetByRangeAsync(0, long.MaxValue).ConfigureAwait(false);

                        if (all.Count == 0) return null;
                        return todayRows.Count == 0
                            ? $"库里 {all.Count} 条"
                            : $"今天 {todayRows.Count} 条 · 库里共 {all.Count} 条";
                    },
                    pendingProvider: ideas.CountUnsyncedAsync),
            };
        }

        internal static string Money(long cents) => "¥" + (cents / 100m).ToString("0.00");
    }
}
