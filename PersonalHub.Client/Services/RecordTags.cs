namespace PersonalHub.Client.Services
{
    /// <summary>
    /// 事件卡 / 问题卡 共用的标签词表（单一事实来源）。
    ///
    /// 为什么只有一套：两套词表的下场是"事件记的是人际、卡建的是沟通"，
    /// 半年后自己对不上；共用一套之后，AI 路由时也多一个参照（事件带人际标签 →
    /// 命中人际问题卡的概率高）。
    ///
    /// 加标签只改这里：录入页胶囊、事件卡库筛选、问题卡建卡页、问题卡筛选都跟着变。
    /// 注意：已存进库里的老标签字符串不受影响——删词条不会改历史数据。
    /// </summary>
    public static class RecordTags
    {
        public static readonly string[] All =
        {
            "工作", "人际", "技术学习", "情绪内耗", "生活", "其他",
        };

        /// <summary>"待整理"是事件卡的兜底状态，不是标签——筛选行里单独摆一个入口。</summary>
        public const string PendingTitle = "（待整理）";
    }
}
