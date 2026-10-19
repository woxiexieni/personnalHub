using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using SQLite;

namespace personalHub.Server.Data
{
    /// <summary>备份的一份快照。</summary>
    public sealed class BackupFile
    {
        public string Name { get; init; } = string.Empty;
        public DateTime Date { get; init; }
        public long SizeBytes { get; init; }

        /// <summary>各表条数；打不开（文件损坏 / 一半拷贝）时为 null。</summary>
        public IReadOnlyDictionary<string, int>? Counts { get; init; }

        public bool Readable => Counts is not null;
    }

    /// <summary>
    /// 数据库的本地每日快照。
    ///
    /// 为什么必须有它：服务端是唯一数据源，手机只缓存 30 天。
    /// 这个文件一旦损坏或被误删，手机端最多 30 天后也会跟着变成空 —— 那是不可逆的。
    /// 所以备份不是"以后再说"的功能，是这套系统敢不敢长期用的前提。
    ///
    /// 三条实现上的硬要求：
    ///  1. **必须用 `VACUUM INTO`，不能直接复制文件**。库开着 WAL，
    ///     主文件里可能缺最近的事务，复制出来的副本会"少几条"且不报错。
    ///  2. **一天只做一次**（同名文件存在就跳过），并在长期运行的服务里用定时器补做 ——
    ///     服务端常常开着过夜，只在启动时备份会漏掉整天的数据。
    ///  3. **失败必须能被看见**（页面上的红字），静默失败的备份等于没有备份。
    ///
    /// 保留策略：最近 14 天每天 1 份 + 每周日 12 份 + 每月 1 号 12 份。
    /// 单人库一年也就几十 MB，不需要更激进的清理。
    /// </summary>
    public sealed class BackupService
    {
        private const string Prefix = "personalhub-";
        private const string Suffix = ".db3";

        private const int KeepDailyDays = 14;
        private const int KeepWeeklyDays = 7 * 12;
        private const int KeepMonthlyDays = 365;

        private readonly Db _db;

        public BackupService(Db db)
        {
            _db = db;

            // 默认放在 db 同目录的 backups/。仍然允许用环境变量指到别处（比如另一块盘），
            // 但不做任何云端上传 —— 这是刻意的：备份要可解释、可自己掌控。
            string? custom = Environment.GetEnvironmentVariable("PERSONALHUB_BACKUP_DIR");
            Directory = string.IsNullOrWhiteSpace(custom)
                ? Path.Combine(Path.GetDirectoryName(db.Path) ?? ".", "backups")
                : custom;
        }

        public string Directory { get; }

        /// <summary>最近一次成功备份的日期（今天还没备份则为 null）。</summary>
        public DateTime? LastDate { get; private set; }

        /// <summary>最近一次失败原因（成功则清空）。用于页面红字。</summary>
        public string? LastError { get; private set; }

        public DateTimeOffset? LastErrorAt { get; private set; }

        // ============================================================
        // 备份
        // ============================================================

        /// <summary>
        /// 备份今天。已存在则跳过（<paramref name="force"/> = true 时覆盖）。
        /// 返回是否新写了一份。
        /// </summary>
        public bool BackupToday(bool force = false)
        {
            var today = CstToday();
            string path = Path.Combine(Directory, FileName(today));

            try
            {
                System.IO.Directory.CreateDirectory(Directory);

                if (File.Exists(path))
                {
                    if (!force) return false;
                    File.Delete(path);
                }

                // VACUUM INTO：生成一份完整、事务一致、已合并 WAL 的副本。
                // 用参数绑定路径（SQLite 支持在 VACUUM INTO 里用表达式）。
                lock (_db.Sync)
                {
                    _db.Conn.Execute("VACUUM INTO ?", path);
                }

                LastDate = today;
                LastError = null;
                LastErrorAt = null;

                Prune();
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                LastErrorAt = DateTimeOffset.Now;

                // 半成品要清掉：留一个 0 字节的"备份"比没有更危险——它会让人以为有备份
                try { if (File.Exists(path) && new FileInfo(path).Length == 0) File.Delete(path); } catch { }
                return false;
            }
        }

        /// <summary>列出全部快照（新 → 旧）。</summary>
        public IReadOnlyList<BackupFile> List()
        {
            if (!System.IO.Directory.Exists(Directory)) return Array.Empty<BackupFile>();

            var files = new List<BackupFile>();

            foreach (var path in System.IO.Directory.EnumerateFiles(Directory, Prefix + "*" + Suffix))
            {
                var name = Path.GetFileName(path);
                if (!TryParseDate(name, out var date)) continue;

                long size = 0;
                try { size = new FileInfo(path).Length; } catch { }

                files.Add(new BackupFile
                {
                    Name = name,
                    Date = date,
                    SizeBytes = size,
                    Counts = TryCount(path),
                });
            }

            return files.OrderByDescending(f => f.Date).ToList();
        }

        /// <summary>按保留策略删除过期快照。只删本服务生成的文件名，绝不碰目录里别的东西。</summary>
        private void Prune()
        {
            var today = CstToday();

            foreach (var f in List())
            {
                int age = (today - f.Date).Days;
                if (age < 0) continue;   // 未来日期（时钟错乱）一律不动，交给人判断

                bool keep =
                    age <= KeepDailyDays ||                                  // 最近 14 天
                    (f.Date.DayOfWeek == DayOfWeek.Sunday && age <= KeepWeeklyDays) ||
                    (f.Date.Day == 1 && age <= KeepMonthlyDays);

                if (keep) continue;

                try { File.Delete(Path.Combine(Directory, f.Name)); } catch { }
            }
        }

        // ============================================================
        // 工具
        // ============================================================

        /// <summary>"今天"按固定 UTC+8 算，和全项目统计口径一致。</summary>
        public static DateTime CstToday()
        {
            var now = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8));
            return new DateTime(now.Year, now.Month, now.Day);
        }

        public static string FileName(DateTime date) => Prefix + date.ToString("yyyy-MM-dd") + Suffix;

        public static bool TryParseDate(string fileName, out DateTime date)
        {
            date = default;
            if (!fileName.StartsWith(Prefix, StringComparison.Ordinal)) return false;
            if (!fileName.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase)) return false;

            string body = fileName.Substring(Prefix.Length, fileName.Length - Prefix.Length - Suffix.Length);
            return DateTime.TryParseExact(body, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out date);
        }

        /// <summary>各表条数。只读打开，失败返回 null（页面显示"打不开"）。</summary>
        private static IReadOnlyDictionary<string, int>? TryCount(string path)
        {
            try
            {
                var conn = new SQLiteConnection(path,
                    SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex);

                var result = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var table in new[] { "records", "blocks", "goals", "things", "categories" })
                {
                    try
                    {
                        result[table] = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM " + table);
                    }
                    catch
                    {
                        // 旧版本库里可能还没有这张表，不算错误
                    }
                }

                conn.Close();
                return result;
            }
            catch
            {
                return null;
            }
        }
    }
}
