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

        /// <summary>0 待办 / 1 进行中 / 2 完成 / 3 放弃 / 4 阻塞。</summary>
        public int Status { get; set; }

        /// <summary>截止日期（当天零点时间戳毫秒）；0 = 没设。超期只标红提醒，不拦操作。</summary>
        public long Deadline { get; set; }

        /// <summary>阻塞/放弃原因（按 Status 区分语义）；0 状态下为空。</summary>
        public string Reason { get; set; } = string.Empty;

        /// <summary>成果链接（obsidian:// 等 URI）；空 = 没填。完成态有值时状态列变「打开成果」按钮。</summary>
        public string ArtifactUri { get; set; } = string.Empty;

        /// <summary>切到「完成」的时刻；0 = 未完成。</summary>
        public long CompletedAt { get; set; }

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
