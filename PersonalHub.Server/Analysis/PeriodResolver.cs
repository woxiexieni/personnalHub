using System;

namespace personalHub.Server.Analysis
{
    /// <summary>统计档位。客户端只传这个标识，具体区间一律由服务端算。</summary>
    public enum PeriodKind
    {
        Today = 0,
        Week = 1,
        Month = 2,
        Year = 3,
    }

    /// <summary>
    /// 一个档位解析出来的全部时间信息（Unix 毫秒，左闭右开）。
    ///
    /// <paramref name="PrevFrom"/>/<paramref name="PrevTo"/> 是「上一周期同期」，不是上一个完整周期：
    /// 本月 10-01~10-18 对比 09-01~09-18。否则每到月初，拿 3 天去和整月比，
    /// 永远显示「暴跌 90%」，那个百分比没有任何信息量。
    /// </summary>
    public sealed record PeriodRange(
        PeriodKind Kind,
        long From,
        long To,
        long PrevFrom,
        long PrevTo,
        int ElapsedDays);

    /// <summary>
    /// 档位 → 区间。和 <see cref="Period"/> 一起构成"时间口径"的唯一定义处。
    ///
    /// 两个容易踩的点，都在这里被钉死：
    ///   1. 「本周」从**周一**开始。C# 的 DayOfWeek 里周日是 0，直接取会差一天。
    ///   2. 上一周期的结束点按"已过时长"算，并且**不能超过上一周期自己的长度**
    ///      （3 月 31 日往前一个月是 2 月，2 月只有 28 天，不加钳制会算出 2 月 31 日）。
    /// </summary>
    public static class PeriodResolver
    {
        public static PeriodKind Parse(string? period) => (period ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "week" => PeriodKind.Week,
            "month" => PeriodKind.Month,
            "year" => PeriodKind.Year,
            _ => PeriodKind.Today,   // 空值 / 拼错都退化成今日，不抛异常
        };

        public static PeriodRange Resolve(PeriodKind kind, DateTimeOffset now)
        {
            var start = StartOf(kind, now);
            var end = NextStart(kind, start);

            var prevStart = PrevStart(kind, start);
            var prevEndOfPeriod = NextStart(kind, prevStart);

            // 已过时长：决定「同期」比到哪儿
            var elapsed = now - start;
            if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;

            var prevEnd = prevStart + elapsed;
            if (prevEnd > prevEndOfPeriod) prevEnd = prevEndOfPeriod;

            // 日均按"已过天数"算：本月只过了 3 天，就不能拿 3 天的花费去除 30
            int elapsedDays = (int)Math.Floor(elapsed.TotalDays) + 1;
            int periodDays = (int)Math.Round((end - start).TotalDays);
            if (elapsedDays < 1) elapsedDays = 1;
            if (periodDays > 0 && elapsedDays > periodDays) elapsedDays = periodDays;

            return new PeriodRange(
                kind,
                start.ToUnixTimeMilliseconds(),
                end.ToUnixTimeMilliseconds(),
                prevStart.ToUnixTimeMilliseconds(),
                prevEnd.ToUnixTimeMilliseconds(),
                elapsedDays);
        }

        /// <summary>档位起点的 00:00（UTC+8）。</summary>
        private static DateTimeOffset StartOf(PeriodKind kind, DateTimeOffset now)
        {
            var local = now.ToOffset(Period.Cst);
            var day = new DateTimeOffset(local.Year, local.Month, local.Day, 0, 0, 0, Period.Cst);

            return kind switch
            {
                // 周一 = 0 … 周日 = 6
                PeriodKind.Week => day.AddDays(-(((int)local.DayOfWeek + 6) % 7)),
                PeriodKind.Month => new DateTimeOffset(local.Year, local.Month, 1, 0, 0, 0, Period.Cst),
                PeriodKind.Year => new DateTimeOffset(local.Year, 1, 1, 0, 0, 0, Period.Cst),
                _ => day,
            };
        }

        private static DateTimeOffset NextStart(PeriodKind kind, DateTimeOffset start) => kind switch
        {
            PeriodKind.Week => start.AddDays(7),
            PeriodKind.Month => start.AddMonths(1),
            PeriodKind.Year => start.AddYears(1),
            _ => start.AddDays(1),
        };

        /// <summary>上一个周期的起点。注意 AddMonths(-1) 会自动把 3-31 压到 2-28。</summary>
        private static DateTimeOffset PrevStart(PeriodKind kind, DateTimeOffset start) => kind switch
        {
            PeriodKind.Week => start.AddDays(-7),
            PeriodKind.Month => start.AddMonths(-1),
            PeriodKind.Year => start.AddYears(-1),
            _ => start.AddDays(-1),
        };
    }
}
