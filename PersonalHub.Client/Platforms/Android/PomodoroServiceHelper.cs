using global::Android.Content;

namespace PersonalHub.Client.Platforms.Android
{
    /// <summary>
    /// 番茄钟前台服务的启动口（R14.5）。卡片（MAUI 层）不能直接摸 Android 类型，
    /// 走这个 Helper 转一手 —— 与 R13 的 SegmentServiceHelper 同款分工。
    /// </summary>
    public static class PomodoroServiceHelper
    {
        public static void StartService()
        {
            try
            {
                var ctx = global::Android.App.Application.Context;
                var intent = new Intent(ctx, typeof(PomodoroForegroundService));

                if (OperatingSystem.IsAndroidVersionAtLeast(26))
                {
                    ctx.StartForegroundService(intent);
                }
                else
                {
                    ctx.StartService(intent);
                }
            }
            catch
            {
                // 抢不到前台（ rare）就放弃通知：番茄钟照跑，只是没有通知栏倒计时
            }
        }
    }
}
