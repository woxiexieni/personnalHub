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

            // ===== 离线优先：本地 SQLite 库 =====
            // 两个记录类型共用一个库文件（表名不同：records / ThingsRecords）。
            // sqlite-net 的异步连接按"路径+标志"共享同一底层连接，所以两个 Store 不会互相锁死。
            string dbPath = Path.Combine(FileSystem.AppDataDirectory, "personalhub.db3");
            builder.Services.AddSingleton(new LocalRecordStore<LocalRecord>(dbPath));
            builder.Services.AddSingleton(new LocalRecordStore<ThingRecord>(dbPath));

            // R2：时间块 + 目标（同一库文件，表名 blocks / goals）。
            builder.Services.AddSingleton(new LocalRecordStore<LocalBlock>(dbPath));
            builder.Services.AddSingleton(new LocalRecordStore<LocalGoal>(dbPath));
            builder.Services.AddSingleton(new LocalRecordStore<LocalPlan>(dbPath));
            builder.Services.AddSingleton(new LocalRecordStore<LocalPattern>(dbPath));
            builder.Services.AddSingleton(new LocalRecordStore<LocalPractice>(dbPath));

            // R6：想法（原材料仓库，与"行动"无关）
            builder.Services.AddSingleton(new LocalRecordStore<LocalIdea>(dbPath));

            // ===== 上传器：每类记录一个（proto 差异全部关在这里）=====
            builder.Services.AddSingleton<IRecordUploader<LocalRecord>, RecordUploader>();
            builder.Services.AddSingleton<IRecordUploader<ThingRecord>, ThingUploader>();
            builder.Services.AddSingleton<IRecordUploader<LocalBlock>, BlockUploader>();
            builder.Services.AddSingleton<IRecordUploader<LocalGoal>, GoalUploader>();
            builder.Services.AddSingleton<IRecordUploader<LocalPlan>, PlanUploader>();
            builder.Services.AddSingleton<IRecordUploader<LocalPattern>, PatternUploader>();
            builder.Services.AddSingleton<IRecordUploader<LocalPractice>, PracticeUploader>();
            builder.Services.AddSingleton<IRecordUploader<LocalIdea>, IdeaUploader>();

            // ===== 同步器：泛型只吃本地模型，所以一个类服务三种记录 =====
            builder.Services.AddSingleton<RecordSyncService<LocalRecord>>();
            builder.Services.AddSingleton<RecordSyncService<ThingRecord>>();
            builder.Services.AddSingleton<RecordSyncService<LocalBlock>>();
            builder.Services.AddSingleton<RecordSyncService<LocalGoal>>();
            builder.Services.AddSingleton<RecordSyncService<LocalPlan>>();
            builder.Services.AddSingleton<RecordSyncService<LocalPattern>>();
            builder.Services.AddSingleton<RecordSyncService<LocalPractice>>();
            builder.Services.AddSingleton<RecordSyncService<LocalIdea>>();

            // ===== 缓存层：最近 30 天镜像 + 今日汇总 + 分类 =====
            // 只负责"从服务端拉下来把本地副本对齐"，上传仍然归 RecordSyncService。
            builder.Services.AddSingleton<LocalCacheService>();

            // 恢复：把服务端的全部历史搬回本地（换机 / 重装用）。
            // 与上面两个镜像服务不是一回事：镜像只管最近 30 天，这个管"全部历史"。
            // 2026-10-21：界面上那个"数据与恢复"页撤掉了（用户不需要一个要自己记得点的入口），
            // 改成**本机为空时自动跑一次** —— 见 StartupRestoreService。
            builder.Services.AddSingleton<RestoreService>();
            builder.Services.AddSingleton<StartupRestoreService>();

            // 想法的"服务端 → 本机"对齐：想法以前只有上传、没有镜像，
            // 于是卸载重装之后服务端明明有数据、界面却是空的（用户 2026-10-21 报的问题）。
            builder.Services.AddSingleton<IdeaHistoryService>();

            // 时间块 / 目标的"服务端 → 本地"对齐（与 LocalCacheService 同一思路，独立因为表不同）。
            builder.Services.AddSingleton<BlockMirrorService>();

            // 事情片的本地偏好：钉子 / 改名 / 隐藏 / 上次时长（设备偏好，不同步）。
            builder.Services.AddSingleton<PieceSettings>();

            // AI 助手（§27）：只有用户点按钮时才会走到这里；key 在服务端 ai.json，客户端拿不到。
            builder.Services.AddSingleton<AiAssistService>();

            // ===== 统计：状态页的数据来源（档位 → 服务端算好的结构结果）=====
            // 不做本地聚合：本地只有 30 天副本，拿它算"本月/本年"必然缺数据。
            builder.Services.AddSingleton<StatsService>();

            // 注册页面：让 Shell 通过 DI 创建页面，从而把依赖注入构造函数。
            // 不注册的话，Shell 会退回 Activator.CreateInstance 找无参构造 → MissingMethodException 启动崩溃。
            builder.Services.AddTransient<OverviewHubPage>(); // Tab 1：概览目录
            builder.Services.AddTransient<RecordHubPage>();   // Tab 2：记录目录
            builder.Services.AddTransient<MainPage>();        // 目录 → 支出概览
            builder.Services.AddTransient<TimeOverviewPage>(); // 目录 → 时间概览
            builder.Services.AddTransient<WeekReviewPage>();   // 目录 → 这周回顾（周对比，0 输入）
            builder.Services.AddTransient<RecordEntryPage>();   // 目录 → 账单
            builder.Services.AddTransient<RecordDothingPage>(); // 目录 → 事件反思
            builder.Services.AddTransient<BlockEntryPage>();    // 目录 → 时间块（R2 主入口）
            builder.Services.AddTransient<GoalListPage>();      // 时间块页顶部 → 目标
            builder.Services.AddTransient<GoalDetailPage>();    // 目标列表 → 详情（带 ?id=）
            builder.Services.AddTransient<QuarterReviewPage>(); // 目标列表 → 回顾（三个数 + 该砍的）
            builder.Services.AddTransient<PatternListPage>();   // 目录 → 行为模式
            builder.Services.AddTransient<PatternDetailPage>(); // 模式列表 → 详情（带 ?id=）
            builder.Services.AddTransient<IdeaEntryPage>();     // 目录 → 想法（记一条）
            builder.Services.AddTransient<IdeaListPage>();      // 目录 → 想法库（列表 + 转成目标）

            var app = builder.Build();

            // 启动即后台预热一次地址探测，让首次「Ping / 同步」更快。
            var connection = app.Services.GetRequiredService<ServerConnection>();

            _ = Task.Run(async () =>
            {
                try { await connection.EnsureAsync().ConfigureAwait(false); }
                catch { /* 探测失败不影响启动，页面/同步触发时会重试 */ }
            });

            // 网络恢复时自动同步（每类记录各挂一次；Start() 内部幂等，只会订阅一次）。
            app.Services.GetRequiredService<RecordSyncService<LocalRecord>>().Start();
            app.Services.GetRequiredService<RecordSyncService<ThingRecord>>().Start();
            app.Services.GetRequiredService<RecordSyncService<LocalBlock>>().Start();
            app.Services.GetRequiredService<RecordSyncService<LocalGoal>>().Start();
            app.Services.GetRequiredService<RecordSyncService<LocalPlan>>().Start();
            app.Services.GetRequiredService<RecordSyncService<LocalPattern>>().Start();
            app.Services.GetRequiredService<RecordSyncService<LocalPractice>>().Start();
            app.Services.GetRequiredService<RecordSyncService<LocalIdea>>().Start();

            return app;
        }
    }
}
