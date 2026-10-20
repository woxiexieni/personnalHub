using SQLite;

namespace PersonalHub.Client.Data
{
    /// <summary>
    /// 本地「想法」。
    ///
    /// 定位：**原材料仓库**。它是这个系统里唯一一个"只进不出、不要求任何后续动作"的表。
    ///   账单     → 钱出去了（事实，有金额）
    ///   时间块   → 刚才那段时间去哪了（事实，有时长）
    ///   事件反思 → 一件事值得深挖（9 段，低频深写）
    ///   行为模式 → 要练的那一类反应（一季度两三个）
    ///   想法     → 还没成形的东西（可能永远不成形）
    ///
    /// 为什么它必须单独成表，而不是塞进事件反思：
    /// 事件反思是"这件事已经发生了、我要复盘它"，想法是"我脑子里刚冒出个东西、还不知道算不算事"。
    /// 合成一张表，就会要求每条想法先回答"这算什么事"，于是人就干脆不记了。
    ///
    /// Kind 允许 0（未分类）：**默认就是 0**。分类是事后整理的动作，不是记录的动作。
    /// </summary>
    [Table("ideas")]
    public class LocalIdea : LocalRecordBase
    {
        [PrimaryKey]
        public override string Id { get; set; } = string.Empty;

        /// <summary>正文。不拆标题 —— 拆开就要求"先概括再记录"。</summary>
        public string Text { get; set; } = string.Empty;

        /// <summary>0 未分类 / 1 事实 / 2 评价 / 3 计划行动 / 4 想象脑补。</summary>
        public int Kind { get; set; }

        /// <summary>「转成目标」之后回填；空 = 还没转化。它只做标记，不代表想法"完成了"。</summary>
        public string GoalId { get; set; } = string.Empty;

        public long CreatedAt { get; set; }

        public long UpdatedAt { get; set; }

        public override bool Deleted { get; set; }

        public override bool Synced { get; set; }

        public override long SyncedAt { get; set; }

        /// <summary>想法按创建时间归档：它没有"发生时刻"这个概念，冒出来就是那时候。</summary>
        public override long Timestamp => CreatedAt;

        [Ignore]
        public override long DayTimestamp => CreatedAt;
    }
}
