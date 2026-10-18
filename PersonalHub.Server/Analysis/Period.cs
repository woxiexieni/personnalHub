using System;

namespace personalHub.Server.Analysis
{
    /// <summary>
    /// 时间口径的唯一来源。全项目只有这一处定义"一天从哪里开始"。
    ///
    /// 为什么固定 UTC+8 而不是 DateTime.Now：
    /// 你哪天把服务端挪到一台 UTC 的机器（或云主机）上，"今天"就整体偏 8 小时，
    /// 历史数据看起来像丢了一天，而且症状会分散到趋势图、日均、今日总额好几处，极难回溯。
    ///
    /// 为什么左闭右开：
    /// 23:59:59.999 属于当天，次日 00:00:00.000 属于次日。不定死这条，
    /// 跨天的那笔账永远有争议（而且只在深夜出现，平时测不出来）。
    /// </summary>
    public static class Period
    {
        /// <summary>中国标准时间（UTC+8）。没有夏令时，可以直接按固定偏移处理。</summary>
        public static readonly TimeSpan Cst = TimeSpan.FromHours(8);

        /// <summary>某个瞬间所在的东八区自然日的 00:00。</summary>
        public static DateTimeOffset StartOfDay(DateTimeOffset instant)
        {
            var local = instant.ToOffset(Cst);
            return new DateTimeOffset(local.Year, local.Month, local.Day, 0, 0, 0, Cst);
        }

        /// <summary>任意 Unix 毫秒所属自然日的 00:00。</summary>
        public static DateTimeOffset DayStartOf(long unixMs)
            => StartOfDay(DateTimeOffset.FromUnixTimeMilliseconds(unixMs));

        /// <summary>今天：[今天 00:00, 明天 00:00)</summary>
        public static (long From, long To) Today(DateTimeOffset now)
        {
            var start = StartOfDay(now);
            return (start.ToUnixTimeMilliseconds(), start.AddDays(1).ToUnixTimeMilliseconds());
        }

        /// <summary>最近 N 天（含今天）：[今天-(N-1) 00:00, 明天 00:00)</summary>
        public static (long From, long To) LastDays(int days, DateTimeOffset now)
        {
            if (days < 1) days = 1;
            var start = StartOfDay(now).AddDays(-(days - 1));
            return (start.ToUnixTimeMilliseconds(), start.AddDays(days).ToUnixTimeMilliseconds());
        }
    }
}
