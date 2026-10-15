using Microsoft.Extensions.Logging;
using PersonalHub.Client.Data;
using PersonalHub.Client.Services;

namespace PersonalHub.Client
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                    fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                });

#if DEBUG
    		builder.Logging.AddDebug();
#endif

            // 服务端连接：启动时自动探测（缓存 → 模拟器别名 → 扫描本网段找 :5000）。
            // 候选/扫描逻辑在 Services/ServerLocator.cs；通道的持有与重建在 Services/ServerConnection.cs。
            // 这里只注册管理器，真正定位放到后台做，避免拖慢启动。
            builder.Services.AddSingleton<ServerConnection>();

            // ===== 离线优先：本地 SQLite 库 + 同步器 =====
            string dbPath = Path.Combine(FileSystem.AppDataDirectory, "personalhub.db3");
            builder.Services.AddSingleton(new LocalRecordStore(dbPath));
            builder.Services.AddSingleton<RecordSyncService>();

            // 注册页面：让 Shell 通过 DI 创建页面，从而把依赖注入构造函数。
            // 不注册的话，Shell 会退回 Activator.CreateInstance 找无参构造 → MissingMethodException 启动崩溃。
            builder.Services.AddTransient<MainPage>();
            builder.Services.AddTransient<RecordEntryPage>();

            var app = builder.Build();

            // 启动即后台预热一次地址探测，让首次「Ping / 同步」更快。
            var connection = app.Services.GetRequiredService<ServerConnection>();
            _ = Task.Run(async () =>
            {
                try { await connection.EnsureAsync().ConfigureAwait(false); }
                catch { /* 探测失败不影响启动，页面/同步触发时会重试 */ }
            });

            // 订阅网络恢复事件：回家连上 Wi-Fi 时自动触发一次同步。
            // 即使这里订阅失败，页面 OnAppearing 仍会触发同步，不影响主流程。
            app.Services.GetRequiredService<RecordSyncService>().Start();

            return app;
        }
    }
}
