using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using PersonalHub.Client.Services;

namespace PersonalHub.Client.Platforms.Android
{
    /// <summary>
    /// 番茄钟前台服务（R14.5）：只做两件事 ——
    ///   1. 把倒计时渲染成常驻通知（锁屏/通知栏随手可见，即「Android 版灵动岛」的等价物）：
    ///      文本走系统 Chronometer 倒计时（每秒自动跳，不用我们自己刷），进度条每 30 秒推一次；
    ///   2. 通知上的「暂停/继续」「结束」按钮 → 动态注册的 receiver 转调
    ///      <see cref="PomodoroState"/>（静态类，进程内直调，无跨进程）。
    ///
    /// 计时本身不在这里：PomodoroState 的循环在进程内跑，本服务只负责保活进程 + 渲染。
    /// 状态归 NotRunning 时自动 StopSelf。前台服务类型沿用 specialUse（理由见
    /// SegmentForegroundService 头注释）。回调吞异常的铁律同 R12/R13。
    /// </summary>
    [Service(
        Name = "com.personalhub.client.PomodoroForegroundService",
        Enabled = true,
        Exported = false,
        Label = "PersonalHub 番茄钟",
        ForegroundServiceType = (ForegroundService)0x40000000)]
    public class PomodoroForegroundService : Service
    {
        private const int NotificationId = 1003;

        /// <summary>到点提醒用另一个 id：它会留在通知栏里，而进行中那条要跟着服务一起消失。</summary>
        private const int DoneNotificationId = 1004;

        private const string ChannelId = NotificationChannels.Pomodoro;
        private const string ActionPauseResume = "ph.pomodoro.PAUSE_RESUME";
        private const string ActionStop = "ph.pomodoro.STOP";

        private PomodoroStateWatcher? _watcher;
        private Handler? _handler;
        private Java.Lang.Runnable? _progressTick;
        private Java.Lang.Runnable? _delayedStop;
        private bool _stopScheduled;

        public override void OnCreate()
        {
            base.OnCreate();

            try
            {
                _handler = new Handler(Looper.MainLooper);

                _delayedStop = new Java.Lang.Runnable(() =>
                {
                    try { StopSelf(); } catch { }
                });

                // 状态 → 通知的桥：1Hz 的 Changed 里只有进度条需要节流刷新（文本是系统 Chronometer 自己跳）
                _watcher = new PomodoroStateWatcher(this);
                PomodoroState.Changed += _watcher.OnChanged;

                // 到点提醒：番茄钟唯一的"响"。前台服务要等它发完才退场，
                // 所以退场走 ScheduleStop 的延迟路径，不在这里掐。
                PomodoroState.CompletedNaturally += OnCompletedNaturally;

                _progressTick = new Java.Lang.Runnable(() =>
                {
                    UpdateNotification();
                    if (PomodoroState.Running)
                    {
                        _handler!.PostDelayed(_progressTick!, 30_000);
                    }
                });
                _handler.PostDelayed(_progressTick, 30_000);

                var receiver = new PomodoroActionReceiver();
                var filter = new IntentFilter(ActionPauseResume);
                filter.AddAction(ActionStop);
                RegisterReceiver(receiver, filter, (ReceiverFlags)4);   // 4 = ReceiverNotExported
            }
            catch { /* 初始化失败不炸服务 */ }
        }

        public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
        {
            try
            {
                if (!PomodoroState.Running)
                {
                    // START_STICKY 会在进程被杀之后把服务单独叫回来，但静态状态已经归零。
                    // 先尝试从本地存储恢复那一次番茄；恢复不出来（本来就没有 / 早就结束）才退场。
                    if (!PomodoroState.TryRestore())
                    {
                        StopSelf();
                        return StartCommandResult.NotSticky;
                    }
                }

                StartForegroundCompat();
                UpdateNotification();
            }
            catch (Exception ex)
            {
                // 进不了前台就别硬撑：系统给 startForegroundService 的窗口只有 5 秒，
                // 超时会以 "did not then call Service.startForeground()" 把**整个进程**杀掉
                // （表现是点开始专注后 app 自己退到桌面）。主动 StopSelf + 留日志更可控。
                global::Android.Util.Log.Error("PH.Pomodoro", "startForeground 失败：" + ex);
                try { StopSelf(); } catch { }
                return StartCommandResult.NotSticky;
            }

            return StartCommandResult.Sticky;
        }

        public override IBinder? OnBind(Intent? intent) => null;

        public override void OnDestroy()
        {
            if (_watcher is not null)
            {
                PomodoroState.Changed -= _watcher.OnChanged;
            }

            PomodoroState.CompletedNaturally -= OnCompletedNaturally;

            try { _handler?.RemoveCallbacks(_progressTick!); } catch { }
            try { _handler?.RemoveCallbacks(_delayedStop!); } catch { }

            base.OnDestroy();
        }

        /// <summary>
        /// 到点提醒。走 High 渠道（NotificationChannels.PomodoroDone）：横幅 + 声音 + 震动。
        ///
        /// 为什么单独一条、不复用进行中那条：
        /// 进行中是 ongoing（划不掉、静音渠道），而到点这条必须能**主动消失**、能响。
        /// 同一个 id 上两种性格会互相打架，最终必然有一种不生效。
        /// </summary>
        private void OnCompletedNaturally(int minutes)
        {
            try
            {
                _handler?.Post(() => PostDoneNotification(minutes));
            }
            catch { }
        }

        private void PostDoneNotification(int minutes)
        {
            try
            {
                var nm = GetSystemService(Context.NotificationService) as NotificationManager;
                if (nm is null) return;

                string piece = PomodoroState.PieceDisplay;
                string detail = string.IsNullOrEmpty(PomodoroState.Note)
                    ? $"{piece} · 已记入时间块"
                    : $"{piece} · {PomodoroState.Note}";

                var builder = OperatingSystem.IsAndroidVersionAtLeast(26)
                    ? new Notification.Builder(this, NotificationChannels.PomodoroDone)
#pragma warning disable CS0618 // API <26 旧构造，仅兜底
                    : new Notification.Builder(this);
#pragma warning restore CS0618

                builder
                    .SetContentTitle($"🍅 到点了 · {minutes} 分钟")
                    .SetContentText(detail)
                    .SetSmallIcon(global::Android.Resource.Drawable.SymDefAppIcon)
                    .SetAutoCancel(true);

                if (OperatingSystem.IsAndroidVersionAtLeast(21))
                {
                    builder.SetCategory(Notification.CategoryAlarm)
                           .SetPriority(NotificationCompatPriorityHigh);
                }

#pragma warning disable CS0618 // 26+ 由渠道决定声音，这里的 Defaults 只对 <26 生效
                builder.SetDefaults(NotificationDefaults.All);
#pragma warning restore CS0618

                // 点一下回到 app（不想回到某个具体页面：这条通知的价值是"提醒你看一眼"）
                var launch = PackageManager?.GetLaunchIntentForPackage(PackageName ?? string.Empty);
                if (launch is not null)
                {
                    var pi = PendingIntent.GetActivity(this, 2, launch, (PendingIntentFlags)0x0C000000);
                    builder.SetContentIntent(pi);
                }

                nm.Notify(DoneNotificationId, builder.Build());
            }
            catch
            {
                // 通知发不出去也不能影响入库 —— 数据已经写进时间块了
            }
        }

        private const int NotificationCompatPriorityHigh = 1;   // Notification.PriorityHigh

        /// <summary>给 watcher 用：暂停状态翻转时才重画，不然 1Hz 会每秒重建一次通知。</summary>
        internal void RefreshFromState() => UpdateNotification();

        /// <summary>
        /// 延迟退场：到点提醒是"停在原地"的那一刻发的，
        /// StopSelf 立刻走会把服务连同 handler 一起撤掉。
        /// 2.5 秒足够 Post 出来的那条跑到通知栏。
        /// </summary>
        internal void ScheduleStop()
        {
            if (_stopScheduled) return;
            _stopScheduled = true;

            try { _handler?.PostDelayed(_delayedStop!, 2500); }
            catch { try { StopSelf(); } catch { } }
        }

        private void StartForegroundCompat()
        {
            var notification = BuildNotification();

            if (OperatingSystem.IsAndroidVersionAtLeast(29))
            {
                // 0x40000000 = specialUse。**这个值必须同时写在清单的
                // foregroundServiceType 里**（AndroidManifest.xml 那两个手写 service 节点），
                // 否则框架按"清单未声明的类型"抛 IllegalArgumentException —— 通知永远不出现。
                StartForeground(NotificationId, notification, (ForegroundService)0x40000000);
            }
            else
            {
                StartForeground(NotificationId, notification);
            }
        }

        private void UpdateNotification()
        {
            try
            {
                var nm = GetSystemService(Context.NotificationService) as NotificationManager;
                nm?.Notify(NotificationId, BuildNotification());
            }
            catch { /* 通知失败不炸服务 */ }
        }

        internal Notification BuildNotification()
        {
            string pieceName = PomodoroState.PieceDisplay;
            long remainSec = PomodoroState.RemainingMs / 1000;
            long elapsedSec = PomodoroState.ElapsedMs / 1000;
            bool stopwatch = PomodoroState.Mode == PomodoroMode.Stopwatch;
            string context = string.IsNullOrEmpty(PomodoroState.StepTitle)
                ? PomodoroState.Note
                : PomodoroState.StepTitle;
            string clock = stopwatch
                ? $"{elapsedSec / 60:D2}:{elapsedSec % 60:D2}"
                : $"{remainSec / 60:D2}:{remainSec % 60:D2}";
            string detail = string.IsNullOrEmpty(context)
                ? (stopwatch ? $"已专注 {clock}" : $"剩余 {clock}")
                : $"{context} · {(stopwatch ? $"已专注 {clock}" : $"剩余 {clock}")}";

            // 渠道由 MainApplication 在进程启动时建好（NotificationChannels.EnsureAll）。
            // 不在这里建：用户拒过通知权限时这里会静默失败，接着 startForeground 抛异常，
            // 服务当场起不来 —— 那正是"番茄钟没有任何通知"的成因。

            var builder = OperatingSystem.IsAndroidVersionAtLeast(26)
                ? new Notification.Builder(this, ChannelId)
#pragma warning disable CS0618 // API <26 旧构造，仅兜底
                : new Notification.Builder(this);
#pragma warning restore CS0618

            builder
                .SetContentTitle(PomodoroState.PausedPhase
                    ? "🍅 已暂停"
                    : $"🍅 {(stopwatch ? "正计时" : "专注中")} · {pieceName}")
                .SetContentText(detail)
                .SetSmallIcon(global::Android.Resource.Drawable.SymDefAppIcon)
                .SetOngoing(true)
                .SetOnlyAlertOnce(true)
                .AddAction(MakeAction(
                    PomodoroState.PausedPhase ? "继续" : "暂停",
                    PomodoroState.PausedPhase
                        ? global::Android.Resource.Drawable.IcMediaPlay
                        : global::Android.Resource.Drawable.IcMediaPause,
                    ActionPauseResume, 1))
                .AddAction(MakeAction(
                    "结束",
                    global::Android.Resource.Drawable.IcMenuCloseClearCancel,
                    ActionStop, 2));

            if (OperatingSystem.IsAndroidVersionAtLeast(24) && !PomodoroState.PausedPhase)
            {
                // 倒计时用系统 Chronometer 递减；正计时从“现在 - 已专注”开始递增。
                builder.SetUsesChronometer(true)
                       .SetWhen(stopwatch
                           ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - PomodoroState.ElapsedMs
                           : PomodoroState.EndsAtMs)
                       .SetChronometerCountDown(!stopwatch);
            }

            if (!stopwatch && PomodoroState.PlannedMinutes > 0)
            {
                builder.SetProgress(
                    PomodoroState.PlannedMinutes * 60,
                    (int)(PomodoroState.PlannedMinutes * 60 - remainSec),
                    false);
            }

            return builder.Build();
        }

        private Notification.Action MakeAction(string title, int iconRes, string action, int requestCode)
        {
            var intent = new Intent(action);
            intent.SetPackage(PackageName);

            // UpdateCurrent | Immutable：Immutable 自 API 23 起就有，写数值避 CA1416
            var pi = PendingIntent.GetBroadcast(
                this, requestCode, intent, (PendingIntentFlags)0x0C000000);

            return new Notification.Action.Builder(iconRes, title, pi).Build();
        }

        private sealed class PomodoroStateWatcher
        {
            private readonly PomodoroForegroundService _svc;
            private bool _lastPaused;

            public PomodoroStateWatcher(PomodoroForegroundService svc) => _svc = svc;

            /// <summary>
            /// 运行中只在「暂停 ⇄ 继续」翻转时重画一次（秒级跳动交给系统 Chronometer）；
            /// 停下来则延迟退场 —— 给到点提醒留出发通知的时间。
            /// </summary>
            public void OnChanged()
            {
                if (PomodoroState.Running)
                {
                    if (PomodoroState.PausedPhase != _lastPaused)
                    {
                        _lastPaused = PomodoroState.PausedPhase;
                        try { _svc.RefreshFromState(); } catch { }
                    }

                    return;
                }

                _svc.ScheduleStop();
            }
        }
    }

    /// <summary>通知按钮的回调：进程内直调 PomodoroState（同 R13 的信号 receiver 模式）。</summary>
    public sealed class PomodoroActionReceiver : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent)
        {
            try
            {
                switch (intent?.Action)
                {
                    case "ph.pomodoro.PAUSE_RESUME":
                        if (PomodoroState.PausedPhase)
                        {
                            PomodoroState.ResumePhase();
                        }
                        else
                        {
                            PomodoroState.PausePhase();
                        }
                        break;

                    case "ph.pomodoro.STOP":
                        PomodoroState.StopEarly();
                        break;
                }
            }
            catch { /* 吞掉：receiver 里抛异常可能被系统解绑（R12 的教训） */ }
        }
    }
}
