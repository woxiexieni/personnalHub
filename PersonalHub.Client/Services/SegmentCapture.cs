using PersonalHub.Client.Data;

namespace PersonalHub.Client.Services
{
    /// <summary>
    /// 自动时间片的采集桥 + 规则切片引擎（R13 v0）。
    ///
    /// 为什么是静态类：Android 的前台服务在 MAUI 的 DI 容器外面（系统直接拉起），
    /// MauiProgram 启动时把 store 塞进来（见 Initialize），服务进程内直接调
    /// <see cref="HandleSignal"/> —— 同一个进程，没有跨进程通信。与 R12 的
    /// <see cref="PaymentCapture"/> 同一个模子。
    ///
    /// v0 是**纯规则**，没有模型：
    ///   边界 = 灭屏（screen_off）/ 解锁（unlock）/ 强制超时；
    ///   段内用前台应用采样投票出候选片代号（映射表见 <see cref="PieceForApp"/>）。
    /// 人确认的时间片就是标注数据 —— 攒够了 v1 才有资格谈模型。
    /// 数据源新增（加速度 / 蓝牙 / 定位）= 多一种信号类型 + 多一列特征，
    /// 对外接口（HandleSignal）不动，这是拍板时定下的可迭代约束。
    ///
    /// 铁律：信号只进内存环形缓冲和本地 pending_segments 表，绝不上传、绝不打日志。
    /// 服务线程会调用本类，**公开方法都不能抛异常** —— 采集挂了宁可漏段，不可杀服务。
    /// </summary>
    public static class SegmentCapture
    {
        /// <summary>落了一条新的待确认段后触发（后台线程）。UI 层订阅它刷新卡片。</summary>
        public static event Action? SegmentsChanged;

        /// <summary>前台服务是否活着（服务 OnCreate/OnDestroy 维护；进程重启后自动归 false）。</summary>
        public static volatile bool ServiceRunning;

        // ---- 信号类型常量（服务层用；数据源新增就是在这里加一个）----
        public const string SigScreenOn = "screen_on";
        public const string SigScreenOff = "screen_off";
        public const string SigUnlock = "unlock";
        public const string SigNetChange = "net_change";
        public const string SigApp = "app";

        private static LocalRecordStore<Data.LocalPendingSegment>? _store;

        public static void Initialize(LocalRecordStore<Data.LocalPendingSegment> store) => _store = store;

        // ============================================================
        // 信号缓冲与当前段（多线程：服务线程写，UI 线程读 → 全部过锁）
        // ============================================================

        private sealed record SignalEvent(string Type, long At, string App);

        private static readonly object _lock = new();
        private static readonly List<SignalEvent> _signals = new();

        /// <summary>当前开着的段（null = 没有段开着）。</summary>
        private static (long Start, string FirstSignal)? _current;

        // ---- v0 规则参数（全在常量里，调参不用翻逻辑）----
        private const int MaxSignals = 600;                 // 缓冲上限，防内存涨
        private const int MinSegmentMinutes = 3;            // 短于这个的段直接丢（碎信号不值一条记录）
        private const int MaxSegmentMinutes = 90;           // 超过这个强制收口，避免"一天一个块"
        private const long RetainMs = 7 * 86_400_000L;      // 待确认段保留 7 天，过期物理删
        private const long DebounceMs = 60_000;             // 同类信号去抖（亮灭抖动折叠成一次）
        private const long UnlockDebounceMs = 5_000;        // 解锁不去抖会和亮屏挤在一起，给个短的

        /// <summary>
        /// Android 服务的主入口：一种信号 + 时刻（+ 前台应用包名）。
        /// 任意线程调用；绝不能抛异常。
        /// </summary>
        public static void HandleSignal(string type, long atMs, string app = "")
        {
            try
            {
                if (atMs <= 0) atMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                long pendingEnd = -1;   // >=0 表示锁内决定要收口一个段
                long pendingStart = 0;
                string pendingSignal = string.Empty;

                lock (_lock)
                {
                    // 1) 去抖：同类型信号在窗口内的重复直接丢（亮灭抖动 / 网络抖动）。
                    long debounce = type == SigUnlock ? UnlockDebounceMs : DebounceMs;
                    if (type != SigApp)
                    {
                        bool recent = _signals.Count > 0
                            && _signals[^1].Type == type
                            && atMs - _signals[^1].At < debounce;
                        if (recent) return;
                    }

                    // 2) 进缓冲（app 采样是段内特征，别的信号是边界依据，都留痕）
                    _signals.Add(new SignalEvent(type, atMs, app ?? string.Empty));
                    if (_signals.Count > MaxSignals)
                    {
                        _signals.RemoveRange(0, _signals.Count - MaxSignals);
                    }

                    // 3) 状态机：决定是否收口 / 开新段
                    long duration = _current is { } cur ? atMs - cur.Start : 0;

                    switch (type)
                    {
                        case SigScreenOn:
                            if (_current is null)
                            {
                                _current = (atMs, SigScreenOn);
                            }
                            else if (duration >= MaxSegmentMinutes * 60_000L)
                            {
                                // 超时强切：上一段收口，同一刻继续开新段
                                pendingEnd = atMs;
                                pendingStart = _current.Value.Start;
                                pendingSignal = "timeout";
                                _current = (atMs, SigScreenOn);
                            }
                            break;

                        case SigUnlock:
                            if (_current is null)
                            {
                                _current = (atMs, SigUnlock);
                            }
                            else if (duration >= MaxSegmentMinutes * 60_000L)
                            {
                                pendingEnd = atMs;
                                pendingStart = _current.Value.Start;
                                pendingSignal = "timeout";
                                _current = (atMs, SigUnlock);
                            }
                            break;

                        case SigScreenOff:
                            if (_current is { } c)
                            {
                                pendingEnd = atMs;
                                pendingStart = c.Start;
                                pendingSignal = SigScreenOff;
                                _current = null;
                            }
                            break;

                        case SigApp:
                            // app 采样不切段，只负责在段超时时强切
                            if (_current is { } a && atMs - a.Start >= MaxSegmentMinutes * 60_000L)
                            {
                                pendingEnd = atMs;
                                pendingStart = a.Start;
                                pendingSignal = "timeout";
                                _current = (atMs, SigApp);
                            }
                            break;

                        // net_change 只是段内特征，不参与状态机
                    }

                    // 缓冲只靠上限裁剪（投票时要读段内 app 信号，这里不做更细的清理）
                }

                if (pendingEnd >= 0)
                {
                    _ = CloseSegmentAsync(pendingStart, pendingEnd, pendingSignal);
                }
            }
            catch
            {
                // 吞掉：采集服务里抛异常可能被系统杀掉，宁可漏一段。
            }
        }

        // ============================================================
        // 收口 → 落库
        // ============================================================

        private static async Task CloseSegmentAsync(long start, long end, string boundarySignal)
        {
            var store = _store;
            if (store is null) return;

            try
            {
                int minutes = (int)((end - start) / 60_000);
                if (minutes < MinSegmentMinutes) return;   // 太碎的段不值得一条记录

                // 段内 app 采样投票：出现次数最多的包名当主用应用
                string app;
                lock (_lock)
                {
                    app = _signals
                        .Where(s => s.Type == SigApp && s.At >= start && s.At <= end && !string.IsNullOrEmpty(s.App))
                        .GroupBy(s => s.App)
                        .OrderByDescending(g => g.Count())
                        .FirstOrDefault()?.Key ?? string.Empty;
                }

                string piece = PieceForApp(app);

                await store.EnsureReadyAsync().ConfigureAwait(false);
                await store.InsertAsync(new Data.LocalPendingSegment
                {
                    Id = Guid.NewGuid().ToString("N"),
                    StartedAt = start,
                    EndedAt = end,
                    PieceCode = piece,
                    SourceApp = app,
                    BoundarySignal = boundarySignal,
                    Status = 0,
                    CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                }).ConfigureAwait(false);

                // 过期清理：7 天前的待确认段没人理就物理删
                long cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - RetainMs;
                await store.ExecuteAsync(
                    "DELETE FROM pending_segments WHERE StartedAt < ? AND Status != 1", cutoff)
                    .ConfigureAwait(false);

                SegmentsChanged?.Invoke();
            }
            catch
            {
                // 吞掉，理由同上。
            }
        }

        // ============================================================
        // 应用 → 片 映射（v0 的"模型"就是这张表，认不出就空着等人挑）
        // ============================================================

        /// <summary>
        /// 包名 → 片代号。刻意只收录**高置信**的映射：
        /// 短视频/游戏/音乐/视频 ≈ 娱乐，聊天 ≈ 陪人，电商/支付/生活服务 ≈ 家务杂事。
        /// 浏览器、系统应用、工具类一概不猜 —— 一个错的候选标签比一个空的更伤
        /// （空的自然被人挑，错的会被顺手点过去，污染 blocks 的统计口径）。
        /// </summary>
        private static readonly IReadOnlyDictionary<string, string> AppPieceMap =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                // 消耗：娱乐
                ["com.ss.android.ugc.aweme"]   = "entertain",   // 抖音
                ["com.smile.gifmaker"]         = "entertain",   // 快手
                ["tv.danmaku.bili"]            = "entertain",   // 哔哩哔哩
                ["com.tencent.qqlive"]         = "entertain",   // 腾讯视频
                ["com.qiyi.video"]             = "entertain",   // 爱奇艺
                ["com.netease.cloudmusic"]     = "entertain",   // 网易云音乐
                ["com.kuaishou.nebula"]        = "entertain",   // 快手极速版

                // 人际：聊天
                ["com.tencent.mm"]             = "people",      // 微信
                ["com.tencent.mobileqq"]       = "people",      // QQ
                ["com.tencent.tim"]            = "people",      // TIM

                // 生活：电商 / 支付 / 生活服务（"家务杂事"是用户自己定的筐）
                ["com.eg.android.AlipayGphone"] = "chore",      // 支付宝
                ["com.taobao.taobao"]           = "chore",      // 淘宝
                ["com.jingdong.app.mall"]       = "chore",      // 京东
                ["com.xunmeng.pinduoduo"]       = "chore",      // 拼多多

                // 产出：办公套件（手机上真用它干活的时候远多于刷它的时候）
                ["com.ss.android.lark"]         = "work",       // 飞书
            };

        /// <summary>包名 → 候选片代号。没收录的返回空串（等人挑）。</summary>
        public static string PieceForApp(string packageName)
        {
            if (string.IsNullOrEmpty(packageName)) return string.Empty;
            return AppPieceMap.TryGetValue(packageName, out var code) ? code : string.Empty;
        }
    }
}
