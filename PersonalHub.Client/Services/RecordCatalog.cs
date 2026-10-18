using System.ComponentModel;
using PersonalHub.Client.Data;

namespace PersonalHub.Client.Services
{
    /// <summary>
    /// 「记录」目录里的一项 = 一种可以录入的记录类型。
    ///
    /// 这是导航重构的核心：底部 Tab **不按记录类型分**，只分「概览 / 记录」两项。
    /// 类型一律挂在这个目录里，所以以后加"习惯 / 体重 / 阅读"，
    /// 只需要在 <see cref="RecordCatalog.Build"/> 里加一条、在 AppShell 注册一个路由，
    /// **不用碰底部导航**——底部一排最多也就放得下 4~5 个，按类型分会很快加不下。
    /// </summary>
    public sealed class RecordTypeItem : INotifyPropertyChanged
    {
        private string _summary = string.Empty;
        private string _badge = string.Empty;

        public RecordTypeItem(
            string key,
            string title,
            string subtitle,
            string icon,
            string route,
            Func<Task<string?>>? summaryProvider = null,
            Func<Task<int>>? pendingProvider = null)
        {
            Key = key;
            Title = title;
            Subtitle = subtitle;
            Icon = icon;
            Route = route;
            SummaryProvider = summaryProvider;
            PendingProvider = pendingProvider;
        }

        /// <summary>稳定标识（日志/埋点用，不参与界面）。</summary>
        public string Key { get; }

        public string Title { get; }

        /// <summary>一句话说明，让人在目录里就知道这类记的是什么。</summary>
        public string Subtitle { get; }

        /// <summary>用 emoji 而不是图片：省一套图标资源，也不用为深色模式准备两套。</summary>
        public string Icon { get; }

        /// <summary>Shell 路由（AppShell 里注册）。</summary>
        public string Route { get; }

        /// <summary>副标题下面那行小字，例如"今日支出 ¥12.30"。返回 null = 不显示。</summary>
        public string SummaryText => _summary;

        public bool HasSummary => _summary.Length > 0;

        /// <summary>右上角徽标，例如"3 条待同步"。</summary>
        public string BadgeText => _badge;

        public bool HasBadge => _badge.Length > 0;

        internal Func<Task<string?>>? SummaryProvider { get; }

        internal Func<Task<int>>? PendingProvider { get; }

        /// <summary>
        /// 刷新小字与徽标。**故意吞掉异常**：目录页是"入口"，某个类型的本地库读失败
        /// 不该让整个目录打不开——那一项只是少行小字而已。
        /// </summary>
        public async Task RefreshAsync()
        {
            string summary = string.Empty;
            string badge = string.Empty;

            try
            {
                if (SummaryProvider is not null)
                {
                    // ConfigureAwait(true)：改的是绑定到界面的属性，必须回 UI 线程
                    summary = await SummaryProvider().ConfigureAwait(true) ?? string.Empty;
                }

                if (PendingProvider is not null)
                {
                    int pending = await PendingProvider().ConfigureAwait(true);
                    badge = pending > 0 ? $"{pending} 条待同步" : string.Empty;
                }
            }
            catch
            {
                // 读不到就当作"没有额外信息"，不影响点进去录入
            }

            Set(ref _summary, summary, nameof(SummaryText), nameof(HasSummary));
            Set(ref _badge, badge, nameof(BadgeText), nameof(HasBadge));
        }

        private void Set(ref string field, string value, string textProp, string boolProp)
        {
            if (field == value) return;

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(textProp));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(boolProp));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>
    /// 记录类型目录 —— 新增一种记录类型，只改这里（+ 一个路由 + 一个页面）。
    ///
    /// 为什么用"静态方法 + 参数注入"而不是注册成 DI 服务：
    /// 目录项各自要读自己的 Store（账单表 / 事件表），而这些 Store 是闭合泛型、
    /// 类型不同；写成静态构建方法最直白，也不用为每种组合再登记一次容器。
    /// </summary>
    public static class RecordCatalog
    {
        /// <summary>账单录入页路由。</summary>
        public const string ExpenseRoute = "entry/expense";

        /// <summary>事件反思录入页路由。</summary>
        public const string ThingRoute = "entry/thing";

        public static List<RecordTypeItem> Build(
            LocalRecordStore<LocalRecord> expenses,
            LocalRecordStore<ThingRecord> things)
        {
            return new List<RecordTypeItem>
            {
                new(
                    key: "expense",
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

                new(
                    key: "thing",
                    title: "事件反思",
                    subtitle: "发生了什么、我的情绪与反思",
                    icon: "📝",
                    route: ThingRoute,
                    summaryProvider: async () =>
                    {
                        var (from, to) = Period.Today();
                        var rows = await things.GetByRangeAsync(from, to).ConfigureAwait(false);
                        return rows.Count == 0 ? null : $"今日已记 {rows.Count} 条";
                    },
                    pendingProvider: things.CountUnsyncedAsync),
            };
        }

        private static string Money(long cents) => "¥" + (cents / 100m).ToString("0.00");
    }
}
