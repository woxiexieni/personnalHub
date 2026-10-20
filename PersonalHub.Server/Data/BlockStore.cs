using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Personalhub;
using personalHub.Server.Analysis;

namespace personalHub.Server.Data
{
    /// <summary>时间块写入结果。和 <see cref="SaveResult"/> 同一套路：错误必须是一句能直接展示的话。</summary>
    public sealed class BlockSaveResult
    {
        public bool Ok { get; private init; }
        public bool Conflict { get; private init; }
        public string? Error { get; private init; }
        public Block? Block { get; private init; }

        public static BlockSaveResult Success(Block? block) => new() { Ok = true, Block = block };
        public static BlockSaveResult Fail(string error) => new() { Error = error };
        public static BlockSaveResult ConflictWith(Block current) =>
            new() { Conflict = true, Error = "版本冲突（conflict）：服务端已有更新的数据", Block = current };
    }

    /// <summary>一个片的聚合结果（管理端 / 手机统计用）。</summary>
    public sealed record PieceAggregate(string PieceCode, int Minutes, int Count);

    /// <summary>
    /// 时间块存储层。
    ///
    /// 与 <see cref="RecordStore"/> 最大的不同：**校验极松**。
    /// 除了 piece_code，什么都可以空 —— 时长空着、目标空着、备注空着。
    /// 旧事件表单正是死在"全必填"上，所以这里宁可有半条记录，也不拒绝半条记录。
    ///
    /// 但有一条线不能松：**删除优先于业务校验**。
    /// 片被你改名/隐藏之后，历史块仍然要能删掉，否则就是删不掉的数据。
    /// </summary>
    public sealed class BlockStore
    {
        private readonly Db _db;

        public BlockStore(Db db) => _db = db;

        public string DbPath => _db.Path;

        // ============================================================
        // 写
        // ============================================================

        /// <summary>统一写入入口：手机上传与管理端补录共用，校验只有这一份。</summary>
        public BlockSaveResult Save(Block b)
        {
            if (b is null) return BlockSaveResult.Fail("空记录");

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            b.Id ??= string.Empty;

            // ---- 1) 删除优先，不做业务校验 ----
            if (b.Deleted) return SoftDelete(b.Id, b.UpdatedAt);

            // ---- 2) 补齐服务端负责的字段 ----
            if (string.IsNullOrEmpty(b.Id)) b.Id = Guid.NewGuid().ToString("N");
            if (b.CreatedAt == 0) b.CreatedAt = now;
            if (b.OccurredAt == 0) b.OccurredAt = b.CreatedAt;

            // ---- 3) 唯一的硬约束：片必须有 ----
            b.PieceCode = (b.PieceCode ?? string.Empty).Trim();
            if (b.PieceCode.Length == 0) return BlockSaveResult.Fail("缺少事情片");
            if (b.PieceCode.Length > 40) return BlockSaveResult.Fail("事情片代号过长");

            // ---- 4) 时长：0 合法（半条记录），但上限要有，否则统计一夜之间失去意义 ----
            if (b.DurationMin < 0) return BlockSaveResult.Fail("时长不能为负");
            if (b.DurationMin > 1440) return BlockSaveResult.Fail("单条时长不能超过 24 小时");

            // 容差 5 分钟：手机时钟快几十秒是常事
            if (b.OccurredAt > now + 5 * 60_000) return BlockSaveResult.Fail("时间不能晚于当前时间");
            if (b.OccurredAt < 946684800000L) return BlockSaveResult.Fail("时间早于 2000 年，请检查");

            if ((b.Note?.Length ?? 0) > 200) return BlockSaveResult.Fail("备注不能超过 200 字");
            if ((b.Lesson?.Length ?? 0) > 500) return BlockSaveResult.Fail("一句话经验不能超过 500 字");

            // ---- 5) 版本比较 ----
            BlockRow? old;
            lock (_db.Sync)
            {
                old = _db.Conn.Find<BlockRow>(b.Id);
            }

            // 双方都有版本号才比较：老客户端不传 updated_at（=0）必须直接接受
            if (old is not null && b.UpdatedAt != 0 && old.UpdatedAt != 0 && b.UpdatedAt < old.UpdatedAt)
            {
                return BlockSaveResult.ConflictWith(ToProto(old));
            }

            if (b.UpdatedAt == 0) b.UpdatedAt = now;

            var row = ToRow(b);
            if (old is not null)
            {
                // 更新不能顺手“复活”一条已删的块
                row.Deleted = old.Deleted;
            }

            lock (_db.Sync)
            {
                _db.Conn.InsertOrReplace(row);
            }

            return BlockSaveResult.Success(ToProto(row));
        }

        /// <summary>软删除。删不存在的 id 视为成功（幂等）。</summary>
        public BlockSaveResult SoftDelete(string id, long updatedAt = 0)
        {
            if (string.IsNullOrEmpty(id)) return BlockSaveResult.Fail("缺少记录 id");

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            lock (_db.Sync)
            {
                var old = _db.Conn.Find<BlockRow>(id);
                if (old is null) return BlockSaveResult.Success(null);

                old.Deleted = 1;
                old.UpdatedAt = updatedAt != 0 ? updatedAt : now;
                _db.Conn.InsertOrReplace(old);
            }

            return BlockSaveResult.Success(null);
        }

        // ============================================================
        // 读
        // ============================================================

        /// <summary>区间查询，语义 [from, to)。</summary>
        public IReadOnlyList<Block> List(
            long from, long to,
            string? goalId = null,
            string? pieceCode = null,
            bool includeDeleted = false,
            int limit = 0)
        {
            var sql = new StringBuilder("SELECT * FROM blocks WHERE occurred_at >= ? AND occurred_at < ?");
            var args = new List<object> { from, to };

            if (!includeDeleted) sql.Append(" AND deleted = 0");
            if (!string.IsNullOrWhiteSpace(goalId))
            {
                sql.Append(" AND goal_id = ?");
                args.Add(goalId);
            }
            if (!string.IsNullOrWhiteSpace(pieceCode))
            {
                sql.Append(" AND piece_code = ?");
                args.Add(pieceCode);
            }

            sql.Append(" ORDER BY occurred_at DESC, created_at DESC");

            if (limit > 0)
            {
                sql.Append(" LIMIT ").Append(Math.Min(limit, 5000));
            }

            lock (_db.Sync)
            {
                return _db.Conn.Query<BlockRow>(sql.ToString(), args.ToArray())
                    .Select(ToProto)
                    .ToList();
            }
        }

        /// <summary>按 id 取一条（含已删除的）。管理端编辑页要能用它给出"这条已删除"的明确提示。</summary>
        public Block? Get(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;

            lock (_db.Sync)
            {
                var row = _db.Conn.Find<BlockRow>(id);
                return row is null ? null : ToProto(row);
            }
        }

        /// <summary>区间内按片聚合（分钟降序）。时长 0 的块不计入分钟，但计入条数。</summary>
        public IReadOnlyList<PieceAggregate> AggregateByPiece(long from, long to)
        {
            lock (_db.Sync)
            {
                return _db.Conn.Query<PieceAggRow>(
                        "SELECT IFNULL(piece_code,'') AS code, " +
                        "       SUM(IFNULL(duration_min,0)) AS minutes, " +
                        "       COUNT(*) AS cnt " +
                        "FROM blocks WHERE deleted = 0 AND occurred_at >= ? AND occurred_at < ? " +
                        "GROUP BY piece_code ORDER BY minutes DESC, cnt DESC",
                        from, to)
                    .Select(r => new PieceAggregate(r.code ?? string.Empty, r.minutes, r.cnt))
                    .ToList();
            }
        }

        /// <summary>
        /// 每个目标的累计投入 + 最后活动时间。
        ///
        /// 刻意做成查询而不是 goals 表里的字段：
        /// 你删掉一条块、或把块改到另一个目标之后，存下来的值会永久错掉，
        /// 而且没有任何界面能让它自己修回来。
        /// </summary>
        public IReadOnlyDictionary<string, (int Minutes, long LastAt)> GoalTotals()
        {
            lock (_db.Sync)
            {
                var rows = _db.Conn.Query<GoalTotalRow>(
                    "SELECT goal_id AS gid, " +
                    "       SUM(IFNULL(duration_min,0)) AS minutes, " +
                    "       MAX(occurred_at) AS last_at " +
                    "FROM blocks " +
                    "WHERE deleted = 0 AND IFNULL(goal_id,'') <> '' " +
                    "GROUP BY goal_id");

                return rows.ToDictionary(
                    r => r.gid ?? string.Empty,
                    r => (r.minutes, r.last_at),
                    StringComparer.Ordinal);
            }
        }

        /// <summary>某个目标最近四周的分钟数（[本周-3周, 本周]，4 个桶）。</summary>
        public IReadOnlyList<int> WeeklyMinutes(string goalId, DateTimeOffset now)
        {
            var start = Period.StartOfDay(now);
            // 本周一
            start = start.AddDays(-(((int)start.DayOfWeek + 6) % 7)).AddDays(-21);

            var result = new List<int>(4);
            for (int i = 0; i < 4; i++)
            {
                long from = start.AddDays(i * 7).ToUnixTimeMilliseconds();
                long to = start.AddDays((i + 1) * 7).ToUnixTimeMilliseconds();

                lock (_db.Sync)
                {
                    result.Add(_db.Conn.ExecuteScalar<int>(
                        "SELECT IFNULL(SUM(duration_min),0) FROM blocks " +
                        "WHERE deleted = 0 AND goal_id = ? AND occurred_at >= ? AND occurred_at < ?",
                        goalId, from, to));
                }
            }

            return result;
        }

        private sealed class PieceAggRow
        {
            public string? code { get; set; }
            public int minutes { get; set; }
            public int cnt { get; set; }
        }

        private sealed class GoalTotalRow
        {
            public string? gid { get; set; }
            public int minutes { get; set; }
            public long last_at { get; set; }
        }

        // ============================================================
        // 映射
        // ============================================================

        private static BlockRow ToRow(Block b) => new()
        {
            Id = b.Id,
            PieceCode = b.PieceCode,
            OccurredAt = b.OccurredAt,
            DurationMin = b.DurationMin,
            GoalId = b.GoalId,
            Note = b.Note,
            Worth = b.Worth,
            Lesson = b.Lesson,
            PlanId = b.PlanId,
            Anomaly = b.Anomaly,
            Mood = b.Mood,
            StartedAt = b.StartedAt,
            EndedAt = b.EndedAt,
            CreatedAt = b.CreatedAt,
            UpdatedAt = b.UpdatedAt,
            Deleted = b.Deleted ? 1 : 0,
        };

        private static Block ToProto(BlockRow r) => new()
        {
            Id = r.Id,
            PieceCode = r.PieceCode ?? string.Empty,
            OccurredAt = r.OccurredAt,
            DurationMin = r.DurationMin,
            GoalId = r.GoalId ?? string.Empty,
            Note = r.Note ?? string.Empty,
            Worth = r.Worth,
            Lesson = r.Lesson ?? string.Empty,
            PlanId = r.PlanId ?? string.Empty,
            Anomaly = r.Anomaly,
            Mood = r.Mood,
            StartedAt = r.StartedAt,
            EndedAt = r.EndedAt,
            CreatedAt = r.CreatedAt,
            UpdatedAt = r.UpdatedAt,
            Deleted = r.Deleted != 0,
        };
    }
}
