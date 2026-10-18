namespace PersonalHub.Client.Services
{
    /// <summary>
    /// 客户端的时间口径 —— 必须和服务端 `Analysis/Period.cs` 逐字一致。
    ///
    /// 为什么不用手机本地时区：
    /// 服务端按 UTC+8 划分"今天"。手机如果跟着系统时区走（出差、模拟器、改时区），
    /// 就会出现"手机显示今天能改、服务端认为那是昨天"这种对不上的情况，
    /// 而且两边各自都能自圆其说，排查起来非常费劲。
    ///
    /// 所以：**日界只有一处定义，两端共用同一套规则**。服务端改了这里也要改。
    /// </summary>
    public static class Period
    {
        public static readonly TimeSpan Cst = TimeSpan.FromHours(8);

        /// <summary>某个瞬间所在的东八区自然日 00:00。</summary>
        public static DateTimeOffset StartOfDay(DateTimeOffset instant)
        {
            var local = instant.ToOffset(Cst);
            return new DateTimeOffset(local.Year, local.Month, local.Day, 0, 0, 0, Cst);
        }

        public static DateTimeOffset DayStartOf(long unixMs)
            => StartOfDay(DateTimeOffset.FromUnixTimeMilliseconds(unixMs));

        /// <summary>今天：[今天 00:00, 明天 00:00)。</summary>
        public static (long From, long To) Today()
        {
            var start = StartOfDay(DateTimeOffset.UtcNow);
            return (start.ToUnixTimeMilliseconds(), start.AddDays(1).ToUnixTimeMilliseconds());
        }

        /// <summary>最近 N 天（含今天）：[今天-(N-1) 00:00, 明天 00:00)。</summary>
        public static (long From, long To) LastDays(int days)
        {
            if (days < 1) days = 1;
            var start = StartOfDay(DateTimeOffset.UtcNow).AddDays(-(days - 1));
            return (start.ToUnixTimeMilliseconds(), start.AddDays(days).ToUnixTimeMilliseconds());
        }

        /// <summary>
        /// 某个时间戳是否属于"今天"（决定这条记录在手机上能不能改）。
        ///
        /// 规则来自需求：今日（无论是否已同步）可编辑；昨天及更早只读，去电脑端改。
        /// </summary>
        public static bool IsToday(long unixMs)
            => DayStartOf(unixMs).ToUnixTimeMilliseconds() == StartOfDay(DateTimeOffset.UtcNow).ToUnixTimeMilliseconds();

        /// <summary>换成本地时区显示（只影响展示，不影响归属判定）。</summary>
        public static DateTime ToLocalTime(long unixMs)
            => DateTimeOffset.FromUnixTimeMilliseconds(unixMs).LocalDateTime;
    }
}
