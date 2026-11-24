using Android.App;
using Android.Content;
using Android.OS;

namespace PersonalHub.Client
{
    /// <summary>
    /// 时间片采集服务的检查与跳转（R13，照 R12 的 PaymentListenerHelper 模子）。
    ///
    /// 授权分两层：
    ///   1. 「使用情况访问权限」—— 没有 runtime 弹窗，只能领用户去系统设置页手动开
    ///      （跟"通知使用权"同一种体验，R12 已经趟过）；
    ///   2. 电池优化白名单 —— 国产 ROM 杀后台是常驻服务最大的死因，
    ///      这里只检查 + 跳系统设置页，不弹 RequestIgnoreBatteryOptimizations
    ///      那种吓人的直接申请弹窗。
    /// </summary>
    public static class SegmentServiceHelper
    {
        /// <summary>本 app 是否已被授予「使用情况访问权限」。</summary>
        public static bool HasUsageAccess()
        {
            try
            {
                var ctx = global::Android.App.Application.Context;
                var ops = ctx.GetSystemService(Context.AppOpsService) as AppOpsManager;
                if (ops is null || string.IsNullOrEmpty(ctx.PackageName)) return false;

#pragma warning disable CS0618 // checkOpNoThrow 在 API 29 被标废弃，但全版本可用且行为一致
                var mode = ops.CheckOpNoThrow(
                    "android:get_usage_stats", Process.MyUid(), ctx.PackageName);
#pragma warning restore CS0618

                return mode == AppOpsManagerMode.Allowed;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>跳到系统的「使用情况访问权限」设置页。</summary>
        public static void OpenUsageAccessSettings()
        {
            var intent = new Intent("android.settings.USAGE_ACCESS_SETTINGS");
            intent.AddFlags(ActivityFlags.NewTask);
            global::Android.App.Application.Context.StartActivity(intent);
        }

        /// <summary>电池优化是否已豁免（API 23 起才有这个概念；更老系统当已豁免）。</summary>
        public static bool IsIgnoringBattery()
        {
            try
            {
                if (!OperatingSystem.IsAndroidVersionAtLeast(23)) return true;

                var ctx = global::Android.App.Application.Context;
                var pm = ctx.GetSystemService(Context.PowerService) as PowerManager;
                return pm?.IsIgnoringBatteryOptimizations(ctx.PackageName ?? string.Empty) ?? false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>跳到系统的「电池优化」列表页（用户自己找到本 app 设为不优化）。</summary>
        public static void OpenBatterySettings()
        {
            var intent = new Intent("android.settings.IGNORE_BATTERY_OPTIMIZATION_SETTINGS");
            intent.AddFlags(ActivityFlags.NewTask);
            global::Android.App.Application.Context.StartActivity(intent);
        }

        /// <summary>拉起前台服务（幂等：重复拉起只是再走一次 OnStartCommand）。</summary>
        public static void EnsureStarted()
        {
            try
            {
                var ctx = global::Android.App.Application.Context;
                var intent = new Intent(ctx, typeof(SegmentForegroundService));

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
                // 拉不起来（权限被 ROM 收紧等）：卡片还显示"已关闭"，用户能手动重试
            }
        }

        /// <summary>停掉采集服务（总开关关掉时用）。</summary>
        public static void Stop()
        {
            try
            {
                var ctx = global::Android.App.Application.Context;
                ctx.StopService(new Intent(ctx, typeof(SegmentForegroundService)));
            }
            catch { }
        }
    }
}
