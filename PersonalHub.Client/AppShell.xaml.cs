using PersonalHub.Client.Services;

namespace PersonalHub.Client
{
    public partial class AppShell : Shell
    {
        public AppShell()
        {
            InitializeComponent();
            RegisterRoutes();
        }

        /// <summary>路由表是静态的，重复注册没有意义（某些平台重建 Shell 时还会抛重复路由）。</summary>
        private static bool _routesRegistered;

        /// <summary>
        /// 把「录入页」注册成可推进的路由。
        ///
        /// 这些页面不在 TabBar 里，而是从「记录」目录点进去。
        /// 新增一种记录类型时，这里加一行、RecordCatalog 加一条——底部导航不用动。
        /// </summary>
        private static void RegisterRoutes()
        {
            if (_routesRegistered) return;
            _routesRegistered = true;

            Routing.RegisterRoute(
                RecordCatalog.ExpenseRoute,
                new ServiceRouteFactory(typeof(RecordEntryPage)));

            // 目标回顾（三个数 + 该砍的）与「行为模式」：都不做成 Tab，只从目录里进
            Routing.RegisterRoute(
                RecordCatalog.QuarterReviewRoute,
                new ServiceRouteFactory(typeof(QuarterReviewPage)));
            Routing.RegisterRoute(
                RecordCatalog.PatternRoute,
                new ServiceRouteFactory(typeof(PatternListPage)));
            Routing.RegisterRoute(
                RecordCatalog.PatternDetailRoute,
                new ServiceRouteFactory(typeof(PatternDetailPage)));

            Routing.RegisterRoute(
                RecordCatalog.ThingRoute,
                new ServiceRouteFactory(typeof(RecordDothingPage)));

            // R6：想法（录入 + 想法库）。两级：先记，再整理。
            Routing.RegisterRoute(
                RecordCatalog.IdeaRoute,
                new ServiceRouteFactory(typeof(IdeaEntryPage)));
            Routing.RegisterRoute(
                RecordCatalog.IdeaListRoute,
                new ServiceRouteFactory(typeof(IdeaListPage)));

            // R2：时间块 + 目标
            Routing.RegisterRoute(
                RecordCatalog.BlockRoute,
                new ServiceRouteFactory(typeof(BlockEntryPage)));

            Routing.RegisterRoute(
                RecordCatalog.GoalRoute,
                new ServiceRouteFactory(typeof(GoalListPage)));

            // 带 ?id= 的详情页。Shell 用 [QueryProperty] 把 id 塞进页面属性。
            Routing.RegisterRoute(
                RecordCatalog.GoalDetailRoute,
                new ServiceRouteFactory(typeof(GoalDetailPage)));

            // 「概览」目录里的页
            Routing.RegisterRoute(
                OverviewCatalog.SpendingRoute,
                new ServiceRouteFactory(typeof(MainPage)));

            Routing.RegisterRoute(
                OverviewCatalog.TimeRoute,
                new ServiceRouteFactory(typeof(TimeOverviewPage)));

            // 周回顾：这周 vs 上周。概览目录进，和「时间概览」同一层。
            Routing.RegisterRoute(
                OverviewCatalog.WeekReviewRoute,
                new ServiceRouteFactory(typeof(WeekReviewPage)));

            // 以前这里还有一个「数据与恢复」页。2026-10-21 撤掉：
            // 它把"数据会不会丢"变成一件要用户记得去做的事。
            // 现在由 StartupRestoreService 在本机为空时自动跑一次，没有入口。
        }
    }

    /// <summary>
    /// 让 Shell 的路由通过 DI 创建页面。
    ///
    /// 为什么需要它：路由页如果靠默认的 Activator 创建，会去找**无参构造**，
    /// 而我们的页面都要注入 Store / 同步器 → 运行时直接 MissingMethodException。
    /// 默认实现是否走容器随 MAUI 版本变化，写死一个工厂最省心，也不依赖版本行为。
    /// </summary>
    internal sealed class ServiceRouteFactory : RouteFactory
    {
        private readonly Type _pageType;

        public ServiceRouteFactory(Type pageType) => _pageType = pageType;

        public override Element GetOrCreate() => Resolve(null);

        public override Element GetOrCreate(IServiceProvider services) => Resolve(services);

        private Element Resolve(IServiceProvider? services)
        {
            services ??= IPlatformApplication.Current?.Services;

            object? page = services?.GetService(_pageType);

            if (page is null)
            {
                // 只有真的有无参构造时才兜底。否则别让 Activator 抛 MissingMethodException ——
                // 那个异常只说"没有无参构造"，不说"你忘了注册"，排查要绕一大圈。
                page = _pageType.GetConstructor(Type.EmptyTypes) is not null
                    ? Activator.CreateInstance(_pageType)
                    : null;
            }

            return page as Element
                ?? throw new InvalidOperationException(
                    $"页面 {_pageType.Name} 打不开：它需要依赖注入，但 MauiProgram 里没有注册。" +
                    $"在 MauiProgram.cs 里加一行 builder.Services.AddTransient<{_pageType.Name}>(); 即可。");
        }
    }
}
