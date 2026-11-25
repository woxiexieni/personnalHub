using SQLite;

namespace PersonalHub.Client.Data
{
    /// <summary>
    /// 钱包通知探针（R15，纯本地，只留最近 40 条）。
    ///
    /// 为什么需要它：自动记账只认"支付宝/微信发出来的通知"，而有些支付路径
    /// （典型是支付宝「碰一碰」）**根本不发通知**，或者只发标题不发正文。
    /// 这两种情况在 app 里看起来一模一样 —— "什么都没抓到"。
    /// 没有探针就只能靠猜，用户也没法回答"到底有没有通知"。
    ///
    /// 所以：凡是从支付宝/微信包名进来的通知，无论认没认出来，都先记一条。
    /// 认出来的进 pending_pays 等确认；认不出的留在这里，让你能看到原文长什么样。
    /// 正文只存本地、不上传、不打日志 —— 跟 pending_pays 一个口径。
    /// </summary>
    [Table("wallet_probes")]
    public class LocalWalletProbe : LocalRecordBase
    {
        [PrimaryKey]
        public override string Id { get; set; } = string.Empty;

        /// <summary>通知来源包名（com.eg.android.AlipayGphone / com.tencent.mm）。</summary>
        public string Package { get; set; } = string.Empty;

        /// <summary>归类："alipay" / "wechat"。</summary>
        public string Source { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        /// <summary>正文（多个字段拼起来的，截 300 字）。</summary>
        public string Text { get; set; } = string.Empty;

        /// <summary>这一条有没有被解析成待确认流水。</summary>
        public bool Parsed { get; set; }

        public long NotifiedAt { get; set; }

        // ---- 本表不走同步 ----
        public override bool Synced { get; set; }
        public override long SyncedAt { get; set; }
        public override bool Deleted { get; set; }
        public override long Timestamp => NotifiedAt;

        [Ignore]
        public override long DayTimestamp => NotifiedAt;
    }
}
