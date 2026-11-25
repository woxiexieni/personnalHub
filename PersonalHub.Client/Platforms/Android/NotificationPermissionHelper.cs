namespace PersonalHub.Client
{
    /// <summary>
    /// 通知权限（POST_NOTIFICATIONS）的申请与检查（R15）。
    ///
    /// 之前 v0 的取舍是"不弹这个授权框，省一次打扰"，代价是 Android 13+ 上
    /// **整个 app 一条通知都发不出来**（前台服务的常驻通知也一样，只出现在系统的
    /// 「前台服务」列表里，不在通知栏）。番茄钟因此变成"看不见的计时器"。
    /// 现在改成：按需申请 —— 只在你要用番茄钟的那一刻问，不占启动流程。
    /// </summary>
    public static class NotificationPermissionHelper
    {
        /// <summary>当前是否允许发通知。Android 13 以下恒 true。</summary>
        public static async Task<bool> IsGrantedAsync()
        {
            try
            {
                if (!OperatingSystem.IsAndroidVersionAtLeast(33)) return true;
                return await Permissions.CheckStatusAsync<Permissions.PostNotifications>()
                    == PermissionStatus.Granted;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>没授权就弹一次系统框；返回最终是否可用。任何异常都按"不可用"处理。</summary>
        public static async Task<bool> EnsureAsync()
        {
            try
            {
                if (!OperatingSystem.IsAndroidVersionAtLeast(33)) return true;

                var status = await Permissions.CheckStatusAsync<Permissions.PostNotifications>();
                if (status != PermissionStatus.Granted)
                {
                    status = await Permissions.RequestAsync<Permissions.PostNotifications>();
                }

                return status == PermissionStatus.Granted;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>用户点过"不再询问"之后，只能把他领到系统设置页自己开。</summary>
        public static void OpenSettings()
        {
            try { AppInfo.Current.ShowSettingsUI(); } catch { }
        }

        /// <summary>
        /// 渠道是不是被关掉了（重要性 = None）。权限开着但渠道被静音/关闭时，
        /// 前台服务照跑、通知就是不出现在通知栏 —— 这两种情况在界面上没法区分，
        /// 所以统一按"不可见"处理。渠道还没建出来时算正常（不是问题）。
        /// </summary>
        public static bool IsChannelBlocked(string channelId)
        {
            try
            {
                if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return false;

                var ctx = global::Android.App.Application.Context;
                if (ctx.GetSystemService(global::Android.Content.Context.NotificationService)
                        is not global::Android.App.NotificationManager mgr)
                {
                    return false;
                }

                var channel = mgr.GetNotificationChannel(channelId);
                return channel is not null && channel.Importance == global::Android.App.NotificationImportance.None;
            }
            catch
            {
                return false;
            }
        }
    }
}
