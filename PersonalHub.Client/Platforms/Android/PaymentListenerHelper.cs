using Android.Content;
using Android.Provider;

namespace PersonalHub.Client
{
    /// <summary>
    /// 通知使用权的检查与跳转（R12）。
    ///
    /// 这项权限没有运行时弹窗，只能把用户领到系统设置页里手动打开；
    /// 装完 app 后也必须进过一次 app 服务才会被系统重新绑定。
    /// </summary>
    public static class PaymentListenerHelper
    {
        /// <summary>本 app 是否已被授予「通知使用权」。</summary>
        public static bool IsListening()
        {
            try
            {
                var context = global::Android.App.Application.Context;
                string? enabled = Settings.Secure.GetString(
                    context.ContentResolver, "enabled_notification_listeners");

                if (string.IsNullOrEmpty(enabled)) return false;
                return enabled.Contains(context.PackageName ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>跳到系统的「通知使用权」设置页。</summary>
        public static void OpenListenerSettings()
        {
            // 用常量而不是 Settings.ActionNotificationListenerSettings：
            // 后者被标注为 API 22+，会触发 CA1416 警告；常量字符串 API 19 起就存在，全版本通用。
            var intent = new Intent("android.settings.ACTION_NOTIFICATION_LISTENER_SETTINGS");
            intent.AddFlags(ActivityFlags.NewTask);
            global::Android.App.Application.Context.StartActivity(intent);
        }
    }
}
