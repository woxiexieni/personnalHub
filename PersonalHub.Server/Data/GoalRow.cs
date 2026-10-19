using SQLite;

namespace personalHub.Server.Data
{
    /// <summary>
    /// goals 表的行模型（长目标），对应 proto 的 <see cref="Personalhub.Goal"/>。
    ///
    /// 刻意只有四样东西：title / status / next_step / is_current。
    /// **没有 progress、没有 deadline、没有 last_active_at** ——
    ///   进度百分比要人估，一估就假；
    ///   截止日期会制造"超期失败"，而真实推进本来就是断的；
    ///   最后活动存成字段，在删块 / 改归属后会永久错掉，且错得看不出来。
    /// 三样都是查询出来的（见 <see cref="GoalStore.List"/>）。
    /// </summary>
    [Table("goals")]
    public class GoalRow
    {
        [PrimaryKey, Column("id")]
        public string Id { get; set; } = string.Empty;

        [Column("title")]
        public string? Title { get; set; }

        /// <summary>0 进行中 / 1 暂停 / 2 完成 / 3 放弃。</summary>
        [Column("status")]
        public int Status { get; set; }

        /// <summary>「一条下一步」，一句话，可空。不是清单。</summary>
        [Column("next_step")]
        public string? NextStep { get; set; }

        /// <summary>录入时新块默认归它。全局最多一个为 1。</summary>
        [Column("is_current")]
        public int IsCurrent { get; set; }

        [Column("started_at")]
        public long StartedAt { get; set; }

        [Column("created_at")]
        public long CreatedAt { get; set; }

        [Column("updated_at")]
        public long UpdatedAt { get; set; }

        [Column("deleted")]
        public int Deleted { get; set; }
    }
}
