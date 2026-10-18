using Personalhub;

namespace PersonalHub.Client.Data
{
    /// <summary>
    /// 只对「账单表」有意义的本地查询 / 清理。
    ///
    /// 为什么做成扩展方法而不是塞进 <see cref="LocalRecordStore{T}"/>：
    /// 这些 SQL 里写死了 Amount / Direction / SpentAt 这些**只有账单表才有的列**，
    /// 放进泛型类就变成"能用但用错会炸"的陷阱。挂在闭合泛型
    /// <c>LocalRecordStore&lt;LocalRecord&gt;</c> 上，编译器就替我们挡住了误用。
    /// </summary>
    public static class RecordLocalQueries
    {
        private const string Records = "records";

        // ============================================================
        // 今日总额（本地即时值）
        // ============================================================

        /// <summary>
        /// 区间内支出合计，单位：分。
        ///
        /// 先 ROUND 再 CAST —— SQLite 的 CAST 是**截断**，直接 CAST(amount*100) 会把 0.29 算成 28 分。
        /// 这个顺序写反了不会报错，只会让总额长期偏小几分钱，几乎不可能靠肉眼发现。
        /// </summary>
        public static Task<long> SumExpenseCentsAsync(this LocalRecordStore<LocalRecord> store, long from, long to)
            => store.ExecuteScalarAsync<long>(
                "SELECT COALESCE(SUM(CAST(ROUND(Amount * 100) AS INTEGER)), 0) " +
                $"FROM {Records} WHERE Deleted = 0 AND Direction = 0 AND SpentAt >= ? AND SpentAt < ?",
                from, to);

        /// <summary>区间内收入合计，单位：分。</summary>
        public static Task<long> SumIncomeCentsAsync(this LocalRecordStore<LocalRecord> store, long from, long to)
            => store.ExecuteScalarAsync<long>(
                "SELECT COALESCE(SUM(CAST(ROUND(Amount * 100) AS INTEGER)), 0) " +
                $"FROM {Records} WHERE Deleted = 0 AND Direction = 1 AND SpentAt >= ? AND SpentAt < ?",
                from, to);

        /// <summary>区间内支出笔数。</summary>
        public static Task<int> CountExpenseAsync(this LocalRecordStore<LocalRecord> store, long from, long to)
            => store.ExecuteScalarAsync<int>(
                $"SELECT COUNT(*) FROM {Records} WHERE Deleted = 0 AND Direction = 0 AND SpentAt >= ? AND SpentAt < ?",
                from, to);

        /// <summary>区间内待同步条数（含待上传的删除墓碑）。</summary>
        public static async Task<int> CountPendingAsync(this LocalRecordStore<LocalRecord> store, long from, long to)
        {
            var rows = await store.GetByRangeAsync(from, to, includeDeleted: true).ConfigureAwait(false);
            return rows.Count(r => !r.Synced);
        }

        // ============================================================
        // 清理（30 天窗口）
        // ============================================================

        /// <summary>
        /// 删掉超过窗口的**已同步**本地副本。
        ///
        /// 三条铁律：
        /// 1. 只删本地，**绝不调服务端删除接口** —— 方法名带 Prune 就是为了防止有人接错线；
        /// 2. 条件必须带 Synced = 1 —— 否则"出差两周没连上服务端"的未同步记录会被删光，
        ///    而那是**唯一一份**，删了就真没了；
        /// 3. 已同步的删除墓碑（Deleted=1, Synced=1）也一起清掉，否则表只增不减。
        /// </summary>
        public static Task<int> PruneLocalCacheAsync(this LocalRecordStore<LocalRecord> store, long cutoffMs)
            => store.ExecuteAsync($"DELETE FROM {Records} WHERE Synced = 1 AND SpentAt < ?", cutoffMs);

        // ============================================================
        // 镜像合并
        // ============================================================

        public sealed record MergeStats(int Applied, int Removed);

        /// <summary>
        /// 用服务端的 30 天窗口结果覆盖本地（全量镜像，不是增量协议）。
        ///
        /// 为什么不做增量：一个月最多几百条、不到 100KB，全量拉回来直接覆盖
        /// 既简单又自愈（本地库被写脏了，一次同步就修回来），不需要版本向量、不需要冲突合并。
        ///
        /// 唯一必须小心的一条：**本地 Synced=false 的行永远不做覆盖或删除**。
        /// 用户刚记的账不能被服务端的旧版本盖掉 —— 这是唯一会造成"数据凭空消失"的 bug。
        /// </summary>
        public static async Task<MergeStats> MergeServerWindowAsync(
            this LocalRecordStore<LocalRecord> store,
            IEnumerable<Personalhub.Record> remote,
            long from,
            long to)
        {
            var remoteIds = new HashSet<string>(StringComparer.Ordinal);
            int applied = 0;

            foreach (var p in remote)
            {
                remoteIds.Add(p.Id);

                var local = await store.FindAsync(p.Id).ConfigureAwait(false);

                // 本地有待同步改动（可能还没上传、也可能是待上传的墓碑）→ 以本地为准
                if (local is not null && !local.Synced) continue;

                if (p.Deleted)
                {
                    if (local is not null) await store.DeleteAsync(p.Id).ConfigureAwait(false);
                    applied++;
                    continue;
                }

                await store.UpsertAsync(ToLocal(p)).ConfigureAwait(false);
                applied++;
            }

            // 服务端已不存在的本地已同步行 → 本地也删（保持镜像一致，不累积幽灵数据）
            int removed = 0;
            var window = await store.GetByRangeAsync(from, to, includeDeleted: true).ConfigureAwait(false);
            foreach (var stale in window)
            {
                if (!stale.Synced) continue;
                if (remoteIds.Contains(stale.Id)) continue;

                await store.DeleteAsync(stale.Id).ConfigureAwait(false);
                removed++;
            }

            return new MergeStats(applied, removed);
        }

        /// <summary>proto → 本地行。映射规则集中在 RecordExtentions，这里只补同步标记。</summary>
        private static LocalRecord ToLocal(Personalhub.Record p)
        {
            var row = p.ToLocalRecord();
            row.Synced = true;
            row.SyncedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return row;
        }
    }
}
