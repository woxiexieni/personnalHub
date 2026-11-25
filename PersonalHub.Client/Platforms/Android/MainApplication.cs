using Android.App;
using Android.Runtime;

namespace PersonalHub.Client
{
    [Application]
    public class MainApplication : MauiApplication
    {
        public MainApplication(IntPtr handle, JniHandleOwnership ownership)
            : base(handle, ownership)
        {
        }

        public override void OnCreate()
        {
            base.OnCreate();

            // 通知渠道必须在进程起来时建好，不能等发通知那一刻：
            // 用户拒过通知权限时，临到 startForeground 再建渠道会静默失败，
            // 服务起不来，表现就是"番茄钟一条通知都没有"（见 NotificationChannels 注释）。
            NotificationChannels.EnsureAll();
        }

        protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
    }
}
