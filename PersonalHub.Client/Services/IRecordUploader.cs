using PersonalHub.Client.Data;

namespace PersonalHub.Client.Services
{
    /// <summary>
    /// 一次上传（某一类记录的某一天）的结果。
    ///
    /// 这是「同步流程」和「具体 proto 类型」之间的**唯一契约**：
    /// 上层只管"哪些 id 被服务端收下了、哪些被拒了"，不关心到底是
    /// UploadRecordsRequest 还是 UploadThingsRequest。
    /// </summary>
    public sealed class UploadOutcome
    {
        /// <summary>服务端确认接收的条数（仅用于日志/展示）。</summary>
        public int Accepted { get; init; }

        /// <summary>被服务端拒绝的 id，这些记录要继续留在本地待下次重试。</summary>
        public IReadOnlyList<string> FailedIds { get; init; } = Array.Empty<string>();

        /// <summary>服务端附带的错误说明（可选）。</summary>
        public string? ErrorMsg { get; init; }

        /// <summary>空结果（比如本地这组一条都没转换出来时用）。</summary>
        public static UploadOutcome Empty { get; } = new();
    }

    /// <summary>
    /// 「某一类本地记录 → 服务端」的搬运工。
    ///
    /// ===== 为什么要这一层（你原来的困惑点）=====
    /// 你原来想写 RecordSyncService&lt;T, TProto&gt;，让同步器自己 `new UploadRecordsRequest{...}`。
    /// 但 protobuf 生成的每个 message 都是**互不相关的独立类**（Record / Thing 没有共同基类、
    /// 没有共同接口），请求类型和 RPC 方法名也各不相同，于是泛型参数 TProto 在 SyncAsync 里
    /// 根本没法"通用地"拼请求、也找不到对应的 RPC 方法 —— 只能靠 if/else 判断类型，
    /// 那就等于没抽象。
    ///
    /// 正确的切法：把「变化的部分」关进一个**只有一个方法**的接口里。
    /// 可变的是：字段怎么映射、请求怎么拼、调哪个 RPC、响应怎么读；
    /// 不变的是：取未同步 → 按天分组 → 逐天上传 → 按失败 id 标记已同步。
    /// 于是泛型只留在**本地模型**上（T : LocalRecordBase，这一层本来就有公共基类），
    /// proto 的差异被 UploadAsync 这个方法整个吃掉了。
    ///
    /// 新增一类记录（比如"日记""读书笔记"）时要做的事固定为三步：
    ///   1) proto 加 message + service；
    ///   2) 写一个 XxxUploader : IRecordUploader&lt;XxxRecord&gt;（约 30 行，只搬字段）；
    ///   3) 在 MauiProgram 里注册。
    /// 同步器（RecordSyncService）**一行都不用改**，这才是复用。
    /// </summary>
    public interface IRecordUploader<T> where T : LocalRecordBase
    {
        /// <summary>展示用名字，比如"账单""事件"，只用于拼提示语。</summary>
        string DisplayName { get; }

        /// <summary>
        /// 把同一批（同一天的）本地记录上传到服务端。
        /// 实现里负责：本地模型 → proto、拼请求、调对应 RPC、把响应翻译成 <see cref="UploadOutcome"/>。
        /// </summary>
        Task<UploadOutcome> UploadAsync(IReadOnlyList<T> items, CancellationToken ct = default);
    }
}
