using PersonalHub.Client.Data;

namespace PersonalHub.Client.Services
{
    /// <summary>
    /// 番茄钟状态机（R14.5）。静态类，两端共用：
    ///   - Android：前台服务（PomodoroForegroundService）只是"通知渲染器"，订阅
    ///     <see cref="Changed"/> 刷通知；进程被服务保活，计时循环跟着活着；
    ///   - Windows：没有服务，计时循环照跑，卡片页内显示倒计时。
    ///
    /// 与切片引擎的协作：开始时 <see cref="SegmentCapture.Pause"/>（先把当前段收口），
    /// 结束时 <see cref="SegmentCapture.Resume"/> —— 同一分钟不双重记录。
    ///
    /// 结束语义（用户拍板「自动入库」）：自然走完静默入库；中途手动停也入库，
    /// 时长按实际专注分钟算（不足 1 分钟丢弃）—— 手动停通常是"刚才也算数，但我要走了"。
    ///
    /// 入库的是带真实 StartedAt/EndedAt 的 LocalBlock（时间块表第一次有了人手动
    /// 之外的真值来源），目标按片记忆（_settings.GoalFor）自动带出，与手动录入同款。
    /// </summary>
    public static class PomodoroState
    {
        /// <summary>每秒触发（后台线程）：倒计时刷新。UI 订阅后必须自己回主线程。</summary>
        public static event Action? Changed;

        /// <summary>一次番茄钟结束且已入库后触发（含手动停）。</summary>
        public static event Action<int>? Finished;   // 参数：实际入库分钟数

        public static volatile bool Running;
        public static volatile bool PausedPhase;

        public static string PieceCode { get; private set; } = string.Empty;
        public static string Note { get; private set; } = string.Empty;
        public static int PlannedMinutes { get; private set; }
        public static long StartedAtMs { get; private set; }
        public static long EndsAtMs { get; private set; }

        /// <summary>剩余毫秒（暂停时不减）。UI 每秒读它显示。</summary>
        public static long RemainingMs { get; private set; }

        /// <summary>当前片的显示名（Android 通知在 DI 容器外，拿不到 PieceSettings，走这里）。</summary>
        public static string PieceDisplay => _settings?.DisplayName(PieceCode) ?? PieceCode;

        private static LocalRecordStore<LocalBlock>? _blocks;
        private static PieceSettings? _settings;
        private static CancellationTokenSource? _cts;
        private static volatile bool _stopRequested;

        public static void Initialize(LocalRecordStore<LocalBlock> blocks, PieceSettings settings)
        {
            _blocks = blocks;
            _settings = settings;
        }

        // ============================================================
        // 对外动作（任意线程可调；UI 卡片 / Android 通知按钮都走这里）
        // ============================================================

        public static void Start(string pieceCode, string note, int minutes)
        {
            if (Running || minutes <= 0 || string.IsNullOrEmpty(pieceCode)) return;

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            PieceCode = pieceCode;
            Note = note ?? string.Empty;
            PlannedMinutes = minutes;
            StartedAtMs = now;
            EndsAtMs = now + minutes * 60_000L;
            RemainingMs = minutes * 60_000L;
            PausedPhase = false;
            _stopRequested = false;
            Running = true;

            // 切片让路：先把开着的时间段收口，再停状态机
            SegmentCapture.Pause();

            _cts = new CancellationTokenSource();
            _ = RunAsync(_cts.Token);

            Changed?.Invoke();
        }

        public static void PausePhase()
        {
            if (!Running) return;
            PausedPhase = true;
            Changed?.Invoke();
        }

        public static void ResumePhase()
        {
            if (!Running) return;
            PausedPhase = false;
            Changed?.Invoke();
        }

        /// <summary>手动结束（含通知栏按钮）。实际分钟 ≥1 就入库，否则丢弃。</summary>
        public static void StopEarly()
        {
            if (!Running) return;
            _stopRequested = true;
        }

        // ============================================================
        // 计时循环（进程内 1Hz；Android 端进程由前台服务保活，杀不掉）
        // ============================================================

        private static async Task RunAsync(CancellationToken ct)
        {
            try
            {
                while (true)
                {
                    await Task.Delay(1000, ct).ConfigureAwait(false);

                    if (_stopRequested) break;

                    if (!PausedPhase)
                    {
                        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        RemainingMs = Math.Max(0, EndsAtMs - now);
                        Changed?.Invoke();
                        if (RemainingMs <= 0) break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 正常取消路径
            }
            catch
            {
                // 计时循环任何意外都不许把状态留在 Running —— 兜底收尾
            }

            await FinishAsync().ConfigureAwait(false);
        }

        private static async Task FinishAsync()
        {
            bool wasRunning = Running;
            int actualMinutes = 0;
            long endAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            try
            {
                if (wasRunning)
                {
                    actualMinutes = (int)Math.Round((endAt - StartedAtMs) / 60_000.0,
                        MidpointRounding.ToEven);
                    if (actualMinutes < 1) actualMinutes = 0;   // 不足 1 分钟丢弃

                    if (actualMinutes > 0 && _blocks is not null)
                    {
                        var settings = _settings;
                        var now = endAt;

                        await _blocks.EnsureReadyAsync().ConfigureAwait(false);
                        await _blocks.InsertAsync(new LocalBlock
                        {
                            Id = Guid.NewGuid().ToString("N"),
                            PieceCode = PieceCode,
                            OccurredAt = StartedAtMs,          // 归属日 = 开始那天
                            DurationMin = actualMinutes,
                            GoalId = settings?.GoalFor(PieceCode) ?? string.Empty,
                            Note = Note,
                            StartedAt = StartedAtMs,
                            EndedAt = StartedAtMs + (long)actualMinutes * 60_000L,
                            CreatedAt = now,
                            UpdatedAt = now,
                        }).ConfigureAwait(false);
                    }
                }
            }
            catch
            {
                // 入库失败也不把状态卡死在 Running；数据留给下次同步兜底不了 —— 接受丢失
                actualMinutes = 0;
            }
            finally
            {
                Running = false;
                PausedPhase = false;
                _cts?.Dispose();
                _cts = null;

                SegmentCapture.Resume();
                Changed?.Invoke();

                if (wasRunning && actualMinutes > 0)
                {
                    Finished?.Invoke(actualMinutes);
                }
            }
        }
    }
}
