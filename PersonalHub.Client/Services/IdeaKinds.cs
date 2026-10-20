namespace PersonalHub.Client.Services
{
    /// <summary>
    /// 想法的五种类型（含"未分类"）—— 录入页、列表页、管理端共用同一份定义。
    ///
    /// 为什么要有这个类：标签文案和颜色散在两个页面里，第一次改就会不一致
    /// （录入页写"计划行动"，列表页写"计划"），而这种不一致最难被当成 bug 提出来。
    ///
    /// 类型的用途（见需求 §22）：
    ///   事实类   → 可核对的记录，将来能拿来算
    ///   评价类   → 自我判断 / 对他人的判断，是"行为模式"最主要的原料
    ///   计划行动 → 可以转成目标或每日计划
    ///   想象脑补 → 反复出现的担忧/假设，值得单独看，但不该驱动决策
    /// 所以 kind 不是装饰：它是后面"挑哪一类去加工"的入口。
    /// </summary>
    public static class IdeaKinds
    {
        public const int Unclassified = 0;
        public const int Fact = 1;
        public const int Evaluation = 2;
        public const int Plan = 3;
        public const int Imagination = 4;

        /// <summary>类型定义。Hint 是给用户看的判据 —— 光给四个词没人知道自己该点哪个。</summary>
        public static readonly (int Kind, string Label, string Hint)[] All =
        {
            (Unclassified, "未分类", "先记下。以后想分再分，不选也不会丢任何东西。"),
            (Fact,         "事实",   "发生了什么、能核对的东西。「今天开了 3 小时会」"),
            (Evaluation,   "评价",   "好 / 坏、应该 / 不应该的判断。「我真笨」「他根本不听」"),
            (Plan,         "计划行动", "打算做但还没做的事。「下周把简历改一版」"),
            (Imagination,  "脑补",   "想象、担忧、假设。「要是当时答应了会怎样」"),
        };

        public static string LabelOf(int kind)
        {
            foreach (var k in All)
            {
                if (k.Kind == kind) return k.Label;
            }

            return "未分类";
        }

        public static string HintOf(int kind)
        {
            foreach (var k in All)
            {
                if (k.Kind == kind) return k.Hint;
            }

            return All[0].Hint;
        }

        /// <summary>列表里的类型色。只用来"一眼分得开"，不做任何语义评价。</summary>
        public static Color ColorOf(int kind) => kind switch
        {
            Fact => Color.FromArgb("#185FA5"),
            Evaluation => Color.FromArgb("#993C1D"),
            Plan => Color.FromArgb("#3B6D11"),
            Imagination => Color.FromArgb("#534AB7"),
            _ => Color.FromArgb("#8A8A8E"),
        };
    }
}
