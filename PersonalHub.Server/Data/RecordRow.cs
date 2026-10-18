using SQLite;

namespace personalHub.Server.Data
{
    /// <summary>
    /// records 表的行模型（SQLite 持久化实体）。
    ///
    /// 刻意不复用 protobuf 生成的 <see cref="Personalhub.Record"/>：
    /// 生成类由 .proto 决定、字段可能随协议演进，直接当表模型会让"协议"和"存储"耦死。
    /// 这里用一张独立的扁平表，映射在 <see cref="RecordStore"/> 里做，两边各自演进。
    /// </summary>
    [Table("records")]
    public class RecordRow
    {
        /// <summary>业务主键（服务端补的 Guid，或客户端生成的 id）。Upsert 幂等就靠它。</summary>
        [PrimaryKey, Column("id")]
        public string Id { get; set; } = string.Empty;

        [Column("amount")]
        public double Amount { get; set; }

        [Column("category_id")]
        public int CategoryId { get; set; }

        [Column("note")]
        public string? Note { get; set; }

        /// <summary>Unix 毫秒时间戳（与客户端、proto 约定一致）。</summary>
        [Column("spent_at")]
        public long SpentAt { get; set; }

        [Column("pay_method")]
        public string? PayMethod { get; set; }

        /// <summary>Unix 毫秒时间戳。</summary>
        [Column("created_at")]
        public long CreatedAt { get; set; }

        // ---------------- R1 新增列 ----------------

        /// <summary>0 = 支出，1 = 收入。老数据默认 0（迁移时 ALTER 带 DEFAULT 0）。</summary>
        [Column("direction")]
        public int Direction { get; set; }

        /// <summary>最后修改时间（Unix 毫秒）。用于「谁更新」的比较，避免旧客户端覆盖新数据。</summary>
        [Column("updated_at")]
        public long UpdatedAt { get; set; }

        /// <summary>软删除标记：0 = 正常，1 = 已删除。所有查询默认过滤 1。</summary>
        [Column("deleted")]
        public int Deleted { get; set; }
    }
}
