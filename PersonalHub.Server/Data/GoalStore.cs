using System;
using System.Collections.Generic;
using System.Linq;
using Personalhub;

namespace personalHub.Server.Data
{
    /// <summary>目标写入结果。</summary>
    public sealed class GoalSaveResult
    {
        public bool Ok { get; private init; }
        public bool Conflict { get; private init; }
        public string? Error { get; private init; }
        public Goal? Goal { get; private init; }

        public static GoalSaveResult Success(Goal? goal) => new() { Ok = true, Goal = goal };
        public static GoalSaveResult Fail(string error) => new() { Error = error };
        public static GoalSaveResult ConflictWith(Goal current) =>
            new() { Conflict = true, Error = "版本冲突：服务端已有更新的数据", Goal = current };
    }

    /// <summary>
    /// 目标存储层（极简层）。
    ///
    /// 表里只有 title / status / next_step / is_current 四个业务字段。
    /// 「累计投入」「最后活动」不出现在表里，是 <see cref="BlockStore.GoalTotals"/> 现算的，
    /// 在 <see cref="List"/> 里贴上去 —— 这是本文件唯一需要小心的地方：
    /// **不要把这两个值写回库**，否则删掉一条块之后它们就永久错了。
    /// </summary>
    public sealed class GoalStore
    {
        /// <summary>活跃目标上限。与 Pattern 的活跃上限同一个数字，理由也一样：注意力只有那么多。</summary>
        public const int MaxActive = 3;

        private readonly Db _db;
        private readonly BlockStore _blocks;

        public GoalStore(Db db, BlockStore blocks)
        {
            _db = db;
            _blocks = blocks;
        }

        // ============================================================
        // 写
        // ============================================================

        public GoalSaveResult Save(Goal g)
        {
            if (g is null) return GoalSaveResult.Fail("空目标");

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            g.Id ??= string.Empty;

            // ---- 删除优先 ----
            if (g.Deleted)
            {
                lock (_db.Sync)
                {
                    var existing = _db.Conn.Find<GoalRow>(g.Id);
                    if (existing is not null)
                    {
                        existing.Deleted = 1;
                        existing.IsCurrent = 0;
                        existing.UpdatedAt = g.UpdatedAt != 0 ? g.UpdatedAt : now;
                        _db.Conn.InsertOrReplace(existing);
                    }
                }
                return GoalSaveResult.Success(null);
            }

            if (string.IsNullOrEmpty(g.Id)) g.Id = Guid.NewGuid().ToString("N");
            if (g.CreatedAt == 0) g.CreatedAt = now;
            if (g.StartedAt == 0) g.StartedAt = g.CreatedAt;
            if (g.Status < 0 || g.Status > 3) g.Status = 0;

            // ---- 校验 ----
            g.Title = (g.Title ?? string.Empty).Trim();
            if (g.Title.Length == 0) return GoalSaveResult.Fail("目标名称不能为空");
            if (g.Title.Length > 60) return GoalSaveResult.Fail("目标名称不能超过 60 字");
            if ((g.NextStep?.Length ?? 0) > 100) return GoalSaveResult.Fail("「下一步」不能超过 100 字");

            GoalRow? old;
            lock (_db.Sync)
            {
                old = _db.Conn.Find<GoalRow>(g.Id);
            }

            if (old is not null && g.UpdatedAt != 0 && old.UpdatedAt != 0 && g.UpdatedAt < old.UpdatedAt)
            {
                return GoalSaveResult.ConflictWith(ToProto(old, 0, 0));
            }

            // ---- 活跃上限：只在"变成活跃"这一步拦，避免历史数据被卡住 ----
            bool becomingActive = g.Status == 0 && (old is null || old.Status != 0);
            if (becomingActive)
            {
                int active = CountActive(excludeId: g.Id);
                if (active >= MaxActive)
                {
                    return GoalSaveResult.Fail($"已有 {MaxActive} 个目标在推进，先暂停一个再建新的");
                }
            }

            if (g.UpdatedAt == 0) g.UpdatedAt = now;

            var row = ToRow(g);
            if (old is not null) row.Deleted = old.Deleted;

            lock (_db.Sync)
            {
                _db.Conn.RunInTransaction(() =>
                {
                    // 「当前目标」全局最多一个：设它的时候把别人的清掉。
                    // 不做"自动继承最近活跃"—— 那会在两个项目之间静默归错，而且不可预测。
                    if (row.IsCurrent != 0)
                    {
                        _db.Conn.Execute(
                            "UPDATE goals SET is_current = 0 WHERE id <> ? AND is_current <> 0", row.Id);
                    }

                    _db.Conn.InsertOrReplace(row);
                });
            }

            return GoalSaveResult.Success(ToProto(row, 0, 0));
        }

        public int CountActive(string? excludeId = null)
        {
            lock (_db.Sync)
            {
                if (string.IsNullOrEmpty(excludeId))
                {
                    return _db.Conn.ExecuteScalar<int>(
                        "SELECT COUNT(*) FROM goals WHERE deleted = 0 AND status = 0");
                }

                return _db.Conn.ExecuteScalar<int>(
                    "SELECT COUNT(*) FROM goals WHERE deleted = 0 AND status = 0 AND id <> ?", excludeId);
            }
        }

        // ============================================================
        // 读
        // ============================================================

        /// <summary>
        /// 全部目标，附带累计投入与最后活动。
        ///
        /// 排序刻意是：当前目标 → 最近 14 天内有活动的 → 其余。
        /// 不按创建时间排 —— 你会永远看到最上面那个三个月没动过的目标。
        /// 停滞折叠交给客户端（14 天无块 → 折叠），服务端只提供 last_activity_at。
        /// </summary>
        public IReadOnlyList<Goal> List()
        {
            var totals = _blocks.GoalTotals();

            List<GoalRow> rows;
            lock (_db.Sync)
            {
                rows = _db.Conn.Table<GoalRow>()
                    .Where(r => r.Deleted == 0)
                    .ToList();
            }

            return rows
                .Select(r =>
                {
                    totals.TryGetValue(r.Id, out var t);
                    return ToProto(r, t.Minutes, t.LastAt);
                })
                .OrderByDescending(g => g.IsCurrent)
                .ThenBy(g => g.Status)
                .ThenByDescending(g => g.LastActivityAt)
                .ThenByDescending(g => g.CreatedAt)
                .ToList();
        }

        public Goal? Get(string id)
        {
            var totals = _blocks.GoalTotals();
            lock (_db.Sync)
            {
                var row = _db.Conn.Find<GoalRow>(id);
                if (row is null || row.Deleted != 0) return null;

                totals.TryGetValue(row.Id, out var t);
                return ToProto(row, t.Minutes, t.LastAt);
            }
        }

        private static GoalRow ToRow(Goal g) => new()
        {
            Id = g.Id,
            Title = g.Title,
            Status = g.Status,
            NextStep = g.NextStep,
            IsCurrent = g.IsCurrent ? 1 : 0,
            StartedAt = g.StartedAt,
            CreatedAt = g.CreatedAt,
            UpdatedAt = g.UpdatedAt,
            Deleted = g.Deleted ? 1 : 0,
        };

        private static Goal ToProto(GoalRow r, int totalMinutes, long lastAt) => new()
        {
            Id = r.Id,
            Title = r.Title ?? string.Empty,
            Status = r.Status,
            NextStep = r.NextStep ?? string.Empty,
            IsCurrent = r.IsCurrent != 0,
            StartedAt = r.StartedAt,
            CreatedAt = r.CreatedAt,
            UpdatedAt = r.UpdatedAt,
            Deleted = r.Deleted != 0,
            TotalMinutes = totalMinutes,
            LastActivityAt = lastAt,
        };
    }
}
