using SQLite;

namespace personalHub.Server.Data
{
    /// <summary>
    /// categories 表的行模型。
    ///
    /// 为什么分类要变成实体（而不是客户端里那个硬编码数组）：
    /// 1. 服务端原来只收到 category_id = 3，**它根本不知道 3 叫什么**，
    ///    按分类聚合只能输出数字，做不出"餐饮占 42%"；
    /// 2. 你改一次分类名，客户端硬编码和历史统计立刻分叉；
    /// 3. 收入分类要和支出分类并存，必须有人管方向。
    ///
    /// id 分段约定：支出 1~99，收入 101~199。分段不是必须的（direction 已经能判断），
    /// 但它让「看一眼 id 就知道方向」成立，排查数据时省事。
    /// </summary>
    [Table("categories")]
    public class CategoryRow
    {
        /// <summary>业务主键，不由数据库自增（要保证收支两段不撞号）。</summary>
        [PrimaryKey, Column("id")]
        public int Id { get; set; }

        [Column("name")]
        public string Name { get; set; } = string.Empty;

        [Column("icon")]
        public string? Icon { get; set; }

        /// <summary>0 = 支出分类，1 = 收入分类。</summary>
        [Column("direction")]
        public int Direction { get; set; }

        /// <summary>显示顺序。固定顺序 = 位置记忆稳定，不按使用频次动态重排。</summary>
        [Column("sort_order")]
        public int SortOrder { get; set; }

        /// <summary>停用后不再出现在录入页，但历史记录仍然引用它（统计不能因此断掉）。</summary>
        [Column("enabled")]
        public int Enabled { get; set; } = 1;

        [Column("updated_at")]
        public long UpdatedAt { get; set; }
    }
}
