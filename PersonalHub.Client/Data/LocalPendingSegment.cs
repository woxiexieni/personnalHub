using SQLite;

namespace PersonalHub.Client.Data
{
    /// <summary>
    /// 自动时间片的「待确认段」（R13，纯本地，不入同步四件套）。
    ///
    /// 来源：前台服务采集的手机信号（屏幕亮灭/解锁/网络切换/前台应用）
    /// 经 <see cref="Services.SegmentCapture"/> 切成的候选时间段。
    /// 设计立场与 R12 自动记账一致：**机器干苦力（切段、猜标签），人只做确认**，
    /// 没确认的不直接进时间块表，躺在这个队列里等晚间过一遍时间线。
    ///
    /// 不上服务端的理由：跟 pending_pays 一样，待确认的价值只在"当天还没过完"
    /// 的这段时间里；确认后生成正式 LocalBlock（这次 StartedAt/EndedAt 有真值），
    /// 才走同步链路。
    /// </summary>
    [Table("pending_segments")]
    public class LocalPendingSegment : LocalRecordBase
    {
        [PrimaryKey]
        public override string Id { get; set; } = string.Empty;

        /// <summary>段开始（Unix 毫秒）。通常 = 亮屏/解锁时刻，v0 规则的近似值。</summary>
        public long StartedAt { get; set; }

        /// <summary>段结束（Unix 毫秒）。通常 = 灭屏/超时时刻。</summary>
        public long EndedAt { get; set; }

        /// <summary>
        /// 候选片代号（段内前台应用按映射表投票得出）。空 = 没猜出来，
        /// 确认页让用户手动挑；"全部自动入库"时会跳过空候选段。
        /// </summary>
        public string PieceCode { get; set; } = string.Empty;

        /// <summary>段内主用的应用包名（候选标签的证据，确认页显示给人看）。</summary>
        public string SourceApp { get; set; } = string.Empty;

        /// <summary>收口依据的信号（"screen_off" / "timeout"），纯本地排查用。</summary>
        public string BoundarySignal { get; set; } = string.Empty;

        /// <summary>
        /// 段内运动状态（R14.5 特征列）："still" 基本没动 / "move" 有动作 / "big" 大幅移动
        /// （加速度方差按分钟折算取众数）。空 = 传感器不可用或没采到。
        /// </summary>
        public string Motion { get; set; } = string.Empty;

        /// <summary>
        /// 段内 Wi-Fi 身份（R14.5 特征列）：连接的 SSID 原文（只在机内，不上传）。
        /// 空 = 没连 Wi-Fi 或没权限读。这是「在家 / 在公司」的主要证据 —— 比定位便宜得多。
        /// </summary>
        public string Wifi { get; set; } = string.Empty;

        /// <summary>true = 没经人确认、按候选标签自动入库的（关页/全部入库的兜底路径）。</summary>
        public bool AutoApplied { get; set; }

        /// <summary>
        /// 处理完成的时刻（Unix 毫秒，记入/换片/自动入库都算）。0 = 还没处理。
        /// 确认页的灰卡（已处理但当天还在时间线上躺着）靠它做"隔天清空"的显示过滤 —— 纯显示层，
        /// 数据本体不动，历史随时可翻。
        /// </summary>
        public long ConfirmedAt { get; set; }

        /// <summary>
        /// 0 待确认 / 1 已确认（人点过「记入」或「换片」，已写 blocks） /
        /// 2 已忽略 / 3 已自动入库（兜底路径，AutoApplied = true）。
        /// </summary>
        public int Status { get; set; }

        public long CreatedAt { get; set; }

        // ---- LocalRecordBase 必须实现的部分（本表不走同步，恒为默认值）----

        public override bool Synced { get; set; }
        public override long SyncedAt { get; set; }

        /// <summary>恒 false：本表不用软删墓碑，过期清理直接物理删。</summary>
        public override bool Deleted { get; set; }

        /// <summary>排序用时间 = 段开始。</summary>
        public override long Timestamp => StartedAt;

        [Ignore]
        public override long DayTimestamp => StartedAt;

        /// <summary>时长（分钟，向下取整）。sqlite-net 自动忽略只读属性，不建列。</summary>
        public int Minutes => Math.Max(0, (int)((EndedAt - StartedAt) / 60_000));
    }
}
