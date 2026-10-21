using PersonalHub.Client.Data;

namespace Personalhub
{
    /// <summary>
    /// 本地模型 → proto 消息的映射（扩展方法集合）。
    ///
    /// 为什么单独放一个文件：
    ///   proto 生成的 <c>Record</c> / <c>Thing</c> 都是 partial class，可以在这个命名空间里给它们补代码，
    ///   但**映射规则属于客户端**，不该塞进生成文件，也不该散落在各个 Uploader 里。
    ///   放这里，以后要加字段只改这一个地方。
    ///
    /// 两个注意点：
    ///   1) proto2 的 string 字段 setter 不接受 null（会抛 ArgumentNullException），所以统一 ?? ""。
    ///   2) 时间统一 Unix **毫秒**，和 .proto 里的注释约定一致。
    /// </summary>
    public static class RecordMappingExtensions
    {
        /// <summary>本地账单 → proto Record。</summary>
        public static Record ToProto(this LocalRecord r) => new()
        {
            Id = r.Id,
            Amount = r.Amount,
            CategoryId = r.CategoryId,
            Note = r.Note ?? string.Empty,
            SpentAt = r.SpentAt,
            PayMethod = r.PayMethod ?? string.Empty,
            CreatedAt = r.CreatedAt,
            Direction = r.Direction,
            UpdatedAt = r.UpdatedAt,
            Deleted = r.Deleted,
        };

        /// <summary>
        /// proto Record → 本地账单（镜像同步用）。
        ///
        /// 这里**不设 Synced**：调用方（MergeServerWindowAsync）负责补同步标记，
        /// 因为"从服务端来的"和"本地新记的"在同步状态上语义完全不同，
        /// 让映射函数顺手改同步状态，是以后排查"为什么明明传上去了还是待同步"的经典坑。
        /// </summary>
        public static LocalRecord ToLocalRecord(this Record p) => new()
        {
            Id = p.Id,
            Amount = p.Amount,
            CategoryId = p.CategoryId,
            Note = p.Note ?? string.Empty,
            SpentAt = p.SpentAt,
            PayMethod = p.PayMethod ?? string.Empty,
            CreatedAt = p.CreatedAt,
            Direction = p.Direction,
            UpdatedAt = p.UpdatedAt == 0 ? p.CreatedAt : p.UpdatedAt,
            Deleted = p.Deleted,
        };

        /// <summary>本地事件反思 → proto Thing。</summary>
        public static Thing ToProto(this ThingRecord r) => new()
        {
            Id = r.Id,
            Title = r.Title ?? string.Empty,
            Tag = r.Tag ?? string.Empty,
            Emotion = r.Emotion ?? string.Empty,
            OccurredAt = r.OccurredAt,
            ObjectiveDescription = r.ObjectiveDescription ?? string.Empty,
            InitialThought = r.InitialThought ?? string.Empty,
            ActionTaken = r.ActionTaken ?? string.Empty,
            FinalResult = r.FinalResult ?? string.Empty,
            Reflection = r.Reflection ?? string.Empty,
            ActionPlan = r.ActionPlan ?? string.Empty,
            CreatedAt = r.CreatedTimestamp,
        };

        // ============================================================
        // R2：时间块
        // ============================================================

        /// <summary>本地时间块 → proto Block。</summary>
        public static Block ToProto(this LocalBlock b) => new()
        {
            Id = b.Id,
            PieceCode = b.PieceCode ?? string.Empty,
            OccurredAt = b.OccurredAt,
            DurationMin = b.DurationMin,
            GoalId = b.GoalId ?? string.Empty,
            Note = b.Note ?? string.Empty,
            Worth = b.Worth,
            Lesson = b.Lesson ?? string.Empty,
            PlanId = b.PlanId ?? string.Empty,
            Anomaly = b.Anomaly,
            Mood = b.Mood,
            StartedAt = b.StartedAt,
            EndedAt = b.EndedAt,
            CreatedAt = b.CreatedAt,
            UpdatedAt = b.UpdatedAt,
            Deleted = b.Deleted,
        };

        /// <summary>
        /// proto Block → 本地时间块（镜像同步用）。
        /// **不设 Synced**，由调用方补 —— 理由同 <see cref="ToLocalRecord"/>。
        /// </summary>
        public static LocalBlock ToLocalBlock(this Block p) => new()
        {
            Id = p.Id,
            PieceCode = p.PieceCode ?? string.Empty,
            OccurredAt = p.OccurredAt,
            DurationMin = p.DurationMin,
            GoalId = p.GoalId ?? string.Empty,
            Note = p.Note ?? string.Empty,
            Worth = p.Worth,
            Lesson = p.Lesson ?? string.Empty,
            PlanId = p.PlanId ?? string.Empty,
            Anomaly = p.Anomaly,
            Mood = p.Mood,
            StartedAt = p.StartedAt,
            EndedAt = p.EndedAt,
            CreatedAt = p.CreatedAt,
            UpdatedAt = p.UpdatedAt == 0 ? p.CreatedAt : p.UpdatedAt,
            Deleted = p.Deleted,
        };

        // ============================================================
        // R2：目标
        // ============================================================

        /// <summary>
        /// 本地目标 → proto Goal。
        /// 注意**不回传** total_minutes / last_activity_at —— 那是服务端现算的，回传只会制造脏数据。
        /// </summary>
        public static Goal ToProto(this LocalGoal g) => new()
        {
            Id = g.Id,
            Title = g.Title ?? string.Empty,
            Status = g.Status,
            NextStep = g.NextStep ?? string.Empty,
            IsCurrent = g.IsCurrent,
            StartedAt = g.StartedAt,
            CreatedAt = g.CreatedAt,
            UpdatedAt = g.UpdatedAt,
            Deleted = g.Deleted,
        };

        public static LocalGoal ToLocalGoal(this Goal p) => new()
        {
            Id = p.Id,
            Title = p.Title ?? string.Empty,
            Status = p.Status,
            NextStep = p.NextStep ?? string.Empty,
            IsCurrent = p.IsCurrent,
            StartedAt = p.StartedAt,
            CreatedAt = p.CreatedAt,
            UpdatedAt = p.UpdatedAt == 0 ? p.CreatedAt : p.UpdatedAt,
            Deleted = p.Deleted,
        };

        // ============================================================
        // R4：每日计划
        // ============================================================

        public static Plan ToProto(this LocalPlan p) => new()
        {
            Id = p.Id,
            Day = p.Day ?? string.Empty,
            PieceCode = p.PieceCode ?? string.Empty,
            BudgetMin = p.BudgetMin,
            GoalId = p.GoalId ?? string.Empty,
            CreatedAt = p.CreatedAt,
            UpdatedAt = p.UpdatedAt,
            Deleted = p.Deleted,
        };

        /// <summary>不设 Synced，由调用方补（镜像下载 ≠ 已上传，两者别混）。</summary>
        public static LocalPlan ToLocalPlan(this Plan p) => new()
        {
            Id = p.Id,
            Day = p.Day ?? string.Empty,
            PieceCode = p.PieceCode ?? string.Empty,
            BudgetMin = p.BudgetMin,
            GoalId = p.GoalId ?? string.Empty,
            CreatedAt = p.CreatedAt,
            UpdatedAt = p.UpdatedAt == 0 ? p.CreatedAt : p.UpdatedAt,
            Deleted = p.Deleted,
        };

        // ============================================================
        // R5：行为模式 / 练习
        // ============================================================

        public static Pattern ToProto(this LocalPattern p) => new()
        {
            Id = p.Id,
            Title = p.Title ?? string.Empty,
            Trigger = p.Trigger ?? string.Empty,
            OldResponse = p.OldResponse ?? string.Empty,
            NewResponse = p.NewResponse ?? string.Empty,
            RewardKept = p.RewardKept ?? string.Empty,
            Status = p.Status,
            CreatedAt = p.CreatedAt,
            UpdatedAt = p.UpdatedAt,
            Deleted = p.Deleted,
        };

        public static LocalPattern ToLocalPattern(this Pattern p) => new()
        {
            Id = p.Id,
            Title = p.Title ?? string.Empty,
            Trigger = p.Trigger ?? string.Empty,
            OldResponse = p.OldResponse ?? string.Empty,
            NewResponse = p.NewResponse ?? string.Empty,
            RewardKept = p.RewardKept ?? string.Empty,
            Status = p.Status,
            CreatedAt = p.CreatedAt,
            UpdatedAt = p.UpdatedAt == 0 ? p.CreatedAt : p.UpdatedAt,
            Deleted = p.Deleted,
        };

        public static Practice ToProto(this LocalPractice p) => new()
        {
            Id = p.Id,
            PatternId = p.PatternId ?? string.Empty,
            Result = p.Result,
            OccurredAt = p.OccurredAt,
            CreatedAt = p.CreatedAt,
            UpdatedAt = p.UpdatedAt,
            Deleted = p.Deleted,
        };

        public static LocalPractice ToLocalPractice(this Practice p) => new()
        {
            Id = p.Id,
            PatternId = p.PatternId ?? string.Empty,
            Result = p.Result,
            OccurredAt = p.OccurredAt,
            CreatedAt = p.CreatedAt,
            UpdatedAt = p.UpdatedAt == 0 ? p.CreatedAt : p.UpdatedAt,
            Deleted = p.Deleted,
        };

        // ============================================================
        // R6：想法
        // ============================================================

        public static Idea ToProto(this LocalIdea p) => new()
        {
            Id = p.Id,
            Text = p.Text ?? string.Empty,
            Kind = p.Kind,
            GoalId = p.GoalId ?? string.Empty,
            CreatedAt = p.CreatedAt,
            UpdatedAt = p.UpdatedAt,
            Deleted = p.Deleted,
            Done = p.Done,
            DoneAt = p.Done ? p.DoneAt : 0,
        };

        /// <summary>镜像下载用：不设 Synced（下载 ≠ 已上传）。</summary>
        public static LocalIdea ToLocalIdea(this Idea p) => new()
        {
            Id = p.Id,
            Text = p.Text ?? string.Empty,
            Kind = p.Kind,
            GoalId = p.GoalId ?? string.Empty,
            CreatedAt = p.CreatedAt,
            UpdatedAt = p.UpdatedAt == 0 ? p.CreatedAt : p.UpdatedAt,
            Deleted = p.Deleted,
            Done = p.Done,
            DoneAt = p.Done ? p.DoneAt : 0,
        };
    }
}
