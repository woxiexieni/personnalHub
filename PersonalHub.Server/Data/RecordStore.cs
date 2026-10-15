using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Personalhub;
using SQLite;

namespace personalHub.Server.Data
{
    /// <summary>
    /// 账单存储层 —— SQLite 落盘实现（原为内存 List，进程重启即清空）。
    ///
    /// 对外方法签名与内存版完全一致（Add / Upsert / All / ByDay / ToLocalTime），
    /// 所以上层的 <see cref="Services.RecordServiceImpl"/> 一行都不用改。
    ///
    /// 线程安全：gRPC 多个请求会并发进来，而 SQLiteConnection 不是线程安全的，
    /// 这里用一把锁把访问串行化（本项目量级足够；将来要提并发再换连接池/WAL 读写分离）。
    /// </summary>
    public class RecordStore
    {
        private readonly SQLiteConnection _db;
        private readonly object _lock = new();

        /// <summary>默认库文件路径：仓库根目录（含 PersonalHub.sln 的文件夹）下的 personalhub.db3。可用环境变量 PERSONALHUB_DB 覆盖。</summary>
        public RecordStore() : this(ResolveDefaultDbPath())
        {
        }

        /// <summary>指定库文件路径（测试/迁移用）。</summary>
        public RecordStore(string dbPath)
        {
            string? dir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            _db = new SQLiteConnection(
                dbPath,
                SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create | SQLiteOpenFlags.FullMutex);

            _db.CreateTable<RecordRow>();

            // WAL：写入不阻塞读取，且意外断电时更不容易损坏库文件。
            // 注意：PRAGMA journal_mode 会返回一行结果，必须用 ExecuteScalar；
            // 用 Execute()（内部走 ExecuteNonQuery）会因"语句返回了行"而抛 SQLiteException: not an error。
            _db.ExecuteScalar<string>("PRAGMA journal_mode=WAL;");

            DbPath = dbPath;
            Console.WriteLine($"[RecordStore] SQLite 已就绪: {dbPath}");
        }

        /// <summary>当前使用的库文件绝对路径（启动日志/排查用）。</summary>
        public string DbPath { get; }

        private static string ResolveDefaultDbPath()
        {
            string? custom = Environment.GetEnvironmentVariable("PERSONALHUB_DB");
            if (!string.IsNullOrWhiteSpace(custom))
            {
                return custom;
            }

            // 默认放在「仓库根目录」（含 PersonalHub.sln 的那个文件夹），开发时一眼可见、方便备份与查看。
            // 从 exe 所在目录逐级向上找 .sln；找不到（例如发布到别处）就退回 exe 所在目录。
            string baseDir = AppContext.BaseDirectory;
            string root = FindSolutionRoot(baseDir) ?? baseDir;
            return Path.Combine(root, "personalhub.db3");
        }

        /// <summary>从 startDir 起逐级向上查找包含 .sln 的目录，找不到返回 null。</summary>
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

        /// <summary>追加一批账单，返回实际写入条数（同 id 覆盖）。</summary>
        public int Add(IEnumerable<Record> records) => Upsert(records);

        /// <summary>
        /// 幂等写入：同 id 已存在则覆盖，否则新增。返回处理条数。
        ///
        /// 这是"离线优先 + 回家自动同步"的正确性保障：
        /// 同步可能被重复触发（开机一次、切回前台又一次），若只是 Append，
        /// 同一条记录会被写入多次，导致账单金额翻倍。
        /// </summary>
        public int Upsert(IEnumerable<Record> records)
        {
            var list = records as IList<Record> ?? records.ToList();
            if (list.Count == 0) return 0;

            lock (_lock)
            {
                _db.RunInTransaction(() =>
                {
                    foreach (var r in list)
                    {
                        _db.InsertOrReplace(ToRow(r));
                    }
                });
            }

            return list.Count;
        }

        /// <summary>取出全部账单。</summary>
        public IReadOnlyList<Record> All()
        {
            lock (_lock)
            {
                return _db.Table<RecordRow>()
                    .ToList()
                    .Select(ToProto)
                    .ToList();
            }
        }

        /// <summary>取出某个自然日（服务器本地时区）的账单。</summary>
        public IReadOnlyList<Record> ByDay(DateTime localDate)
        {
            // spent_at 约定为 Unix 毫秒时间戳（与客户端一致）
            long startMs = new DateTimeOffset(localDate.Date).ToUnixTimeMilliseconds();
            long endMs = startMs + 24L * 60 * 60 * 1000;

            lock (_lock)
            {
                return _db.Table<RecordRow>()
                    .Where(r => r.SpentAt >= startMs && r.SpentAt < endMs)
                    .ToList()
                    .Select(ToProto)
                    .ToList();
            }
        }

        /// <summary>把 Unix 毫秒时间戳还原成服务器本地时间。</summary>
        public static DateTime ToLocalTime(long unixMs)
            => DateTimeOffset.FromUnixTimeMilliseconds(unixMs).LocalDateTime;

        private static RecordRow ToRow(Record r) => new()
        {
            Id = r.Id,
            Amount = r.Amount,
            CategoryId = r.CategoryId,
            Note = r.Note,
            SpentAt = r.SpentAt,
            PayMethod = r.PayMethod,
            CreatedAt = r.CreatedAt,
        };

        private static Record ToProto(RecordRow row) => new()
        {
            Id = row.Id,
            Amount = row.Amount,
            CategoryId = row.CategoryId,
            Note = row.Note ?? string.Empty,
            SpentAt = row.SpentAt,
            PayMethod = row.PayMethod ?? string.Empty,
            CreatedAt = row.CreatedAt,
        };
    }
}
