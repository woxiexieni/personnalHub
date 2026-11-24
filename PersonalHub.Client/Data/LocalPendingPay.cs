using SQLite;

namespace PersonalHub.Client.Data
{
    /// <summary>
    /// 自动记账的「待确认流水」（R12，纯本地，不入同步四件套）。
    ///
    /// 来源：Android 通知监听服务抓到的支付通知（支付宝 / 微信支付）。
    /// 设计立场：**机器干苦力（抠金额），人只做确认** —— 解析错了不直接进账本，
    /// 而是躺在这个队列里等用户点「记一笔」或「忽略」。
    /// 错记比漏记伤（与账本"能改不删"的口径一致），所以不提供静默入账。
    ///
    /// 不上服务端的理由：消费完成的那一刻用户就在手机前，待确认队列的
    /// 生命周期只有几秒到几分钟，换机恢复它毫无意义 —— 恢复出来的"待确认"
    /// 早就过期了。确认后生成的正式 LocalRecord 才走同步链路。
    /// </summary>
    [Table("pending_pays")]
    public class LocalPendingPay : LocalRecordBase
    {
        [PrimaryKey]
        public override string Id { get; set; } = string.Empty;

        /// <summary>金额（正数，元）。方向另存 Direction，不靠符号。</summary>
        public double Amount { get; set; }

        /// <summary>0 = 支出 / 1 = 收入（与 LocalRecord.Direction 同口径）。</summary>
        public int Direction { get; set; }

        /// <summary>来源："alipay" / "wechat"。显示时翻成中文名。</summary>
        public string Source { get; set; } = string.Empty;

        /// <summary>原始通知文本。纯本地排查用，绝不上传、绝不打日志。</summary>
        public string RawText { get; set; } = string.Empty;

        /// <summary>通知到达时刻（Unix 毫秒）。分组和过期都按它算。</summary>
        public long NotifiedAt { get; set; }

        /// <summary>
        /// 0 = 待确认 / 1 = 已用（点过「记一笔」，金额已填进表单） / 2 = 已忽略。
        /// 没有"已保存"状态：确认动作只负责预填，真正入账以用户点「保存」为准。
        /// </summary>
        public int Status { get; set; }

        // ---- LocalRecordBase 必须实现的部分（本表不走同步，恒为默认值）----

        public override bool Synced { get; set; }
        public override long SyncedAt { get; set; }

        /// <summary>恒 false：本表不用软删墓碑，过期清理直接物理删。</summary>
        public override bool Deleted { get; set; }

        /// <summary>排序用时间 = 通知到达时刻。</summary>
        public override long Timestamp => NotifiedAt;

        [Ignore]
        public override long DayTimestamp => NotifiedAt;
    }
}
