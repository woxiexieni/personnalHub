using System;
using System.Collections.Generic;
using System.Linq;
using Personalhub;

namespace personalHub.Server.Data
{
    /// <summary>问题卡写入结果（与其它 Store 同一套路：错误必须是一句能直接展示的话）。</summary>
    public sealed class ProblemSaveResult
    {
        public bool Ok { get; private init; }
        public string? Error { get; private init; }
        public Problem? Problem { get; private init; }

        public static ProblemSaveResult Success(Problem? p) => new() { Ok = true, Problem = p };

        public static ProblemSaveResult Fail(string error) => new() { Error = error };

        /// <summary>版本冲突必须算失败（不能"报成功、其实没写"）。</summary>
        public static ProblemSaveResult ConflictWith(Problem current) =>
            new() { Error = "版本冲突（conflict）：服务端已有更新的数据", Problem = current };
    }

    /// <summary>
    /// 问题卡存储（v2：从纯本地升级为可备份 / 管理端可看）。
    ///
    /// 校验只有两条，都是**结构性**的：
    ///   1. 标题不能为空；
    ///   2. 判据不能为空 —— 这是闸 1，服务端也守一遍：
    ///      客户端可能是老版本、也可能是 AI 起草的卡漏了确认，写不出"怎么算解决"的问题
    ///      一旦落库，就变成一张永远关不掉的卡。
    ///
    /// 刻意**没有**的校验：
    ///   - 不校验试法（留空 = 观察中，是合法状态）；
    ///   - 不校验判据是否被确认（AI 起草的卡可以先存在，列表里挂"未确认"提醒）；
    ///   - 不校验标签（分类是事后整理的动作）。
    /// </summary>
    public sealed class ProblemStore
    {
        private readonly Db _db;

        public ProblemStore(Db db) => _db = db;

        // ============================================================
        // 写
        // ============================================================

        public ProblemSaveResult Save(Problem p)
        {
            if (p is null) return ProblemSaveResult.Fail("空问题卡");

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            p.Id ??= string.Empty;

            // 删除优先：墓碑不受内容校验影响，否则一条老数据改了规则就再也删不掉。
            if (p.Deleted) return SoftDelete(p.Id, p.UpdatedAt);

            if (string.IsNullOrEmpty(p.Id)) p.Id = Guid.NewGuid().ToString("N");
            if (p.CreatedAt == 0) p.CreatedAt = now;

            p.Title = (p.Title ?? string.Empty).Trim();
            if (p.Title.Length == 0) return ProblemSaveResult.Fail("问题卡标题不能为空");

            p.Criteria = (p.Criteria ?? string.Empty).Trim();
            if (p.Criteria.Length == 0)
            {
                return ProblemSaveResult.Fail("判据不能为空：写不出「怎么算解决」，就还不是一个问题");
            }

            if (p.Status < 0 || p.Status > 2)
            {
                return ProblemSaveResult.Fail("问题卡状态只能是 进行中 / 已解决 / 放弃");
            }

            ProblemRow? old;
            lock (_db.Sync)
            {
                old = _db.Conn.Find<ProblemRow>(p.Id);
            }

            if (old is not null && p.UpdatedAt != 0 && old.UpdatedAt != 0 && p.UpdatedAt < old.UpdatedAt)
            {
                return ProblemSaveResult.ConflictWith(ToProto(old));
            }

            if (p.UpdatedAt == 0) p.UpdatedAt = now;

            // 关卡时间口径统一放服务端：客户端只说 status 变了。
            // 否则手机点一次、电脑点一次，closed_at 会被各算一遍，将来对不上。
            if (p.Status != 0)
            {
                if (p.ClosedAt == 0) p.ClosedAt = now;
            }
            else
            {
                p.ClosedAt = 0;   // 重新打开必须清掉，否则归档列表里会出现"开着却有关卡时间"的卡
            }

            var row = ToRow(p);
            if (old is not null) row.Deleted = old.Deleted;   // 更新不能顺手复活墓碑

            lock (_db.Sync)
            {
                _db.Conn.InsertOrReplace(row);
            }

            return ProblemSaveResult.Success(ToProto(row));
        }

        public ProblemSaveResult SoftDelete(string id, long updatedAt = 0)
        {
            if (string.IsNullOrEmpty(id)) return ProblemSaveResult.Fail("缺少问题卡 id");

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            lock (_db.Sync)
            {
                var old = _db.Conn.Find<ProblemRow>(id);
                if (old is null) return ProblemSaveResult.Success(null);

                old.Deleted = 1;
                old.UpdatedAt = updatedAt != 0 ? updatedAt : now;
                _db.Conn.InsertOrReplace(old);
            }

            return ProblemSaveResult.Success(null);
        }

        // ============================================================
        // 读
        // ============================================================

        /// <param name="status">-1 全部 / 0 进行中 / 1 已解决 / 2 放弃。</param>
        public IReadOnlyList<Problem> List(int status, long from, long to, bool includeDeleted)
        {
            var where = new List<string>();
            var args = new List<object>();

            if (!includeDeleted) where.Add("deleted = 0");

            if (status >= 0)
            {
                where.Add("status = ?");
                args.Add(status);
            }

            if (from > 0)
            {
                where.Add("created_at >= ?");
                args.Add(from);
            }

            if (to > 0)
            {
                where.Add("created_at < ?");
                args.Add(to);
            }

            string sql = "SELECT * FROM problems";
            if (where.Count > 0) sql += " WHERE " + string.Join(" AND ", where);
            sql += " ORDER BY updated_at DESC";

            lock (_db.Sync)
            {
                return _db.Conn.Query<ProblemRow>(sql, args.ToArray()).Select(ToProto).ToList();
            }
        }

        /// <summary>各状态条数（管理端 KPI 用）。</summary>
        public IReadOnlyDictionary<int, int> CountByStatus()
        {
            lock (_db.Sync)
            {
                return _db.Conn
                    .Query<StatusCount>("SELECT status, COUNT(*) AS cnt FROM problems WHERE deleted = 0 GROUP BY status")
                    .ToDictionary(x => x.status, x => x.cnt);
            }
        }

        /// <summary>id → 标题，管理端把事件卡的 problem_id 翻成人能看的名字。</summary>
        public IReadOnlyDictionary<string, string> Titles()
        {
            lock (_db.Sync)
            {
                return _db.Conn
                    .Query<IdTitle>("SELECT id, title FROM problems WHERE deleted = 0")
                    .Where(x => !string.IsNullOrEmpty(x.id))
                    .ToDictionary(x => x.id, x => x.title ?? string.Empty);
            }
        }

        // ============================================================
        // 工具
        // ============================================================

        private static ProblemRow ToRow(Problem p) => new()
        {
            Id = p.Id,
            Title = p.Title,
            Criteria = p.Criteria,
            CriteriaConfirmed = p.CriteriaConfirmed ? 1 : 0,
            Attempt = p.Attempt,
            AttemptStartedAt = p.AttemptStartedAt,
            AttemptCount = p.AttemptCount,
            Status = p.Status,
            Source = p.Source,
            Tag = p.Tag,
            ClosingNote = p.ClosingNote,
            CreatedAt = p.CreatedAt,
            UpdatedAt = p.UpdatedAt,
            ClosedAt = p.ClosedAt,
            Deleted = p.Deleted ? 1 : 0,
        };

        private static Problem ToProto(ProblemRow r) => new()
        {
            Id = r.Id,
            Title = r.Title ?? string.Empty,
            Criteria = r.Criteria ?? string.Empty,
            CriteriaConfirmed = r.CriteriaConfirmed != 0,
            Attempt = r.Attempt ?? string.Empty,
            AttemptStartedAt = r.AttemptStartedAt,
            AttemptCount = r.AttemptCount,
            Status = r.Status,
            Source = r.Source ?? string.Empty,
            Tag = r.Tag ?? string.Empty,
            ClosingNote = r.ClosingNote ?? string.Empty,
            CreatedAt = r.CreatedAt,
            UpdatedAt = r.UpdatedAt,
            ClosedAt = r.ClosedAt,
            Deleted = r.Deleted != 0,
        };

        private sealed class StatusCount
        {
            public int status { get; set; }
            public int cnt { get; set; }
        }

        private sealed class IdTitle
        {
            public string id { get; set; } = string.Empty;
            public string? title { get; set; }
        }
    }
}
