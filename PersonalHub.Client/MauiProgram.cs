using Grpc.Net.Client;
using Microsoft.Extensions.Logging;

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

            // 注册 gRPC 通道
            var channel = GrpcChannel.ForAddress("http://192.168.0.100:50051");
            builder.Services.AddSingleton<GrpcChannel>(channel);

            //// 注册生成的客户端
            //builder.Services.AddSingleton(sp =>
            //    new Personalhub.System.SystemClient(sp.GetRequiredService<GrpcChannel>()));
            return builder.Build();
        }
    }
}
