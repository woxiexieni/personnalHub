using System;
using System.Collections.Generic;
using System.Linq;
using Personalhub;

namespace personalHub.Server.Data
{
    /// <summary>想法写入结果（与其它 Store 同一套路：错误必须是一句能直接展示的话）。</summary>
    public sealed class IdeaSaveResult
    {
        public bool Ok { get; private init; }
        public string? Error { get; private init; }
        public Idea? Idea { get; private init; }

        public static IdeaSaveResult Success(Idea? idea) => new() { Ok = true, Idea = idea };

        public static IdeaSaveResult Fail(string error) => new() { Error = error };

        /// <summary>
        /// 版本冲突必须算**失败**。
        /// 早期 PatternStore 在这里返回过 Success(旧值)，结果是"界面显示保存成功、服务端其实没改"——
        /// 这是最难发现的一类 bug：不报错、不生效。所有 Store 现在统一走这个工厂方法。
        /// </summary>
        public static IdeaSaveResult ConflictWith(Idea current) =>
            new() { Error = "版本冲突（conflict）：服务端已有更新的数据", Idea = current };
    }

    /// <summary>
    /// 想法存储。
    ///
    /// 校验只有三条，而且都是"结构性"的：
    ///   1. 正文不能为空（空想法没有意义，也没法在列表里显示）；
    ///   2. 正文上限 2000 字（防止把整篇文章贴进来当想法）；
    ///   3. kind 必须在 0~4 之间。
    ///
    /// 刻意**没有**的校验：
    ///   - 不校验"类型必填"（0 = 未分类，完全合法）；
    ///   - 不校验重复（同一个念头记两遍就记两遍，去重需要判断"是不是同一个想法"，那是 AI 的事，
    ///     而误判合并会让用户丢失东西 —— 宁可重复，不可丢失）；
    ///   - 不限制每天条数（想法不像计划，它没有"今天该有几个"的概念）。
    /// </summary>
    public sealed class IdeaStore
    {
        /// <summary>正文上限。够写一段完整想法，又不至于变成文章仓库。</summary>
        public const int MaxTextLength = 2000;

        /// <summary>kind 的最大合法值（0 未分类 / 1 事实 / 2 评价 / 3 计划行动 / 4 想象脑补）。</summary>
        public const int MaxKind = 4;

        private readonly Db _db;

        public IdeaStore(Db db) => _db = db;

        // ============================================================
        // 写
        // ============================================================

        public IdeaSaveResult Save(Idea p)
        {
            if (p is null) return IdeaSaveResult.Fail("空想法");

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            p.Id ??= string.Empty;

            // 删除优先：墓碑不受正文校验影响，否则一条"正文本该合法但后来改了规则"的旧数据会删不掉
            if (p.Deleted) return SoftDelete(p.Id, p.UpdatedAt);

            if (string.IsNullOrEmpty(p.Id)) p.Id = Guid.NewGuid().ToString("N");
            if (p.CreatedAt == 0) p.CreatedAt = now;

            p.Text = (p.Text ?? string.Empty).Trim();
            if (p.Text.Length == 0) return IdeaSaveResult.Fail("想法不能是空的");

            if (p.Text.Length > MaxTextLength)
            {
                return IdeaSaveResult.Fail($"想法最多 {MaxTextLength} 字，这条有 {p.Text.Length} 字");
            }

            if (p.Kind < 0 || p.Kind > MaxKind)
            {
                return IdeaSaveResult.Fail("想法类型只能是 未分类/事实/评价/计划行动/想象脑补");
            }

            IdeaRow? old;
            lock (_db.Sync)
            {
                old = _db.Conn.Find<IdeaRow>(p.Id);
            }

            if (old is not null && p.UpdatedAt != 0 && old.UpdatedAt != 0 && p.UpdatedAt < old.UpdatedAt)
            {
                // 与其它表一致：冲突不重试，让客户端拉服务端版本覆盖
                return IdeaSaveResult.ConflictWith(ToProto(old));
            }

            if (p.UpdatedAt == 0) p.UpdatedAt = now;

            var row = ToRow(p);
            if (old is not null) row.Deleted = old.Deleted;   // 更新不能顺手复活墓碑

            lock (_db.Sync)
            {
                _db.Conn.InsertOrReplace(row);
            }

            return IdeaSaveResult.Success(ToProto(row));
        }

        public IdeaSaveResult SoftDelete(string id, long updatedAt = 0)
        {
            if (string.IsNullOrEmpty(id)) return IdeaSaveResult.Fail("缺少想法 id");

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            lock (_db.Sync)
            {
                var old = _db.Conn.Find<IdeaRow>(id);
                if (old is null) return IdeaSaveResult.Success(null);

                old.Deleted = 1;
                old.UpdatedAt = updatedAt != 0 ? updatedAt : now;
                _db.Conn.InsertOrReplace(old);
            }

            return IdeaSaveResult.Success(null);
        }

        // ============================================================
        // 读
        // ============================================================

        public IReadOnlyList<Idea> List(int kind, long from, long to, bool includeDeleted)
        {
            // 拼 SQL 而不是在内存里过滤：想法库是要长期攒的（可能上万条），
            // 全表读出来再筛会在某一天突然变慢，而且慢得没有明显征兆。
            var where = new List<string>();
            var args = new List<object>();

            if (!includeDeleted) where.Add("deleted = 0");

            if (kind >= 0)
            {
                where.Add("kind = ?");
                args.Add(kind);
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

            string sql = "SELECT * FROM ideas";
            if (where.Count > 0) sql += " WHERE " + string.Join(" AND ", where);
            sql += " ORDER BY created_at DESC";

            lock (_db.Sync)
            {
                return _db.Conn.Query<IdeaRow>(sql, args.ToArray()).Select(ToProto).ToList();
            }
        }

        /// <summary>各类型条数（管理端只看分布，不看"未处理"）。</summary>
        public IReadOnlyDictionary<int, int> CountByKind()
        {
            lock (_db.Sync)
            {
                return _db.Conn
                    .Query<KindCount>("SELECT kind, COUNT(*) AS cnt FROM ideas WHERE deleted = 0 GROUP BY kind")
                    .ToDictionary(x => x.kind, x => x.cnt);
            }
        }

        /// <summary>
        /// 已经「转成目标」的条数（goal_id 有值）。
        /// 注意这不是"转化率"的分母/分子 —— 想法库不考核转化，
        /// 这个数字只用来确认"这个出口真的被用过"，不是用来催人。
        /// </summary>
        public int CountLinked()
        {
            lock (_db.Sync)
            {
                return _db.Conn.ExecuteScalar<int>(
                    "SELECT COUNT(*) FROM ideas WHERE deleted = 0 AND goal_id IS NOT NULL AND goal_id <> ''");
            }
        }

        // ============================================================
        // 工具
        // ============================================================

        private static IdeaRow ToRow(Idea p) => new()
        {
            Id = p.Id,
            Text = p.Text,
            Kind = p.Kind,
            GoalId = p.GoalId,
            CreatedAt = p.CreatedAt,
            UpdatedAt = p.UpdatedAt,
            Deleted = p.Deleted ? 1 : 0,
        };

        private static Idea ToProto(IdeaRow r) => new()
        {
            Id = r.Id,
            Text = r.Text ?? string.Empty,
            Kind = r.Kind,
            GoalId = r.GoalId ?? string.Empty,
            CreatedAt = r.CreatedAt,
            UpdatedAt = r.UpdatedAt,
            Deleted = r.Deleted != 0,
        };

        /// <summary>GROUP BY 的返回行。</summary>
        private sealed class KindCount
        {
            public int kind { get; set; }
            public int cnt { get; set; }
        }
    }
}
