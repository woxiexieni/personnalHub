using System;
using System.Collections.Generic;
using System.Linq;

namespace personalHub.Server.Data
{
    /// <summary>
    /// 分类存储层。
    ///
    /// 只读部分（R1）：手机端录入页的 chips、服务端写入校验、统计聚合的名称来源。
    /// 增删改（管理端分类维护）留到 P1，届时也走这一层，不要另开一条 SQL 路径。
    /// </summary>
    public sealed class CategoryStore
    {
        private readonly Db _db;

        public CategoryStore(Db db)
        {
            _db = db;
        }

        /// <summary>
        /// 全部（或仅启用的）分类，按 方向 → 排序号 排列。
        /// 顺序固定，不按使用频次动态重排 —— 动态排序会让按钮位置跳动，反而更难找。
        /// </summary>
        public IReadOnlyList<CategoryRow> All(bool enabledOnly = false)
        {
            lock (_db.Sync)
            {
                var rows = _db.Conn.Table<CategoryRow>().ToList();
                if (enabledOnly) rows = rows.Where(c => c.Enabled != 0).ToList();

                return rows
                    .OrderBy(c => c.Direction)
                    .ThenBy(c => c.SortOrder)
                    .ThenBy(c => c.Id)
                    .ToList();
            }
        }

        /// <summary>单条读取（含已停用的）。写入校验要用：停用的分类不能记新账。</summary>
        public CategoryRow? Get(int id)
        {
            lock (_db.Sync)
            {
                return _db.Conn.Find<CategoryRow>(id);
            }
        }

        /// <summary>分类数据的最后更新时间，客户端可据此判断要不要重新拉。</summary>
        public long LastUpdatedAt()
        {
            lock (_db.Sync)
            {
                return _db.Conn.ExecuteScalar<long>(
                    "SELECT COALESCE(MAX(updated_at),0) FROM categories");
            }
        }
    }
}
