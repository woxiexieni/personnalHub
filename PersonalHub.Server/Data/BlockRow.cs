using SQLite;

namespace personalHub.Server.Data
{
    /// <summary>
    /// blocks 表的行模型（时间块），对应 proto 的 <see cref="Personalhub.Block"/>。
    ///
    /// 时间字段统一 Unix 毫秒。软删用 int（0/1），SQLite 没有 bool，
    /// 用 int 是为了让 `WHERE deleted = 0` 在 SQL 里可读，也避免 sqlite-net 的 bool 映射差异。
    ///
    /// R2 就把 R2.5 / R3 / R4 要用的列全部建好（started_at / ended_at / worth / lesson /
    /// plan_id / anomaly / mood），后续版本只加界面和接口，不再动表结构 ——
    /// 每次动表都要写迁移，而迁移是"只在老库上出错"的那类问题，越少越好。
    /// </summary>
    [Table("blocks")]
    public class BlockRow
    {
        [PrimaryKey, Column("id")]
        public string Id { get; set; } = string.Empty;

        /// <summary>事情片代号。生成后不改（统计要对得上历史），显示名可改。</summary>
        [Column("piece_code")]
        public string? PieceCode { get; set; }

        /// <summary>归属时刻（Unix 毫秒）。决定它算哪一天。</summary>
        [Column("occurred_at")]
        public long OccurredAt { get; set; }

        /// <summary>时长（分钟）。0 = 没填，合法（半条记录）。</summary>
        [Column("duration_min")]
        public int DurationMin { get; set; }

        /// <summary>归属目标；空字符串 = 没归。</summary>
        [Column("goal_id")]
        public string? GoalId { get; set; }

        [Column("note")]
        public string? Note { get; set; }

        /// <summary>0 未评 / 1 值得 / 2 一般 / 3 别做（R3 复盘用）。</summary>
        [Column("worth")]
        public int Worth { get; set; }

        [Column("lesson")]
        public string? Lesson { get; set; }

        [Column("plan_id")]
        public string? PlanId { get; set; }

        /// <summary>0 正常 / 1 跨天或超长待确认（R2.5 计时用）。</summary>
        [Column("anomaly")]
        public int Anomaly { get; set; }

        /// <summary>本版不采集，字段先留着。</summary>
        [Column("mood")]
        public int Mood { get; set; }

        [Column("started_at")]
        public long StartedAt { get; set; }

        [Column("ended_at")]
        public long EndedAt { get; set; }

        [Column("created_at")]
        public long CreatedAt { get; set; }

        /// <summary>最后修改时间（Unix 毫秒）。版本比较用。</summary>
        [Column("updated_at")]
        public long UpdatedAt { get; set; }

        /// <summary>软删除标记：0 正常 / 1 已删除。</summary>
        [Column("deleted")]
        public int Deleted { get; set; }
    }
}
