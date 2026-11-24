using Microsoft.Maui.Graphics;

namespace PersonalHub.Client.Services
{
    /// <summary>
    /// 片 → 色块的填充色（日历视图用）。
    ///
    /// 为什么放在这里而不挂在 Piece 上：色是**视图层**的事，片清单是数据层；
    /// 片定义不该背着一堆 UI 颜色到处走。
    ///
    /// 选色规则：
    ///   1. **中等饱和度的实色 + 白字**——浅色/深色主题下都能压住，块内文字用白
    ///      （深底浅字、浅底深字都容易翻车，白字压中饱和度实色是唯一两头都稳的组合）。
    ///   2. 同组色相相近、组间拉开：产出=蓝/青系，身体=橙/红系，消耗=品红……
    ///      扫一眼日历先看出"这周产出还是消耗多"。
    ///   3. 不认识的 code 给灰（历史上被合并掉的老片、手改库出现的怪 code 都有）。
    /// </summary>
    public static class PieceColors
    {
        private static readonly Dictionary<string, Color> Map = new(StringComparer.Ordinal)
        {
            // 产出：蓝青系
            ["code"]    = Color.FromArgb("#2F6FBF"),   // 写代码
            ["work"]    = Color.FromArgb("#3B8AB4"),   // 干活
            ["review"]  = Color.FromArgb("#6B5CA5"),   // 写复盘

            // 身体：橙红系
            ["sleep"]   = Color.FromArgb("#5B6B8C"),   // 睡觉（夜里，偏灰蓝）
            ["meal"]    = Color.FromArgb("#C96F2E"),   // 吃饭
            ["sport"]   = Color.FromArgb("#C24A4A"),   // 运动

            // 生活：土黄系
            ["commute"] = Color.FromArgb("#9A8A3E"),   // 通勤
            ["chore"]   = Color.FromArgb("#4E8C57"),   // 家务杂事

            // 学习：青
            ["learn"]   = Color.FromArgb("#2E8F8F"),   // 学习

            // 人际：粉
            ["people"]  = Color.FromArgb("#C25B8C"),   // 陪人

            // 消耗：品红
            ["entertain"] = Color.FromArgb("#A84FA8"), // 娱乐

            // 零散：灰
            ["idle"]    = Color.FromArgb("#8C8C8C"),   // 发呆
        };

        /// <summary>块填充色。别名与怪 code 都归一后查，兜底灰。</summary>
        public static Color For(string? code)
        {
            string canon = Pieces.Canonical(code);
            return Map.TryGetValue(canon, out var c) ? c : Color.FromArgb("#8C8C8C");
        }

        /// <summary>块内文字色：白，压任何填充色都稳。</summary>
        public static Color OnFill => Colors.White;
    }
}
