using SQLite;

namespace PersonalHub.Client.Data
{
    /// <summary>
    /// 本地账单记录（离线优先的落点）。
    ///
    /// 与 proto 的 Personalhub.Record 字段一一对应，额外多一个 Synced 同步标记。
    /// 关键设计：**同步状态落在"每条记录"上，而不是给"整天"上锁**。
    /// 因为同一天可能分多次上传（比如晚上传了今天的，第二天补记一笔昨天的），
    /// 按天分组只是"打包单位"，不代表这天只能传一次。
    ///
    /// 约定：SpentAt / CreatedAt / SyncedAt 均为 Unix 毫秒时间戳。
    /// </summary>
    [Table("records")]
    public class LocalRecord
    {
        [PrimaryKey]
        public string Id { get; set; } = string.Empty;

        public double Amount { get; set; }

        public int CategoryId { get; set; }

        public string Note { get; set; } = string.Empty;

        /// <summary>消费时间（Unix 毫秒）。决定它归到哪一天。</summary>
        public long SpentAt { get; set; }

        public string PayMethod { get; set; } = string.Empty;

        /// <summary>本地创建时间（Unix 毫秒）。</summary>
        public long CreatedAt { get; set; }

        /// <summary>是否已同步到服务端。false = 待同步。</summary>
        public bool Synced { get; set; }

        /// <summary>同步成功时间（Unix 毫秒），0 表示从未同步。</summary>
        public long SyncedAt { get; set; }

        /// <summary>
        /// 本地自然日 yyyy-MM-dd，仅用于"按天分组"，不入库（[Ignore]）。
        /// </summary>
        [Ignore]
        public string DayKey =>
            DateTimeOffset.FromUnixTimeMilliseconds(SpentAt).LocalDateTime.ToString("yyyy-MM-dd");
    }
}
