using Grpc.AspNetCore; 
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;
using personalHub.Server.Data;
using personalHub.Server.Services;


namespace PersonHub.Server
{
    class Progrom
    {
        static void Main(string[] args)
        {

            var builder = WebApplication.CreateBuilder(args);

            // 1. 注册 gRPC 服务
            builder.Services.AddGrpc();

            // 账单存储（SQLite 落盘实现，单例）。
            // 库文件默认 %LOCALAPPDATA%\PersonalHub\personalhub.db3，可用环境变量 PERSONALHUB_DB 覆盖。
            // 进程重启、关掉服务端都不丢数据。
            builder.Services.AddSingleton<RecordStore>();

            // 监听所有网卡（0.0.0.0，含局域网 IP），端口 5000，并强制 HTTP/2（gRPC 必需）。
            // 原 ListenLocalhost 只绑 127.0.0.1，局域网/手机连不上，且未指定协议会默认 HTTP/1.1。
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.ListenAnyIP(5000, o => o.Protocols = HttpProtocols.Http2);
            });


            var app = builder.Build();


            //3. 将我们的服务类映射到路由
            app.MapGrpcService<RecordServiceImpl>();
            app.MapGrpcService<SystemServiceImpl>();

            app.Run();

        }
    }
}


