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
        private const string ChannelId = "pomodoro_service";
        private const string ActionPauseResume = "ph.pomodoro.PAUSE_RESUME";
        private const string ActionStop = "ph.pomodoro.STOP";

        private PomodoroStateWatcher? _watcher;
        private Handler? _handler;
        private Java.Lang.Runnable? _progressTick;

        public override void OnCreate()
        {
            base.OnCreate();

            try
            {
                // 状态 → 通知的桥：1Hz 的 Changed 里只有进度条需要节流刷新（文本是系统 Chronometer 自己跳）
                _watcher = new PomodoroStateWatcher(this);
                PomodoroState.Changed += _watcher.OnChanged;

                _handler = new Handler(Looper.MainLooper);
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
                    StopSelf();
                    return StartCommandResult.NotSticky;
                }

                StartForegroundCompat();
                UpdateNotification();
            }
            catch { }

            return StartCommandResult.Sticky;
        }

        public override IBinder? OnBind(Intent? intent) => null;

        public override void OnDestroy()
        {
            if (_watcher is not null)
            {
                PomodoroState.Changed -= _watcher.OnChanged;
            }

            try { _handler?.RemoveCallbacks(_progressTick!); } catch { }

            base.OnDestroy();
        }

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

        private void UpdateNotification()
        {
            try
            {
                var nm = GetSystemService(Context.NotificationService) as NotificationManager;
                nm?.Notify(NotificationId, BuildNotification());

                if (!PomodoroState.Running)
                {
                    StopSelf();
                }
            }
            catch { /* 通知失败不炸服务 */ }
        }

        internal Notification BuildNotification()
        {
            string pieceName = PomodoroState.PieceDisplay;
            long remainSec = PomodoroState.RemainingMs / 1000;

            if (OperatingSystem.IsAndroidVersionAtLeast(26))
            {
                var channel = new NotificationChannel(
                    ChannelId, "番茄钟", NotificationImportance.Low);
                (GetSystemService(Context.NotificationService) as NotificationManager)
                    ?.CreateNotificationChannel(channel);
            }

            var builder = OperatingSystem.IsAndroidVersionAtLeast(26)
                ? new Notification.Builder(this, ChannelId)
#pragma warning disable CS0618 // API <26 旧构造，仅兜底
                : new Notification.Builder(this);
#pragma warning restore CS0618

            builder
                .SetContentTitle(PomodoroState.PausedPhase ? "🍅 已暂停" : $"🍅 专注中 · {pieceName}")
                .SetContentText(string.IsNullOrEmpty(PomodoroState.Note)
                    ? $"剩余 {remainSec / 60:D2}:{remainSec % 60:D2}"
                    : $"{PomodoroState.Note} · 剩余 {remainSec / 60:D2}:{remainSec % 60:D2}")
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

            if (OperatingSystem.IsAndroidVersionAtLeast(24))
            {
                // 系统 Chronometer 倒计时：文本每秒自己跳，零刷新成本
                builder.SetUsesChronometer(true)
                       .SetWhen(PomodoroState.EndsAtMs)
                       .SetChronometerCountDown(true);
            }

            if (PomodoroState.PlannedMinutes > 0)
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
            public PomodoroStateWatcher(PomodoroForegroundService svc) => _svc = svc;

            /// <summary>只在「运行 → 停止」的边界刷一次（停服务）；秒级刷新交给 Chronometer。</summary>
            public void OnChanged()
            {
                if (!PomodoroState.Running)
                {
                    _svc.StopSelf();
                }
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
