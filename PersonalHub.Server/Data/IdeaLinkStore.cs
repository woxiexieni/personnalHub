using System;
using System.Collections.Generic;
using System.Linq;
using Personalhub;

namespace personalHub.Server.Data
{
    /// <summary>想法关系写入结果（与其它 Store 同一套路：错误必须是一句能直接展示的话）。</summary>
    public sealed class IdeaLinkSaveResult
    {
        public bool Ok { get; private init; }
        public string? Error { get; private init; }
        public IdeaLink? Link { get; private init; }

        public static IdeaLinkSaveResult Success(IdeaLink? link) => new() { Ok = true, Link = link };

        public static IdeaLinkSaveResult Fail(string error) => new() { Error = error };

        /// <summary>版本冲突必须算失败（不能"报成功、其实没写"）。</summary>
        public static IdeaLinkSaveResult ConflictWith(IdeaLink current) =>
            new() { Error = "版本冲突（conflict）：服务端已有更新的数据", Link = current };
    }

    /// <summary>
    /// 想法关系存储（R9：衍生 + 矛盾）。
    ///
    /// 校验只有四条，全是**结构性**的：
    ///   1. from_id / to_id 不能为空；
    ///   2. 不能自己连自己（衍生自己没有意义，矛盾自己是内耗的极致形态）；
    ///   3. kind 只能是 0（衍生）或 1（矛盾）；
    ///   4. 同一条边不允许重复 —— 客户端建边前会查重，服务端再守一遍
    ///      （老版本客户端 / 手快连点两次都会产生重复行）。
    ///
    /// 刻意**没有**的校验：
    ///   - 不校验 from/to 指向的想法是否存在 —— 软引用，想法可能在另一台设备上还没同步过来；
    ///   - 不校验反向重复（A 衍生 B 的同时有人建 B 衍生 A）—— 衍生方向有语义，两头各记一条
    ///     极少见且无害，交给客户端建边时查重就够了。
    /// </summary>
    public sealed class IdeaLinkStore
    {
        /// <summary>kind 的最大合法值（0 衍生 / 1 矛盾）。</summary>
        public const int MaxKind = 1;

        private readonly Db _db;

        public IdeaLinkStore(Db db) => _db = db;

        // ============================================================
        // 写
        // ============================================================

        public IdeaLinkSaveResult Save(IdeaLink p)
        {
            if (p is null) return IdeaLinkSaveResult.Fail("空关系");

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            p.Id ??= string.Empty;

            // 删除优先：墓碑不受内容校验影响。
            if (p.Deleted) return SoftDelete(p.Id, p.UpdatedAt);

            if (string.IsNullOrEmpty(p.Id)) p.Id = Guid.NewGuid().ToString("N");
            if (p.CreatedAt == 0) p.CreatedAt = now;

            p.FromId = (p.FromId ?? string.Empty).Trim();
            p.ToId = (p.ToId ?? string.Empty).Trim();

            if (p.FromId.Length == 0 || p.ToId.Length == 0)
            {
                return IdeaLinkSaveResult.Fail("关系的两端都不能为空");
            }

            if (p.FromId == p.ToId)
            {
                return IdeaLinkSaveResult.Fail("一条想法不能跟自己建关系");
            }

            if (p.Kind < 0 || p.Kind > MaxKind)
            {
                return IdeaLinkSaveResult.Fail("关系类型只能是 衍生 / 矛盾");
            }

            IdeaLinkRow? old;
            lock (_db.Sync)
            {
                old = _db.Conn.Find<IdeaLinkRow>(p.Id);
            }

            if (old is not null && p.UpdatedAt != 0 && old.UpdatedAt != 0 && p.UpdatedAt < old.UpdatedAt)
            {
                return IdeaLinkSaveResult.ConflictWith(ToProto(old));
            }

            if (p.UpdatedAt == 0) p.UpdatedAt = now;

            // 同一条边查重（不含墓碑）：from + to + kind 相同就视为重复。
            // 方向相异（B→A）不拦：衍生方向有语义，矛盾方向虽无语义但重复概率极低、无害。
            if (old is null)
            {
                IdeaLinkRow? dup;
                lock (_db.Sync)
                {
                    dup = _db.Conn.Query<IdeaLinkRow>(
                        "SELECT * FROM idea_links WHERE deleted = 0 AND kind = ? AND from_id = ? AND to_id = ? LIMIT 1",
                        p.Kind, p.FromId, p.ToId).FirstOrDefault();
                }

                if (dup is not null)
                {
                    // 重复不算失败：返回已存在的那条，客户端照常标记 Synced。
                    return IdeaLinkSaveResult.Success(ToProto(dup));
                }
            }

            var row = ToRow(p);
            if (old is not null) row.Deleted = old.Deleted;   // 更新不能顺手复活墓碑

            lock (_db.Sync)
            {
                _db.Conn.InsertOrReplace(row);
            }

            return IdeaLinkSaveResult.Success(ToProto(row));
        }

        public IdeaLinkSaveResult SoftDelete(string id, long updatedAt = 0)
        {
            if (string.IsNullOrEmpty(id)) return IdeaLinkSaveResult.Fail("缺少关系 id");

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            lock (_db.Sync)
            {
                var old = _db.Conn.Find<IdeaLinkRow>(id);
                if (old is null) return IdeaLinkSaveResult.Success(null);

                old.Deleted = 1;
                old.UpdatedAt = updatedAt != 0 ? updatedAt : now;
                _db.Conn.InsertOrReplace(old);
            }

            return IdeaLinkSaveResult.Success(null);
        }

        // ============================================================
        // 读
        // ============================================================

        /// <param name="kind">-1 全部 / 0 衍生 / 1 矛盾。</param>
        public IReadOnlyList<IdeaLink> List(int kind, bool includeDeleted)
        {
            var where = new List<string>();
            var args = new List<object>();

            if (!includeDeleted) where.Add("deleted = 0");

            if (kind >= 0)
            {
                where.Add("kind = ?");
                args.Add(kind);
            }

            string sql = "SELECT * FROM idea_links";
            if (where.Count > 0) sql += " WHERE " + string.Join(" AND ", where);
            sql += " ORDER BY updated_at DESC";

            lock (_db.Sync)
            {
                return _db.Conn.Query<IdeaLinkRow>(sql, args.ToArray()).Select(ToProto).ToList();
            }
        }

        /// <summary>各类型条数（管理端 KPI 用）。</summary>
        public IReadOnlyDictionary<int, int> CountByKind()
        {
            lock (_db.Sync)
            {
                return _db.Conn
                    .Query<KindCount>("SELECT kind, COUNT(*) AS cnt FROM idea_links WHERE deleted = 0 GROUP BY kind")
                    .ToDictionary(x => x.kind, x => x.cnt);
            }
        }

        // ============================================================
        // 工具
        // ============================================================

        private static IdeaLinkRow ToRow(IdeaLink p) => new()
        {
            Id = p.Id,
            FromId = p.FromId,
            ToId = p.ToId,
            Kind = p.Kind,
            CreatedAt = p.CreatedAt,
            UpdatedAt = p.UpdatedAt,
            Deleted = p.Deleted ? 1 : 0,
        };

        private static IdeaLink ToProto(IdeaLinkRow r) => new()
        {
            Id = r.Id,
            FromId = r.FromId,
            ToId = r.ToId,
            Kind = r.Kind,
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
