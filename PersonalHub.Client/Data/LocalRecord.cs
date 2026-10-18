using SQLite;

namespace PersonalHub.Client.Data
{
    /// <summary>
    /// 所有本地记录的公共基类。
    /// 提供主键、同步状态、按天分组所需的时间戳。
    /// </summary>
    public abstract class LocalRecordBase
    {
        [PrimaryKey]
        public abstract string Id { get; set; } 

        /// <summary>是否已同步到服务端。false = 待同步。</summary>
        public abstract bool Synced { get; set; }

        /// <summary>同步成功时间（Unix 毫秒），0 表示从未同步。</summary>
        public abstract long SyncedAt { get; set; }

        /// <summary>用于按天分组的时间戳（Unix 毫秒）。子类必须指定自己的时间字段。
        /// LocalRecordStore的按天查询功能
        /// 
        /// </summary>
        public abstract long Timestamp { get; }

        /// <summary>本地自然日 yyyy-MM-dd，仅用于"按天分组"，不入库。
        ///
        /// 默认用 <see cref="Timestamp"/> 换算；如果"分组用的时间"和"排序用的时间"不是同一个字段
        /// （比如账单：排序用创建时间、分组用消费时间），子类就 override 它。
        /// </summary>
        [Ignore]
        public virtual string DayKey =>
            DateTimeOffset.FromUnixTimeMilliseconds(Timestamp).LocalDateTime.ToString("yyyy-MM-dd");
    }

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
    public class LocalRecord: LocalRecordBase
    {
        [PrimaryKey]
        public override string Id { get; set; } = string.Empty;

        public double Amount { get; set; }

        public int CategoryId { get; set; }

        public string Note { get; set; } = string.Empty;

        /// <summary>消费时间（Unix 毫秒）。决定它归到哪一天。</summary>
        public long SpentAt { get; set; }

        public string PayMethod { get; set; } = string.Empty;

        /// <summary>本地创建时间（Unix 毫秒）。</summary>
        public long CreatedAt { get; set; }

        /// <summary>是否已同步到服务端。false = 待同步。</summary>
        public override bool Synced { get; set; }

        /// <summary>同步成功时间（Unix 毫秒），0 表示从未同步。</summary>
        public override long SyncedAt { get; set; }

        /// <summary>
        /// 本地自然日 yyyy-MM-dd，仅用于"按天分组"，不入库（[Ignore]）。
        ///
        /// 账单的排序时间(created_at)和分组时间(spent_at)不是同一个字段：
        /// 一笔昨晚的消费今天补记，应该归到昨晚那天上传。所以这里 override，用 SpentAt。
        /// </summary>
        [Ignore]
        public override string DayKey =>
            DateTimeOffset.FromUnixTimeMilliseconds(SpentAt).LocalDateTime.ToString("yyyy-MM-dd");


        public override long Timestamp => CreatedAt;



    }


    [Table("ThingsRecords")]
    public class ThingRecord :LocalRecordBase
    {

        [PrimaryKey]//自增
        public override string Id { get; set; }= string.Empty;

        //事件标题
        public string Title { get; set; } = string.Empty;

        //事件标签
        public string Tag {get; set; } = string.Empty;

        // ---------------- 字段 3 ----------------
        /// <summary>
        /// 事发时情绪 (Picker 下拉单选)
        /// 必填，内置: 平静/焦虑/烦躁/激动/委屈/兴奋/失落
        /// </summary>
        [NotNull]
        public string Emotion { get; set; } = string.Empty;

        // ---------------- 字段 4 ----------------
        /// <summary>
        /// 事件发生时间 (DatePicker + TimePicker)
        /// 双列并排，格式为 yyyy-MM-dd HH:mm
        /// 
        /// 定义为long 适配proto的int64架构
        /// </summary>
        [NotNull]
        public long OccurredAt { get; set; }

        // ---------------- 字段 5 ----------------
        /// <summary>
        /// 事件简述 (Editor 多行，高度80)
        /// 必填，只写客观事实，不带主观评价
        /// </summary>
        [NotNull]
        public string ObjectiveDescription { get; set; } = string.Empty;

        // ---------------- 字段 6 ----------------
        /// <summary>
        /// 事发那一刻的想法 (Editor 多行，高度80)
        /// 必填，当时脑子里怎么想
        /// </summary>
        [NotNull]
        public string InitialThought { get; set; } = string.Empty;

        // ---------------- 字段 7 ----------------
        /// <summary>
        /// 当时采取的行动 (Editor 多行，高度80)
        /// 必填，做了哪些行为
        /// </summary>
        [NotNull]
        public string ActionTaken { get; set; } = string.Empty;

        // ---------------- 字段 8 ----------------
        /// <summary>
        /// 事件最终结果 (Editor 多行，高度80)
        /// 必填，最后走向、后果
        /// </summary>
        [NotNull]
        public string FinalResult { get; set; } = string.Empty;

        // ---------------- 字段 9 ----------------
        /// <summary>
        /// 复盘反思 (Editor 多行，高度100)
        /// 必填，做得好/存在不足
        /// </summary>
        [NotNull]
        public string Reflection { get; set; } = string.Empty;

        // ---------------- 字段 10 ----------------
        /// <summary>
        /// 后续行动计划 (Editor 多行，高度100)
        /// 必填，下次遇到如何处理
        /// </summary>
        [NotNull]
        public string ActionPlan { get; set; } = string.Empty;


        
        /// <summary>
        /// 是否已经同步到服务器
        /// </summary>
        public override bool Synced { get; set; }

        /// <summary>同步成功时间（Unix 毫秒），0 表示从未同步。</summary>
        public  override long SyncedAt { get; set; }

        /// <summary>
        /// 创建时间戳（Unix 毫秒），用于本地排序/同步。
        /// 注意：全项目统一毫秒，别再混用 UnixTimeSeconds —— 差 1000 倍，
        /// 一旦混进来，排序、按天分组会集体错位。
        /// </summary>
        public long CreatedTimestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // 分组时间 = 事件发生时间（基类 DayKey 会用 Timestamp 换算，这里不需要再写一遍）
        public override long Timestamp => OccurredAt;
    }

}
