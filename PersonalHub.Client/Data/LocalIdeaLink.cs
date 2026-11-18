using SQLite;

namespace PersonalHub.Client.Data
{
    /// <summary>
    /// 本地「想法关系边」（R9）—— 想法库里的"这张卡连着哪张卡"。
    ///
    /// 只有两种边（豆包那七种砍完剩下的），刻意不再细分：
    ///   Kind 0 衍生 —— A 想到 B，方向有意义：A 卡显示「→ B」，B 卡显示「← 来自 A」。
    ///     推理链（MES 会被冲击 → 学具身智能）和发散联想（社保基数 → 养老）都用它。
    ///   Kind 1 矛盾 —— 对称：查询两边都查，显示不带方向。
    ///     两个想法打架是内耗的来源，摆在一起半年后自己就能裁决。
    ///
    /// from_id / to_id 都是软引用，没有外键：一端被作废/删除，边不跟着删 ——
    /// 关系也是认知资产。显示时目标查不到就标灰「已不在库」。
    ///
    /// R9 起整条上服务端同步（四件套），换机不丢。
    /// </summary>
    [Table("idea_links")]
    public class LocalIdeaLink : LocalRecordBase, IVersioned
    {
        [PrimaryKey]
        public override string Id { get; set; } = string.Empty;

        /// <summary>边的起点。矛盾边（Kind=1）无语义，只是记录方向。</summary>
        public string FromId { get; set; } = string.Empty;

        /// <summary>边的终点。</summary>
        public string ToId { get; set; } = string.Empty;

        /// <summary>0 衍生 / 1 矛盾。将来模式证据真关联加 2。</summary>
        public int Kind { get; set; }

        public long CreatedAt { get; set; }

        public long UpdatedAt { get; set; }

        public override bool Deleted { get; set; }

        public override bool Synced { get; set; }

        public override long SyncedAt { get; set; }

        public override long Timestamp => UpdatedAt;

        [Ignore]
        public override long DayTimestamp => CreatedAt;
    }
}
