using SQLite;

namespace personalHub.Server.Data
{
    /// <summary>
    /// things 表的行模型（SQLite 持久化实体），对应 proto 的 <see cref="Personalhub.Thing"/>。
    ///
    /// 和 <see cref="RecordRow"/> 一样的思路：表模型独立于 proto 生成类，
    /// 协议改了不影响历史数据，存储改了不影响客户端。
    /// 时间字段统一 Unix 毫秒。
    /// </summary>
    [Table("things")]
    public class ThingRow
    {
        /// <summary>业务主键（客户端生成的 Guid）。Upsert 幂等就靠它。</summary>
        [PrimaryKey, Column("id")]
        public string Id { get; set; } = string.Empty;

        [Column("title")]
        public string? Title { get; set; }

        [Column("tag")]
        public string? Tag { get; set; }

        [Column("emotion")]
        public string? Emotion { get; set; }

        /// <summary>事件发生时间（Unix 毫秒）。决定它归到哪一天。</summary>
        [Column("occurred_at")]
        public long OccurredAt { get; set; }

        [Column("objective_description")]
        public string? ObjectiveDescription { get; set; }

        [Column("initial_thought")]
        public string? InitialThought { get; set; }

        [Column("action_taken")]
        public string? ActionTaken { get; set; }

        [Column("final_result")]
        public string? FinalResult { get; set; }

        [Column("reflection")]
        public string? Reflection { get; set; }

        [Column("action_plan")]
        public string? ActionPlan { get; set; }

        /// <summary>客户端创建时间（Unix 毫秒）。</summary>
        [Column("created_at")]
        public long CreatedAt { get; set; }
    }
}
