using Android.App;
using Android.App.Usage;
using Android.Content;
using Android.Content.PM;
using Android.Net;
using Android.OS;
using PersonalHub.Client.Services;

namespace PersonalHub.Client
{
    /// <summary>
    /// 时间片采集服务（R13）：常驻前台，把手机信号喂给 <see cref="SegmentCapture"/>。
    ///
    /// 采集三件套（拍板时的 v0 范围，刻意不上定位——权限凶且耗电）：
    ///   1. 屏幕亮灭 / 解锁 —— 动态注册的广播接收器（SCREEN_ON/OFF 自 API 8 起
    ///      就不允许静态注册，只能在运行中的服务里注册）；
    ///   2. 网络切换 —— RegisterDefaultNetworkCallback，只当"段内特征"记录；
    ///   3. 前台应用 —— UsageStatsManager 每 15 分钟查一次最近的前台应用
    ///      （需要"使用情况访问权限"，由 SegmentServiceHelper 领用户去开）。
    ///
    /// 前台服务类型 = specialUse（Android 14+ 硬性要求声明类型；dataSync 在
    /// Android 15 有 6 小时限额会半夜被杀，specialUse 没有限额，自装 app 不走
    /// Play 审核，不需要 PROPERTY_SPECIAL_USE_FGS_SUBTYPE property）。
    /// 0x40000000 就是 ForegroundServiceType.SpecialUse 的数值 —— 写数值而不引用
    /// 枚举成员，是因为那个成员标注 API 34+，引用它会触发 CA1416。
    ///
    /// 铁律（照 R12 监听服务的教训）：所有回调吞异常 —— 服务里抛了，
    /// 系统可能杀掉/不再重启，时间片就整个静默失效。
    /// </summary>
    [Service(
        Name = "com.personalhub.client.SegmentForegroundService",
        Enabled = true,
        Exported = false,
        Label = "PersonalHub 时间片",
        ForegroundServiceType = (ForegroundService)0x40000000)]
        // 0x40000000 = ForegroundService.TypeSpecialUse（API 34+ 才有具名成员，
        // 写数值避免 CA1416；属性声明的绑定枚举类型是 Android.Content.PM.ForegroundService）
    public class SegmentForegroundService : Service
    {
        private const int NotificationId = 1002;
        private const string ChannelId = "segment_service";
        private const long UsageTickMs = 15 * 60_000L;

        private ScreenStateReceiver? _screenReceiver;
        private NetworkProbe? _netCallback;
        private Handler? _handler;
        private Java.Lang.Runnable? _usageTick;
        private bool _screenRegistered;
        private bool _netRegistered;

        public override void OnCreate()
        {
            base.OnCreate();

            try
            {
                RegisterScreenReceiver();
                RegisterNetProbe();
                StartUsageLoop();
            }
            catch { /* 初始化失败不炸服务：缺哪个信号就少哪个信号 */ }
        }

        public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
        {
            try
            {
                StartForegroundCompat();
                SegmentCapture.ServiceRunning = true;
            }
            catch { /* 抢不到前台也别崩 —— 下一次 StartForegroundService 会再试 */ }

            return StartCommandResult.Sticky;
        }

        public override IBinder? OnBind(Intent? intent) => null;

        public override void OnDestroy()
        {
            SegmentCapture.ServiceRunning = false;

            try
            {
                if (_screenRegistered && _screenReceiver is not null)
                {
                    UnregisterReceiver(_screenReceiver);
                }
            }
            catch { }

            try
            {
                if (_netRegistered && _netCallback is not null)
                {
                    (GetSystemService(Context.ConnectivityService) as ConnectivityManager)
                        ?.UnregisterNetworkCallback(_netCallback);
                }
            }
            catch { }

            try
            {
                _handler?.RemoveCallbacks(_usageTick!);
            }
            catch { }

            base.OnDestroy();
        }

        // ============================================================
        // 前台通知
        // ============================================================

        private void StartForegroundCompat()
        {
            var notification = BuildNotification();

            if (OperatingSystem.IsAndroidVersionAtLeast(29))
            {
                StartForeground(NotificationId, notification, (ForegroundService)0x40000000);
            }
            else
            {
                StartForeground(NotificationId, notification);
            }
        }

        private Notification BuildNotification()
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
            {
                var channel = new NotificationChannel(
                    ChannelId, "时间片记录", NotificationImportance.Min);
                (GetSystemService(Context.NotificationService) as NotificationManager)
                    ?.CreateNotificationChannel(channel);

                return new Notification.Builder(this, ChannelId)
                    .SetContentTitle("时间片记录中")
                    .SetContentText("自动划分你的一天，稍后在时间线里确认")
                    .SetSmallIcon(Android.Resource.Drawable.SymDefAppIcon)
                    .SetOngoing(true)
                    .Build();
            }

#pragma warning disable CS0618 // API <26 的旧构造，仅兜底
            return new Notification.Builder(this)
                .SetContentTitle("时间片记录中")
                .SetContentText("自动划分你的一天，稍后在时间线里确认")
                .SetSmallIcon(Android.Resource.Drawable.SymDefAppIcon)
                .Build();
#pragma warning restore CS0618
        }

        // ============================================================
        // 1) 屏幕亮灭 / 解锁
        // ============================================================

        private void RegisterScreenReceiver()
        {
            _screenReceiver = new ScreenStateReceiver();
            var filter = new IntentFilter();
            filter.AddAction(Intent.ActionScreenOn);
            filter.AddAction(Intent.ActionScreenOff);
            filter.AddAction(Intent.ActionUserPresent);

            // Android 13+ 动态注册必须显式给 exported 标志，否则 SecurityException。
            // 系统广播对 not-exported 的接收器照常投递（系统是特权发送者）。
            if (OperatingSystem.IsAndroidVersionAtLeast(33))
            {
                RegisterReceiver(_screenReceiver, filter, (ReceiverFlags)4);
                    // 4 = Context.ReceiverNotExported；写数值避开对 API 33 常量的 CA1416 引用
            }
            else
            {
                RegisterReceiver(_screenReceiver, filter);
            }
            _screenRegistered = true;
        }

        private sealed class ScreenStateReceiver : BroadcastReceiver
        {
            public override void OnReceive(Context? context, Intent? intent)
            {
                long now = Java.Lang.JavaSystem.CurrentTimeMillis();
                switch (intent?.Action)
                {
                    case Intent.ActionScreenOn:
                        SegmentCapture.HandleSignal(SegmentCapture.SigScreenOn, now);
                        break;
                    case Intent.ActionScreenOff:
                        SegmentCapture.HandleSignal(SegmentCapture.SigScreenOff, now);
                        break;
                    case Intent.ActionUserPresent:
                        SegmentCapture.HandleSignal(SegmentCapture.SigUnlock, now);
                        break;
                }
            }
        }

        // ============================================================
        // 2) 网络切换（段内特征，不当边界）
        // ============================================================

        private void RegisterNetProbe()
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(24)) return;

            var cm = GetSystemService(Context.ConnectivityService) as ConnectivityManager;
            if (cm is null) return;

            _netCallback = new NetworkProbe();
            cm.RegisterDefaultNetworkCallback(_netCallback);
            _netRegistered = true;
        }

        private sealed class NetworkProbe : ConnectivityManager.NetworkCallback
        {
            private long _lastAt;

            public override void OnAvailable(Network network) => Fire();
            public override void OnLost(Network network) => Fire();

            private void Fire()
            {
                long now = Java.Lang.JavaSystem.CurrentTimeMillis();
                // 去抖在这里做最省：Wi-Fi/蜂窝来回切换时别把缓冲刷爆
                if (now - _lastAt < 60_000) return;
                _lastAt = now;
                SegmentCapture.HandleSignal(SegmentCapture.SigNetChange, now);
            }
        }

        // ============================================================
        // 3) 前台应用采样（15 分钟一次，最近的前台包名）
        // ============================================================

        private void StartUsageLoop()
        {
            _handler = new Handler(Looper.MainLooper);
            _usageTick = new Java.Lang.Runnable(() =>
            {
                PollUsageOnce();
                _handler!.PostDelayed(_usageTick!, UsageTickMs);
            });
            _handler.PostDelayed(_usageTick, UsageTickMs);
        }

        private void PollUsageOnce()
        {
            try
            {
                if (!SegmentServiceHelper.HasUsageAccess()) return;

                var usm = GetSystemService(Context.UsageStatsService) as UsageStatsManager;
                if (usm is null) return;

                long end = Java.Lang.JavaSystem.CurrentTimeMillis();
                long begin = end - UsageTickMs;
                var events = usm.QueryEvents(begin, end);

                var ev = new UsageEvents.Event();
                string? pkg = null;
                long at = 0;

                while (events.HasNextEvent)
                {
                    events.GetNextEvent(ev);
                    // 1 = MOVE_TO_FOREGROUND（API <29）/ ACTIVITY_RESUMED（API 29+），同值兼容
                    if ((int)ev.EventType == 1)
                    {
                        pkg = ev.PackageName;
                        at = ev.TimeStamp;
                    }
                }

                if (!string.IsNullOrEmpty(pkg))
                {
                    SegmentCapture.HandleSignal(SegmentCapture.SigApp, at, pkg);
                }
            }
            catch { /* 没授权 / 系统限制 —— 这一拍放弃，下一拍再试 */ }
        }
    }
}
