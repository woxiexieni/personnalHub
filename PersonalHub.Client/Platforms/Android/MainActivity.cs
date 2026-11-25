using Android.App;
using Android.Content.PM;
using Android.OS;
using PersonalHub.Client.Services;

namespace PersonalHub.Client
{
    [Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
    public class MainActivity : MauiAppCompatActivity
    {
        /// <summary>
        /// 用户重开 app 的一刻，是唯一「一定能拉起前台服务」的时机
        /// （有可见 Activity，不受 Android 12+ 后台启动前台服务的限制）。
        ///
        /// 为什么需要：被 force-stop 之后系统不会再重启粘性服务，手机重启后也只有
        /// 开机广播那一次机会。少了这里，用户一旦清过后台，时间片就永久停止采集，
        /// 而且界面上看不出来（卡片仍显示"已开启"）。
        /// </summary>
        protected override void OnResume()
        {
            base.OnResume();

            try
            {
                if (Preferences.Default.Get("ph.segment.enabled", true)
                    && SegmentServiceHelper.HasUsageAccess())
                {
                    SegmentServiceHelper.EnsureStarted();
                }

                // 番茄钟：如果是从持久化恢复出来的（清后台时还在走），
                // 那一轮前台服务已经不在了，这里把通知重新挂回去。
                if (PomodoroState.Running)
                {
                    Platforms.Android.PomodoroServiceHelper.StartService();
                }
            }
            catch
            {
                // 拉不起来不影响 app 使用，页面里的开关卡片还能手动重试
            }
        }
    }
}
