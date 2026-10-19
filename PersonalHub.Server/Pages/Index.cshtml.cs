using Microsoft.AspNetCore.Mvc.RazorPages;
using Personalhub;
using personalHub.Server.Analysis;
using personalHub.Server.Data;

namespace PersonalHub.Server.Pages
{
    /// <summary>
    /// 概览页：今日 / 本月 / 最近记录。
    ///
    /// 只调 Store 和 Analyzer，不写裸 SQL —— 否则会出现"手机不让存 0 元、网页能存进去"，
    /// 统计当场就错，而且错得很隐蔽。
    /// </summary>
    public class IndexModel : PageModel
    {
        private readonly RecordStore _store;
        private readonly CategoryStore _categories;
        private readonly BlockStore _blocks;
        private readonly BackupService _backup;

        public IndexModel(RecordStore store, CategoryStore categories, BlockStore blocks,
            BackupService backup)
        {
            _store = store;
            _categories = categories;
            _blocks = blocks;
            _backup = backup;
        }

        /// <summary>
        /// 备份异常时的红字提示（null = 正常）。
        /// 放在首页是刻意的：备份每天只跑两三次，没人会主动打开 /backup 去确认它还在工作。
        /// </summary>
        public string? BackupWarning { get; private set; }

        public TodaySummary Today { get; private set; } = default!;
        public StructureResult Month { get; private set; } = default!;
        public IReadOnlyList<Record> Recent { get; private set; } = Array.Empty<Record>();
        public IReadOnlyDictionary<int, (string Name, string Icon)> CategoryMap { get; private set; }
            = new Dictionary<int, (string, string)>();

        /// <summary>今日时间块：已记录 / 未记录 / 按片。</summary>
        public TimeSlice TodayTime { get; private set; } = default!;

        /// <summary>本周时间块（周一至今）。</summary>
        public TimeSlice WeekTime { get; private set; } = default!;

        /// <summary>一个档位的时间汇总。<paramref name="Pieces"/> 已按分钟降序。</summary>
        public sealed record TimeSlice(
            int RecordedMinutes,
            int ElapsedMinutes,
            int UnrecordedMinutes,
            int Count,
            IReadOnlyList<PieceAggregate> Pieces)
        {
            public int CoverPct => ElapsedMinutes > 0
                ? (int)Math.Round(RecordedMinutes * 100.0 / ElapsedMinutes)
                : 0;
        }

        public string CategoryName(int id) =>
            CategoryMap.TryGetValue(id, out var c) ? c.Name : "未分类";

        public void OnGet()
        {
            var now = DateTimeOffset.UtcNow;

            var todayRange = Period.Today(now);
            var todayRows = _store.ListRows(todayRange.From, todayRange.To);
            Today = SpendingAnalyzer.Summarize(todayRows, todayRange, now);

            CategoryMap = _categories.All().ToDictionary(c => c.Id, c => (c.Name, c.Icon ?? string.Empty));
            (string Name, string Icon) Lookup(int id) =>
                CategoryMap.TryGetValue(id, out var c) ? c : ("未分类", string.Empty);

            var monthRange = PeriodResolver.Resolve(PeriodKind.Month, now);
            var monthRows = _store.ListRows(monthRange.From, monthRange.To);
            var prevRows = _store.ListRows(monthRange.PrevFrom, monthRange.PrevTo);
            Month = SpendingAnalyzer.BuildStructure(monthRows, prevRows, monthRange, Lookup);

            // 不限区间的最近 6 条（列表页显示 5 条，最后一条用来判断"还有更多"）
            Recent = _store.Search(0, now.ToUnixTimeMilliseconds() + 86_400_000L,
                direction: -1, categoryId: -1, keyword: null, includeDeleted: false,
                page: 1, pageSize: 6).Items;

            // 时间块：今日 + 本周（口径与服务端 GetPieceStats 同一套）
            TodayTime = BuildTimeSlice(todayRange.From, todayRange.To, now);
            var weekRange = PeriodResolver.Resolve(PeriodKind.Week, now);
            WeekTime = BuildTimeSlice(weekRange.From, weekRange.To, now);

            // 备份状态：只在"有问题"时提示，正常时不占位置
            var latest = _backup.List().FirstOrDefault();
            if (_backup.LastError is not null)
            {
                BackupWarning = "备份失败：" + _backup.LastError;
            }
            else if (latest is null)
            {
                BackupWarning = "还没有任何数据库快照。打开「备份」页看一眼，或点一次「现在备份一份」。";
            }
            else
            {
                int days = (BackupService.CstToday() - latest.Date).Days;
                if (days >= 2) BackupWarning = $"已经 {days} 天没有生成数据库快照了";
            }
        }

        /// <summary>
        /// 一个区间的时间汇总。
        ///
        /// 未记录 = 已过去的分钟数 − 已记录。右端钳到此刻，否则早上八点打开概览
        /// 会看到"今天未记录 16 小时" —— 那是纯焦虑，不是数据。
        /// </summary>
        private TimeSlice BuildTimeSlice(long from, long to, DateTimeOffset now)
        {
            var pieces = _blocks.AggregateByPiece(from, to);
            int recorded = pieces.Sum(p => p.Minutes);

            long end = Math.Min(to, now.ToUnixTimeMilliseconds());
            int elapsed = end <= from ? 0 : (int)((end - from) / 60000L);

            return new TimeSlice(
                recorded,
                elapsed,
                Math.Max(0, elapsed - recorded),
                pieces.Sum(p => p.Count),
                pieces);
        }
    }
}
