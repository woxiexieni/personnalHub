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

            Routing.RegisterRoute(
                RecordCatalog.ThingRoute,
                new ServiceRouteFactory(typeof(RecordDothingPage)));
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

            object? page = services?.GetService(_pageType) ?? Activator.CreateInstance(_pageType);

            return page as Element
                ?? throw new InvalidOperationException($"无法创建页面 {_pageType.Name}，请检查 MauiProgram 里是否注册了它。");
        }
    }
}
