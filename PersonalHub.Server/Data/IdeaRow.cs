using SQLite;

namespace personalHub.Server.Data
{
    /// <summary>
    /// ideas 表的行模型（想法），对应 proto 的 <see cref="Personalhub.Idea"/>。
    ///
    /// 只有五个业务字段：text / kind / goal_id / 时间戳 / 墓碑。
    ///
    /// **没有 title 字段**：想法只有一段正文，不拆标题和内容。
    /// 拆开的那一刻就要求用户先概括再记录，而"先概括"正是想法流失的主要原因。
    ///
    /// **没有处理状态**：不做"未处理 / 已处理 / 已归档"。
    /// 想法库是原材料仓库，不是待办清单；一旦有未处理计数，它就会变成第二个待办，
    /// 然后开始有过期感、然后就不想打开它了。
    ///
    /// kind 允许 0（未分类）：分类是事后整理的动作，不该挡在记录前面。
    /// </summary>
    [Table("ideas")]
    public class IdeaRow
    {
        [PrimaryKey, Column("id")]
        public string Id { get; set; } = string.Empty;

        [Column("text")]
        public string? Text { get; set; }

        /// <summary>0 未分类 / 1 事实 / 2 评价 / 3 计划行动 / 4 想象脑补。</summary>
        [Column("kind")]
        public int Kind { get; set; }

        /// <summary>「转成目标」之后回填的目标 id；空 = 还没转化。</summary>
        [Column("goal_id")]
        public string? GoalId { get; set; }

        [Column("created_at")]
        public long CreatedAt { get; set; }

        [Column("updated_at")]
        public long UpdatedAt { get; set; }

        [Column("deleted")]
        public int Deleted { get; set; }
    }
}
