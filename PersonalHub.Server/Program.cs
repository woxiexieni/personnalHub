using Grpc.AspNetCore; 
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;
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

            //
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.ListenLocalhost(5000);
            });


            var app = builder.Build();


            //3. 将我们的服务类映射到路由
           // app.MapGrpcService<RecordServiceImpl>();

            app.MapGrpcService<SystemServiceImpl>();

            app.Run();

        }
    }
}


