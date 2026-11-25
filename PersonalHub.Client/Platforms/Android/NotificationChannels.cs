using Android.App;
using Android.Content;
using Android.OS;

namespace PersonalHub.Client
{
    /// <summary>
    /// 通知渠道集中创建（R15）。
    ///
    /// 为什么必须在这里建、不能"发通知前顺手建"：
    /// Android 13+ 用户拒绝通知权限后，<c>CreateNotificationChannel</c> 会**静默失败**，
    /// 紧接着 <c>startForeground</c> 抛异常（渠道不存在 / 通知发不出来），
    /// 而服务里那段 catch 一吞 —— 表现就是「番茄钟跑起来了，但通知栏什么都没有」。
    /// 启动时建一次，之后每次调用只是幂等更新，不影响用户已改过的设置。
    /// </summary>
    public static class NotificationChannels
    {
        /// <summary>自动时间片（安静，IMPORTANCE_MIN）。</summary>
        public const string Segment = "segment_service";

        /// <summary>番茄钟进行中（倒计时 + 暂停/结束按钮）。</summary>
        public const string Pomodoro = "pomodoro_service";

        /// <summary>番茄钟到点提醒。**番茄钟唯一的"响"就靠它**，所以是 High。</summary>
        public const string PomodoroDone = "pomodoro_done";

        public static void EnsureAll()
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return;

            try
            {
                var ctx = Android.App.Application.Context;
                if (ctx.GetSystemService(Context.NotificationService) is not NotificationManager mgr) return;

                var segment = new NotificationChannel(Segment, "时间片记录", NotificationImportance.Min)
                {
                    Description = "自动采集时间段时在通知栏留一个安静的存在感",
                };
                segment.SetShowBadge(false);
                Put(mgr, segment);

                var pomodoro = new NotificationChannel(Pomodoro, "番茄钟进行中", NotificationImportance.Low)
                {
                    Description = "专注倒计时，带「暂停 / 继续」和「结束」",
                };
                pomodoro.SetShowBadge(false);
                Put(mgr, pomodoro);

                // 到点这一条要能吵醒人：High = 横幅 + 声音 + 震动。
                // 频道建好之后声音由系统管，用户可以在系统设置里单独关掉它，我们不去覆盖。
                var done = new NotificationChannel(PomodoroDone, "番茄钟到点", NotificationImportance.High)
                {
                    Description = "专注时间到了会响一声 —— 这是番茄钟唯一的提醒",
                };
                done.EnableVibration(true);
                done.EnableLights(true);
                done.SetShowBadge(true);
                Put(mgr, done);
            }
            catch
            {
                // 建渠道失败不该影响启动：没有通知也照样能记时间块
            }
        }

        private static void Put(NotificationManager mgr, NotificationChannel channel)
        {
            try { mgr.CreateNotificationChannel(channel); } catch { }
        }
    }
}
