using SQLite;

namespace PersonalHub.Client.Data
{
    /// <summary>
    /// 本地「问题卡」—— 控制环里"向前看的欠账"层。
    ///
    /// 和「行为模式」的分工：模式回答"我总是这样"（向后看的规律），
    /// 问题卡回答"这次要改什么"（向前看的债）。模式是事件卡的影子（自动长），
    /// 问题卡是从影子里挑出来要还的债（要确认 + 判据）。
    ///
    /// 闸 1：Criteria 写不出"怎么算解决"就不许建 —— 拦掉永远关不掉的伪问题。
    /// CriteriaConfirmed = 用户亲手写/改过判据。AI 代起草的卡默认 false，
    /// 未确认的卡列表里挂红提醒，提示"本卡未生效"。
    ///
    /// v1 纯本地，不进服务端同步链路（没注册 uploader / sync service）——
    /// 服务端保持无状态。要跨机统计证据时（v2）再走 goals 那套三件套。
    /// </summary>
    [Table("problems")]
    public class LocalProblem : LocalRecordBase, IVersioned
    {
        [PrimaryKey]
        public override string Id { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        /// <summary>解决判据：怎么才算解决了。一句话，必填。</summary>
        public string Criteria { get; set; } = string.Empty;

        /// <summary>判据是否经用户亲手写/改（闸 1 的软件化）。false = AI 起草未确认。</summary>
        public bool CriteriaConfirmed { get; set; }

        /// <summary>当前试法（环的"处理"格）。这招没用不等于我没用 —— 换招，环再转。</summary>
        public string Attempt { get; set; } = string.Empty;

        /// <summary>当前试法开始时刻（Unix 毫秒）。换招时刷新 —— 统计"这招跑了几天"。</summary>
        public long AttemptStartedAt { get; set; }

        /// <summary>换招次数。评估"没用，换招"时 +1。关卡时它就是"换过几回"的战绩。</summary>
        public int AttemptCount { get; set; }

        /// <summary>0 进行中 / 1 已解决 / 2 放弃。</summary>
        public int Status { get; set; }

        /// <summary>建卡来源：manual 手动 / event 事件卡 AI 提议 / pattern 模式页转来。</summary>
        public string Source { get; set; } = string.Empty;

        /// <summary>
        /// 分类标签，可空。词表与事件卡共用（Services/RecordTags.All）——
        /// 只做一层，不做子分类：问题卡是"欠债型"数据，靠归档区就够，不需要目录学。
        /// </summary>
        public string Tag { get; set; } = string.Empty;

        /// <summary>关卡时的一句话（怎么解决的/为什么放弃），可空。</summary>
        public string ClosingNote { get; set; } = string.Empty;

        public long CreatedAt { get; set; }

        public long UpdatedAt { get; set; }

        /// <summary>关卡/放弃时刻（Unix 毫秒）。0 = 还开着。</summary>
        public long ClosedAt { get; set; }

        public override bool Deleted { get; set; }

        public override bool Synced { get; set; }

        public override long SyncedAt { get; set; }

        public override long Timestamp => UpdatedAt;

        [Ignore]
        public override long DayTimestamp => CreatedAt;
    }
}
