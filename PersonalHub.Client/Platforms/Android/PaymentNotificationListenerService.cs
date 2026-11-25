using Android.App;
using Android.OS;
using Android.Service.Notification;
using PersonalHub.Client.Services;

namespace PersonalHub.Client
{
    /// <summary>
    /// 通知监听服务（自动记账的数据入口，R12）。
    ///
    /// 工作方式：用户在系统设置里给本 app 授予「通知使用权」后，
    /// 系统会把每一条通知回调进来。这里只做最薄的一层 —— 抠出包名 +
    /// 标题 + 正文交给 <see cref="PaymentCapture"/>，过滤与解析全在托管代码里。
    ///
    /// 清单声明靠下面的 [Service] / [IntentFilter] 特性由 .NET Android
    /// 自动合并生成（不需要手改 AndroidManifest.xml —— 手写会跟特性
    /// 生成的节点撞车）。核心要求：
    ///   android:permission = BIND_NOTIFICATION_LISTENER_SERVICE（系统绑定专用权限）
    ///   intent-filter      = NotificationListenerService（系统靠它发现服务）
    ///
    /// R15：正文不再只取 EXTRA_TEXT。有些通知（尤其支付类的自定义样式）
    /// 正文在 EXTRA_BIG_TEXT 或 EXTRA_TEXT_LINES 里，EXTRA_TEXT 是空的 ——
    /// 以前直接 return，等于把已经拿到的通知白丢了。
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
            try
            {
                var notification = sbn?.Notification;
                var extras = notification?.Extras;

                if (sbn is null || extras is null) return;

                string title = Text(extras, Android.App.Notification.ExtraTitle);
                string text = CollectText(extras, notification?.TickerText?.ToString());

                // 标题和正文都没有的（只带自定义 RemoteViews）也照会一声：
                // PaymentCapture 会记成一条"没认出来"的探针 —— 能看到"通知来了但没内容"，
                // 比"nothing happened"好判断得多。
                PaymentCapture.HandleNotification(sbn.PackageName ?? string.Empty, title, text);
            }
            catch
            {
                // 监听服务里抛异常可能被系统解绑，一律吞掉
            }
        }

        private static string Text(Bundle? extras, string key)
        {
            try { return extras?.GetCharSequence(key)?.ToString() ?? string.Empty; }
            catch { return string.Empty; }
        }

        /// <summary>
        /// 把正文的各个可能字段拼成一段（去重、保序）：
        /// TEXT → BIG_TEXT → SUB_TEXT → SUMMARY_TEXT → TEXT_LINES → TickerText。
        /// 解析器只需要"能搜到金额和支付词"的一段文本，不需要知道它在哪个字段里。
        /// </summary>
        private static string CollectText(Bundle extras, string? ticker)
        {
            var parts = new List<string>();

            void Add(string? value)
            {
                if (string.IsNullOrWhiteSpace(value)) return;
                string v = value!.Trim();
                if (!parts.Contains(v)) parts.Add(v);
            }

            Add(Text(extras, Android.App.Notification.ExtraText));
            Add(Text(extras, Android.App.Notification.ExtraBigText));
            Add(Text(extras, Android.App.Notification.ExtraSubText));
            Add(Text(extras, Android.App.Notification.ExtraSummaryText));
            Add(ticker);

            try
            {
                var lines = extras.GetCharSequenceArray(Android.App.Notification.ExtraTextLines);
                if (lines is not null)
                {
                    foreach (var line in lines) Add(line?.ToString());
                }
            }
            catch
            {
                // 字段不存在 / 类型不对：跳过
            }

            return string.Join(" | ", parts);
        }
    }
}
