using Personalhub;

namespace personalHub.Server.Data
{
    public sealed class GoalStepSaveResult
    {
        public bool Ok { get; private init; }
        public GoalStep? Step { get; private init; }
        public string? Error { get; private init; }

        public static GoalStepSaveResult Success(GoalStep? step) => new() { Ok = true, Step = step };
        public static GoalStepSaveResult Fail(string error) => new() { Error = error };
    }

    public sealed class GoalStepStore
    {
        private readonly Db _db;

        public GoalStepStore(Db db) => _db = db;

        public GoalStepSaveResult Save(GoalStep s)
        {
            if (s is null) return GoalStepSaveResult.Fail("空小项");

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (string.IsNullOrEmpty(s.Id)) s.Id = Guid.NewGuid().ToString("N");
            if (s.CreatedAt == 0) s.CreatedAt = now;
            if (s.UpdatedAt == 0) s.UpdatedAt = now;

            if (s.Deleted)
            {
                lock (_db.Sync)
                {
                    var row = _db.Conn.Find<GoalStepRow>(s.Id);
                    if (row is not null)
                    {
                        row.Deleted = 1;
                        row.UpdatedAt = s.UpdatedAt;
                        _db.Conn.InsertOrReplace(row);
                    }
                }
                return GoalStepSaveResult.Success(null);
            }

            s.GoalId = (s.GoalId ?? string.Empty).Trim();
            s.Title = (s.Title ?? string.Empty).Trim();
            s.PieceCode = (s.PieceCode ?? string.Empty).Trim();
            s.SourceIdeaId = (s.SourceIdeaId ?? string.Empty).Trim();
            s.Reason = (s.Reason ?? string.Empty).Trim();
            s.ArtifactUri = (s.ArtifactUri ?? string.Empty).Trim();

            if (s.GoalId.Length == 0) return GoalStepSaveResult.Fail("小项缺少目标");
            if (s.Title.Length == 0) return GoalStepSaveResult.Fail("小项名称不能为空");
            if (s.Title.Length > 120) return GoalStepSaveResult.Fail("小项名称不能超过 120 字");
            if (s.Status < 0 || s.Status > 4) s.Status = 0;
            if (s.EstimateMin < 0) return GoalStepSaveResult.Fail("预计时间不能为负");
            if (s.EstimateMin > 1440) return GoalStepSaveResult.Fail("小项预计时间不能超过 24 小时");

            lock (_db.Sync)
            {
                var old = _db.Conn.Find<GoalStepRow>(s.Id);
                if (old is not null && old.UpdatedAt != 0 && s.UpdatedAt < old.UpdatedAt)
                {
                    return GoalStepSaveResult.Fail("版本冲突（conflict）：服务端已有更新的数据");
                }

                var row = ToRow(s);
                if (old is not null) row.Deleted = old.Deleted;
                _db.Conn.InsertOrReplace(row);
                return GoalStepSaveResult.Success(ToProto(row));
            }
        }

        public IReadOnlyList<GoalStep> List(string? goalId, bool includeDeleted)
        {
            lock (_db.Sync)
            {
                var sql = "SELECT * FROM goal_steps WHERE 1=1";
                var args = new List<object>();
                if (!includeDeleted) sql += " AND deleted = 0";
                if (!string.IsNullOrWhiteSpace(goalId))
                {
                    sql += " AND goal_id = ?";
                    args.Add(goalId);
                }
                sql += " ORDER BY sort_order, created_at";
                return _db.Conn.Query<GoalStepRow>(sql, args.ToArray()).Select(ToProto).ToList();
            }
        }

        private static GoalStepRow ToRow(GoalStep s) => new()
        {
            Id = s.Id,
            GoalId = s.GoalId,
            Title = s.Title,
            Status = s.Status,
            PieceCode = s.PieceCode,
            EstimateMin = s.EstimateMin,
            SourceIdeaId = s.SourceIdeaId,
            Deadline = s.Deadline,
            Reason = s.Reason,
            ArtifactUri = s.ArtifactUri,
            CompletedAt = s.CompletedAt,
            SortOrder = s.SortOrder,
            CreatedAt = s.CreatedAt,
            UpdatedAt = s.UpdatedAt,
            Deleted = s.Deleted ? 1 : 0,
        };

        private static GoalStep ToProto(GoalStepRow r) => new()
        {
            Id = r.Id,
            GoalId = r.GoalId ?? string.Empty,
            Title = r.Title ?? string.Empty,
            Status = r.Status,
            PieceCode = r.PieceCode ?? string.Empty,
            EstimateMin = r.EstimateMin,
            SourceIdeaId = r.SourceIdeaId ?? string.Empty,
            Deadline = r.Deadline,
            Reason = r.Reason ?? string.Empty,
            ArtifactUri = r.ArtifactUri ?? string.Empty,
            CompletedAt = r.CompletedAt,
            SortOrder = r.SortOrder,
            CreatedAt = r.CreatedAt,
            UpdatedAt = r.UpdatedAt,
            Deleted = r.Deleted != 0,
        };
    }
}
