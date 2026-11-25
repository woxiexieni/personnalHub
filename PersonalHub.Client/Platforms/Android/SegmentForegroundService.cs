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
    /// 前台服务类型 = specialUse + location（R14.5：读 Wi-Fi SSID 要求 app 持有精确定位
    /// 权限且前台服务声明了 location 类型，只声明 specialUse 拿到的 SSID 永远是
    /// "&lt;unknown ssid&gt;"）。specialUse = 0x40000000，location = 0x8，都写数值 ——
    /// 具名成员 API 34+ 才有，引用会触发 CA1416；specialUse 没有 6 小时限额（dataSync 有）。
    ///
    /// 铁律（照 R12 监听服务的教训）：所有回调吞异常 —— 服务里抛了，
    /// 系统可能杀掉/不再重启，时间片就整个静默失效。
    /// </summary>
    [Service(
        Name = "com.personalhub.client.SegmentForegroundService",
        Enabled = true,
        Exported = false,
        Label = "PersonalHub 时间片",
        ForegroundServiceType = (ForegroundService)(0x40000000 | 0x8))]
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

            // 各信号独立兜底：缺哪个少哪个，互不拖垮
            try { RegisterScreenReceiver(); } catch { }
            try { RegisterNetProbe(); } catch { }
            try { StartUsageLoop(); } catch { }
            try { RegisterMotionLoop(); } catch { }
            try { ProbeWifiOnce(); } catch { }
            try { RegisterPassiveLocation(); } catch { }
        }

        public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
        {
            try
            {
                StartForegroundCompat();
                SegmentCapture.ServiceRunning = true;
            }
            catch (Exception ex)
            {
                // 进不了前台就主动退场：系统给 startForegroundService 的窗口只有 5 秒，
                // 超时会以 "did not then call Service.startForeground()" 杀掉整个进程。
                // 留日志 + 下次 StartForegroundService 会再来一次。
                global::Android.Util.Log.Error("PH.Segment", "startForeground 失败：" + ex);
                try { StopSelf(); } catch { }
                return StartCommandResult.NotSticky;
            }

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

            try { UnregisterMotion(); } catch { }

            try { UnregisterPassiveLocation(); } catch { }

            base.OnDestroy();
        }

        // ============================================================
        // 前台通知
        // ============================================================

        private void StartForegroundCompat()
        {
            var notification = BuildNotification();

            if (!OperatingSystem.IsAndroidVersionAtLeast(29))
            {
                StartForeground(NotificationId, notification);
                return;
            }

            // 0x40000000 = specialUse，0x8 = location。
            // 两个值都必须写在清单的 foregroundServiceType 里（AndroidManifest.xml），
            // 否则框架按"清单未声明的类型"抛 IllegalArgumentException，服务进不了前台。
            //
            // location 这一段要按权限决定：Android 10+ 声明了 location 类型却**没拿到**
            // 定位权限时，startForeground 会抛 SecurityException（不是静默失败）。
            // 拿不到就只报 specialUse —— 代价是 SSID 读不出来，其余信号照常。
            int type = 0x40000000;
            if (HasLocationPermission()) type |= 0x8;

            try
            {
                StartForeground(NotificationId, notification, (ForegroundService)type);
            }
            catch when (type != 0x40000000)
            {
                // 带 location 被拒：Android 14 起「从后台启动的定位前台服务」要求
                // 后台定位权限，开机广播拉起的那一次正好属于这种情况。
                // 退回只报 specialUse —— 代价是 SSID 读不出来，
                // 亮灭 / 网络 / 前台 app / 运动这些主信号照常。
                global::Android.Util.Log.Warn("PH.Segment", "带 location 的 startForeground 被拒，退回 specialUse");
                StartForeground(NotificationId, notification, (ForegroundService)0x40000000);
            }
        }

        /// <summary>粗/精定位任一授权即可（读 SSID 只要求其一，各机型要求不同）。</summary>
        private bool HasLocationPermission()
        {
            try
            {
                if (!OperatingSystem.IsAndroidVersionAtLeast(23)) return true;

                return CheckSelfPermission(global::Android.Manifest.Permission.AccessFineLocation) == Permission.Granted
                    || CheckSelfPermission(global::Android.Manifest.Permission.AccessCoarseLocation) == Permission.Granted;
            }
            catch
            {
                return false;
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

            _netCallback = new NetworkProbe(this);
            cm.RegisterDefaultNetworkCallback(_netCallback);
            _netRegistered = true;
        }

        private sealed class NetworkProbe : ConnectivityManager.NetworkCallback
        {
            private readonly SegmentForegroundService _owner;
            private long _lastAt;

            public NetworkProbe(SegmentForegroundService owner) => _owner = owner;

            public override void OnAvailable(Network network) => Fire();
            public override void OnLost(Network network) => Fire();

            private void Fire()
            {
                long now = Java.Lang.JavaSystem.CurrentTimeMillis();
                // 去抖在这里做最省：Wi-Fi/蜂窝来回切换时别把缓冲刷爆
                if (now - _lastAt < 60_000) return;
                _lastAt = now;
                SegmentCapture.HandleSignal(SegmentCapture.SigNetChange, now);

                // 网络刚变，趁机读一次 Wi-Fi 身份（此刻 ConnectionInfo 最新鲜）
                _owner.ProbeWifiOnce();
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

        // ============================================================
        // 4) 运动状态（R14.5：加速度计 2Hz 降频 → 每分钟算抖动均值 → 三态）
        // ============================================================
        //   2Hz 采样足够分辨「躺着不动 / 有动作 / 走路坐车」，耗电比游戏级采样低一个量级。
        //   三态阈值是拍脑袋的合理值，不对味就改下面两个数。

        private const int MotionSamplePeriodUs = 500_000;   // 2Hz
        private const double MotionStillMax = 0.12;         // 均值抖动 < 0.12 ≈ 设备没动
        private const double MotionMoveMax = 2.2;           // < 2.2 ≈ 有动作（做饭/打字）；更大 ≈ 走路/坐车

        private Android.Hardware.SensorManager? _sensorMgr;
        private Android.Hardware.Sensor? _accelSensor;
        private MotionListener? _motionListener;
        private Handler? _motionHandler;
        private Java.Lang.Runnable? _motionTick;

        private void RegisterMotionLoop()
        {
            _sensorMgr = GetSystemService(Context.SensorService) as Android.Hardware.SensorManager;
            _accelSensor = _sensorMgr?.GetDefaultSensor(Android.Hardware.SensorType.Accelerometer);
            if (_sensorMgr is null || _accelSensor is null) return;

            _motionListener = new MotionListener();
            // 绑定的第三参是 SensorDelay 枚举，但 Android 原生接受任意微秒值 ——
            // 强转 500_000（= 2Hz）正是我们要的低频档（枚举成员最小 Normal 是 60ms，太快）
            _sensorMgr.RegisterListener(_motionListener, _accelSensor, (Android.Hardware.SensorDelay)MotionSamplePeriodUs);

            _motionHandler = new Handler(Looper.MainLooper);
            _motionTick = new Java.Lang.Runnable(() =>
            {
                try
                {
                    string state = _motionListener!.DrainState();
                    SegmentCapture.HandleSignal(SegmentCapture.SigMotion,
                        Java.Lang.JavaSystem.CurrentTimeMillis(), state);
                }
                catch { }
                _motionHandler!.PostDelayed(_motionTick!, 60_000);
            });
            _motionHandler.PostDelayed(_motionTick, 60_000);
        }

        private void UnregisterMotion()
        {
            try { _sensorMgr?.UnregisterListener(_motionListener); } catch { }
            try { _motionHandler?.RemoveCallbacks(_motionTick!); } catch { }
        }

        private sealed class MotionListener : Java.Lang.Object, Android.Hardware.ISensorEventListener
        {
            private double _sumDelta;
            private int _count;
            private readonly float[] _last = new float[3];

            public void OnSensorChanged(Android.Hardware.SensorEvent? e)
            {
                try
                {
                    if (e?.Values is null || e.Values.Count < 3) return;

                    float x = e.Values[0], y = e.Values[1], z = e.Values[2];

                    if (_count > 0)
                    {
                        // 相邻两次采样的分量差之和 ≈ 抖动强度（不用真方差，省一次开方）
                        _sumDelta += Math.Abs(x - _last[0]) + Math.Abs(y - _last[1]) + Math.Abs(z - _last[2]);
                    }
                    _last[0] = x;
                    _last[1] = y;
                    _last[2] = z;
                    _count++;
                }
                catch { }
            }

            public void OnAccuracyChanged(Android.Hardware.Sensor? sensor, Android.Hardware.SensorStatus status)
            {
                // 不关心
            }

            /// <summary>每分钟由服务的 tick 调一次：清空累计并给出本分钟的三态。</summary>
            public string DrainState()
            {
                double avg = _count > 1 ? _sumDelta / (_count - 1) : 0;
                _sumDelta = 0;
                _count = 0;

                if (avg < MotionStillMax) return "still";
                if (avg < MotionMoveMax) return "move";
                return "big";
            }
        }

        // ============================================================
        // 5) Wi-Fi 身份（R14.5：读 SSID 当「在家/在公司」的主证据）
        // ============================================================
        //   读 SSID 的三个条件：精确定位权限（manifest）+ 定位服务开着 + 本服务 FGS
        //   带 location 类型。缺任何一个拿到的都是 "<unknown ssid>"，直接丢弃。
        //   SSID 只进本地表，绝不上传。

        internal void ProbeWifiOnce()
        {
            try
            {
                var wm = GetSystemService(Context.WifiService) as Android.Net.Wifi.WifiManager;
                string ssid = wm?.ConnectionInfo?.SSID?.Trim('"') ?? string.Empty;

                if (string.IsNullOrEmpty(ssid)) return;
                if (ssid.Contains("unknown", StringComparison.OrdinalIgnoreCase)) return;

                SegmentCapture.HandleSignal(SegmentCapture.SigWifi,
                    Java.Lang.JavaSystem.CurrentTimeMillis(), ssid);
            }
            catch { }
        }

        // ============================================================
        // 6) 被动定位（R14.5：只当 Wi-Fi 断开时的辅助证据）
        // ============================================================
        //   PassiveProvider = 别的 app 请求定位时系统顺路分发的修复点，本 app 零额外耗电；
        //   状态变化才上报（static → moved 各一次），不刷缓冲。

        private const float MovedMeters = 80f;

        private Android.Locations.LocationManager? _locMgr;
        private PassiveLocationListener? _locListener;

        private void RegisterPassiveLocation()
        {
            _locMgr = GetSystemService(Context.LocationService) as Android.Locations.LocationManager;
            if (_locMgr is null) return;

            try
            {
                _locListener = new PassiveLocationListener();
                _locMgr.RequestLocationUpdates(
                    Android.Locations.LocationManager.PassiveProvider,
                    300_000,        // 5 分钟最小间隔（被动分发本身零成本，这只是限频）
                    MovedMeters,
                    _locListener,
                    Looper.MainLooper);
            }
            catch { /* 没定位权限：这一信号缺失，其余不受影响 */ }
        }

        private void UnregisterPassiveLocation()
        {
            try { _locMgr?.RemoveUpdates(_locListener); } catch { }
        }

        private sealed class PassiveLocationListener : Java.Lang.Object, Android.Locations.ILocationListener
        {
            private Android.Locations.Location? _last;
            private string _lastState = string.Empty;

            public void OnLocationChanged(Android.Locations.Location? location)
            {
                try
                {
                    if (location is null) return;

                    string state = "static";
                    if (_last is not null)
                    {
                        float meters = _last.DistanceTo(location);
                        state = meters >= MovedMeters ? "moved" : "static";
                    }

                    // 只在状态翻转时上报，第一针必报
                    if (state != _lastState)
                    {
                        _lastState = state;
                        SegmentCapture.HandleSignal(SegmentCapture.SigLocation,
                            Java.Lang.JavaSystem.CurrentTimeMillis(), state);
                    }

                    _last = location;
                }
                catch { }
            }

            public void OnProviderDisabled(string? provider) { }
            public void OnProviderEnabled(string? provider) { }

#pragma warning disable CS0618 // ILocationListener 接口要求的旧成员，API 29 起废弃但必须实现
            public void OnStatusChanged(string? provider, Android.Locations.Availability status, Bundle? extras) { }
#pragma warning restore CS0618
        }
    }
}
