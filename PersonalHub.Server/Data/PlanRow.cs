using SQLite;

namespace personalHub.Server.Data
{
    /// <summary>
    /// plans 表的行模型（每日计划），对应 proto 的 <see cref="Personalhub.Plan"/>。
    ///
    /// 刻意只有四样业务字段：day / piece_code / budget_min / goal_id。
    ///
    /// `day` 用字符串而不是时间戳：计划天然是"某一天"的概念，存时间戳就得回答
    /// "用谁的时区、几点算跨天"，而这些在时间块那边已经有一个固定答案（UTC+8 自然日）。
    /// 存 yyyy-MM-dd 之后，两边不可能对不上。
    ///
    /// 没有 status / 没有完成标记：**达成率是算出来的**（当天同片的时长 ÷ 预算），
    /// 存一个"已完成"就必然会有人手动勾、然后忘了勾，数字就废了。
    /// </summary>
    [Table("plans")]
    public class PlanRow
    {
        [PrimaryKey, Column("id")]
        public string Id { get; set; } = string.Empty;

        /// <summary>yyyy-MM-dd（UTC+8 自然日）。</summary>
        [Column("day")]
        public string? Day { get; set; }

        [Column("piece_code")]
        public string? PieceCode { get; set; }

        /// <summary>预算分钟；0 = 没设，合法。</summary>
        [Column("budget_min")]
        public int BudgetMin { get; set; }

        [Column("goal_id")]
        public string? GoalId { get; set; }

        [Column("created_at")]
        public long CreatedAt { get; set; }

        [Column("updated_at")]
        public long UpdatedAt { get; set; }

        [Column("deleted")]
        public int Deleted { get; set; }
    }
}
