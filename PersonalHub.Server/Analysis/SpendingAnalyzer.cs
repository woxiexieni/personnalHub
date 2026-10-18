using System;
using System.Collections.Generic;
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
    }
}
