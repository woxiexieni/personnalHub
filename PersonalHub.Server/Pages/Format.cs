using System;
using System.Globalization;
using personalHub.Server.Analysis;

namespace PersonalHub.Server.Pages
{
    /// <summary>
    /// 页面显示/输入的格式转换。
    ///
    /// 时间一律按 UTC+8 呈现和解析（与服务端统计口径同一套），
    /// 不用 DateTime.Now / 服务器本地时区 —— 否则同一笔账在页面上会"跑到隔壁那天"。
    /// </summary>
    public static class Fmt
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>分 → "¥1,234.56"。</summary>
        public static string Yuan(long cents)
            => "¥" + (cents / 100.0).ToString("N2", Inv);

        /// <summary>分 → "1,234.56"（表格里不带符号，避免整列都是 ¥ 显得吵）。</summary>
        public static string Amount(long cents)
            => (cents / 100.0).ToString("N2", Inv);

        public static string Amount(double yuan)
            => yuan.ToString("N2", Inv);

        /// <summary>0.345 → "35"（给 style="width:35%" 用，不能带小数点后多余的零）。</summary>
        public static string Pct(double ratio)
            => ((int)Math.Round(ratio * 100, MidpointRounding.AwayFromZero)).ToString(Inv);

        /// <summary>0.123 → "+12.3%"。没有上期数据时调用方要自己显示「—」。</summary>
        public static string Delta(double ratio)
            => (ratio >= 0 ? "+" : string.Empty) + (ratio * 100).ToString("F1", Inv) + "%";

        /// <summary>Unix 毫秒 → "10-18"（列表里省地方）。</summary>
        public static string ShortDate(long ms)
            => DateTimeOffset.FromUnixTimeMilliseconds(ms).ToOffset(Period.Cst).ToString("MM-dd", Inv);

        /// <summary>Unix 毫秒 → "2026-10-18"。</summary>
        public static string Date(long ms)
            => DateTimeOffset.FromUnixTimeMilliseconds(ms).ToOffset(Period.Cst).ToString("yyyy-MM-dd", Inv);

        /// <summary>Unix 毫秒 → "2026-10-18 21:30"。</summary>
        public static string DateTimeText(long ms)
            => DateTimeOffset.FromUnixTimeMilliseconds(ms).ToOffset(Period.Cst).ToString("yyyy-MM-dd HH:mm", Inv);

        /// <summary>Unix 毫秒 → input[type=datetime-local] 的 value 格式。</summary>
        public static string ToLocalInput(long ms)
            => DateTimeOffset.FromUnixTimeMilliseconds(ms).ToOffset(Period.Cst).ToString("yyyy-MM-ddTHH:mm", Inv);

        /// <summary>input[type=date] 的 value 格式。</summary>
        public static string ToDateInput(long ms)
            => DateTimeOffset.FromUnixTimeMilliseconds(ms).ToOffset(Period.Cst).ToString("yyyy-MM-dd", Inv);

        /// <summary>input[type=datetime-local] / [type=date] → Unix 毫秒；解析失败返回 0。</summary>
        public static long ParseLocal(string? text, bool dateOnly = false)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;

            string s = text.Trim().Replace(' ', 'T');
            var styles = DateTimeStyles.AllowWhiteSpaces;
            if (dateOnly) styles |= DateTimeStyles.AssumeUniversal;

            if (!DateTime.TryParse(s, Inv, styles, out var dt)) return 0;

            // AssumeUniversal 在 date-only 时会给出 Kind=Utc，这里统一按"东八区的墙上时间"解释
            var local = DateTime.SpecifyKind(dt, DateTimeKind.Unspecified);
            return new DateTimeOffset(local, Period.Cst).ToUnixTimeMilliseconds();
        }

        /// <summary>方向 → 「支出 / 收入」。</summary>
        public static string Direction(int direction) => direction == 1 ? "收入" : "支出";
    }
}
