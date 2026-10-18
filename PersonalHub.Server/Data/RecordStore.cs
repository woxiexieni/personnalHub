using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Personalhub;
using SQLite;

namespace personalHub.Server.Data
{
    /// <summary>
    /// 单条写入的结果。所有写路径（手机上传 / 管理端保存 / 补录 / 删除）都返回它，
    /// 这样"为什么没存进去"永远是一句能直接展示给人看的话，而不是靠 failed_ids 猜。
    /// </summary>
    public sealed class SaveResult
    {
        public bool Ok { get; private init; }

        /// <summary>服务端已有更新的版本 —— 客户端不要重试，应该拉取覆盖。</summary>
        public bool Conflict { get; private init; }

        public string? Error { get; private init; }

        /// <summary>写入后的最终版本（服务端补齐了 id / created_at / updated_at）。</summary>
        public Record? Record { get; private init; }

        public static SaveResult Success(Record? record) => new() { Ok = true, Record = record };
        public static SaveResult Fail(string error) => new() { Error = error };
        public static SaveResult ConflictWith(Record current) =>
            new() { Conflict = true, Error = "版本冲突：服务端已有更新的数据", Record = current };
    }

    /// <summary>管理端列表的一页（含总数，页面要显示"共 N 条"）。</summary>
    public sealed record PagedRecords(IReadOnlyList<Record> Items, int Total);

    /// <summary>
    /// 账单存储层 —— SQLite 落盘实现。
    ///
    /// 三条设计约束（改代码前先看这三条）：
    /// 1. **删除先于业务校验**：分类停用后，该分类下的记录必须还能删掉，否则就是删不掉的数据。
    /// 2. **一条 SQL 都不拼字符串值**：全部用参数，只有 limit 这种整数做了钳制后内联。
    /// 3. **写入只有 <see cref="Save"/> 一个入口**：手机端和管理端共用，校验不允许出现第二份。
    /// </summary>
    public sealed class RecordStore
    {
        private readonly Db _db;
        private readonly CategoryStore _categories;

        public RecordStore(Db db, CategoryStore categories)
        {
            _db = db;
            _categories = categories;
        }

        /// <summary>库文件路径（排查用）。</summary>
        public string DbPath => _db.Path;

        // ============================================================
        // 写
        // ============================================================

        /// <summary>
        /// 统一写入入口：校验 → 版本比较 → Upsert。
        ///
        /// 手机端通过 UploadRecords 逐条调用它；管理端通过 SaveRecord / 补录表单调用它。
        /// 两条路必须是同一份校验，否则会出现"手机不让存 0 元、网页能存进去"，统计当场算错。
        /// </summary>
        public SaveResult Save(Record r)
        {
            if (r is null) return SaveResult.Fail("空记录");

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            r.Id ??= string.Empty;

            // ---- 1) 删除优先。分类可能已经停用，但删除不能被业务校验拦住。 ----
            if (r.Deleted)
            {
                return SoftDelete(r.Id, r.UpdatedAt);
            }

            // ---- 2) 补齐服务端负责的字段 ----
            if (string.IsNullOrEmpty(r.Id)) r.Id = Guid.NewGuid().ToString("N");
            if (r.CreatedAt == 0) r.CreatedAt = now;
            if (r.SpentAt == 0) r.SpentAt = r.CreatedAt;

            // ---- 3) 业务校验 ----
            if (double.IsNaN(r.Amount) || double.IsInfinity(r.Amount))
                return SaveResult.Fail("金额不是有效数字");
            if (r.Amount <= 0)
                return SaveResult.Fail("金额必须大于 0");
            if (r.Amount > 100_000_000)
                return SaveResult.Fail("金额过大，请检查");
            if (HasMoreThanTwoDecimals(r.Amount))
                return SaveResult.Fail("金额最多两位小数");

            var cat = _categories.Get(r.CategoryId);
            if (cat is null || cat.Enabled == 0)
                return SaveResult.Fail("分类不存在或已停用");
            if (cat.Direction != r.Direction)
                return SaveResult.Fail("分类与收支方向不匹配");

            // 容差 5 分钟：手机时钟快几十秒是常事，卡死 0 容差会让人莫名记不上账。
            if (r.SpentAt > now + 5 * 60_000)
                return SaveResult.Fail("消费时间不能晚于当前时间");
            if (r.SpentAt < 946684800000L)
                return SaveResult.Fail("消费时间早于 2000 年，请检查");
            if ((r.Note?.Length ?? 0) > 200)
                return SaveResult.Fail("备注不能超过 200 字");

            // ---- 4) 版本比较 ----
            RecordRow? old;
            lock (_db.Sync)
            {
                old = _db.Conn.Find<RecordRow>(r.Id);
            }

            // 只有"双方都有版本号"时才比较：老客户端不传 updated_at（=0），必须直接接受，
            // 否则整个旧版本客户端会被版本校验全部拒绝。
            if (old is not null && r.UpdatedAt != 0 && old.UpdatedAt != 0 && r.UpdatedAt < old.UpdatedAt)
            {
                return SaveResult.ConflictWith(ToProto(old));
            }

            if (r.UpdatedAt == 0) r.UpdatedAt = now;

            var row = ToRow(r);
            if (old is not null)
            {
                // 更新不能顺手"复活"一条已删记录（否则客户端手里一份旧副本就能把删除撤销掉）
                row.Deleted = old.Deleted;
            }

            lock (_db.Sync)
            {
                _db.Conn.InsertOrReplace(row);
            }

            return SaveResult.Success(ToProto(row));
        }

        /// <summary>
        /// 软删除：只置 deleted = 1，数据仍在库里，随时能恢复。
        /// 删除不存在的 id 视为成功（幂等）—— 客户端的墓碑可能对应一条从未成功上传的创建记录。
        /// </summary>
        public SaveResult SoftDelete(string id, long updatedAt = 0)
        {
            if (string.IsNullOrEmpty(id)) return SaveResult.Fail("缺少记录 id");

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            lock (_db.Sync)
            {
                var old = _db.Conn.Find<RecordRow>(id);
                if (old is null) return SaveResult.Success(null);

                old.Deleted = 1;
                old.UpdatedAt = updatedAt != 0 ? updatedAt : now;
                _db.Conn.InsertOrReplace(old);
            }

            return SaveResult.Success(null);
        }

        // ============================================================
        // 读
        // ============================================================

        /// <summary>按区间查询，时间语义 [from, to)。</summary>
        public IReadOnlyList<Record> List(long from, long to, int direction = -1, bool includeDeleted = false, int limit = 0)
            => ListRows(from, to, direction, includeDeleted, limit).Select(ToProto).ToList();

        /// <summary>同 <see cref="List"/>，返回行模型（分析用，避免 proto 往返映射）。</summary>
        public IReadOnlyList<RecordRow> ListRows(long from, long to, int direction = -1, bool includeDeleted = false, int limit = 0)
        {
            var sql = new StringBuilder("SELECT * FROM records WHERE spent_at >= ? AND spent_at < ?");
            var args = new List<object> { from, to };

            if (!includeDeleted) sql.Append(" AND deleted = 0");
            if (direction >= 0)
            {
                sql.Append(" AND direction = ?");
                args.Add(direction);
            }

            sql.Append(" ORDER BY spent_at DESC, created_at DESC");

            if (limit > 0)
            {
                // 整数不做参数化（SQLite 的 LIMIT 只接受字面量/绑定整数，这里钳制上限防滥用）
                sql.Append(" LIMIT ").Append(Math.Min(limit, 5000));
            }

            lock (_db.Sync)
            {
                return _db.Conn.Query<RecordRow>(sql.ToString(), args.ToArray());
            }
        }

        /// <summary>单条读取（含已删的，管理端编辑时要能看到）。</summary>
        public Record? Get(string id)
        {
            lock (_db.Sync)
            {
                var row = _db.Conn.Find<RecordRow>(id);
                return row is null ? null : ToProto(row);
            }
        }

        /// <summary>各分类下的记录条数（分类页展示用）。分类停用后历史仍在，所以这里含已删的排除、不含已删的。</summary>
        public IReadOnlyDictionary<int, int> CountByCategory()
        {
            lock (_db.Sync)
            {
                var rows = _db.Conn.Query<CategoryCount>(
                    "SELECT category_id AS id, COUNT(*) AS cnt FROM records WHERE deleted = 0 GROUP BY category_id");
                return rows.ToDictionary(r => r.id, r => r.cnt);
            }
        }

        private sealed class CategoryCount
        {
            public int id { get; set; }
            public int cnt { get; set; }
        }


        /// <summary>
        /// 管理端列表：区间 + 方向 + 分类 + 关键字 + 是否含已删，分页返回。
        ///
        /// 单独一个方法而不是给 <see cref="ListRows"/> 堆参数，是因为它的分页要返回总数
        /// （页面要显示"共 128 条 / 3 页"），而 ListRows 是给同步镜像用的、不能有分页语义。
        /// </summary>
        public PagedRecords Search(
            long from, long to,
            int direction = -1,
            int categoryId = -1,
            string? keyword = null,
            bool includeDeleted = false,
            int page = 1,
            int pageSize = 50)
        {
            if (page < 1) page = 1;
            if (pageSize < 1) pageSize = 50;
            if (pageSize > 500) pageSize = 500;

            var where = new StringBuilder(" WHERE spent_at >= ? AND spent_at < ?");
            var args = new List<object> { from, to };

            if (!includeDeleted) where.Append(" AND deleted = 0");
            if (direction >= 0)
            {
                where.Append(" AND direction = ?");
                args.Add(direction);
            }
            if (categoryId > 0)
            {
                where.Append(" AND category_id = ?");
                args.Add(categoryId);
            }
            if (!string.IsNullOrWhiteSpace(keyword))
            {
                // 关键字同时匹配备注与支付方式，方便查"那笔 25 块午饭"
                where.Append(" AND (IFNULL(note,'') LIKE ? OR IFNULL(pay_method,'') LIKE ?)");
                string like = "%" + keyword.Trim() + "%";
                args.Add(like);
                args.Add(like);
            }

            string filter = where.ToString();

            lock (_db.Sync)
            {
                int total = _db.Conn.ExecuteScalar<int>("SELECT COUNT(*) FROM records" + filter, args.ToArray());

                var pageArgs = new List<object>(args) { pageSize, (page - 1) * pageSize };
                var rows = _db.Conn.Query<RecordRow>(
                    "SELECT * FROM records" + filter + " ORDER BY spent_at DESC, created_at DESC LIMIT ? OFFSET ?",
                    pageArgs.ToArray());

                return new PagedRecords(rows.Select(ToProto).ToList(), total);
            }
        }

        // ============================================================
        // 映射
        // ============================================================

        private static bool HasMoreThanTwoDecimals(double amount)
        {
            double scaled = amount * 100.0;
            return Math.Abs(scaled - Math.Round(scaled)) > 1e-6;
        }

        private static RecordRow ToRow(Record r) => new()
        {
            Id = r.Id,
            Amount = r.Amount,
            CategoryId = r.CategoryId,
            Note = r.Note,
            SpentAt = r.SpentAt,
            PayMethod = r.PayMethod,
            CreatedAt = r.CreatedAt,
            Direction = r.Direction,
            UpdatedAt = r.UpdatedAt,
            Deleted = r.Deleted ? 1 : 0,
        };

        private static Record ToProto(RecordRow row) => new()
        {
            Id = row.Id,
            Amount = row.Amount,
            CategoryId = row.CategoryId,
            Note = row.Note ?? string.Empty,
            SpentAt = row.SpentAt,
            PayMethod = row.PayMethod ?? string.Empty,
            CreatedAt = row.CreatedAt,
            Direction = row.Direction,
            UpdatedAt = row.UpdatedAt,
            Deleted = row.Deleted != 0,
        };
    }
}
