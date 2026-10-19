using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Personalhub;
using personalHub.Server.Analysis;

namespace personalHub.Server.Data
{
    /// <summary>计划写入结果（与其它 Store 同一套路：错误必须是一句能直接展示的话）。</summary>
    public sealed class PlanSaveResult
    {
        public bool Ok { get; private init; }
        public string? Error { get; private init; }
        public Plan? Plan { get; private init; }

        public static PlanSaveResult Success(Plan? plan) => new() { Ok = true, Plan = plan };
        public static PlanSaveResult Fail(string error) => new() { Error = error };
    }

    /// <summary>
    /// 每日计划存储。
    ///
    /// 三条硬约束：
    ///  1. **一天最多 3 条**。第 4 条会被拒（不是提醒，是拒绝）——
    ///     允许 5 条的那一刻，它就变成了待办清单，而待办清单会过期、过期会焦虑。
    ///  2. **只点选、0 打字**：没有 title 字段，只有 piece_code（片名本来就是现成的）。
    ///  3. **不做完成标记**：达成率由 BlockStore 的当日时长现算，不在这里存状态。
    /// </summary>
    public sealed class PlanStore
    {
        public const int MaxPerDay = 3;

        private readonly Db _db;

        public PlanStore(Db db) => _db = db;

        // ============================================================
        // 写
        // ============================================================

        public PlanSaveResult Save(Plan p)
        {
            if (p is null) return PlanSaveResult.Fail("空计划");

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            p.Id ??= string.Empty;

            // 删除优先：片被改名/隐藏之后，旧计划仍然要能删掉
            if (p.Deleted) return SoftDelete(p.Id, p.UpdatedAt);

            if (string.IsNullOrEmpty(p.Id)) p.Id = Guid.NewGuid().ToString("N");
            if (p.CreatedAt == 0) p.CreatedAt = now;

            p.Day = (p.Day ?? string.Empty).Trim();
            if (!TryParseDay(p.Day, out _)) return PlanSaveResult.Fail("日期格式必须是 yyyy-MM-dd");

            p.PieceCode = (p.PieceCode ?? string.Empty).Trim();
            if (p.PieceCode.Length == 0) return PlanSaveResult.Fail("缺少事情片");

            if (p.BudgetMin < 0) return PlanSaveResult.Fail("预算不能为负");
            if (p.BudgetMin > 1440) return PlanSaveResult.Fail("预算不能超过 24 小时");

            PlanRow? old;
            lock (_db.Sync)
            {
                old = _db.Conn.Find<PlanRow>(p.Id);
            }

            if (old is not null && p.UpdatedAt != 0 && old.UpdatedAt != 0 && p.UpdatedAt < old.UpdatedAt)
            {
                // 与时间块一致：冲突不重试，让客户端拉服务端覆盖
                return PlanSaveResult.Success(ToProto(old));
            }

            if (p.UpdatedAt == 0) p.UpdatedAt = now;

            // ---- 一天最多 3 条：只在"新增一条活计划"时拦 ----
            bool adding = old is null || old.Deleted != 0;
            if (adding)
            {
                int count = CountDay(p.Day, excludeId: p.Id);
                if (count >= MaxPerDay)
                {
                    return PlanSaveResult.Fail($"一天最多计划 {MaxPerDay} 件。想换的话，先去掉一件。");
                }
            }

            var row = ToRow(p);
            if (old is not null) row.Deleted = old.Deleted;   // 更新不能顺手复活墓碑

            lock (_db.Sync)
            {
                _db.Conn.InsertOrReplace(row);
            }

            return PlanSaveResult.Success(ToProto(row));
        }

        public PlanSaveResult SoftDelete(string id, long updatedAt = 0)
        {
            if (string.IsNullOrEmpty(id)) return PlanSaveResult.Fail("缺少计划 id");

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            lock (_db.Sync)
            {
                var old = _db.Conn.Find<PlanRow>(id);
                if (old is null) return PlanSaveResult.Success(null);

                old.Deleted = 1;
                old.UpdatedAt = updatedAt != 0 ? updatedAt : now;
                _db.Conn.InsertOrReplace(old);
            }
            return PlanSaveResult.Success(null);
        }

        public int CountDay(string day, string? excludeId = null)
        {
            lock (_db.Sync)
            {
                return string.IsNullOrEmpty(excludeId)
                    ? _db.Conn.ExecuteScalar<int>(
                        "SELECT COUNT(*) FROM plans WHERE deleted = 0 AND day = ?", day)
                    : _db.Conn.ExecuteScalar<int>(
                        "SELECT COUNT(*) FROM plans WHERE deleted = 0 AND day = ? AND id <> ?", day, excludeId);
            }
        }

        // ============================================================
        // 读
        // ============================================================

        /// <summary>某一天的计划（片名顺序即创建顺序）。</summary>
        public IReadOnlyList<Plan> ListDay(string day, bool includeDeleted = false)
        {
            lock (_db.Sync)
            {
                var sql = includeDeleted
                    ? "SELECT * FROM plans WHERE day = ? ORDER BY created_at"
                    : "SELECT * FROM plans WHERE day = ? AND deleted = 0 ORDER BY created_at";

                return _db.Conn.Query<PlanRow>(sql, day).Select(ToProto).ToList();
            }
        }

        /// <summary>区间查询（按 day 字符串比较，天然有序）。</summary>
        public IReadOnlyList<Plan> ListRange(string fromDay, string toDay, bool includeDeleted = false)
        {
            lock (_db.Sync)
            {
                var sql = includeDeleted
                    ? "SELECT * FROM plans WHERE day >= ? AND day <= ? ORDER BY day, created_at"
                    : "SELECT * FROM plans WHERE day >= ? AND day <= ? AND deleted = 0 ORDER BY day, created_at";

                return _db.Conn.Query<PlanRow>(sql, fromDay, toDay).Select(ToProto).ToList();
            }
        }

        // ============================================================
        // 工具
        // ============================================================

        /// <summary>今天的 yyyy-MM-dd（固定 UTC+8，与全项目口径一致）。</summary>
        public static string Today() => DateTimeOffset.UtcNow.ToOffset(Period.Cst).ToString("yyyy-MM-dd");

        public static bool TryParseDay(string? day, out DateTime date) =>
            DateTime.TryParseExact(day ?? string.Empty, "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

        private static PlanRow ToRow(Plan p) => new()
        {
            Id = p.Id,
            Day = p.Day,
            PieceCode = p.PieceCode,
            BudgetMin = p.BudgetMin,
            GoalId = p.GoalId,
            CreatedAt = p.CreatedAt,
            UpdatedAt = p.UpdatedAt,
            Deleted = p.Deleted ? 1 : 0,
        };

        private static Plan ToProto(PlanRow r) => new()
        {
            Id = r.Id,
            Day = r.Day ?? string.Empty,
            PieceCode = r.PieceCode ?? string.Empty,
            BudgetMin = r.BudgetMin,
            GoalId = r.GoalId ?? string.Empty,
            CreatedAt = r.CreatedAt,
            UpdatedAt = r.UpdatedAt,
            Deleted = r.Deleted != 0,
        };
    }
}
