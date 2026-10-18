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
    }
}
