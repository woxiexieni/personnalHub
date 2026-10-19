using SQLite;

namespace personalHub.Server.Data
{
    /// <summary>
    /// patterns 表：一个行为模式的拆解。
    ///
    /// 四个业务字段对应 Habit Loop：
    ///   trigger      触发场景（Cue）
    ///   old_response 旧自动反应（Routine）
    ///   new_response 要练的新动作（替换 Routine）
    ///   reward_kept  保留的回报（不是要消灭的东西 —— 消灭回报一定会失败）
    ///
    /// 刻意没有的字段：
    ///   成功率 / 连续天数 —— 有比率就会有人不记失败那次；
    ///   改变阶段（TTM）—— 阶段是**推断**出来的，存成字段就会过期而没人更新。
    /// </summary>
    [Table("patterns")]
    public class PatternRow
    {
        [PrimaryKey, Column("id")]
        public string Id { get; set; } = string.Empty;

        [Column("title")]
        public string? Title { get; set; }

        [Column("trigger")]
        public string? Trigger { get; set; }

        [Column("old_response")]
        public string? OldResponse { get; set; }

        [Column("new_response")]
        public string? NewResponse { get; set; }

        [Column("reward_kept")]
        public string? RewardKept { get; set; }

        /// <summary>0 活跃 / 1 暂停 / 2 已自动化 / 3 放弃。</summary>
        [Column("status")]
        public int Status { get; set; }

        [Column("created_at")]
        public long CreatedAt { get; set; }

        [Column("updated_at")]
        public long UpdatedAt { get; set; }

        [Column("deleted")]
        public int Deleted { get; set; }
    }

    /// <summary>
    /// practices 表：一次练习。
    ///
    /// 只有 result 一个业务字段：1 = 守住了（用了新反应），0 = 又回去了（旧反应）。
    /// **不记情绪、不记强度、不写文字** —— 这是一天 0~1 次的动作，多一个字段就少一半点击。
    /// occurred_at 必须真实：归因反查（"旧反应 8/12 次发生在开会后两小时"）完全依赖它。
    /// </summary>
    [Table("practices")]
    public class PracticeRow
    {
        [PrimaryKey, Column("id")]
        public string Id { get; set; } = string.Empty;

        [Column("pattern_id")]
        public string? PatternId { get; set; }

        /// <summary>1 新反应 / 0 旧反应。</summary>
        [Column("result")]
        public int Result { get; set; }

        [Column("occurred_at")]
        public long OccurredAt { get; set; }

        [Column("created_at")]
        public long CreatedAt { get; set; }

        [Column("updated_at")]
        public long UpdatedAt { get; set; }

        [Column("deleted")]
        public int Deleted { get; set; }
    }
}
