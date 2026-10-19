using System.Globalization;
using SQLite;

namespace PersonalHub.Client.Data
{
    /// <summary>
    /// 本地「每日计划」。
    ///
    /// 与目标的区别（也是它独立成表的原因）：
    ///   目标 = 一条跨月的线，建的时候要打字，回答"为什么"
    ///   计划 = 今天想做的 3 件，只点选，回答"今天先把时间给谁"
    ///
    /// `Day` 存 yyyy-MM-dd 字符串而不是时间戳：计划天然是"某一天"，
    /// 存时间戳就得回答"谁的时区、几点算跨天"，而这些在时间块那边已有固定答案（UTC+8 自然日）。
    ///
    /// 没有"完成"字段：达成率是当天同片时长 ÷ 预算算出来的，
    /// 一旦允许手动勾选，就一定会有人忘了勾，数字随即失真。
    /// </summary>
    [Table("plans")]
    public class LocalPlan : LocalRecordBase
    {
        [PrimaryKey]
        public override string Id { get; set; } = string.Empty;

        /// <summary>yyyy-MM-dd（固定 UTC+8 自然日）。</summary>
        public string Day { get; set; } = string.Empty;

        public string PieceCode { get; set; } = string.Empty;

        /// <summary>预算分钟；0 = 没设，合法。</summary>
        public int BudgetMin { get; set; }

        public string GoalId { get; set; } = string.Empty;

        public long CreatedAt { get; set; }

        public long UpdatedAt { get; set; }

        public override bool Deleted { get; set; }

        public override bool Synced { get; set; }

        public override long SyncedAt { get; set; }

        /// <summary>排序用：这一天 00:00（UTC+8）。</summary>
        public override long Timestamp => DayStartMs();

        [Ignore]
        public override long DayTimestamp => DayStartMs();

        private long DayStartMs()
        {
            if (!DateTime.TryParseExact(Day ?? string.Empty, "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            {
                return 0;
            }

            return new DateTimeOffset(d.Year, d.Month, d.Day, 0, 0, 0, Services.Period.Cst)
                .ToUnixTimeMilliseconds();
        }
    }
}
