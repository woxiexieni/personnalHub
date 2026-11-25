using PersonalHub.Client.Data;

namespace PersonalHub.Client.Services
{
    /// <summary>
    /// 自动时间片的采集桥 + 规则切片引擎（R13 v0 → R14.5 v0.5）。
    ///
    /// 为什么是静态类：Android 的前台服务在 MAUI 的 DI 容器外面（系统直接拉起），
    /// MauiProgram 启动时把 store 塞进来（见 Initialize），服务进程内直接调
    /// <see cref="HandleSignal"/> —— 同一个进程，没有跨进程通信。与 R12 的
    /// <see cref="PaymentCapture"/> 同一个模子。
    ///
    /// v0.5 相比 v0 的升级（拍板记录见 memory 2026-11-24/25）：
    ///   1. 信号从三件套扩到六件：+ 运动方差（加速度 2Hz 降频采样）/ Wi-Fi 身份（SSID，
    ///      「在家/在公司」的主要证据）/ 被动定位（只在 Wi-Fi 断开时有意义，辅助）；
    ///   2. 边界回溯：段起点不再从亮屏掐（那可能只是瞄一眼锁屏），回溯到段内第一次
    ///      「解锁或前台应用」信号 —— 真正开始用的时刻；
    ///   3. 相邻合并：候选标签相同、间隔 ≤ MergeGapMinutes 的两段合成一段（切太碎的解药）；
    ///   4. 候选升级：app 映射表 → 同 Wi-Fi 先验 → 同时段先验 → 大幅移动+断网≈通勤。
    ///      先验全部来自**已确认段**（status=1），这就是人工标注在教规则 —— v1 模型的地基。
    ///   5. 番茄钟暂停：PomodoroState 调 Pause()/Resume()，钟走的时候不重复切段。
    ///
    /// 调参常数全在下面一组 const 里，用两天不对味就改数字，别翻逻辑。
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

        /// <summary>
        /// 番茄钟运行期间为 true（<see cref="Pause"/>/<see cref="Resume"/> 维护）。
        /// 暂停时信号照收但不切段，避免番茄钟入库的块和自动段双重记录同一分钟。
        /// </summary>
        public static volatile bool Paused;

        // ---- 信号类型常量（服务层用；数据源新增就是在这里加一个）----
        public const string SigScreenOn = "screen_on";
        public const string SigScreenOff = "screen_off";
        public const string SigUnlock = "unlock";
        public const string SigNetChange = "net_change";
        public const string SigApp = "app";
        public const string SigMotion = "motion";      // 载荷：still / move / big
        public const string SigWifi = "wifi";          // 载荷：SSID 原文（仅机内）
        public const string SigLocation = "location";  // 载荷：moved / static

        private static LocalRecordStore<Data.LocalPendingSegment>? _store;

        public static void Initialize(LocalRecordStore<Data.LocalPendingSegment> store) => _store = store;

        // ============================================================
        // 信号缓冲与当前段（多线程：服务线程写，UI 线程读 → 全部过锁）
        // ============================================================

        /// <summary>App 字段当通用载荷用（app 包名 / 运动状态 / SSID / 位置状态）。</summary>
        private sealed record SignalEvent(string Type, long At, string App);

        private static readonly object _lock = new();
        private static readonly List<SignalEvent> _signals = new();

        /// <summary>当前开着的段（null = 没有段开着）。</summary>
        private static (long Start, string FirstSignal)? _current;

        // ---- 调参区（v0.5 全部集中在这里）----
        private const int MaxSignals = 900;                 // 缓冲上限：运动信号 1/分钟也能撑 15 小时
        private const int MinSegmentMinutes = 5;            // 短段丢弃阈值，v0 是 3，切太碎的用户反馈后提到 5
        private const int MaxSegmentMinutes = 90;           // 超过强制收口
        private const int MergeGapMinutes = 10;             // 相邻同标签段的合并窗口
        private const long RetainMs = 7 * 86_400_000L;      // 待确认段保留 7 天
        private const long DebounceMs = 60_000;             // 同类信号去抖
        private const long UnlockDebounceMs = 5_000;
        private const long MotionDebounceMs = 30_000;       // 运动信号 1/分钟，去抖给短的一半防漂移丢样

        // 先验阈值（v0.5 的"模型"就是这两条统计 + app 映射表）
        private const int PriorDays = 14;                   // 只看最近两周的确认段
        private const int PriorWifiMinCount = 5;            // 同 Wi-Fi 确认段至少这么多条才敢引
        private const double PriorWifiMinRatio = 0.60;      // 且最多片占比 ≥ 这个
        private const int PriorHourMinCount = 8;            // 同时段先验门槛更高（时段证据更弱）
        private const double PriorHourMinRatio = 0.50;

        /// <summary>
        /// Android 服务的主入口：一种信号 + 时刻（+ 载荷）。
        /// 任意线程调用；绝不能抛异常。
        /// </summary>
        public static void HandleSignal(string type, long atMs, string payload = "")
        {
            try
            {
                if (Paused) return;   // 番茄钟在跑：信号收了也不切段（Pause 时已把当前段收口）

                if (atMs <= 0) atMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                long pendingEnd = -1;   // >=0 表示锁内决定要收口一个段
                long pendingStart = 0;
                string pendingSignal = string.Empty;

                lock (_lock)
                {
                    // 1) 去抖：同类型信号在窗口内的重复直接丢（亮灭抖动 / 网络抖动）。
                    long debounce = type switch
                    {
                        SigUnlock => UnlockDebounceMs,
                        SigMotion => MotionDebounceMs,
                        _ => DebounceMs,
                    };
                    if (type != SigApp)
                    {
                        bool recent = _signals.Count > 0
                            && _signals[^1].Type == type
                            && atMs - _signals[^1].At < debounce;
                        if (recent) return;
                    }

                    // 2) 进缓冲（app/运动/Wi-Fi/位置是段内特征，亮灭解锁是边界依据，都留痕）
                    _signals.Add(new SignalEvent(type, atMs, payload ?? string.Empty));
                    if (_signals.Count > MaxSignals)
                    {
                        _signals.RemoveRange(0, _signals.Count - MaxSignals);
                    }

                    // 3) 状态机：决定是否收口 / 开新段（特征信号不参与，见 switch 尾注）
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

                        // motion / wifi / location / net_change 只是段内特征，不参与状态机
                    }
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
        // 番茄钟暂停 / 恢复（PomodoroState 调；任意线程；不抛）
        // ============================================================

        /// <summary>收口当前段并暂停切段。番茄钟开始前调。</summary>
        public static void Pause()
        {
            long closedStart = -1, closedEnd = 0;

            lock (_lock)
            {
                if (_current is { } c)
                {
                    closedStart = c.Start;
                    closedEnd = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    _current = null;
                }
                Paused = true;
            }

            if (closedStart >= 0)
            {
                _ = CloseSegmentAsync(closedStart, closedEnd, "pomodoro_pause");
            }
        }

        /// <summary>恢复切段。番茄钟结束后调（自然完成 / 手动停都算）。</summary>
        public static void Resume() => Paused = false;

        // ============================================================
        // 收口 → 特征提取 → 候选 → 合并/落库
        // ============================================================

        private static async Task CloseSegmentAsync(long start, long end, string boundarySignal)
        {
            var store = _store;
            if (store is null) return;

            try
            {
                int minutes = (int)((end - start) / 60_000);
                if (minutes < MinSegmentMinutes) return;   // 太碎的段不值得一条记录

                // ---- 特征提取（锁内一次读完缓冲）----
                string app, motion, wifi;
                long firstActivity = -1;

                lock (_lock)
                {
                    app = _signals
                        .Where(s => s.Type == SigApp && s.At >= start && s.At <= end && !string.IsNullOrEmpty(s.App))
                        .GroupBy(s => s.App)
                        .OrderByDescending(g => g.Count())
                        .FirstOrDefault()?.Key ?? string.Empty;

                    // 运动众数：段内多数分钟处在哪种状态
                    motion = _signals
                        .Where(s => s.Type == SigMotion && s.At >= start && s.At <= end && !string.IsNullOrEmpty(s.App))
                        .GroupBy(s => s.App)
                        .OrderByDescending(g => g.Count())
                        .FirstOrDefault()?.Key ?? string.Empty;

                    // Wi-Fi 取段内最后一次上报（结束时连着哪个网络，比开始时有代表性）
                    wifi = _signals
                        .LastOrDefault(s => s.Type == SigWifi && s.At >= start && s.At <= end
                                            && !string.IsNullOrEmpty(s.App))?.App ?? string.Empty;

                    // 边界回溯：亮屏可能只是瞄一眼锁屏。段内第一次「解锁 / 前台应用」
                    // 才是真正开始用的证据 —— 起点（只对灭屏收口的段做，超时强切不回溯）。
                    if (boundarySignal == SigScreenOff)
                    {
                        long backtrackDeadline = end - MinSegmentMinutes * 60_000L;
                        var evidence = _signals
                            .Where(s => (s.Type == SigUnlock || s.Type == SigApp)
                                        && s.At > start && s.At <= backtrackDeadline)
                            .OrderBy(s => s.At)
                            .FirstOrDefault();
                        if (evidence is not null) firstActivity = evidence.At;
                    }
                }

                if (firstActivity > start)
                {
                    start = firstActivity;
                    minutes = (int)((end - start) / 60_000);
                    if (minutes < MinSegmentMinutes) return;   // 回溯后不足最短段，丢
                }

                // ---- 候选标签（v0.5 四级）----
                string piece = PieceForApp(app);
                if (string.IsNullOrEmpty(piece))
                {
                    piece = await PieceByPriorAsync(store, start, end, wifi, motion).ConfigureAwait(false);
                }

                // ---- 相邻合并：同候选标签 + 间隔 ≤ MergeGapMinutes → 扩老段而不是开新段 ----
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                await store.EnsureReadyAsync().ConfigureAwait(false);

                if (!string.IsNullOrEmpty(piece))
                {
                    var recent = await store
                        .GetByRangeAsync(end - (MergeGapMinutes + MaxSegmentMinutes) * 60_000L, end)
                        .ConfigureAwait(false);

                    var prior = recent
                        .Where(s => s.Status == 0
                                    && s.PieceCode == piece
                                    && start - s.EndedAt is >= 0 and <= MergeGapMinutes * 60_000L)
                        .OrderByDescending(s => s.EndedAt)
                        .FirstOrDefault();

                    if (prior is not null)
                    {
                        prior.EndedAt = end;                     // 扩到本段结束
                        if (string.IsNullOrEmpty(prior.Motion)) prior.Motion = motion;
                        if (string.IsNullOrEmpty(prior.Wifi)) prior.Wifi = wifi;
                        if (string.IsNullOrEmpty(prior.SourceApp)) prior.SourceApp = app;
                        await store.UpdateAsync(prior).ConfigureAwait(false);
                        SegmentsChanged?.Invoke();
                        return;
                    }
                }

                await store.InsertAsync(new Data.LocalPendingSegment
                {
                    Id = Guid.NewGuid().ToString("N"),
                    StartedAt = start,
                    EndedAt = end,
                    PieceCode = piece,
                    SourceApp = app,
                    Motion = motion,
                    Wifi = wifi,
                    BoundarySignal = boundarySignal,
                    Status = 0,
                    CreatedAt = now,
                }).ConfigureAwait(false);

                // 过期清理：7 天前的待确认段没人理就物理删
                long cutoff = now - RetainMs;
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

        /// <summary>
        /// v0.5 先验候选：app 表认不出时的三级递补。
        /// 数据全部来自**人工确认过的段**（Status == 1）—— 每晚的确认在教它。
        /// </summary>
        private static async Task<string> PieceByPriorAsync(
            LocalRecordStore<Data.LocalPendingSegment> store,
            long start, long end, string wifi, string motion)
        {
            try
            {
                long since = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - PriorDays * 86_400_000L;
                var confirmed = (await store.GetByRangeAsync(since, long.MaxValue).ConfigureAwait(false))
                    .Where(s => s.Status == 1 && !string.IsNullOrEmpty(s.PieceCode))
                    .ToList();
                if (confirmed.Count == 0) return string.Empty;

                // ① 同 Wi-Fi 先验：「这个 Wi-Fi 下你常干什么」—— 在家/在公司的主要推理
                if (!string.IsNullOrEmpty(wifi))
                {
                    var sameWifi = confirmed.Where(s => s.Wifi == wifi).ToList();
                    if (sameWifi.Count >= PriorWifiMinCount)
                    {
                        var top = sameWifi.GroupBy(s => s.PieceCode)
                            .OrderByDescending(g => g.Count()).First();
                        if (top.Count() >= sameWifi.Count * PriorWifiMinRatio)
                        {
                            return top.Key;
                        }
                    }
                }

                // ② 同时段先验：「这个钟点你常干什么」（±1 小时桶）
                var hour = DateTimeOffset.FromUnixTimeMilliseconds(start).ToLocalTime().Hour;
                var sameHours = confirmed
                    .Where(s =>
                    {
                        int h = DateTimeOffset.FromUnixTimeMilliseconds(s.StartedAt).ToLocalTime().Hour;
                        int d = Math.Abs(h - hour);
                        d = d > 12 ? 24 - d : d;           // 时钟环绕：23 点和 0 点差 1 不是 23
                        return d <= 1;
                    })
                    .ToList();
                if (sameHours.Count >= PriorHourMinCount)
                {
                    var top = sameHours.GroupBy(s => s.PieceCode)
                        .OrderByDescending(g => g.Count()).First();
                    if (top.Count() >= sameHours.Count * PriorHourMinRatio)
                    {
                        return top.Key;
                    }
                }

                // ③ 通勤兜底：大幅移动 + 没连 Wi-Fi —— 手机在口袋里移动的场景
                if (motion == "big" && string.IsNullOrEmpty(wifi))
                {
                    return "commute";
                }

                return string.Empty;
            }
            catch
            {
                return string.Empty;   // 先验失败不致命：大不了留空等人挑
            }
        }

        // ============================================================
        // 应用 → 片 映射（高置信才收录，认不出就留给先验/人挑）
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

        /// <summary>包名 → 候选片代号。没收录的返回空串（留给先验/人挑）。</summary>
        public static string PieceForApp(string packageName)
        {
            if (string.IsNullOrEmpty(packageName)) return string.Empty;
            return AppPieceMap.TryGetValue(packageName, out var code) ? code : string.Empty;
        }
    }
}
