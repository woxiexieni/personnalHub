using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SQLite;

namespace personalHub.Server.Data
{
    /// <summary>
    /// 全进程唯一的 SQLite 连接（records / things / categories 同库同连接）。
    ///
    /// 为什么要有这一层：
    /// 原来 RecordStore 和 ThingStore 各自 new 了一个 SQLiteConnection 指向同一个库文件。
    /// 单表时勉强能跑，但 R1 之后要写三张表（还加了管理端网页的并发写入），
    /// 多连接 + 默认 busy_timeout=0 会直接抛 "database is locked"。
    /// 现在统一成"一个连接 + 一把锁"，SQLite 这边就不可能自己跟自己抢锁。
    ///
    /// 线程安全：SQLiteConnection 不是线程安全的，所有访问都必须 lock (Sync)。
    /// 本项目的量级（单人、局域网）用一把全局锁完全够；真到要提并发再换连接池。
    /// </summary>
    public sealed class Db
    {
        /// <summary>唯一连接。不要在外部直接并发使用，必须配合 <see cref="Sync"/>。</summary>
        public SQLiteConnection Conn { get; }

        /// <summary>全局串行化锁。</summary>
        public object Sync { get; } = new object();

        /// <summary>当前库文件绝对路径（启动日志 / 排查用）。</summary>
        public string Path { get; }

        public Db() : this(ResolveDefaultDbPath())
        {
        }

        public Db(string dbPath)
        {
            string? dir = System.IO.Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            Path = dbPath;
            Conn = new SQLiteConnection(
                dbPath,
                SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create | SQLiteOpenFlags.FullMutex);

            // WAL：写入不阻塞读取，断电更不容易损坏库。
            // 注意：PRAGMA journal_mode 会返回一行结果，必须用 ExecuteScalar；
            // 用 Execute()（内部走 ExecuteNonQuery）会因"语句返回了行"抛 SQLiteException: not an error。
            Conn.ExecuteScalar<string>("PRAGMA journal_mode=WAL;");

            // 即便现在只有一个连接，也留个兜底：万一以后有人又开了第二个连接，
            // 写冲突会等 3 秒而不是立刻报 "database is locked"。
            try { Conn.ExecuteScalar<int>("PRAGMA busy_timeout=3000;"); } catch { }

            CreateTables();
            Migrate();

            Console.WriteLine($"[Db] SQLite 已就绪: {dbPath}（records / things / categories / blocks / goals）");
        }

        /// <summary>在全局锁 + 事务里执行，异常会回滚。</summary>
        public void Transaction(Action<SQLiteConnection> work)
        {
            lock (Sync)
            {
                Conn.RunInTransaction(() => work(Conn));
            }
        }

        private void CreateTables()
        {
            Conn.CreateTable<RecordRow>();
            Conn.CreateTable<ThingRow>();
            Conn.CreateTable<CategoryRow>();

            // R2：时间块 + 目标。新建表，CreateTable 就是完整的建表语句，不需要补列迁移。
            Conn.CreateTable<BlockRow>();
            Conn.CreateTable<GoalRow>();
            Conn.CreateTable<PlanRow>();
            Conn.CreateTable<PatternRow>();
            Conn.CreateTable<PracticeRow>();

            // R6：想法（原材料仓库，与"行动"无关）
            Conn.CreateTable<IdeaRow>();
        }

        /// <summary>
        /// 结构迁移：给老库补列 + 建索引 + 灌分类。
        ///
        /// 为什么不完全依赖 CreateTable：sqlite-net 的 CreateTable 确实会给老表补列，
        /// 但补出来的是「可空、无默认值」——老数据那一批行会是 NULL。
        /// 而 `WHERE direction = 0` 匹配不到 NULL，统计会静默漏掉全部历史账单。
        /// 所以这里显式 ALTER 带 DEFAULT，并把可能残留的 NULL 回填掉。
        /// </summary>
        private void Migrate()
        {
            EnsureColumn("records", "direction",  "INTEGER NOT NULL DEFAULT 0", "0");
            EnsureColumn("records", "updated_at", "INTEGER NOT NULL DEFAULT 0", "created_at");
            EnsureColumn("records", "deleted",    "INTEGER NOT NULL DEFAULT 0", "0");
            EnsureColumn("things",  "updated_at", "INTEGER NOT NULL DEFAULT 0", "created_at");
            EnsureColumn("things",  "deleted",    "INTEGER NOT NULL DEFAULT 0", "0");

            // 老数据的 updated_at 用 created_at 兜底。留在 0 会让首次版本比较失效
            // （0 < 任何值 → 老记录永远被判定为"过期"，管理端改一次就被拒）。
            Conn.Execute("UPDATE records SET updated_at = created_at WHERE updated_at = 0");
            Conn.Execute("UPDATE things  SET updated_at = created_at WHERE updated_at = 0");

            // 索引：所有查询都是「区间 + 方向」或「分类 + 区间」，没有一条是单列查询。
            Conn.Execute("CREATE INDEX IF NOT EXISTS ix_records_spent     ON records(spent_at)");
            Conn.Execute("CREATE INDEX IF NOT EXISTS ix_records_dir_spent ON records(direction, spent_at)");
            Conn.Execute("CREATE INDEX IF NOT EXISTS ix_records_cat_spent ON records(category_id, spent_at)");
            Conn.Execute("CREATE INDEX IF NOT EXISTS ix_things_occurred   ON things(occurred_at)");

            // R2：时间块的三条查询路径分别是「按区间」「按目标+区间」「按片+区间」，
            // 没有一条是单列查询，所以索引都带 occurred_at。
            Conn.Execute("CREATE INDEX IF NOT EXISTS ix_blocks_occurred ON blocks(occurred_at)");
            Conn.Execute("CREATE INDEX IF NOT EXISTS ix_blocks_goal     ON blocks(goal_id, occurred_at)");
            Conn.Execute("CREATE INDEX IF NOT EXISTS ix_blocks_piece    ON blocks(piece_code, occurred_at)");
            Conn.Execute("CREATE INDEX IF NOT EXISTS ix_plans_day      ON plans(day)");
            Conn.Execute("CREATE INDEX IF NOT EXISTS ix_practices_pat ON practices(pattern_id, occurred_at)");
            Conn.Execute("CREATE INDEX IF NOT EXISTS ix_practices_at  ON practices(occurred_at)");

            // 想法库是长期攒的（可能上万条），列表按"时间倒序 + 可选按类型筛"，
            // 所以索引带 created_at；单列 kind 没用（几乎不会只按类型查而不排序）。
            Conn.Execute("CREATE INDEX IF NOT EXISTS ix_ideas_created ON ideas(created_at)");
            Conn.Execute("CREATE INDEX IF NOT EXISTS ix_ideas_kind    ON ideas(kind, created_at)");

            SeedCategories();
        }

        private void EnsureColumn(string table, string column, string ddl, string fillExpr)
        {
            var cols = Conn.Query<TableColumn>($"PRAGMA table_info({table})")
                           .Select(c => c.name)
                           .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (!cols.Contains(column))
            {
                Conn.Execute($"ALTER TABLE {table} ADD COLUMN {column} {ddl}");
            }
            else
            {
                // 列已存在但仍可能有 NULL（CreateTable 自动补列的那种情况）
                Conn.Execute($"UPDATE {table} SET {column} = {fillExpr} WHERE {column} IS NULL");
            }
        }

        /// <summary>
        /// 灌内置分类。用 INSERT OR IGNORE：只补不改 ——
        /// 你以后在管理端把「餐饮」改名成「吃饭」，重跑迁移不会把它改回去。
        /// </summary>
        private void SeedCategories()
        {
            var seeds = new (int Id, string Name, int Direction, int Sort)[]
            {
                (1,   "餐饮",     0, 10),
                (2,   "交通",     0, 20),
                (3,   "购物",     0, 30),
                (4,   "居住",     0, 40),
                (5,   "娱乐",     0, 50),
                (6,   "医疗",     0, 60),
                (7,   "其他",     0, 99),
                (101, "工资",     1, 10),
                (102, "奖金",     1, 20),
                (103, "理财收益", 1, 30),
                (104, "退款",     1, 40),
                (105, "其他收入", 1, 99),
            };

            foreach (var s in seeds)
            {
                Conn.Execute(
                    "INSERT OR IGNORE INTO categories (id,name,direction,sort_order,enabled,updated_at) VALUES (?,?,?,?,1,?)",
                    s.Id, s.Name, s.Direction, s.Sort, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            }
        }

        private static string ResolveDefaultDbPath()
        {
            string? custom = Environment.GetEnvironmentVariable("PERSONALHUB_DB");
            if (!string.IsNullOrWhiteSpace(custom))
            {
                return custom;
            }

            // 默认放在「仓库根目录」（含 .sln 的文件夹），开发时一眼可见，备份/查看都方便。
            string baseDir = AppContext.BaseDirectory;
            string root = FindSolutionRoot(baseDir) ?? baseDir;
            return System.IO.Path.Combine(root, "personalhub.db3");
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

        /// <summary>PRAGMA table_info 的返回行（只需要列名）。</summary>
        private sealed class TableColumn
        {
            public string name { get; set; } = string.Empty;
        }
    }
}
