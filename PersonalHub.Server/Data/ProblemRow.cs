using SQLite;

namespace personalHub.Server.Data
{
    /// <summary>
    /// problems 表的行模型（问题卡），对应 proto 的 <see cref="Personalhub.Problem"/>。
    ///
    /// 与 <see cref="ThingRow"/> 的关系：事件卡是**事实**，问题卡是**欠的债**。
    /// 事件卡通过 things.problem_id 软引用问题卡（没有外键）——
    /// 删掉一张问题卡不该连带删掉它下面的事实记录。
    ///
    /// 时间字段统一 Unix 毫秒。
    /// </summary>
    [Table("problems")]
    public class ProblemRow
    {
        /// <summary>业务主键（客户端生成的 Guid）。Upsert 幂等就靠它。</summary>
        [PrimaryKey, Column("id")]
        public string Id { get; set; } = string.Empty;

        [Column("title")]
        public string? Title { get; set; }

        /// <summary>判据：怎么算解决。闸 1 —— 写不出判据就不该建卡。</summary>
        [Column("criteria")]
        public string? Criteria { get; set; }

        /// <summary>判据是否经用户亲手确认（AI 起草的卡默认未确认）。</summary>
        [Column("criteria_confirmed")]
        public int CriteriaConfirmed { get; set; }

        /// <summary>当前试法。可空 = 观察中（先攒证据，不急着定招）。</summary>
        [Column("attempt")]
        public string? Attempt { get; set; }

        [Column("attempt_started_at")]
        public long AttemptStartedAt { get; set; }

        /// <summary>换招次数。评估"没用，换招"时 +1。</summary>
        [Column("attempt_count")]
        public int AttemptCount { get; set; }

        /// <summary>0 进行中 / 1 已解决 / 2 放弃。</summary>
        [Column("status")]
        public int Status { get; set; }

        /// <summary>建卡来源：manual / event / pattern。</summary>
        [Column("source")]
        public string? Source { get; set; }

        [Column("tag")]
        public string? Tag { get; set; }

        /// <summary>关卡时的一句话（怎么解决的 / 为什么放弃）。</summary>
        [Column("closing_note")]
        public string? ClosingNote { get; set; }

        [Column("created_at")]
        public long CreatedAt { get; set; }

        [Column("updated_at")]
        public long UpdatedAt { get; set; }

        /// <summary>关卡/放弃时刻；0 = 还开着。</summary>
        [Column("closed_at")]
        public long ClosedAt { get; set; }

        /// <summary>软删除标记：0 = 正常，1 = 已删除。</summary>
        [Column("deleted")]
        public int Deleted { get; set; }
    }
}
