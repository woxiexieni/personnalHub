using PersonalHub.Client.Data;

namespace PersonalHub.Client.Services
{
    public enum PomodoroMode
    {
        Countdown = 0,
        Stopwatch = 1,
    }

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

        /// <summary>
        /// 倒计时自然走完（不是手动停）时触发。参数：入库分钟数。
        /// Android 的前台服务订阅它发"到点提醒" —— 手动停是"我要走了"，不该响。
        /// </summary>
        public static event Action<int>? CompletedNaturally;

        public static volatile bool Running;
        public static volatile bool PausedPhase;

        public static string PieceCode { get; private set; } = string.Empty;
        public static string Note { get; private set; } = string.Empty;
        public static string GoalId { get; private set; } = string.Empty;
        public static string GoalStepId { get; private set; } = string.Empty;
        public static string StepTitle { get; private set; } = string.Empty;
        public static PomodoroMode Mode { get; private set; } = PomodoroMode.Countdown;
        public static int PlannedMinutes { get; private set; }
        public static long StartedAtMs { get; private set; }
        public static long EndsAtMs { get; private set; }

        /// <summary>剩余毫秒（暂停时不减）。UI 每秒读它显示。</summary>
        public static long RemainingMs { get; private set; }

        /// <summary>正计时已经专注的毫秒数（暂停时不增加）。</summary>
        public static long ElapsedMs { get; private set; }

        /// <summary>
        /// 进程恢复时补记的分钟数（0 = 没有）。卡片读一次就清零。
        /// 用途：用户清后台 → 到点时刻在"没有进程"的状态下过去 → 下次启动补记，
        /// 界面上说一句"上次那段已补记"，否则他会以为白专注了、再手动记一遍。
        /// </summary>
        public static int PendingRecoveredMinutes { get; private set; }

        public static int TakeRecoveredMinutes()
        {
            int minutes = PendingRecoveredMinutes;
            PendingRecoveredMinutes = 0;
            return minutes;
        }

        /// <summary>当前片的显示名（Android 通知在 DI 容器外，拿不到 PieceSettings，走这里）。</summary>
        public static string PieceDisplay => _settings?.DisplayName(PieceCode) ?? PieceCode;

        private static LocalRecordStore<LocalBlock>? _blocks;
        private static PieceSettings? _settings;
        private static CancellationTokenSource? _cts;
        private static volatile bool _stopRequested;

        /// <summary>本次暂停的开始时刻（0 = 没在暂停）。</summary>
        private static long _pauseStartedAtMs;

        /// <summary>本次番茄钟累计暂停了多久 —— 算"真实专注分钟"时要扣掉。</summary>
        private static long _pausedTotalMs;

        public static void Initialize(LocalRecordStore<LocalBlock> blocks, PieceSettings settings)
        {
            _blocks = blocks;
            _settings = settings;

            // 进程刚起来：上一次的番茄可能还没走完（清后台 / 被系统回收）。
            // 这里恢复它 —— 结束时间没到就接着计时，早就过了就补记进时间块。
            TryRestore();
        }

        // ============================================================
        // 跨进程存活
        //
        // 计时本身不依赖循环：剩余时间永远是 EndsAtMs - now。所以只要把这次番茄的
        // 几个时间戳存下来，进程什么时候回来都能接上。
        //
        // 以前这些只在静态字段里 —— 用户清一次后台，状态全部归零，那一段专注
        // 既不显示、也不入库，等于白专注（服务被 START_STICKY 拉回来也救不了，
        // 因为 OnStartCommand 看到 Running == false 就直接 StopSelf）。
        // ============================================================

        private const string PrefActive = "ph.pomodoro.active";
        private const string PrefPiece = "ph.pomodoro.piece";
        private const string PrefNote = "ph.pomodoro.note";
        private const string PrefGoal = "ph.pomodoro.goal";
        private const string PrefGoalStep = "ph.pomodoro.goalStep";
        private const string PrefStepTitle = "ph.pomodoro.stepTitle";
        private const string PrefMode = "ph.pomodoro.mode";
        private const string PrefPlanned = "ph.pomodoro.planned";
        private const string PrefStarted = "ph.pomodoro.started";
        private const string PrefEnds = "ph.pomodoro.ends";
        private const string PrefPaused = "ph.pomodoro.paused";
        private const string PrefPausedAt = "ph.pomodoro.pausedAt";
        private const string PrefPausedSum = "ph.pomodoro.pausedTotal";
        private const string PrefRemaining = "ph.pomodoro.remaining";

        private static void PersistSession()
        {
            try
            {
                Preferences.Default.Set(PrefPiece, PieceCode);
                Preferences.Default.Set(PrefNote, Note);
                Preferences.Default.Set(PrefGoal, GoalId);
                Preferences.Default.Set(PrefGoalStep, GoalStepId);
                Preferences.Default.Set(PrefStepTitle, StepTitle);
                Preferences.Default.Set(PrefMode, (int)Mode);
                Preferences.Default.Set(PrefPlanned, PlannedMinutes);
                Preferences.Default.Set(PrefStarted, StartedAtMs);
                Preferences.Default.Set(PrefEnds, EndsAtMs);
                Preferences.Default.Set(PrefPaused, PausedPhase);
                Preferences.Default.Set(PrefPausedAt, _pauseStartedAtMs);
                Preferences.Default.Set(PrefPausedSum, _pausedTotalMs);
                Preferences.Default.Set(PrefRemaining, RemainingMs);

                // active 最后写：上面任意一步挂了，也不会留下一个"有 active 但字段残缺"的会话
                Preferences.Default.Set(PrefActive, true);
            }
            catch
            {
                // 存不下不影响计时，只是失去跨进程恢复能力
            }
        }

        private static void ClearSession()
        {
            try { Preferences.Default.Set(PrefActive, false); } catch { }
        }

        /// <summary>
        /// 从本地存储恢复上一次番茄钟。返回 true = 现在确实在跑
        /// （调用方据此重画通知 / 重新挂前台服务）。已经在跑就直接返回 true。
        /// </summary>
        public static bool TryRestore()
        {
            if (Running) return true;

            try
            {
                if (!Preferences.Default.Get(PrefActive, false)) return false;

                string piece = Preferences.Default.Get(PrefPiece, string.Empty);
                var mode = (PomodoroMode)Preferences.Default.Get(PrefMode, (int)PomodoroMode.Countdown);
                int planned = Preferences.Default.Get(PrefPlanned, 0);
                long started = Preferences.Default.Get(PrefStarted, 0L);
                long ends = Preferences.Default.Get(PrefEnds, 0L);

                bool validDuration = mode == PomodoroMode.Stopwatch ? planned == 0 : planned > 0 && ends > 0;
                if (string.IsNullOrEmpty(piece) || started <= 0 || !validDuration)
                {
                    ClearSession();
                    return false;
                }

                PieceCode = piece;
                Note = Preferences.Default.Get(PrefNote, string.Empty);
                GoalId = Preferences.Default.Get(PrefGoal, string.Empty);
                GoalStepId = Preferences.Default.Get(PrefGoalStep, string.Empty);
                StepTitle = Preferences.Default.Get(PrefStepTitle, string.Empty);
                Mode = mode;
                PlannedMinutes = planned;
                StartedAtMs = started;
                EndsAtMs = ends;
                PausedPhase = Preferences.Default.Get(PrefPaused, false);
                _pauseStartedAtMs = PausedPhase ? Preferences.Default.Get(PrefPausedAt, 0L) : 0;
                _pausedTotalMs = Math.Max(0, Preferences.Default.Get(PrefPausedSum, 0L));
                _stopRequested = false;

                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                RemainingMs = Mode == PomodoroMode.Countdown
                    ? (PausedPhase
                        ? Math.Max(0, Preferences.Default.Get(PrefRemaining, 0L))
                        : Math.Max(0, ends - now))
                    : 0;
                ElapsedMs = Mode == PomodoroMode.Stopwatch
                    ? Math.Max(0, now - started - _pausedTotalMs)
                    : 0;

                Running = true;

                if (Mode == PomodoroMode.Countdown && RemainingMs <= 0)
                {
                    // 结束时刻早就过了，当时进程不在（服务也一起被杀了）。
                    // 补记这一段 —— 时长按计划上限，不会凭空多算。
                    _ = FinishAsync().ContinueWith(t =>
                    {
                        try { if (t.Result > 0) PendingRecoveredMinutes = t.Result; }
                        catch { /* 补记失败就当没有，不制造幽灵记录 */ }
                    });
                    return false;
                }

                // 和 Start 一致：切片让路，同一分钟不双重记录
                SegmentCapture.Pause();

                _cts = new CancellationTokenSource();
                _ = RunAsync(_cts.Token);

                return true;
            }
            catch
            {
                return false;
            }
        }

        // ============================================================
        // 对外动作（任意线程可调；UI 卡片 / Android 通知按钮都走这里）
        // ============================================================

        public static void Start(string pieceCode, string note, int minutes)
            => Start(pieceCode, note, minutes, PomodoroMode.Countdown, string.Empty, string.Empty, string.Empty);

        public static void Start(
            string pieceCode,
            string note,
            int minutes,
            PomodoroMode mode,
            string goalId = "",
            string goalStepId = "",
            string stepTitle = "")
        {
            if (Running || string.IsNullOrEmpty(pieceCode)) return;
            if (mode == PomodoroMode.Countdown && minutes <= 0) return;

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            PieceCode = pieceCode;
            Note = note ?? string.Empty;
            GoalId = goalId ?? string.Empty;
            GoalStepId = goalStepId ?? string.Empty;
            StepTitle = stepTitle ?? string.Empty;
            Mode = mode;
            PlannedMinutes = mode == PomodoroMode.Countdown ? minutes : 0;
            StartedAtMs = now;
            EndsAtMs = mode == PomodoroMode.Countdown ? now + minutes * 60_000L : 0;
            RemainingMs = mode == PomodoroMode.Countdown ? minutes * 60_000L : 0;
            ElapsedMs = 0;
            PausedPhase = false;
            _stopRequested = false;
            _pausedTotalMs = 0;
            _pauseStartedAtMs = 0;
            Running = true;

            // 切片让路：先把开着的时间段收口，再停状态机
            SegmentCapture.Pause();

            _cts = new CancellationTokenSource();
            _ = RunAsync(_cts.Token);

            PersistSession();   // 先落盘再通知：进程在下一毫秒被杀也不会丢

            Changed?.Invoke();
        }

        public static void PausePhase()
        {
            if (!Running || PausedPhase) return;
            PausedPhase = true;
            _pauseStartedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            PersistSession();    // 暂停那一刻的剩余时长要存下来，否则恢复后时长会跳
            Changed?.Invoke();
        }

        public static void ResumePhase()
        {
            if (!Running || !PausedPhase) return;

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            if (_pauseStartedAtMs > 0)
            {
                long span = Math.Max(0, now - _pauseStartedAtMs);
                _pausedTotalMs += span;

                // 倒计时是从 EndsAtMs 反推的，暂停多久就把终点往后推多久。
                // 以前只停住 RemainingMs、不动 EndsAtMs —— 暂停 10 分钟，
                // 这个 25 分钟的番茄会在第 15 分钟自己"到点"，而库里只记 15 分钟。
                EndsAtMs += span;
                _pauseStartedAtMs = 0;
            }

            PausedPhase = false;
            PersistSession();   // EndsAtMs 被推后了，落盘
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
                        if (Mode == PomodoroMode.Countdown)
                        {
                            RemainingMs = Math.Max(0, EndsAtMs - now);
                        }
                        else
                        {
                            ElapsedMs = Math.Max(0, now - StartedAtMs - _pausedTotalMs);
                        }
                        Changed?.Invoke();
                        if (Mode == PomodoroMode.Countdown && RemainingMs <= 0) break;
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

        /// <summary>返回本次入库分钟数（0 = 不足 1 分钟丢弃 / 写库失败）。</summary>
        private static async Task<int> FinishAsync()
        {
            bool wasRunning = Running;
            int actualMinutes = 0;
            long endAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            bool natural = false;

            try
            {
                if (wasRunning)
                {
                    // 在暂停状态下结束的：最后这段暂停同样不算专注时间
                    if (PausedPhase && _pauseStartedAtMs > 0)
                    {
                        _pausedTotalMs += Math.Max(0, endAt - _pauseStartedAtMs);
                        _pauseStartedAtMs = 0;
                    }

                    // 自然走完 = 没点「结束」+ 剩余已经归零。手动停是"我要走了"，不该响铃。
                    natural = Mode == PomodoroMode.Countdown && !_stopRequested && RemainingMs <= 0;

                    // 真实专注分钟 = 墙钟时长 - 累计暂停。以前直接拿 StartedAtMs 算，
                    // 暂停十分钟会多记十分钟。
                    long focusMs = Math.Max(0, endAt - StartedAtMs - _pausedTotalMs);
                    actualMinutes = (int)Math.Round(focusMs / 60_000.0, MidpointRounding.ToEven);

                    if (Mode == PomodoroMode.Countdown && actualMinutes > PlannedMinutes)
                        actualMinutes = PlannedMinutes;
                    if (actualMinutes < 1) actualMinutes = 0;   // 不足 1 分钟丢弃

                    if (actualMinutes > 0 && _blocks is not null)
                    {
                        var settings = _settings;
                        var now = endAt;

                        await _blocks.EnsureReadyAsync().ConfigureAwait(false);
                        string blockNote = BuildBlockNote();
                        string blockGoalId = !string.IsNullOrEmpty(GoalId)
                            ? GoalId
                            : (settings?.GoalFor(PieceCode) ?? string.Empty);
                        await _blocks.InsertAsync(new LocalBlock
                        {
                            Id = Guid.NewGuid().ToString("N"),
                            PieceCode = PieceCode,
                            OccurredAt = StartedAtMs,          // 归属日 = 开始那天
                            DurationMin = actualMinutes,
                            GoalId = blockGoalId,
                            GoalStepId = GoalStepId,
                            Note = blockNote,
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
                // 先清持久化再改状态：这一段已经入库（或已判定不足 1 分钟），
                // 不能再被下一次进程启动恢复成"还在跑"。
                ClearSession();

                Running = false;
                PausedPhase = false;
                _cts?.Dispose();
                _cts = null;

                SegmentCapture.Resume();
                Changed?.Invoke();

                if (wasRunning && actualMinutes > 0)
                {
                    Finished?.Invoke(actualMinutes);

                    // 到点提醒只给"自然走完"的那一次
                    if (natural) CompletedNaturally?.Invoke(actualMinutes);
                }
            }

            return actualMinutes;
        }

        private static string BuildBlockNote()
        {
            string title = (StepTitle ?? string.Empty).Trim();
            string note = (Note ?? string.Empty).Trim();

            if (title.Length == 0) return note;
            if (note.Length == 0 || string.Equals(note, title, StringComparison.Ordinal)) return title;
            return title + " · " + note;
        }
    }
}
