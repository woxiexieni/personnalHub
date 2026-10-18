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

            // ===== 启动前自检：端口被占用就直接说人话，而不是甩一屏堆栈 =====
            // 场景：服务端已经在跑（或上一个实例没关干净），又开了一个 → 原来会崩在
            // "Failed to bind to address http://[::]:5000: address already in use"。
            if (IsPortInUse(port))
            {
                PrintBanner(port);
                Console.WriteLine();
                Console.WriteLine($"[X] 启动失败：端口 {port} 已经被占用了。");
                Console.WriteLine();
                Console.WriteLine("    最常见的原因是：服务端已经在运行中（比如你之前开过一次，窗口没关）。");
                Console.WriteLine("    请按下面任一方式处理：");
                Console.WriteLine();
                Console.WriteLine("      1) 找到那个还在运行的服务端窗口，它就是你的服务端，直接用就行，不用再开一个；");
                Console.WriteLine($"      2) 确实要重启：先关掉所有服务端窗口，或执行  taskkill /F /IM personalHub.Server.exe");
                Console.WriteLine($"      3) 想换个端口跑：set PERSONALHUB_PORT=5001 后再启动（客户端也要跟着改）。");
                Console.WriteLine();
                // 只有在真的有控制台时才等待按键（被脚本/管道调用时 ReadKey 会抛异常）
                if (!Console.IsInputRedirected)
                {
                    Console.WriteLine("按任意键退出...");
                    try { Console.ReadKey(true); } catch { }
                }
                return;
            }

            var builder = WebApplication.CreateBuilder(args);

            // ===== 诊断用：把请求日志打开 =====
            // 默认情况下 ASP.NET Core 把 Microsoft.AspNetCore.Hosting.Diagnostics 压在 Warning，
            // 于是控制台只有「Now listening on」那几行，**来请求了也什么都不打印** ——
            // 排查「手机到底有没有连进来」时完全无从判断（看着像没收到，其实可能收到了）。
            // 这里显式开到 Information，之后每个请求都会打印一行：
            //   Request starting HTTP/2 POST /personalhub.System/System/Ping
            builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Information);

            // 1. 注册 gRPC 服务
            builder.Services.AddGrpc();

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

            // 监听所有网卡（0.0.0.0，含局域网 IP），强制 HTTP/2（gRPC 必需）。
            // 原 ListenLocalhost 只绑 127.0.0.1，局域网/手机连不上，且未指定协议会默认 HTTP/1.1。
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.ListenAnyIP(port, o => o.Protocols = HttpProtocols.Http2);
            });


            var app = builder.Build();


            //3. 将我们的服务类映射到路由
            app.MapGrpcService<RecordServiceImpl>();
            app.MapGrpcService<ThingServiceImpl>();
            app.MapGrpcService<SystemServiceImpl>();
            app.MapGrpcService<CategoryServiceImpl>();
            app.MapGrpcService<AnalysisServiceImpl>();

            // 启动前打印可连接的地址，方便手机侧排查。
            // 这里主动解析一次 Db：让"表结构迁移"在启动时就跑完。
            // 否则迁移要等到第一个手机请求进来才执行，出错时你只会在手机上看到一个 gRPC 错误码。
            var db = app.Services.GetRequiredService<Db>();
            Console.WriteLine($"[OK] 数据库: {db.Path}");

            app.Start();
            PrintBanner(port);
            Console.WriteLine("[OK] 服务端已启动，等待手机连接...");
            Console.WriteLine("     (手机要和这台电脑连同一个 Wi-Fi；客户端会自动扫描本网段找到它)");
            Console.WriteLine();
            Console.WriteLine("     关闭这个窗口 = 停止服务端。");
            Console.WriteLine();
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

        /// <summary>打印本机的可用连接地址（本机 + 各网卡局域网 IP）。</summary>
        private static void PrintBanner(int port)
        {
            Console.WriteLine();
            Console.WriteLine("============================================================");
            Console.WriteLine($" PersonalHub Server  v{AppVersion()}  (端口 {port}, gRPC / HTTP2)");
            Console.WriteLine("------------------------------------------------------------");
            Console.WriteLine($"   本机:        http://127.0.0.1:{port}");
            foreach (string ip in GetLanAddresses())
            {
                Console.WriteLine($"   局域网:      http://{ip}:{port}");
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
