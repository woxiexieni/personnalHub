using SQLite;

namespace PersonalHub.Client.Data
{
    /// <summary>
    /// 目标小项。它和 ideas 分开存：ideas 是原料，GoalStep 是目标内的任务。
    /// </summary>
    [Table("goal_steps")]
    public class LocalGoalStep : LocalRecordBase, IVersioned
    {
        [PrimaryKey]
        public override string Id { get; set; } = string.Empty;

        public string GoalId { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        /// <summary>0 未开始 / 1 进行中 / 2 完成 / 3 放弃。</summary>
        public int Status { get; set; }

        /// <summary>默认事情片；实际时间块可以改。</summary>
        public string PieceCode { get; set; } = string.Empty;

        /// <summary>预计分钟；0 = 没估。</summary>
        public int EstimateMin { get; set; }

        /// <summary>可选：从哪条想法转为小项。</summary>
        public string SourceIdeaId { get; set; } = string.Empty;

        public int SortOrder { get; set; }

        public long CreatedAt { get; set; }

        public long UpdatedAt { get; set; }

        public override bool Deleted { get; set; }

        public override bool Synced { get; set; }

        public override long SyncedAt { get; set; }

        public override long Timestamp => CreatedAt;

        [Ignore]
        public override long DayTimestamp => CreatedAt;
    }

}
