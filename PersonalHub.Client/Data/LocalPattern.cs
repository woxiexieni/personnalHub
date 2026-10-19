using SQLite;

namespace PersonalHub.Client.Data
{
    /// <summary>
    /// 本地「行为模式」。
    ///
    /// 四段对应 Habit Loop：触发（Cue）/ 旧反应（Routine）/ 新反应（替换 Routine）/ 保留的回报。
    ///
    /// 为什么它必须和"时间块"分开：块回答"时间去哪了"，一天 6~10 次、零打字；
    /// 模式回答"我对这类事的反应"，一季度两三个、要写四段。合表会互相污染。
    /// </summary>
    [Table("patterns")]
    public class LocalPattern : LocalRecordBase
    {
        [PrimaryKey]
        public override string Id { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        /// <summary>触发场景：什么情况下会发生。</summary>
        public string Trigger { get; set; } = string.Empty;

        /// <summary>旧自动反应：现在的默认行为。</summary>
        public string OldResponse { get; set; } = string.Empty;

        /// <summary>新反应：要练的那个动作（必填）。</summary>
        public string NewResponse { get; set; } = string.Empty;

        /// <summary>保留的回报：不打算消灭的那个好处。</summary>
        public string RewardKept { get; set; } = string.Empty;

        /// <summary>0 活跃 / 1 暂停 / 2 已自动化 / 3 放弃。</summary>
        public int Status { get; set; }

        public long CreatedAt { get; set; }

        public long UpdatedAt { get; set; }

        public override bool Deleted { get; set; }

        public override bool Synced { get; set; }

        public override long SyncedAt { get; set; }

        public override long Timestamp => UpdatedAt;

        [Ignore]
        public override long DayTimestamp => CreatedAt;
    }

    /// <summary>
    /// 本地「练习」：一次"守住了"或"又回去了"。
    ///
    /// 只有 result 一个业务字段。**不记情绪、不记强度、不写文字** ——
    /// 这是一天 0~1 次的动作，多一个字段就少一半点击。
    /// </summary>
    [Table("practices")]
    public class LocalPractice : LocalRecordBase
    {
        [PrimaryKey]
        public override string Id { get; set; } = string.Empty;

        public string PatternId { get; set; } = string.Empty;

        /// <summary>1 守住了（新反应）/ 0 又回去了（旧反应）。</summary>
        public int Result { get; set; }

        /// <summary>发生时刻。归因反查（前后 3 小时窗口找触发）完全依赖它。</summary>
        public long OccurredAt { get; set; }

        public long CreatedAt { get; set; }

        public long UpdatedAt { get; set; }

        public override bool Deleted { get; set; }

        public override bool Synced { get; set; }

        public override long SyncedAt { get; set; }

        public override long Timestamp => OccurredAt;

        [Ignore]
        public override long DayTimestamp => OccurredAt;
    }
}
