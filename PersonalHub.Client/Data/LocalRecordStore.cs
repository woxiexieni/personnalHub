using SQLite;

namespace PersonalHub.Client.Data
{
    /// <summary>
    /// 本地 SQLite 账单库的封装。
    ///
    /// 这是"离线优先"的地基：所有录入先落到这里，界面一律从这里读；
    /// 联网上传只是把 Synced=false 的记录搬去服务端，不阻塞用户操作。
    ///
    /// 懒初始化（首次访问才建表），避免 App 启动时做 IO。
    /// </summary>
    public class LocalRecordStore
    {
        private readonly SQLiteAsyncConnection _db;
        private readonly SemaphoreSlim _initGate = new(1, 1);
        private bool _ready;

        public LocalRecordStore(string dbPath)
        {
            _db = new SQLiteAsyncConnection(
                dbPath,
                SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create | SQLiteOpenFlags.SharedCache);
        }

        /// <summary>确保本地库与表已就绪（懒初始化，内部并发安全）。</summary>
        public async Task EnsureReadyAsync()
        {
            if (_ready) return;

            await _initGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!_ready)
                {
                    await _db.CreateTableAsync<LocalRecord>().ConfigureAwait(false);
                    _ready = true;
                }
            }
            finally
            {
                _initGate.Release();
            }
        }

        /// <summary>写入一条本地记录。返回受影响行数（1 = 成功）。</summary>
        public async Task<int> InsertAsync(LocalRecord record)
        {
            await EnsureReadyAsync().ConfigureAwait(false);
            return await _db.InsertAsync(record).ConfigureAwait(false);
        }

        /// <summary>取某个自然日（本地时区）的记录，按时间倒序。</summary>
        public async Task<List<LocalRecord>> GetByLocalDateAsync(DateTime localDate)
        {
            await EnsureReadyAsync().ConfigureAwait(false);

            long startMs = new DateTimeOffset(localDate.Date).ToUnixTimeMilliseconds();
            long endMs = startMs + 24L * 60 * 60 * 1000;

            var list = await _db.Table<LocalRecord>()
                .Where(r => r.SpentAt >= startMs && r.SpentAt < endMs)
                .ToListAsync()
                .ConfigureAwait(false);

            list.Sort((a, b) => b.SpentAt.CompareTo(a.SpentAt));
            return list;
        }

        /// <summary>取所有待同步（Synced=false）的记录。</summary>
        public async Task<List<LocalRecord>> GetUnsyncedAsync()
        {
            await EnsureReadyAsync().ConfigureAwait(false);
            return await _db.Table<LocalRecord>()
                .Where(r => r.Synced == false)
                .ToListAsync()
                .ConfigureAwait(false);
        }

        /// <summary>待同步条数。</summary>
        public async Task<int> CountUnsyncedAsync()
        {
            await EnsureReadyAsync().ConfigureAwait(false);
            return await _db.Table<LocalRecord>()
                .Where(r => r.Synced == false)
                .CountAsync()
                .ConfigureAwait(false);
        }

        /// <summary>把指定的记录标记为"已同步"。只有服务端确认接收的 id 才应传进来。</summary>
        public async Task MarkSyncedAsync(IEnumerable<string> ids, long syncedAtMs)
        {
            await EnsureReadyAsync().ConfigureAwait(false);

            foreach (var id in ids)
            {
                var row = await _db.FindAsync<LocalRecord>(id).ConfigureAwait(false);
                if (row == null) continue;

                row.Synced = true;
                row.SyncedAt = syncedAtMs;
                await _db.UpdateAsync(row).ConfigureAwait(false);
            }
        }
    }
}
