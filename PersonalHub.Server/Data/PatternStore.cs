using System;
using System.Collections.Generic;
using System.Linq;
using Personalhub;

namespace personalHub.Server.Data
{
    public sealed class PatternSaveResult
    {
        public bool Ok { get; private init; }
        public string? Error { get; private init; }
        public Pattern? Pattern { get; private init; }

        public static PatternSaveResult Success(Pattern? p) => new() { Ok = true, Pattern = p };
        public static PatternSaveResult Fail(string error) => new() { Error = error };

        /// <summary>
        /// 版本冲突必须算**失败**，不能算成功。
        /// 早先这里返回 Success(old)，结果是客户端以为存上了、服务端却没动 ——
        /// 表现是"点了暂停，界面显示成功，但状态没变"，而且完全不报错。
        /// </summary>
        public static PatternSaveResult ConflictWith(Pattern current) =>
            new() { Error = "版本冲突（conflict）：服务端已有更新的数据", Pattern = current };
    }

    public sealed class PracticeSaveResult
    {
        public bool Ok { get; private init; }
        public string? Error { get; private init; }
        public Practice? Practice { get; private init; }

        public static PracticeSaveResult Success(Practice? p) => new() { Ok = true, Practice = p };
        public static PracticeSaveResult Fail(string error) => new() { Error = error };
    }

    /// <summary>
    /// 行为模式与练习的存储。
    ///
    /// 三条硬约束：
    ///  1. **活跃模式上限 3**：和 goals 同一个数字、同一个理由（注意力只有那么多）。
    ///  2. **Pattern 只有 title + new_response 必填**：想清楚一半也能先建，用起来再补。
    ///  3. **Practice 只有 result 一个字段**：1 / 0，别的什么都不记 —— 否则一天点不了几次。
    /// </summary>
    public sealed class PatternStore
    {
        public const int MaxActive = 3;

        private readonly Db _db;

        public PatternStore(Db db) => _db = db;

        // ============================================================
        // Pattern
        // ============================================================

        public PatternSaveResult SavePattern(Pattern p)
        {
            if (p is null) return PatternSaveResult.Fail("空模式");

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            p.Id ??= string.Empty;

            if (p.Deleted) return SoftDeletePattern(p.Id, p.UpdatedAt);

            if (string.IsNullOrEmpty(p.Id)) p.Id = Guid.NewGuid().ToString("N");
            if (p.CreatedAt == 0) p.CreatedAt = now;
            if (p.Status is < 0 or > 3) p.Status = 0;

            p.Title = (p.Title ?? string.Empty).Trim();
            p.NewResponse = (p.NewResponse ?? string.Empty).Trim();

            if (p.Title.Length == 0) return PatternSaveResult.Fail("模式名称不能为空");
            if (p.Title.Length > 60) return PatternSaveResult.Fail("模式名称不能超过 60 字");
            if (p.NewResponse.Length == 0) return PatternSaveResult.Fail("新反应不能为空（它才是要练的那个动作）");
            if (p.NewResponse.Length > 100) return PatternSaveResult.Fail("新反应不能超过 100 字");

            PatternRow? old;
            lock (_db.Sync) old = _db.Conn.Find<PatternRow>(p.Id);

            if (old is not null && p.UpdatedAt != 0 && old.UpdatedAt != 0 && p.UpdatedAt < old.UpdatedAt)
            {
                return PatternSaveResult.ConflictWith(ToProto(old));
            }

            if (p.UpdatedAt == 0) p.UpdatedAt = now;

            bool becomingActive = p.Status == 0 && (old is null || old.Status != 0);
            if (becomingActive && CountActive(excludeId: p.Id) >= MaxActive)
            {
                return PatternSaveResult.Fail($"已有 {MaxActive} 个模式在练，先暂停一个");
            }

            var row = ToRow(p);
            if (old is not null) row.Deleted = old.Deleted;

            lock (_db.Sync) _db.Conn.InsertOrReplace(row);
            return PatternSaveResult.Success(ToProto(row));
        }

        public PatternSaveResult SoftDeletePattern(string id, long updatedAt = 0)
        {
            if (string.IsNullOrEmpty(id)) return PatternSaveResult.Fail("缺少模式 id");

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            lock (_db.Sync)
            {
                var old = _db.Conn.Find<PatternRow>(id);
                if (old is null) return PatternSaveResult.Success(null);

                old.Deleted = 1;
                old.UpdatedAt = updatedAt != 0 ? updatedAt : now;
                _db.Conn.InsertOrReplace(old);
            }
            return PatternSaveResult.Success(null);
        }

        public int CountActive(string? excludeId = null)
        {
            lock (_db.Sync)
            {
                return string.IsNullOrEmpty(excludeId)
                    ? _db.Conn.ExecuteScalar<int>(
                        "SELECT COUNT(*) FROM patterns WHERE deleted = 0 AND status = 0")
                    : _db.Conn.ExecuteScalar<int>(
                        "SELECT COUNT(*) FROM patterns WHERE deleted = 0 AND status = 0 AND id <> ?", excludeId);
            }
        }

        public IReadOnlyList<Pattern> ListPatterns()
        {
            List<PatternRow> rows;
            lock (_db.Sync)
            {
                rows = _db.Conn.Table<PatternRow>().Where(r => r.Deleted == 0).ToList();
            }

            return rows
                .Select(ToProto)
                .OrderBy(p => p.Status)
                .ThenByDescending(p => p.UpdatedAt)
                .ToList();
        }

        // ============================================================
        // Practice
        // ============================================================

        public PracticeSaveResult SavePractice(Practice p)
        {
            if (p is null) return PracticeSaveResult.Fail("空练习");

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            p.Id ??= string.Empty;

            if (p.Deleted) return SoftDeletePractice(p.Id, p.UpdatedAt);

            if (string.IsNullOrEmpty(p.Id)) p.Id = Guid.NewGuid().ToString("N");
            if (p.CreatedAt == 0) p.CreatedAt = now;

            if (string.IsNullOrEmpty(p.PatternId)) return PracticeSaveResult.Fail("缺少模式 id");
            if (p.Result != 0 && p.Result != 1) return PracticeSaveResult.Fail("练习结果只能是 0 或 1");

            if (p.OccurredAt == 0) p.OccurredAt = now;
            if (p.OccurredAt > now + 5 * 60_000) return PracticeSaveResult.Fail("时间不能晚于当前时间");
            if (p.OccurredAt < 946684800000L) return PracticeSaveResult.Fail("时间早于 2000 年，请检查");

            PracticeRow? old;
            lock (_db.Sync) old = _db.Conn.Find<PracticeRow>(p.Id);

            if (old is not null && p.UpdatedAt != 0 && old.UpdatedAt != 0 && p.UpdatedAt < old.UpdatedAt)
            {
                return PracticeSaveResult.Fail("版本冲突（conflict）：服务端已有更新的数据");
            }

            if (p.UpdatedAt == 0) p.UpdatedAt = now;

            var row = ToRow(p);
            if (old is not null) row.Deleted = old.Deleted;

            lock (_db.Sync) _db.Conn.InsertOrReplace(row);
            return PracticeSaveResult.Success(ToProto(row));
        }

        public PracticeSaveResult SoftDeletePractice(string id, long updatedAt = 0)
        {
            if (string.IsNullOrEmpty(id)) return PracticeSaveResult.Fail("缺少练习 id");

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            lock (_db.Sync)
            {
                var old = _db.Conn.Find<PracticeRow>(id);
                if (old is null) return PracticeSaveResult.Success(null);

                old.Deleted = 1;
                old.UpdatedAt = updatedAt != 0 ? updatedAt : now;
                _db.Conn.InsertOrReplace(old);
            }
            return PracticeSaveResult.Success(null);
        }

        /// <summary>区间查询，语义 [from, to)。patternId 为空 = 全部。</summary>
        public IReadOnlyList<Practice> ListPractices(
            long from, long to, string? patternId = null, bool includeDeleted = false)
        {
            var sql = "SELECT * FROM practices WHERE occurred_at >= ? AND occurred_at < ?";
            var args = new List<object> { from, to };

            if (!includeDeleted) sql += " AND deleted = 0";
            if (!string.IsNullOrWhiteSpace(patternId))
            {
                sql += " AND pattern_id = ?";
                args.Add(patternId);
            }
            sql += " ORDER BY occurred_at DESC";

            lock (_db.Sync)
            {
                return _db.Conn.Query<PracticeRow>(sql, args.ToArray())
                    .Select(ToProto)
                    .ToList();
            }
        }

        // ============================================================
        // 映射
        // ============================================================

        private static PatternRow ToRow(Pattern p) => new()
        {
            Id = p.Id,
            Title = p.Title,
            Trigger = p.Trigger,
            OldResponse = p.OldResponse,
            NewResponse = p.NewResponse,
            RewardKept = p.RewardKept,
            Status = p.Status,
            CreatedAt = p.CreatedAt,
            UpdatedAt = p.UpdatedAt,
            Deleted = p.Deleted ? 1 : 0,
        };

        private static Pattern ToProto(PatternRow r) => new()
        {
            Id = r.Id,
            Title = r.Title ?? string.Empty,
            Trigger = r.Trigger ?? string.Empty,
            OldResponse = r.OldResponse ?? string.Empty,
            NewResponse = r.NewResponse ?? string.Empty,
            RewardKept = r.RewardKept ?? string.Empty,
            Status = r.Status,
            CreatedAt = r.CreatedAt,
            UpdatedAt = r.UpdatedAt,
            Deleted = r.Deleted != 0,
        };

        private static PracticeRow ToRow(Practice p) => new()
        {
            Id = p.Id,
            PatternId = p.PatternId,
            Result = p.Result,
            OccurredAt = p.OccurredAt,
            CreatedAt = p.CreatedAt,
            UpdatedAt = p.UpdatedAt,
            Deleted = p.Deleted ? 1 : 0,
        };

        private static Practice ToProto(PracticeRow r) => new()
        {
            Id = r.Id,
            PatternId = r.PatternId ?? string.Empty,
            Result = r.Result,
            OccurredAt = r.OccurredAt,
            CreatedAt = r.CreatedAt,
            UpdatedAt = r.UpdatedAt,
            Deleted = r.Deleted != 0,
        };
    }
}
