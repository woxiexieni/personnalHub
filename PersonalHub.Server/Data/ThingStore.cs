using Personalhub;
using SQLite;

namespace personalHub.Server.Data
{
    /// <summary>
    /// 事件反思存储层 —— SQLite 落盘实现，结构与 <see cref="RecordStore"/> 对称。
    ///
    /// 独立一张表（things）、独立一个库连接，但**指向同一个库文件**：
    /// 账单和事件反思是同一个人仓库里的两类数据，备份/迁移时希望是一个文件带走。
    ///
    /// 线程安全：gRPC 并发进来，SQLiteConnection 不是线程安全的，这里用锁串行化。
    /// </summary>
    public class ThingStore
    {
        private readonly SQLiteConnection _db;
        private readonly object _lock = new();

        public ThingStore() : this(ResolveDefaultDbPath())
        {
        }

        /// <summary>指定库文件路径（测试/迁移用）。</summary>
        public ThingStore(string dbPath)
        {
            string? dir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            _db = new SQLiteConnection(
                dbPath,
                SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create | SQLiteOpenFlags.FullMutex);

            _db.CreateTable<ThingRow>();
            _db.ExecuteScalar<string>("PRAGMA journal_mode=WAL;");

            DbPath = dbPath;
            Console.WriteLine($"[ThingStore] SQLite 已就绪: {dbPath}");
        }

        /// <summary>当前使用的库文件绝对路径（排查用）。</summary>
        public string DbPath { get; }

        private static string ResolveDefaultDbPath()
        {
            // 与 RecordStore 用同一环境变量/同一默认路径，保证两类数据同库。
            string? custom = Environment.GetEnvironmentVariable("PERSONALHUB_DB");
            if (!string.IsNullOrWhiteSpace(custom))
            {
                return custom;
            }

            string baseDir = AppContext.BaseDirectory;
            string root = FindSolutionRoot(baseDir) ?? baseDir;
            return Path.Combine(root, "personalhub.db3");
        }

        private static string? FindSolutionRoot(string startDir)
        {
            var dir = new DirectoryInfo(startDir);
            while (dir != null)
            {
                if (dir.GetFiles("*.sln").Length > 0)
                {
                    return dir.FullName;
                }
                dir = dir.Parent;
            }
            return null;
        }

        /// <summary>
        /// 幂等写入：同 id 已存在则覆盖，否则新增。返回处理条数。
        /// 客户端同步会被重复触发（切前台、网络恢复），靠这里保证不会写重。
        /// </summary>
        public int Upsert(IEnumerable<Thing> things)
        {
            var list = things as IList<Thing> ?? things.ToList();
            if (list.Count == 0) return 0;

            lock (_lock)
            {
                _db.RunInTransaction(() =>
                {
                    foreach (var t in list)
                    {
                        _db.InsertOrReplace(ToRow(t));
                    }
                });
            }

            return list.Count;
        }

        /// <summary>取出全部事件反思。</summary>
        public IReadOnlyList<Thing> All()
        {
            lock (_lock)
            {
                return _db.Table<ThingRow>()
                    .ToList()
                    .Select(ToProto)
                    .ToList();
            }
        }

        /// <summary>取出某个自然日（服务器本地时区）的事件反思，按发生时间倒序。</summary>
        public IReadOnlyList<Thing> ByDay(DateTime localDate)
        {
            long startMs = new DateTimeOffset(localDate.Date).ToUnixTimeMilliseconds();
            long endMs = startMs + 24L * 60 * 60 * 1000;

            lock (_lock)
            {
                return _db.Table<ThingRow>()
                    .Where(t => t.OccurredAt >= startMs && t.OccurredAt < endMs)
                    .ToList()
                    .OrderByDescending(t => t.OccurredAt)
                    .Select(ToProto)
                    .ToList();
            }
        }

        private static ThingRow ToRow(Thing t) => new()
        {
            Id = t.Id,
            Title = t.Title,
            Tag = t.Tag,
            Emotion = t.Emotion,
            OccurredAt = t.OccurredAt,
            ObjectiveDescription = t.ObjectiveDescription,
            InitialThought = t.InitialThought,
            ActionTaken = t.ActionTaken,
            FinalResult = t.FinalResult,
            Reflection = t.Reflection,
            ActionPlan = t.ActionPlan,
            CreatedAt = t.CreatedAt,
        };

        private static Thing ToProto(ThingRow row) => new()
        {
            Id = row.Id,
            Title = row.Title ?? string.Empty,
            Tag = row.Tag ?? string.Empty,
            Emotion = row.Emotion ?? string.Empty,
            OccurredAt = row.OccurredAt,
            ObjectiveDescription = row.ObjectiveDescription ?? string.Empty,
            InitialThought = row.InitialThought ?? string.Empty,
            ActionTaken = row.ActionTaken ?? string.Empty,
            FinalResult = row.FinalResult ?? string.Empty,
            Reflection = row.Reflection ?? string.Empty,
            ActionPlan = row.ActionPlan ?? string.Empty,
            CreatedAt = row.CreatedAt,
        };
    }
}
