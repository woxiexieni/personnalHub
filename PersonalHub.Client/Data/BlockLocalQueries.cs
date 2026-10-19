using Personalhub;

namespace PersonalHub.Client.Data
{
    /// <summary>按片聚合的一行（分钟 + 条数）。</summary>
    public sealed record PieceMinutes(string PieceCode, int Minutes, int Count);

    /// <summary>镜像合并的结果。</summary>
    public sealed record BlockMergeStats(int Applied, int Removed);

    /// <summary>
    /// 只对「时间块表」有意义的本地查询 / 清理。
    ///
    /// 和 <see cref="RecordLocalQueries"/> 同一个理由做成扩展方法：
    /// 这些逻辑写死了 PieceCode / DurationMin 这些只有时间块才有的列，
    /// 放进泛型 <see cref="LocalRecordStore{T}"/> 就成了"能用但用错会炸"的陷阱。
    ///
    /// 这里全部是"读本地库"：录入页要 0ms 看到今天的合计，
    /// 等服务端回来再校正 —— 记完一条要立刻看到数字变了，否则人会再记一次。
    /// </summary>
    public static class BlockLocalQueries
    {
        /// <summary>区间内按片聚合，分钟降序。时长 0 的块计入条数、不计入分钟。</summary>
        public static async Task<List<PieceMinutes>> AggregateByPieceAsync(
            this LocalRecordStore<LocalBlock> store, long from, long to)
        {
            var rows = await store.GetByRangeAsync(from, to).ConfigureAwait(false);

            return rows
                .GroupBy(r => r.PieceCode ?? string.Empty)
                .Select(g => new PieceMinutes(
                    g.Key,
                    g.Sum(x => Math.Max(0, x.DurationMin)),
                    g.Count()))
                .OrderByDescending(x => x.Minutes)
                .ThenByDescending(x => x.Count)
                .ToList();
        }

        /// <summary>区间内每个片被点了多少次 —— 只用于决定"前排 6 个"里填空的那几个。</summary>
        public static async Task<IReadOnlyDictionary<string, int>> ClickFrequencyAsync(
            this LocalRecordStore<LocalBlock> store, long from, long to)
        {
            var rows = await store.GetByRangeAsync(from, to).ConfigureAwait(false);

            return rows
                .Where(r => !string.IsNullOrEmpty(r.PieceCode))
                .GroupBy(r => r.PieceCode)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        }

        /// <summary>
        /// 删掉超过窗口的**已同步**本地副本。
        /// 与账单同一条铁律：条件必须带 Synced = 1，否则离线期间的记录会被删光。
        /// 方法名带 Prune 而不是 Delete，是为了防止有人接错线。
        /// </summary>
        public static Task<int> PruneLocalCacheAsync(this LocalRecordStore<LocalBlock> store, long cutoffMs)
            => store.ExecuteAsync("DELETE FROM blocks WHERE Synced = 1 AND OccurredAt < ?", cutoffMs);

        /// <summary>
        /// 用服务端 30 天窗口覆盖本地（全量镜像）。
        ///
        /// 唯一必须小心的一条：**本地 Synced = false 的行永远不做覆盖或删除**。
        /// 那是"刚记完、还没传上去"的唯一一份，被服务端旧版本盖掉就是数据凭空消失。
        /// </summary>
        public static async Task<BlockMergeStats> MergeServerWindowAsync(
            this LocalRecordStore<LocalBlock> store,
            IEnumerable<Block> remote,
            long from,
            long to)
        {
            var remoteIds = new HashSet<string>(StringComparer.Ordinal);
            int applied = 0;

            foreach (var p in remote)
            {
                remoteIds.Add(p.Id);

                var local = await store.FindAsync(p.Id).ConfigureAwait(false);
                if (local is not null && !local.Synced) continue;

                if (p.Deleted)
                {
                    if (local is not null) await store.DeleteAsync(p.Id).ConfigureAwait(false);
                    applied++;
                    continue;
                }

                var row = p.ToLocalBlock();
                row.Synced = true;
                row.SyncedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                await store.UpsertAsync(row).ConfigureAwait(false);
                applied++;
            }

            int removed = 0;
            var window = await store.GetByRangeAsync(from, to, includeDeleted: true).ConfigureAwait(false);
            foreach (var stale in window)
            {
                if (!stale.Synced) continue;
                if (remoteIds.Contains(stale.Id)) continue;

                await store.DeleteAsync(stale.Id).ConfigureAwait(false);
                removed++;
            }

            return new BlockMergeStats(applied, removed);
        }
    }
}
