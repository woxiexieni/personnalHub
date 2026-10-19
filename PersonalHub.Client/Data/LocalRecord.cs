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

        /// <summary>
        /// 软删标记。true = 这条已被删掉，等下次上传时告诉服务端。
        ///
        /// 提到基类上是因为 <see cref="LocalRecordStore{T}"/> 的通用查询要过滤墓碑，
        /// 原来是一个 switch 判断具体类型 —— 加一类记录就得记得加一个 case，
        /// 忘了的后果是"删掉的记录又出现在列表里"，而且不报错。
        /// </summary>
        public abstract bool Deleted { get; set; }

        /// <summary>用于按天分组的时间戳（Unix 毫秒）。子类必须指定自己的时间字段。
        /// LocalRecordStore的按天查询功能
        /// 
        /// </summary>
        public abstract long Timestamp { get; }

        /// <summary>
        /// 用于"归属到哪一天"的时间戳（Unix 毫秒）。
        ///
        /// 为什么要和 <see cref="Timestamp"/> 分开：
        /// 账单的 Timestamp 是**创建时间**（本地列表按它排序），但归属日期必须用**消费时间**。
        /// 昨晚 23:50 的账今早补记，应该算昨天 —— 用创建时间去归天就会算成今天，
        /// 于是"今日总额"和趋势图会集体偏一天。
        /// </summary>
        [Ignore]
        public abstract long DayTimestamp { get; }

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

        // ---------------- R1 新增（对应 proto 的 8 / 9 / 10 号字段）----------------

        /// <summary>0 = 支出，1 = 收入。老数据由迁移补 0。</summary>
        public int Direction { get; set; }

        /// <summary>最后修改时间（Unix 毫秒）。服务端靠它判断"谁更新"，避免旧副本覆盖新数据。</summary>
        public long UpdatedAt { get; set; }

        /// <summary>
        /// 软删标记。true = 这条已被删掉，等下次上传时告诉服务端。
        ///
        /// 为什么不直接从本地 table 里删掉：
        /// 已同步的记录如果只在本地删，服务端并不知道，下次镜像同步又会被拉回来 —— 删了个寂寞。
        /// 所以"已同步的删除"必须留一个墓碑，上传成功后才能清掉。
        /// </summary>
        public override bool Deleted { get; set; }

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

        /// <summary>归天用消费时间，排序用创建时间。</summary>
        [Ignore]
        public override long DayTimestamp => SpentAt;
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

        /// <summary>最后修改时间（Unix 毫秒）。R1 起与账单表对齐，为将来"电脑端改反思"留位。</summary>
        public long UpdatedAt { get; set; }

        /// <summary>软删标记。查询默认过滤。</summary>
        public override bool Deleted { get; set; }

        // 分组时间 = 事件发生时间（基类 DayKey 会用 Timestamp 换算，这里不需要再写一遍）
        public override long Timestamp => OccurredAt;

        /// <summary>归天用事件发生时间。</summary>
        [Ignore]
        public override long DayTimestamp => OccurredAt;
    }

    /// <summary>
    /// 本地时间块（R2 新增的第三类可同步记录）。
    ///
    /// 与账单 / 事件反思最关键的区别：**除 PieceCode 外全部可空**。
    /// 时长可以没填、目标可以没归、备注可以不写 —— 半条记录必须能存下,
    /// 否则又会回到"全必填表单没人用"的老路上。
    ///
    /// 字段一次建全（worth / lesson / planId / anomaly / mood / startedAt / endedAt），
    /// R2.5 的计时、R3 的复盘、R4 的计划都只用界面 + 接口，不再动表。
    /// </summary>
    [Table("blocks")]
    public class LocalBlock : LocalRecordBase
    {
        [PrimaryKey]
        public override string Id { get; set; } = string.Empty;

        /// <summary>事情片代号（code 冻结，显示名可改）。</summary>
        public string PieceCode { get; set; } = string.Empty;

        /// <summary>归属时刻（Unix 毫秒）。决定它算哪一天。</summary>
        public long OccurredAt { get; set; }

        /// <summary>时长（分钟）。0 = 没填，合法。</summary>
        public int DurationMin { get; set; }

        /// <summary>归属目标；空 = 没归（不报错）。</summary>
        public string GoalId { get; set; } = string.Empty;

        public string Note { get; set; } = string.Empty;

        /// <summary>0 未评 / 1 值得 / 2 一般 / 3 别做（R3）。</summary>
        public int Worth { get; set; }

        /// <summary>只在「值得」时问的那一句话（R3）。</summary>
        public string Lesson { get; set; } = string.Empty;

        /// <summary>R4 的计划关联。</summary>
        public string PlanId { get; set; } = string.Empty;

        /// <summary>0 正常 / 1 跨天或超长待确认（R2.5 计时）。</summary>
        public int Anomaly { get; set; }

        /// <summary>本版不采集，恒 0（与 worth 重复，省一次决策）。</summary>
        public int Mood { get; set; }

        public long StartedAt { get; set; }

        public long EndedAt { get; set; }

        public long CreatedAt { get; set; }

        public long UpdatedAt { get; set; }

        public override bool Deleted { get; set; }

        public override bool Synced { get; set; }

        public override long SyncedAt { get; set; }

        /// <summary>排序用归属时刻。</summary>
        public override long Timestamp => OccurredAt;

        [Ignore]
        public override long DayTimestamp => OccurredAt;
    }

    /// <summary>
    /// 本地目标（长目标层）。
    ///
    /// 表里只有名字 / 状态 / 一条下一步 / 当前标记，别的**一个都不存**：
    /// 累计投入、最后活动、四周趋势全是查询出来的 ——
    /// 存下来会在删块或改归属之后永久错掉，而且错得看不出来。
    /// </summary>
    [Table("goals")]
    public class LocalGoal : LocalRecordBase
    {
        [PrimaryKey]
        public override string Id { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        /// <summary>0 进行中 / 1 暂停 / 2 完成 / 3 放弃。</summary>
        public int Status { get; set; }

        /// <summary>「一条下一步」，一句话，可空。不是清单。</summary>
        public string NextStep { get; set; } = string.Empty;

        /// <summary>录入时新块默认归它。全局最多一个为 true。</summary>
        public bool IsCurrent { get; set; }

        public long StartedAt { get; set; }

        public long CreatedAt { get; set; }

        public long UpdatedAt { get; set; }

        public override bool Deleted { get; set; }

        public override bool Synced { get; set; }

        public override long SyncedAt { get; set; }

        public override long Timestamp => StartedAt;

        [Ignore]
        public override long DayTimestamp => StartedAt;
    }

}
