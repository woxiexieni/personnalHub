using SQLite;

namespace PersonalHub.Client.Data
{
    /// <summary>
    /// 本地「想法」。
    ///
    /// 定位：**原材料仓库**。它是这个系统里唯一一个"只进不出、不要求任何后续动作"的表。
    ///   账单     → 钱出去了（事实，有金额）
    ///   时间块   → 刚才那段时间去哪了（事实，有时长）
    ///   事件反思 → 一件事值得深挖（9 段，低频深写）
    ///   行为模式 → 要练的那一类反应（一季度两三个）
    ///   想法     → 还没成形的东西（可能永远不成形）
    ///
    /// 为什么它必须单独成表，而不是塞进事件反思：
    /// 事件反思是"这件事已经发生了、我要复盘它"，想法是"我脑子里刚冒出个东西、还不知道算不算事"。
    /// 合成一张表，就会要求每条想法先回答"这算什么事"，于是人就干脆不记了。
    ///
    /// Kind 允许 0（未分类）：**默认就是 0**。分类是事后整理的动作，不是记录的动作。
    /// </summary>
    [Table("ideas")]
    public class LocalIdea : LocalRecordBase, IVersioned
    {
        [PrimaryKey]
        public override string Id { get; set; } = string.Empty;

        /// <summary>正文。不拆标题 —— 拆开就要求"先概括再记录"。</summary>
        public string Text { get; set; } = string.Empty;

        /// <summary>0 未分类 / 1 事实 / 2 评价 / 3 计划行动 / 4 想象脑补。</summary>
        public int Kind { get; set; }

        /// <summary>「转成目标」之后回填；空 = 还没转化。它只做标记，不代表想法"完成了"。</summary>
        public string GoalId { get; set; } = string.Empty;

        /// <summary>
        /// 已经落实（不是"打算做"，是真的做完了）。
        ///
        /// 与 GoalId 是**两个独立**的标记，不互相推断：
        ///   转了目标的可能一直没做完；没转目标的小事照样可以直接落实。
        /// 它不是流程状态 —— 想法库里不会出现「还有 N 条没处理」这种计数，
        /// 这个勾选只在"回头翻"的时候有意义。
        /// </summary>
        public bool Done { get; set; }

        /// <summary>标记落实的时刻（Unix 毫秒）；0 = 没标过。取消落实时清 0。</summary>
        public long DoneAt { get; set; }

        /// <summary>
        /// 「升」成了信念：这句要一遍一遍往脑子里刻。
        ///
        /// 刻意**不另建表** —— 信念就是想法本身，只是多标了一个"值得反复练"。
        /// R8.2 起这三个字段随想法整条同步上服务端（proto 字段 10~12），换机不丢。
        /// </summary>
        public bool IsBelief { get; set; }

        /// <summary>拼词拼对了几次。只增不减，「再来一遍」也加。</summary>
        public int PracticeCount { get; set; }

        /// <summary>最后一次拼对的时间（Unix 毫秒）。0 = 没练过。</summary>
        public long LastPracticedAt { get; set; }

        /// <summary>
        /// 作废：这条想法当时想岔了 / 不想要了，但**不删**。
        ///
        /// 为什么不直接删：很多"废"是当时觉得废，过几个月回头看反而是线索；
        /// 删了就什么都没有了。作废 = 留在库里，但一眼能看出它已经不是活的想法。
        ///
        /// R8.2 起随想法同步上服务端（proto 字段 13~14），换机恢复不丢。
        /// </summary>
        public bool IsVoided { get; set; }

        /// <summary>作废的时刻（Unix 毫秒）。0 = 没作废过。恢复时清 0。</summary>
        public long VoidedAt { get; set; }

        /// <summary>
        /// 落实成果的链接（R9）：obsidian:// URI 或普通路径；空 = 没留。
        /// 跟 GoalId 同一个模子 —— 是"落实"这个动作的附属信息，一对一，不进关系边表。
        /// 软引用：不校验文件是否存在，跳转交给操作系统 / 对应 app。
        /// </summary>
        public string ArtifactUri { get; set; } = string.Empty;

        /// <summary>
        /// 计划行动的二次分类，只对 <see cref="Kind"/> = 3 有意义：
        /// 0 能干的（默认）/ 1 计划。其它 kind 恒为 0。
        ///
        /// 它回答的是**形状**（现在能不能动手），不是意图。方向是单向的：
        /// 1 → 0 允许（想清楚了），0 → 1 禁止（把答应自己的事降级 = 逃避）。
        /// 不新增两个 kind 值 —— 那会让已存的 31 条 kind=3 全部失去归类。
        /// </summary>
        public int PlanKind { get; set; }

        /// <summary>
        /// 四象限的「急」：**过了这周就不值钱了**，不是"今天必须做"。
        ///
        /// 口径刻意不放宽：他这里没有老板和交付日，用"今天必须做"这一格会永远是空的，
        /// 空着又没解释，人就会以为功能坏了。用"会过期"至少能覆盖报名截止、面试、约人这几类。
        /// </summary>
        public bool Urgent { get; set; }

        /// <summary>
        /// 四象限的「重」：没它也行，但有它别的事会更顺。
        ///
        /// **默认 true**，新条目直接落 Ⅱ。理由是"能记下来就说明当时觉得有价值"，
        /// 而"不重要"是少数，应该由人主动拖出去，而不是让所有人先证明自己重要。
        /// 它也会被算：Ⅱ 里 30 天没人碰 → 自动落 Ⅳ（见 IdeaQuadrantRules）。
        /// </summary>
        public bool Important { get; set; } = true;

        /// <summary>
        /// 你最后一次**真的动过**这条的时刻（Unix 毫秒）；0 = 从没动过（老数据）。
        ///
        /// 为什么不复用 <see cref="UpdatedAt"/>：那是版本比较字段，同步回写、镜像覆盖都会改它。
        /// 拿它判"30 天没人碰"，会让一条从没被打开的旧想法在每次同步后都变成"刚碰过"，
        /// 于是永远不落 Ⅳ —— 规则静默失效，而且看不出来。
        ///
        /// 只在本地存，不上服务端：它只服务于客户端的一条显示规则，
        /// 换机后从 CreatedAt 重算，最坏结果是旧条目早点落 Ⅳ，本来就是它该去的地方。
        /// </summary>
        public long TouchedAt { get; set; }

        public long CreatedAt { get; set; }

        public long UpdatedAt { get; set; }

        public override bool Deleted { get; set; }

        public override bool Synced { get; set; }

        public override long SyncedAt { get; set; }

        /// <summary>想法按创建时间归档：它没有"发生时刻"这个概念，冒出来就是那时候。</summary>
        public override long Timestamp => CreatedAt;

        [Ignore]
        public override long DayTimestamp => CreatedAt;
    }
}
