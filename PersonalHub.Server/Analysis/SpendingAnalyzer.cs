using System;
using System.Collections.Generic;
using System.Linq;
using personalHub.Server.Data;

namespace personalHub.Server.Analysis
{
    /// <summary>今日汇总（内部模型，单位：分）。</summary>
    public sealed record TodaySummary(
        long From,
        long To,
        long ExpenseCents,
        long IncomeCents,
        int ExpenseCount,
        DateTimeOffset GeneratedAt);

    /// <summary>构成里的一条（分类或支付方式）。单位：分。</summary>
    public sealed record SliceGroup(
        int Key,
        string Name,
        string Icon,
        long Cents,
        int Count,
        long PrevCents)
    {
        public bool HasPrev => PrevCents > 0;

        public double DeltaRatio => PrevCents > 0 ? (Cents - PrevCents) / (double)PrevCents : 0;
    }

    /// <summary>状态页一次要的全部数字（内部模型，单位：分）。</summary>
    public sealed record StructureResult(
        PeriodRange Range,
        long ExpenseCents,
        long IncomeCents,
        int ExpenseCount,
        long DailyAvgCents,
        long PrevExpenseCents,
        double DeltaRatio,
        bool HasPrev,
        IReadOnlyList<SliceGroup> Categories,
        IReadOnlyList<SliceGroup> PayMethods);

    /// <summary>
    /// 统计计算 —— 纯函数：无 IO、不读时钟、不碰数据库。
    ///
    /// 这样拆的理由很实际：统计口径出错几乎都来自"顺手用了一下 DateTime.Now"
    /// 或者"顺手查了张表"。把计算压成纯函数之后，口径问题可以用单元测试钉死，
    /// 而不是等某天发现"金额加起来差了两分钱"再回头找。
    /// </summary>
    public static class SpendingAnalyzer
    {
        /// <summary>元 → 分，四舍五入到整数分。所有聚合都先转分再相加。</summary>
        public static long Cents(double amount)
            => (long)Math.Round(amount * 100.0, MidpointRounding.AwayFromZero);

        // ============================================================
        // 今日 / 区间汇总
        // ============================================================

        /// <summary>
        /// 汇总一个区间。调用方负责传入"已经落在区间内"的行，
        /// 这里仍然再过滤一遍 —— 防御性，代价可忽略，但能避免调用方漏过滤导致的静默错账。
        /// </summary>
        public static TodaySummary Summarize(IEnumerable<RecordRow> rows, (long From, long To) range, DateTimeOffset now)
        {
            long expense = 0;
            long income = 0;
            int count = 0;

            foreach (var r in rows)
            {
                if (r.Deleted != 0) continue;
                if (r.SpentAt < range.From || r.SpentAt >= range.To) continue;

                long cents = Cents(r.Amount);
                if (cents <= 0) continue;

                if (r.Direction == 0)
                {
                    expense += cents;
                    count++;
                }
                else
                {
                    income += cents;
                }
            }

            return new TodaySummary(range.From, range.To, expense, income, count, now);
        }

        // ============================================================
        // 结构分析
        // ============================================================

        /// <summary>
        /// 结构分析：分类构成 + 支付方式构成 + 收入/结余 + 环比。
        ///
        /// 三条口径（改了就会和别处对不上）：
        ///  1. 只统计 <c>direction = 0</c>（支出）。收入不进分类占比，也不进日均。
        ///  2. 占比分母是"区间内全部支出"，不是"已分类合计" —— 这样各分类占比一定加起来 100%。
        ///  3. 排序按金额降序、同额按 key 升序。**必须稳定**，否则同样数据每次刷新顺序都跳。
        /// </summary>
        public static StructureResult BuildStructure(
            IEnumerable<RecordRow> current,
            IEnumerable<RecordRow> previous,
            PeriodRange range,
            Func<int, (string Name, string Icon)> categoryLookup)
        {
            var curRange = (From: range.From, To: range.To);
            var prevRange = (From: range.PrevFrom, To: range.PrevTo);

            long expense = 0, income = 0;
            int count = 0;

            var byCategory = new Dictionary<int, (long Cents, int Count)>();
            var byPay = new Dictionary<string, (long Cents, int Count)>();
            var prevByCategory = new Dictionary<int, long>();

            foreach (var r in current)
            {
                if (r.Deleted != 0) continue;
                if (r.SpentAt >= curRange.From && r.SpentAt < curRange.To)
                {
                    long cents = Cents(r.Amount);
                    if (cents <= 0) continue;

                    if (r.Direction == 0)
                    {
                        expense += cents;
                        count++;

                        byCategory.TryGetValue(r.CategoryId, out var c);
                        byCategory[r.CategoryId] = (c.Cents + cents, c.Count + 1);

                        string pay = NormalizePayMethod(r.PayMethod);
                        byPay.TryGetValue(pay, out var p);
                        byPay[pay] = (p.Cents + cents, p.Count + 1);
                    }
                    else
                    {
                        income += cents;
                    }
                }
            }

            foreach (var r in previous)
            {
                if (r.Deleted != 0) continue;
                if (r.Direction != 0) continue;
                if (r.SpentAt < prevRange.From || r.SpentAt >= prevRange.To) continue;

                long cents = Cents(r.Amount);
                if (cents <= 0) continue;

                prevByCategory.TryGetValue(r.CategoryId, out long acc);
                prevByCategory[r.CategoryId] = acc + cents;
            }

            long prevExpense = prevByCategory.Values.Sum();

            var categories = byCategory
                .Select(kv =>
                {
                    var (name, icon) = categoryLookup(kv.Key);
                    prevByCategory.TryGetValue(kv.Key, out long prev);
                    return new SliceGroup(kv.Key, name, icon, kv.Value.Cents, kv.Value.Count, prev);
                })
                .OrderByDescending(s => s.Cents)
                .ThenBy(s => s.Key)
                .ToList();

            var payMethods = byPay
                .Select(kv => new SliceGroup(
                    Key: 0,
                    Name: kv.Key,
                    Icon: string.Empty,
                    Cents: kv.Value.Cents,
                    Count: kv.Value.Count,
                    PrevCents: 0))
                .OrderByDescending(s => s.Cents)
                .ThenBy(s => s.Name, StringComparer.Ordinal)
                .ToList();

            long dailyAvg = expense / Math.Max(1, range.ElapsedDays);

            return new StructureResult(
                range,
                expense,
                income,
                count,
                dailyAvg,
                prevExpense,
                prevExpense > 0 ? (expense - prevExpense) / (double)prevExpense : 0,
                prevExpense > 0,
                categories,
                payMethods);
        }

        /// <summary>
        /// 支付方式归一化。
        ///
        /// 老数据是自由文本：`微信` / `微信支付` / `wx` 会被聚合成三类，
        /// 占比图看着就像"三个渠道各占三分之一"，实际是同一个渠道。
        /// 这里做保守映射，认不出来的统一归「其他」——宁可少分一类，也不要多出三类。
        /// </summary>
        public static string NormalizePayMethod(string? raw)
        {
            string s = (raw ?? string.Empty).Trim();
            if (s.Length == 0) return "未填写";

            string lower = s.ToLowerInvariant();

            if (lower.Contains("微信") || lower == "wx" || lower.Contains("wechat") || lower.Contains("weixin"))
                return "微信";
            if (lower.Contains("支付宝") || lower.Contains("alipay") || lower == "zfb")
                return "支付宝";
            if (lower.Contains("银行") || lower.Contains("bank") || lower.Contains("信用卡") || lower.Contains("储蓄卡"))
                return "银行卡";
            if (lower.Contains("现金") || lower.Contains("cash"))
                return "现金";

            return "其他";
        }
    }
}
