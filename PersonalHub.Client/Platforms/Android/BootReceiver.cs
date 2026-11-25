using Android.App;
using Android.Content;

namespace PersonalHub.Client
{
    /// <summary>
    /// 开机 / 应用更新后把常驻服务拉回来（R15）。
    ///
    /// 「像微信那样不管它也在」靠的是三条互补的命，缺一条就会永久静默：
    ///   1. 前台服务 + START_STICKY —— 系统为了内存杀掉后会自己重启；
    ///   2. 本接收器（开机广播）—— 重启手机后没人拉起服务，只有这里能补；
    ///   3. MainActivity.OnResume —— 被 force-stop 之后系统不再重启粘性服务，
    ///      只能等用户重开 app 的那一下（此刻有可见 Activity，前台启动不受限）。
    ///
    /// 不做"闹钟/JobScheduler 定时唤醒"那种土办法：它们在国产 ROM 上一样会被杀，
    /// 却会真的耗电，而且失败时完全无声。
    /// </summary>
    [BroadcastReceiver(Enabled = true, Exported = true)]
    [IntentFilter(new[]
    {
        Intent.ActionBootCompleted,
        Intent.ActionMyPackageReplaced,
        "android.intent.action.QUICKBOOT_POWERON",       // 部分国产 ROM 的快速开机
        "com.htc.intent.action.QUICKBOOT_POWERON",
    })]
    public class BootReceiver : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent)
        {
            try
            {
                // 用户主动关掉的总开关要尊重：关了就不拉起，不偷偷复活。
                if (!Preferences.Default.Get(SegmentEnabledKey, true)) return;

                // 没有「使用情况访问权限」时，服务起来也只能记个"亮屏/网络"，
                // 意义不大，反而白留一条通知。等用户下次打开 app 授权后再起。
                if (!SegmentServiceHelper.HasUsageAccess()) return;

                SegmentServiceHelper.EnsureStarted();
            }
            catch
            {
                // 开机广播里抛异常会被系统记成一次崩溃，可能影响之后的拉起，吞掉。
            }
        }

        /// <summary>与 BlockEntryPage 里的总开关同一个键（那份是私有的，这里复制一份常量）。</summary>
        internal const string SegmentEnabledKey = "ph.segment.enabled";
    }
}
