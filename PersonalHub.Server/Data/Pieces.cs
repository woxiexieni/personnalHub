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
            "产出", "学习", "身体", "生活", "人际", "消耗", "零散"
        };

        public static readonly IReadOnlyList<PieceDef> All = new List<PieceDef>
        {
            new("code",     "写代码",       "产出", "⌨"),
            new("doc",      "写文档",       "产出", "✎"),
            new("meeting",  "开会",         "产出", "◷"),

            new("learn",    "学技术",       "学习", "◈"),
            new("leetcode", "刷题",         "学习", "◇"),
            new("book",     "读专业书",     "学习", "▤"),
            new("english",  "学英语",       "学习", "A"),

            new("sport",    "运动",         "身体", "▲"),
            new("nap",      "午睡",         "身体", "☾"),
            new("meal",     "吃饭",         "身体", "○"),

            new("commute",  "通勤",         "生活", "→"),
            new("chore",    "家务",         "生活", "■"),
            new("tidy",     "收拾",         "生活", "□"),
            new("errand",   "杂事",         "生活", "·"),

            new("chat",     "聊天",         "人际", "◍"),
            new("family",   "陪家人",       "人际", "♥"),
            new("dinner",   "聚餐",         "人际", "◎"),

            new("video",    "短视频",       "消耗", "▷"),
            new("game",     "游戏",         "消耗", "◆"),
            new("show",     "看剧",         "消耗", "▣"),
            new("scroll",   "无目的刷手机", "消耗", "▤"),

            new("idle",     "发呆",         "零散", "…"),
            new("review",   "写复盘",       "零散", "☆"),
        };

        /// <summary>时长档 —— 与客户端同一组数字（45 / 90 是真实主力档，150 不在档上）。</summary>
        public static readonly int[] Durations = { 5, 15, 30, 45, 60, 90, 120, 180 };

        private static readonly Dictionary<string, PieceDef> Map =
            All.ToDictionary(p => p.Code, StringComparer.Ordinal);

        public static bool Exists(string? code)
            => !string.IsNullOrEmpty(code) && Map.ContainsKey(code);

        /// <summary>code → 默认名。未知 code（老数据 / 手写错）原样返回，绝不显示成"未知片"以外的假名字。</summary>
        public static string Name(string? code)
            => !string.IsNullOrEmpty(code) && Map.TryGetValue(code, out var p) ? p.Name : (code ?? "未填片");

        public static string Group(string? code)
            => !string.IsNullOrEmpty(code) && Map.TryGetValue(code, out var p) ? p.Group : "其他";

        public static string Emoji(string? code)
            => !string.IsNullOrEmpty(code) && Map.TryGetValue(code, out var p) ? p.Emoji : "·";

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
