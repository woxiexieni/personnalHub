using SQLite;

namespace personalHub.Server.Data
{
    /// <summary>
    /// idea_links 表的行模型（想法关系边），对应 proto 的 <see cref="Personalhub.IdeaLink"/>。
    ///
    /// 想法之间只有两种边：0 衍生（方向有意义）/ 1 矛盾（对称）。
    /// from_id / to_id 都是**软引用**，没有外键 —— 一端被作废/删除，边不跟着删
    /// （关系也是认知资产；显示时目标查不到就标灰，由客户端负责）。
    ///
    /// 时间字段统一 Unix 毫秒。
    /// </summary>
    [Table("idea_links")]
    public class IdeaLinkRow
    {
        /// <summary>业务主键（客户端生成的 Guid）。Upsert 幂等就靠它。</summary>
        [PrimaryKey, Column("id")]
        public string Id { get; set; } = string.Empty;

        /// <summary>边的起点。矛盾边（kind=1）无语义，只是记录方向。</summary>
        [Column("from_id")]
        public string FromId { get; set; } = string.Empty;

        /// <summary>边的终点。</summary>
        [Column("to_id")]
        public string ToId { get; set; } = string.Empty;

        /// <summary>0 衍生 / 1 矛盾。将来模式证据真关联加 2。</summary>
        [Column("kind")]
        public int Kind { get; set; }

        [Column("created_at")]
        public long CreatedAt { get; set; }

        [Column("updated_at")]
        public long UpdatedAt { get; set; }

        /// <summary>软删除标记：0 = 正常，1 = 已删除。</summary>
        [Column("deleted")]
        public int Deleted { get; set; }
    }
}
