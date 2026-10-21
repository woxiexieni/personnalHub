using PersonalHub.Client.Services;

namespace PersonalHub.Client
{
    public partial class App : Application
    {
        private readonly StartupRestoreService _startupRestore;

        public App(StartupRestoreService startupRestore)
        {
            InitializeComponent();

            _startupRestore = startupRestore;

            MainPage = new AppShell();
        }

        protected override void OnStart()
        {
            base.OnStart();

            // 本机一条数据都没有（新装 / 重装 / 换机）时，自己从服务端补一次。
            // 不阻塞启动、失败静默、整台设备只跑一次 —— 详见 StartupRestoreService。
            _ = _startupRestore.TryAsync();
        }
    }
}
