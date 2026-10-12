# **PersonalHub 技术路线与设计文档 (v0.2)**

## **1. 架构概述**

系统采用 **C/S (Client/Server) 架构**。电脑端作为常驻的局域网数据中枢，手机端/平板端作为轻量级跨平台客户端。全栈采用 **.NET 生态**，实现语言统一、Proto 协议共用、开发工具链统一。

## **2. 技术栈选型**

表格

| 层级           | 技术选型         | 版本/说明                                              |
| :------------- | :--------------- | :----------------------------------------------------- |
| **服务端语言** | C#               | .NET 8 (LTS)                                           |
| **服务端 UI**  | WinForms         | .NET 8 内置，用于服务端状态展示和分类管理              |
| **客户端框架** | **.NET MAUI**    | 跨平台框架，一套代码编译 Android (手机/鸿蒙平板)       |
| **客户端语言** | C#               | 与服务端完全一致                                       |
| **客户端架构** | MVVM             | 使用 `CommunityToolkit.Mvvm`                           |
| **通信框架**   | gRPC             | `Grpc.AspNetCore` (服务端), `Grpc.Net.Client` (客户端) |
| **协议定义**   | Protocol Buffers | **proto2** 语法                                        |
| **服务端存储** | SQLite + Dapper  | 本地文件存储，轻量级 ORM                               |
| **客户端存储** | SQLite-net-pcl   | 本地轻量级数据库，缓存今日账单和配置                   |

## **3. 项目工程结构**

整个解决方案采用多项目结构，确保协议层和核心逻辑的复用：

text

编辑







```
PersonalHub.sln
├── PersonalHub.Contracts/          # [类库] Proto 定义及生成的 C# 代码 (双端共用)
├── PersonalHub.Core/               # [类库] 共享的数据模型、枚举、工具类
├── PersonalHub.Server/             # [WinForms + gRPC] 电脑端服务端
├── PersonalHub.Client/             # [.NET MAUI] 手机/平板客户端
└── PersonalHub.Tests/              # [xUnit] 单元测试
```

## **4. 服务端设计 (PersonalHub.Server)**

### **4.1 WinForms 与 gRPC 共存**

在 .NET 8 中，gRPC 依赖 ASP.NET Core 的 Kestrel 服务器。为了与 WinForms 桌面 UI 共存，采用以下启动策略：

- 使用 `WebApplication` 在后台线程启动 gRPC 服务，监听局域网 IP。
- 使用 `Application.Run()` 在主线程启动 WinForms UI。
- 两者共享同一个依赖注入容器 (`IServiceCollection`)。

### **4.2 模块化设计**

为了后续扩展（文件同步、遥控等），服务端采用模块注册制。

PersonalHub服务端核心架构与模块接口

编辑







```
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace PersonalHub.Server.Modules
{
    /// <summary>
    /// 模块扩展接口。未来新增的功能（如文件同步）只需实现此接口。
    /// </summary>
    public interface IAppModule
    {
        string ModuleId { get; }
        string ModuleName { get; }
        
        // 注册模块特有的依赖注入服务
        void RegisterServices(IServiceCollection services);
        
        // 注册模块特有的 gRPC 服务或 HTTP 路由
        void RegisterEndpoints(IEndpointRouteBuilder endpoints);
    }

    /// <summary>
    /// 模块管理器，负责在启动时扫描并注册所有模块
    /// </summary>
    public static class ModuleManager
    {
        public static void RegisterModules(WebApplicationBuilder builder, IEnumerable<IAppModule> modules)
        {
            foreach (var module in modules)
            {
                Console.WriteLine($"[Init] Loading module: {module.ModuleName} ({module.ModuleId})");
                module.RegisterServices(builder.Services);
            }
        }

        public static void MapModules(WebApplication app, IEnumerable<IAppModule> modules)
        {
            foreach (var module in modules)
            {
                module.RegisterEndpoints(app);
            }
        }
    }
}

// ==========================================
// Program.cs 启动骨架 (WinForms + gRPC 共存)
// ==========================================
namespace PersonalHub.Server
{
    public class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            // 1. 初始化 WinForms
            ApplicationConfiguration.Initialize();
            var mainForm = new MainForm();

            // 2. 构建 gRPC WebApplication (后台运行)
            var builder = WebApplication.CreateBuilder(args);
            builder.Services.AddGrpc();
            
            // 注册模块 (例如记账模块)
            var modules = new List<Modules.IAppModule> { new AccountingModule() };
            Modules.ModuleManager.RegisterModules(builder, modules);

            var app = builder.Build();
            Modules.ModuleManager.MapModules(app, modules);

            // 3. 启动 gRPC 服务 (非阻塞)
            _ = app.RunAsync();

            // 4. 启动 WinForms UI (阻塞主线程)
            Application.Run(mainForm);
        }
    }
}
```

## **5. 客户端设计 (PersonalHub.Client)**

### **5.1 .NET MAUI 跨平台策略**

- **目标平台**：主要构建 Android 目标（`net8.0-android`）。
- **鸿蒙兼容**：目前的鸿蒙系统（HarmonyOS 4.0 及之前）完全兼容 Android APK，MAUI 打包的 Android 应用可直接安装运行。
- **UI 布局**：使用 XAML 编写界面，利用 MAUI 的自适应布局（Grid, FlexLayout）适配手机和平板的不同屏幕尺寸。

### **5.2 客户端分层 (MVVM)**

- **Views (XAML)**：记账页、今日账单页、趋势图表页、设置页。

- **ViewModels**：使用 `[ObservableProperty]` 和 `[RelayCommand]` 简化数据绑定和命令绑定。

- Services

  ：

  - `GrpcClientService`：封装 gRPC Channel 和 Stub。
  - `SyncService`：处理自动同步逻辑（Ping -> 拉分类 -> 传账单 -> 拉今日数据）。
  - `LocalDbService`：使用 `sqlite-net-pcl` 管理本地缓存。

## **6. 通信与协议细节**

### **6.1 Proto2 编译与共用**

- `.proto` 文件统一放在 `PersonalHub.Contracts` 项目中。
- 服务端和客户端均通过 NuGet 引入 `Grpc.Tools`，在 `.csproj` 中配置 `<Protobuf>` 节点，编译时自动生成 C# 代码。
- **优势**：修改协议后，双端重新编译即可，杜绝字段不一致问题。

### **6.2 gRPC 网络配置**

- **服务端**：监听 `http://0.0.0.0:50051`（允许局域网访问）。
- **客户端**：使用 `GrpcChannel.ForAddress("http://<电脑局域网IP>:50051")`。由于是局域网环境，直接使用明文传输（`AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true)`），无需配置 TLS 证书。

## **7. 数据存储细节**

### **7.1 服务端 (SQLite + Dapper)**

- 数据库文件 `PersonalHub.db` 存放在服务端运行目录下。
- 使用 Dapper 执行原生 SQL，性能极高，且完美支持 SQLite 的日期函数。

### **7.2 客户端 (SQLite-net-pcl)**

- 客户端数据库 `local_cache.db` 存放在 App 的本地沙盒目录。
- 仅存储：服务端 IP 配置、今日账单缓存、分类模板缓存。
- 每次同步成功后，清空旧缓存，写入最新数据。

## **8. 部署与运行**

1. 服务端部署

   ：

   - 在电脑上编译 Release 版本：`dotnet publish -c Release -o ./publish`。
   - 运行 `PersonalHub.Server.exe`，WinForms 窗口弹出，显示当前局域网 IP 和端口。

2. 客户端部署

   ：

   - 在 Visual Studio 中连接 Android 手机或模拟器。
   - 编译并部署 `PersonalHub.Client`。
   - 在 App 设置页输入电脑 IP，点击“同步”即可开始使用。

> ## gRPC的技术路线
>
> grpc的proto文件和我之前学的那个还不一样，需要下面这些：
>
> Nuget 安装包：
>
> - `Grpc.AspNetCore`（服务端，[ASP.NET](https://ASP.NET) Core）
>
> - `Grpc.Net.Client`（客户端）
>
> - `Grpc.Tools`（编译工具，核心！包装`protoc`+grpc csharp 插件，编译时自动跑）
>
>   另外它还是编译时生