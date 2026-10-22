using System;
using System.Collections.Generic;
using System.Linq;

namespace personalHub.Server.Data
{
    /// <summary>一个事情片的定义（服务端副本）。</summary>
    public sealed record PieceDef(string Code, string Name, string Group, string Emoji);

    /// <summary>
    /// 事情片清单 —— **与客户端 <c>PersonalHub.Client/Services/Pieces.cs</c> 逐条对应**。
    ///
    /// 为什么服务端要抄一份：管理端要显示"写代码 8 小时"，而库里只存了 <c>code</c>。
    /// 抄一份的代价是"两边要同步改"，但这个清单本身是**冻结的**（需求 §4.1：code 生成后永不修改），
    /// 加片时才需要两边各加一行；比起把片名也存进库（那样改名会让历史统计断裂）更安全。
    ///
    /// 客户端可以在手机上给片改名，那个名字只存在本机 Preferences 里 ——
    /// 所以管理端显示的是**默认名**。这是刻意的：一个片在电脑上叫"写代码"，在手机上
    /// 你改成"敲代码"，两边显示不同不要紧，**统计靠 code 对得上**才是要害。
    /// </summary>
    public static class Pieces
    {
        /// <summary>组的显示顺序（与客户端一致）。</summary>
        public static readonly string[] GroupOrder =
        {
            "产出", "身体", "生活", "学习", "人际", "消耗", "零散"
        };

        public static readonly IReadOnlyList<PieceDef> All = new List<PieceDef>
        {
            new("code",      "写代码",   "产出", "⌨"),
            new("work",      "干活",     "产出", "✎"),
            new("review",    "写复盘",   "产出", "☆"),

            new("sleep",     "睡觉",     "身体", "☾"),
            new("meal",      "吃饭",     "身体", "○"),
            new("sport",     "运动",     "身体", "▲"),

            new("commute",   "通勤",     "生活", "→"),
            new("chore",     "家务杂事", "生活", "■"),

            new("learn",     "学习",     "学习", "◈"),

            new("people",    "陪人",     "人际", "♥"),

            new("entertain", "娱乐",     "消耗", "▷"),

            new("idle",      "发呆",     "零散", "…"),
        };

        /// <summary>
        /// 旧片代号 → 现在的片（与客户端同名表逐条一致）。
        ///
        /// 库里存的是历史 code，合并片之后不可能回去改数据（改了等于篡改历史，
        /// 而且离线设备上的未同步记录还会把旧 code 再传上来）。
        /// 所以读的时候归一：nap 显示成「睡觉」、doc 显示成「干活」。
        /// </summary>
        public static readonly IReadOnlyDictionary<string, string> Alias =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["nap"]      = "sleep",
                ["doc"]      = "work",
                ["meeting"]  = "work",
                ["tidy"]     = "chore",
                ["errand"]   = "chore",
                ["book"]     = "learn",
                ["english"]  = "learn",
                ["leetcode"] = "learn",
                ["chat"]     = "people",
                ["family"]   = "people",
                ["dinner"]   = "people",
                ["video"]    = "entertain",
                ["game"]     = "entertain",
                ["show"]     = "entertain",
                ["scroll"]   = "entertain",
            };

        /// <summary>任意历史 code → 现在的 code。都不认识就原样返回。</summary>
        public static string Canonical(string? code)
        {
            if (string.IsNullOrEmpty(code)) return string.Empty;
            if (All.Any(p => string.Equals(p.Code, code, StringComparison.Ordinal))) return code;
            return Alias.TryGetValue(code, out var now) ? now : code;
        }

        /// <summary>时长档 —— 与客户端同一组数字（45 / 90 是真实主力档，150 不在档上）。</summary>
        public static readonly int[] Durations = { 5, 15, 30, 45, 60, 90, 120, 180 };

        private static readonly Dictionary<string, PieceDef> Map =
            All.ToDictionary(p => p.Code, StringComparer.Ordinal);

        /// <summary>
        /// 片是否合法。**旧代号也算合法** —— 手机上还装着旧版客户端时，
        /// 它发上来的 nap 不该被拒（拒了就是一条数据凭空消失）。
        /// </summary>
        public static bool Exists(string? code)
            => !string.IsNullOrEmpty(code) && Map.ContainsKey(Canonical(code));

        /// <summary>code → 默认名。未知 code（老数据 / 手写错）原样返回，绝不显示成"未知片"以外的假名字。</summary>
        public static string Name(string? code)
            => !string.IsNullOrEmpty(code) && Map.TryGetValue(Canonical(code), out var p) ? p.Name : (code ?? "未填片");

        public static string Group(string? code)
            => !string.IsNullOrEmpty(code) && Map.TryGetValue(Canonical(code), out var p) ? p.Group : "其他";

        public static string Emoji(string? code)
            => !string.IsNullOrEmpty(code) && Map.TryGetValue(Canonical(code), out var p) ? p.Emoji : "·";

        /// <summary>分钟 → "3 小时 20 分"。管理端所有时长都走这个，避免各处写法不一。</summary>
        public static string Minutes(int minutes)
        {
            if (minutes <= 0) return "0 分";
            if (minutes < 60) return minutes + " 分";

            int h = minutes / 60, m = minutes % 60;
            return m == 0 ? h + " 小时" : h + " 小时 " + m + " 分";
        }
    }
}
