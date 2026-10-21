using Grpc.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using personalHub.Server.Data;
using personalHub.Server.Services;
using System.Net;
using System.Net.Sockets;


namespace PersonHub.Server
{
    class Progrom
    {
        static void Main(string[] args)
        {
            // 控制台切到 UTF-8 输出（start-server.bat 里已执行 chcp 65001，这里再显式指定一次，
            // 保证中文提示不变成乱码）。可能被重定向/无控制台，失败就忽略。
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

            // ===== 版本号：来自仓库根目录 version.txt（Directory.Build.props 烧进程序集）=====
            // 用途：确认手机上连的、你启动的到底是哪一版。--version 只打印版本就退出。
            if (Array.Exists(args, a => a is "--version" or "-v"))
            {
                Console.WriteLine($"{AppVersion()}");
                return;
            }

            // ===== 端口：默认 5000，可用环境变量 PERSONALHUB_PORT 或命令行 --port 覆盖 =====
            int port = 5000;
            string? envPort = Environment.GetEnvironmentVariable("PERSONALHUB_PORT");
            if (!string.IsNullOrWhiteSpace(envPort) && int.TryParse(envPort, out int ep)) port = ep;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--port" && int.TryParse(args[i + 1], out int ap)) port = ap;
            }

            // ===== 管理端（网页）端口：默认 gRPC 端口 +1 =====
            // 为什么必须分成两个端口：gRPC 强制 HTTP/2，浏览器只讲 HTTP/1.1。
            // 同一个端口在不启用 TLS 的情况下无法同时讲两种协议（ALPN 需要证书），
            // 所以「5000 给手机 gRPC、5001 给浏览器」是最省事、也最不容易出错的做法。
            int webPort = port + 1;
            string? envWebPort = Environment.GetEnvironmentVariable("PERSONALHUB_WEB_PORT");
            if (!string.IsNullOrWhiteSpace(envWebPort) && int.TryParse(envWebPort, out int ewp)) webPort = ewp;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--web-port" && int.TryParse(args[i + 1], out int awp)) webPort = awp;
            }

            // ===== 启动前自检：端口被占用就直接说人话，而不是甩一屏堆栈 =====
            // 场景：服务端已经在跑（或上一个实例没关干净），又开了一个 → 原来会崩在
            // "Failed to bind to address http://[::]:5000: address already in use"。
            // 现在有两个端口（gRPC + 网页），任何一个被占都拦下来，并明确指出是哪一个。
            int busyPort = IsPortInUse(port) ? port : (IsPortInUse(webPort) ? webPort : 0);
            if (busyPort != 0)
            {
                PrintBanner(port, webPort);
                Console.WriteLine();
                Console.WriteLine($"[X] 启动失败：端口 {busyPort} 已经被占用了。");
                Console.WriteLine();
                Console.WriteLine("    最常见的原因是：服务端已经在运行中（比如你之前开过一次，窗口没关）。");
                Console.WriteLine("    请按下面任一方式处理：");
                Console.WriteLine();
                Console.WriteLine("      1) 找到那个还在运行的服务端窗口，它就是你的服务端，直接用就行，不用再开一个；");
                Console.WriteLine($"      2) 确实要重启：先关掉所有服务端窗口，或执行  taskkill /F /IM PersonalHub.Server.exe");
                Console.WriteLine($"      3) 想换个端口跑：set PERSONALHUB_PORT=5001 后再启动（客户端也要跟着改）；");
                Console.WriteLine($"         只想换网页端口：set PERSONALHUB_WEB_PORT=8080。");
                Console.WriteLine();
                // 只有在真的有控制台时才等待按键（被脚本/管道调用时 ReadKey 会抛异常）
                if (!Console.IsInputRedirected)
                {
                    Console.WriteLine("按任意键退出...");
                    try { Console.ReadKey(true); } catch { }
                }
                return;
            }

            // content root 显式钉在程序目录，**不用当前工作目录**：
            // 默认值是 Directory.GetCurrentDirectory()，于是「从仓库根目录启动」和「从 bin 启动」
            // 会得到两个不同的 content root，wwwroot 里的 site.css 就时有时无（页面没样式但不报错）。
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = args,
                ContentRootPath = AppContext.BaseDirectory,
            });

            // ===== 诊断用：把请求日志打开 =====
            // 默认情况下 ASP.NET Core 把 Microsoft.AspNetCore.Hosting.Diagnostics 压在 Warning，
            // 于是控制台只有「Now listening on」那几行，**来请求了也什么都不打印** ——
            // 排查「手机到底有没有连进来」时完全无从判断（看着像没收到，其实可能收到了）。
            // 这里显式开到 Information，之后每个请求都会打印一行：
            //   Request starting HTTP/2 POST /personalhub.System/System/Ping
            builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Information);

            // 1. 注册 gRPC 服务
            builder.Services.AddGrpc();

            // 2. 管理端网页（Razor Pages，和 gRPC 同一个宿主、同一个进程）
            //    换 Web SDK 的唯一原因就在这里：Microsoft.NET.Sdk 不带 Razor 编译任务，
            //    .cshtml 不会被编进程序集，页面会 404 而不是报编译错误。
            builder.Services.AddRazorPages();

            // 数据库连接：全进程唯一一个 SQLite 连接，records / things / categories 同库。
            // 库文件默认在仓库根目录 personalhub.db3，可用环境变量 PERSONALHUB_DB 覆盖。
            // 结构迁移（补列 / 建索引 / 灌分类）在 Db 构造里完成，幂等，可重复执行。
            builder.Services.AddSingleton<Db>();

            // 分类（做录入页 chips + 写入校验 + 统计名称来源）
            builder.Services.AddSingleton<CategoryStore>();

            // 账单存储（SQLite 落盘实现，单例）。
            builder.Services.AddSingleton<RecordStore>();

            // 事件反思存储（同一库文件、独立 things 表）。
            builder.Services.AddSingleton<ThingStore>();

            // 时间块存储（R2 新增，独立 blocks 表；things 一行不动）。
            builder.Services.AddSingleton<BlockStore>();

            // 目标存储（依赖 BlockStore 现算「累计投入 / 最后活动」）。
            builder.Services.AddSingleton<GoalStore>();

            // 每日计划（R4）：一天最多 3 件，达成率由 BlockStore 现算。
            builder.Services.AddSingleton<PlanStore>();

            // 行为模式 / 练习（R5）：模式一季度两三个，练习一天 0~1 次。
            builder.Services.AddSingleton<PatternStore>();

            // 想法（R6）：原材料仓库，只进不出，没有任何"处理状态"。
            builder.Services.AddSingleton<IdeaStore>();

            // ===== AI（§27）：配置 + DeepSeek 客户端 =====
            // AiConfig 是单例且会热加载：用户打开 ai.json 填上 key 保存，下一次点击就生效，
            // 不用重启服务端（重启服务端在这个项目里意味着"手机重连"）。
            builder.Services.AddSingleton<personalHub.Server.Ai.AiConfig>();
            builder.Services.AddSingleton<personalHub.Server.Ai.DeepSeekClient>();


            // 本地每日快照（VACUUM INTO）。服务端是唯一数据源，没有它一次误删就不可逆。
            builder.Services.AddSingleton<BackupService>();

            // 手机端安装包投递目录（updates/）。发布脚本把新包放进去，
            // 手机浏览器打开 /app 就能下载安装 —— 换版本不再需要插 USB。
            builder.Services.AddSingleton<AppReleaseService>();

            // 服务端常常连开好几天，所以备份不能只在启动时做一次。
            builder.Services.AddHostedService<BackupWorker>();

            // 监听所有网卡（0.0.0.0，含局域网 IP）。
            //   port      → HTTP/2，给手机端的 gRPC（强制 HTTP/2）
            //   webPort   → HTTP/1.1，给浏览器开管理端
            // 原 ListenLocalhost 只绑 127.0.0.1，局域网/手机连不上，且未指定协议会默认 HTTP/1.1。
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.ListenAnyIP(port, o => o.Protocols = HttpProtocols.Http2);
                options.ListenAnyIP(webPort, o => o.Protocols = HttpProtocols.Http1);
            });


            var app = builder.Build();


            // 管理端的 site.css。显式加而不是依赖 WebApplication 的"自动推断"：
            // 自动推断只在 wwwroot 存在于启动时的 content root 下才生效，换目录启动就静默失效，
            // 症状是"页面能开但完全没样式"，不报任何错。
            app.UseStaticFiles();

            //3. 将我们的服务类映射到路由
            app.MapGrpcService<RecordServiceImpl>();
            app.MapGrpcService<ThingServiceImpl>();
            app.MapGrpcService<SystemServiceImpl>();
            app.MapGrpcService<CategoryServiceImpl>();
            app.MapGrpcService<AnalysisServiceImpl>();
            app.MapGrpcService<BlockServiceImpl>();
            app.MapGrpcService<GoalServiceImpl>();
            app.MapGrpcService<PlanServiceImpl>();
            app.MapGrpcService<PatternServiceImpl>();
            app.MapGrpcService<IdeaServiceImpl>();
            app.MapGrpcService<AiServiceImpl>();

            // 管理端页面（/、/records、/records/edit、/categories）
            app.MapRazorPages();

            // 备份文件下载。只允许 /backup 目录里、文件名严格符合 personalhub-YYYY-MM-DD.db3 的文件，
            // 其余一律 404 —— 这个端口在局域网里谁都能访问，不能让它变成任意文件读取。
            app.MapGet("/backup/file/{name}", (string name, BackupService backup) =>
            {
                if (!BackupService.TryParseDate(name, out var date) || name != BackupService.FileName(date))
                    return Results.NotFound();

                string path = Path.Combine(backup.Directory, name);
                return File.Exists(path)
                    ? Results.File(path, "application/octet-stream", name)
                    : Results.NotFound();
            });

            // ============================================================
            // 手机端安装包的下载入口（这台电脑的局域网地址 + webPort）
            // ============================================================
            // 为什么要这几条路由：以前换一个版本要「开电脑 → 插 USB → adb install」，
            // 而手机本来就连着这台电脑。把包放在服务端，手机浏览器打开 /app 就能装。
            //
            // 安全性：这个端口在局域网里谁都能访问，所以跟上面的 /backup/file 一样
            // 只认投递目录里、文件名严格合规的文件（三重校验见 AppReleaseService.Resolve），
            // 其余一律 404。
            app.MapGet("/app", (AppReleaseService releases) =>
                Results.Content(releases.RenderPage(), "text/html; charset=utf-8"));

            // 机器可读的版本信息。第二步的客户端自更新就靠它判断"要不要下载"。
            // 没有包时返回 404 + 一个说明，而不是 200 + 空对象 —— 让调用方能分清
            // "服务端没有新版本"和"这个服务端根本没在管安装包"。
            app.MapGet("/app/latest.json", (AppReleaseService releases) =>
            {
                var r = releases.Latest();
                if (r is null)
                    return Results.NotFound(new { ok = false, message = "no package delivered yet" });

                return Results.Json(new
                {
                    ok = true,
                    version = r.Version,
                    code = r.Code,
                    file = r.FileName,
                    size = r.SizeBytes,
                    sha256 = r.Sha256,
                    builtAt = r.BuiltAt,
                    sizeMismatch = r.SizeMismatch,
                });
            });

            app.MapGet("/app/download/{name}", (string name, AppReleaseService releases) =>
            {
                string? path = releases.Resolve(name);
                return path is null
                    ? Results.NotFound()
                    : Results.File(path, "application/vnd.android.package-archive", name);
            });

            // 启动前打印可连接的地址，方便手机侧排查。
            // 这里主动解析一次 Db：让"表结构迁移"在启动时就跑完。
            // 否则迁移要等到第一个手机请求进来才执行，出错时你只会在手机上看到一个 gRPC 错误码。
            var db = app.Services.GetRequiredService<Db>();
            Console.WriteLine($"[OK] 数据库: {db.Path}");

            var backup = app.Services.GetRequiredService<BackupService>();
            Console.WriteLine($"[OK] 备份目录: {backup.Directory}（每天 1 份，保留 14 天 + 12 周 + 12 个月）");

            // 安装包投递目录：打印"有没有包 / 是哪一版"，避免"以为投递成功了其实没放进去"。
            // 这里只读目录、不发网络请求，开销可以忽略。
            var releases = app.Services.GetRequiredService<AppReleaseService>();
            var latestRelease = releases.Latest();
            if (latestRelease is null)
            {
                Console.WriteLine($"[--] 安装包: 还没有（目录 {releases.Directory}）");
                Console.WriteLine("     要投递：先跑 scripts\\client-android-release.bat，打包成功后会自动放进来。");
            }
            else
            {
                Console.WriteLine($"[OK] 安装包: v{latestRelease.Version} · {latestRelease.SizeText} · {latestRelease.BuiltAtText}");
            }

            // AI：这里只打印"配了没有"，**绝不打印 key**（控制台会被截图、会被复制给别人）。
            var ai = app.Services.GetRequiredService<personalHub.Server.Ai.AiConfig>();
            var aiCfg = ai.Current;
            if (aiCfg.Configured)
            {
                Console.WriteLine($"[OK] AI: 已配置 · 模型 {aiCfg.Model} · {aiCfg.Endpoint}");
                Console.WriteLine("     只在手机上点「帮我想 / 找找反复出现的 / 让 AI 补一句」时才会调用。");
            }
            else
            {
                Console.WriteLine("[--] AI: 还没配 key（不影响使用，客户端会自动走本地规则版）");
                Console.WriteLine($"     要开：用记事本打开 {ai.Path}");
                Console.WriteLine("          把 \"apiKey\" 填成 sk- 开头的 key，保存即可生效，不用重启。");
            }

            app.Start();
            PrintBanner(port, webPort);
            Console.WriteLine("[OK] 服务端已启动，等待手机连接...");
            Console.WriteLine("     (手机要和这台电脑连同一个 Wi-Fi；客户端会自动扫描本网段找到它)");
            Console.WriteLine();
            Console.WriteLine("     关闭这个窗口 = 停止服务端。");            Console.WriteLine();
            Console.WriteLine("------------------------------------------------------------");
            app.WaitForShutdown();
        }

        /// <summary>
        /// 程序集版本号，形如 "1.0.0"。
        /// InformationalVersion 会带上 git 提交短哈希（如 1.0.0+6597edd...），
        /// 这里只取 + 前面那段，界面上好看一些。
        /// </summary>
        private static string AppVersion()
        {
            var asm = typeof(Progrom).Assembly;
            var info = asm.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                          .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                          .FirstOrDefault()?.InformationalVersion;
            if (string.IsNullOrWhiteSpace(info))
                info = asm.GetName().Version?.ToString(3) ?? "0.0.0";
            int plus = info.IndexOf('+');
            return plus > 0 ? info.Substring(0, plus) : info;
        }

        /// <summary>打印本机的可用连接地址（gRPC 端口 + 管理端网页端口）。</summary>
        private static void PrintBanner(int port, int webPort)
        {
            Console.WriteLine();
            Console.WriteLine("============================================================");
            Console.WriteLine($" PersonalHub Server  v{AppVersion()}");
            Console.WriteLine("------------------------------------------------------------");
            Console.WriteLine($"   [手机 gRPC]  http://127.0.0.1:{port}   (HTTP/2)");
            foreach (string ip in GetLanAddresses())
            {
                Console.WriteLine($"                 http://{ip}:{port}");
            }
            Console.WriteLine();
            Console.WriteLine($"   [电脑管理端] http://127.0.0.1:{webPort}/   (用浏览器打开)");
            foreach (string ip in GetLanAddresses())
            {
                Console.WriteLine($"                 http://{ip}:{webPort}/");
            }
            Console.WriteLine();
            Console.WriteLine($"   [手机装新版] http://127.0.0.1:{webPort}/app  (手机浏览器打开，下载安装包)");
            foreach (string ip in GetLanAddresses())
            {
                Console.WriteLine($"                 http://{ip}:{webPort}/app");
            }
            Console.WriteLine("============================================================");
        }

        /// <summary>枚举本机非回环的 IPv4 地址（局域网 IP）。</summary>
        private static List<string> GetLanAddresses()
        {
            var list = new List<string>();
            try
            {
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;

                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                        string ip = ua.Address.ToString();
                        if (ip == "127.0.0.1" || ip.StartsWith("169.254.")) continue;
                        if (!list.Contains(ip)) list.Add(ip);
                    }
                }
            }
            catch
            {
                // 取不到就算了，不影响启动
            }
            return list;
        }

        /// <summary>
        /// 端口占用检测：尝试独占绑定一次，绑定失败即视为被占用。
        /// IPv4 / IPv6 任意一种绑不上都算占用（Kestrel 的 ListenAnyIP 会同时占这两者）。
        /// </summary>
        private static bool IsPortInUse(int port)
        {
            // IPv6Any 双栈：与 Kestrel 的 ListenAnyIP 行为一致
            try
            {
                var v6 = new TcpListener(IPAddress.IPv6Any, port);
                v6.Server.DualMode = true;
                v6.Start();
                v6.Stop();
            }
            catch (SocketException)
            {
                return true;
            }
            catch
            {
                // 环境不支持 IPv6 时忽略，继续用 IPv4 判断
            }

            try
            {
                var v4 = new TcpListener(IPAddress.Any, port);
                v4.Start();
                v4.Stop();
            }
            catch (SocketException)
            {
                return true;
            }

            return false;
        }
    }
}
