using PersonalHub.Client.Data;
using Personalhub;

namespace PersonalHub.Client.Services
{
    /// <summary>
    /// 时间块 + 目标的「服务端 → 本地」对齐。
    ///
    /// 为什么不塞进 <see cref="LocalCacheService"/>：那个类只认账单表
    /// （构造里就写死了 <c>LocalRecordStore&lt;LocalRecord&gt;</c>），
    /// 把它泛型化会牵动已经跑通的账单链路，风险大于收益。
    ///
    /// 上传不走这里 —— 上传统一归 <see cref="RecordSyncService{T}"/>。
    /// 这里只做"拉下来覆盖本地副本"，和账单那边是同一条思路：
    /// 全量镜像、幂等、能自愈，不需要增量游标。
    /// </summary>
    public sealed class BlockMirrorService
    {
        /// <summary>本地保留的自然日天数（含今天）。与账单一致。</summary>
        public const int WindowDays = 30;

        private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);

        private readonly ServerConnection _conn;
        private readonly LocalRecordStore<LocalBlock> _blocks;
        private readonly LocalRecordStore<LocalGoal> _goals;
        private readonly SemaphoreSlim _gate = new(1, 1);

        public BlockMirrorService(
            ServerConnection conn,
            LocalRecordStore<LocalBlock> blocks,
            LocalRecordStore<LocalGoal> goals)
        {
            _conn = conn;
            _blocks = blocks;
            _goals = goals;
        }

        /// <summary>拉最近 30 天的时间块覆盖本地，并清理超窗口的已同步副本。</summary>
        public async Task<MirrorResult> MirrorAsync(CancellationToken ct = default)
        {
            if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false))
            {
                return new MirrorResult(true, 0, 0, 0, "已有镜像任务在进行");
            }

            try
            {
                if (!await _conn.EnsureAsync().ConfigureAwait(false))
                {
                    return new MirrorResult(false, 0, 0, 0, "暂时连不上服务端，本地保留最近 30 天的数据");
                }

                var (from, to) = Period.LastDays(WindowDays);

                var resp = await _conn.NewBlockClient()
                    .ListBlocksAsync(
                        new ListBlocksRequest
                        {
                            From = from,
                            To = to,
                            // 必须显式 true：镜像要靠墓碑把本地多余的副本删掉
                            IncludeDeleted = true,
                        },
                        deadline: DateTime.UtcNow.Add(CallTimeout),
                        cancellationToken: ct)
                    .ConfigureAwait(false);

                var stats = await _blocks.MergeServerWindowAsync(resp.Blocks, from, to).ConfigureAwait(false);

                long cutoff = Period.StartOfDay(DateTimeOffset.UtcNow)
                    .AddDays(-WindowDays)
                    .ToUnixTimeMilliseconds();
                int pruned = await _blocks.PruneLocalCacheAsync(cutoff).ConfigureAwait(false);

                return new MirrorResult(true, stats.Applied, stats.Removed, pruned,
                    $"已对齐最近 {WindowDays} 天（更新 {stats.Applied}，移除 {stats.Removed}，清理 {pruned}）");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new MirrorResult(false, 0, 0, 0, "时间块镜像失败：" + ex.Message);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// 拉服务端目标列表并覆盖本地（跳过本地未同步的）。
        ///
        /// 目标的"累计投入 / 最后活动"是服务端现算的，本地算不出来（本地只有 30 天），
        /// 所以这两个数字只在联网时显示，离线时界面显示"—"，不猜。
        /// </summary>
        public async Task<IReadOnlyList<Goal>> FetchGoalsAsync(CancellationToken ct = default)
        {
            try
            {
                if (!await _conn.EnsureAsync().ConfigureAwait(false))
                {
                    return Array.Empty<Goal>();
                }

                var resp = await _conn.NewGoalClient()
                    .ListGoalsAsync(
                        new Empty(),
                        deadline: DateTime.UtcNow.Add(CallTimeout),
                        cancellationToken: ct)
                    .ConfigureAwait(false);

                var remoteIds = new HashSet<string>(resp.Goals.Select(g => g.Id), StringComparer.Ordinal);

                foreach (var g in resp.Goals)
                {
                    var local = await _goals.FindAsync(g.Id).ConfigureAwait(false);
                    if (local is not null && !local.Synced) continue;

                    var row = g.ToLocalGoal();
                    row.Synced = true;
                    row.SyncedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    await _goals.UpsertAsync(row).ConfigureAwait(false);
                }

                // 服务端已删除的 → 本地也删（只删已同步行，未同步的还在等待上传）
                var all = await _goals.GetByRangeAsync(0, long.MaxValue).ConfigureAwait(false);
                foreach (var stale in all)
                {
                    if (!stale.Synced || remoteIds.Contains(stale.Id)) continue;
                    await _goals.DeleteAsync(stale.Id).ConfigureAwait(false);
                }

                return resp.Goals.ToList();
            }
            catch
            {
                // 离线 / 服务端错误都不该让目标页打不开：调用方会退回本地数据
                return Array.Empty<Goal>();
            }
        }

        /// <summary>
        /// 服务端算好的按片统计（today / week / month）。
        ///
        /// 本地 30 天副本其实够算"本周"，但如果你在电脑端补录过、或另一台设备刚记的，
        /// 本地副本还没拉到。所以：本地先渲染（0ms），这个接口回来后直接覆盖 ——
        /// 和「今日总额」同一条思路。失败返回 null，调用方保持本地值，不显示错误。
        /// </summary>
        public async Task<PieceStatsDto?> GetPieceStatsAsync(string period, CancellationToken ct = default)
        {
            try
            {
                if (!await _conn.EnsureAsync().ConfigureAwait(false)) return null;

                var r = await _conn.NewBlockClient()
                    .GetPieceStatsAsync(
                        new PieceStatsRequest { Period = period },
                        deadline: DateTime.UtcNow.Add(CallTimeout),
                        cancellationToken: ct)
                    .ConfigureAwait(false);

                return new PieceStatsDto(
                    r.Period,
                    r.RangeStart,
                    r.RangeEnd,
                    r.TotalMinutes,
                    r.CoveredMinutes,
                    r.UnrecordedMinutes,
                    r.Pieces.Select(p => (p.PieceCode, p.Minutes, p.Count)).ToList());
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>按片统计的服务端结果。</summary>
    public sealed record PieceStatsDto(
        string Period,
        long RangeStart,
        long RangeEnd,
        int TotalMinutes,
        int CoveredMinutes,
        int UnrecordedMinutes,
        IReadOnlyList<(string PieceCode, int Minutes, int Count)> Pieces);
}
