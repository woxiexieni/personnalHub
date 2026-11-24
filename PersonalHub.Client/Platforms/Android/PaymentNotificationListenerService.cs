using Android.App;
using Android.Service.Notification;
using PersonalHub.Client.Services;

namespace PersonalHub.Client
{
    /// <summary>
    /// 通知监听服务（自动记账的数据入口，R12）。
    ///
    /// 工作方式：用户在系统设置里给本 app 授予「通知使用权」后，
    /// 系统会把每一条通知回调进来。这里只做最薄的一层 —— 抠出包名 +
    /// 标题 + 文本交给 <see cref="PaymentCapture"/>，过滤与解析全在托管代码里。
    ///
    /// 清单声明靠下面的 [Service] / [IntentFilter] 特性由 .NET Android
    /// 自动合并生成（不需要手改 AndroidManifest.xml —— 手写会跟特性
    /// 生成的节点撞车）。核心要求：
    ///   android:permission = BIND_NOTIFICATION_LISTENER_SERVICE（系统绑定专用权限）
    ///   intent-filter      = NotificationListenerService（系统靠它发现服务）
    /// </summary>
    [Service(
        Name = "com.personalhub.client.PaymentListener",
        Permission = "android.permission.BIND_NOTIFICATION_LISTENER_SERVICE",
        Exported = true,
        Label = "PersonalHub 自动记账")]
    [IntentFilter(new[] { "android.service.notification.NotificationListenerService" })]
    public class PaymentNotificationListenerService : NotificationListenerService
    {
        public override void OnNotificationPosted(StatusBarNotification? sbn)
        {
            var extras = sbn?.Notification?.Extras;
            if (sbn is null || extras is null) return;

            string title = extras.GetCharSequence(Android.App.Notification.ExtraTitle)?.ToString() ?? string.Empty;
            string text = extras.GetCharSequence(Android.App.Notification.ExtraText)?.ToString() ?? string.Empty;

            if (string.IsNullOrEmpty(text)) return;

            PaymentCapture.HandleNotification(sbn.PackageName ?? string.Empty, title, text);
        }
    }
}
