using System.Collections.Concurrent;
using System.Reflection;
using PersonalHub.Client.Services;
using SQLite;

namespace PersonalHub.Client.Data
{
    /// <summary>
    /// 本地 SQLite 库的封装（账单表 / 事件反思表共用一个库文件）。
    ///
    /// 这是"离线优先"的地基：所有录入先落到这里，界面一律从这里读；
    /// 联网上传只是把 Synced=false 的记录搬去服务端，不阻塞用户操作。
    ///
    /// R1 起本地保留「最近 30 天 + 全部未同步」。本地这份只是**可丢弃的副本**，
    /// 任何时候删掉都能从服务端重建 —— 所以清理策略可以很笨（见 RecordLocalQueries.PruneLocalCacheAsync）。
    ///
    /// 懒初始化（首次访问才建表 + 迁移），避免 App 启动时做 IO。
    /// </summary>
    public class LocalRecordStore<T> where T : LocalRecordBase, new()
    {
        /// <summary>
        /// 历史库补列清单。
        ///
        /// 为什么不用 sqlite-net 的自动迁移：CreateTable 给老表补出来的是「可空、无默认值」，
        /// 老行会是 NULL，而 `WHERE Deleted = 0` 匹配不到 NULL —— 结果是老账在新版本里全部消失。
        /// 这里显式 ALTER 带 DEFAULT，并把可能残留的 NULL 回填掉。
        /// </summary>
        private static readonly (string Table, string Column, string Ddl)[] KnownColumns =
        {
            ("records",       "Direction", "INTEGER NOT NULL DEFAULT 0"),
            ("records",       "UpdatedAt", "INTEGER NOT NULL DEFAULT 0"),
            ("records",       "Deleted",   "INTEGER NOT NULL DEFAULT 0"),
            ("ThingsRecords", "UpdatedAt", "INTEGER NOT NULL DEFAULT 0"),
            ("ThingsRecords", "Deleted",   "INTEGER NOT NULL DEFAULT 0"),
        };

        /// <summary>
        /// 同一个库文件只开一个连接。
        ///
        /// 以前是每个 Store（账单 / 反思 / 时间块 / 目标 / 计划 / 模式 / 练习）各开一个，
        /// 同一个库上挂 7~8 个 SQLiteAsyncConnection：每个都要各自打开文件、各自跑一次
        /// CreateTable + 迁移查询。冷启动/首次进页面时这些开销叠加，正是"要等七八秒"的主要来源。
        /// 连接本身是线程安全的（内部排队），共享没有副作用。
        /// </summary>
        private static readonly ConcurrentDictionary<string, SQLiteAsyncConnection> SharedConnections = new();

        private readonly SQLiteAsyncConnection _db;
        private readonly SemaphoreSlim _initGate = new(1, 1);
        private bool _ready;

        public LocalRecordStore(string dbPath)
        {
            string key = Path.GetFullPath(dbPath);

            _db = SharedConnections.GetOrAdd(key, p => new SQLiteAsyncConnection(
                p,
                SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create | SQLiteOpenFlags.SharedCache));
        }

        /// <summary>本 store 对应的表名（raw SQL 用）。</summary>
        public string TableName =>
            typeof(T).GetCustomAttribute<TableAttribute>()?.Name ?? typeof(T).Name;

        /// <summary>确保本地库与表已就绪（懒初始化，内部并发安全）。</summary>
        public async Task EnsureReadyAsync()
        {
            if (_ready) return;

            await _initGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!_ready)
                {
                    await _db.CreateTableAsync<T>().ConfigureAwait(false);
                    await MigrateAsync().ConfigureAwait(false);
                    _ready = true;
                }
            }
            finally
            {
                _initGate.Release();
            }
        }

        // ============================================================
        // 迁移
        // ============================================================

        private async Task MigrateAsync()
        {
            var tables = (await _db.QueryAsync<SqlNameRow>(
                    "SELECT name FROM sqlite_master WHERE type = 'table'").ConfigureAwait(false))
                .Select(x => x.name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var m in KnownColumns)
            {
                if (!tables.Contains(m.Table)) continue;

                var cols = (await _db.QueryAsync<SqlNameRow>($"PRAGMA table_info({m.Table})").ConfigureAwait(false))
                    .Select(x => x.name)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                if (cols.Contains(m.Column))
                {
                    // 列在但可能有 NULL（自动补列遗留），回填成 0
                    await _db.ExecuteAsync($"UPDATE {m.Table} SET {m.Column} = 0 WHERE {m.Column} IS NULL")
                        .ConfigureAwait(false);
                }
                else
                {
                    await _db.ExecuteAsync($"ALTER TABLE {m.Table} ADD COLUMN {m.Column} {m.Ddl}")
                        .ConfigureAwait(false);
                }
            }
        }

        // ============================================================
        // 写
        // ============================================================

        /// <summary>写入一条本地记录。返回受影响行数（1 = 成功）。</summary>
        public async Task<int> InsertAsync(T record)
        {
            await EnsureReadyAsync().ConfigureAwait(false);
            return await _db.InsertAsync(record).ConfigureAwait(false);
        }

        /// <summary>按主键覆盖写入（镜像同步用：服务端版本覆盖本地）。</summary>
        public async Task<int> UpsertAsync(T record)
        {
            await EnsureReadyAsync().ConfigureAwait(false);
            return await _db.InsertOrReplaceAsync(record).ConfigureAwait(false);
        }

        /// <summary>按主键更新（编辑已有记录用）。</summary>
        public async Task<int> UpdateAsync(T record)
        {
            await EnsureReadyAsync().ConfigureAwait(false);
            return await _db.UpdateAsync(record).ConfigureAwait(false);
        }

        /// <summary>按主键物理删除。只用于「从未同步过」的记录，见 RecordEntryPage 的删除逻辑。</summary>
        public async Task<int> DeleteAsync(string id)
        {
            await EnsureReadyAsync().ConfigureAwait(false);
            return await _db.DeleteAsync<T>(id).ConfigureAwait(false);
        }

        /// <summary>把指定的记录标记为"已同步"。只有服务端确认接收的 id 才应传进来。</summary>
        public async Task MarkSyncedAsync(IEnumerable<string> ids, long syncedAtMs)
        {
            await EnsureReadyAsync().ConfigureAwait(false);

            foreach (var id in ids)
            {
                var row = await _db.FindAsync<T>(id).ConfigureAwait(false);
                if (row == null) continue;

                row.Synced = true;
                row.SyncedAt = syncedAtMs;
                await _db.UpdateAsync(row).ConfigureAwait(false);
            }
        }

        // ============================================================
        // 读
        // ============================================================

        public async Task<T?> FindAsync(string id)
        {
            await EnsureReadyAsync().ConfigureAwait(false);
            return await _db.FindAsync<T>(id).ConfigureAwait(false);
        }

        /// <summary>取某个自然日（固定 UTC+8，与服务端统计口径一致）的记录，按时间倒序。</summary>
        public async Task<List<T>> GetByLocalDateAsync(DateTime localDate)
        {
            var day = Period.StartOfDay(new DateTimeOffset(localDate));
            return await GetByRangeAsync(day.ToUnixTimeMilliseconds(), day.AddDays(1).ToUnixTimeMilliseconds())
                .ConfigureAwait(false);
        }

        /// <summary>
        /// 取 [from, to) 区间内的记录（按"归天时间"过滤）。
        ///
        /// 为什么在内存里过滤而不是写 SQL：
        /// 泛型 T 的归天字段名不同（账单是 SpentAt、事件是 OccurredAt），
        /// 拼 SQL 需要类型判断；而 30 天最多几百行，内存过滤完全够用，还不会写错字段名。
        /// </summary>
        public async Task<List<T>> GetByRangeAsync(long from, long to, bool includeDeleted = false)
        {
            await EnsureReadyAsync().ConfigureAwait(false);

            var all = await _db.Table<T>().ToListAsync().ConfigureAwait(false);
            return all
                .Where(r => includeDeleted || !IsDeleted(r))
                .Where(r => r.DayTimestamp >= from && r.DayTimestamp < to)
                .OrderByDescending(r => r.DayTimestamp)
                .ToList();
        }

        /// <summary>取所有待同步（Synced=false）的记录。包含"待上传的墓碑"，它们也需要发给服务端。</summary>
        public async Task<List<T>> GetUnsyncedAsync()
        {
            await EnsureReadyAsync().ConfigureAwait(false);
            return await _db.Table<T>()
                .Where(r => r.Synced == false)
                .ToListAsync()
                .ConfigureAwait(false);
        }

        /// <summary>待同步条数。</summary>
        public async Task<int> CountUnsyncedAsync()
        {
            await EnsureReadyAsync().ConfigureAwait(false);
            return await _db.Table<T>()
                .Where(r => r.Synced == false)
                .CountAsync()
                .ConfigureAwait(false);
        }

        // ============================================================
        // raw SQL（供 RecordLocalQueries 这类"表专用"逻辑使用）
        // ============================================================

        public async Task<int> ExecuteAsync(string sql, params object[] args)
        {
            await EnsureReadyAsync().ConfigureAwait(false);
            return await _db.ExecuteAsync(sql, args).ConfigureAwait(false);
        }

        public async Task<U> ExecuteScalarAsync<U>(string sql, params object[] args)
        {
            await EnsureReadyAsync().ConfigureAwait(false);
            return await _db.ExecuteScalarAsync<U>(sql, args).ConfigureAwait(false);
        }

        private static bool IsDeleted(T row) => row.Deleted;

        /// <summary>sqlite_master / PRAGMA table_info 的通用返回行。</summary>
        private sealed class SqlNameRow
        {
            public string name { get; set; } = string.Empty;
        }
    }
}
