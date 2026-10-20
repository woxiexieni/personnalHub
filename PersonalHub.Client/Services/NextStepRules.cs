using PersonalHub.Client.Data;

namespace PersonalHub.Client.Services
{
    /// <summary>
    /// 「帮我想」的**离线规则版**。
    ///
    /// 为什么一定要有它：AI 挂了、没网、没配 key、模型抽风，这四种情况里三种是常态。
    /// 如果这时按钮点了没反应，用户会记住"这个功能不可靠"，然后永远不再点它 ——
    /// 而"想不出下一步"本来有 70% 是可以靠历史数据直接答出来的：
    ///   上次做的是哪个片、通常做多久、上次自己写了什么教训。
    ///
    /// 规则版**不猜语义**，只排序历史：
    ///   1. 上次自己写的「下次怎么做」→ 直接摆出来（用户自己的话最可信）
    ///   2. 这个目标最常用的片 + 该片的中位时长 → "写代码再来一段 30m"
    ///   3. 想法库里还没做的「计划/行动」→ 搬一条过来
    ///   4. 什么都不够 → 给一句"先做 15 分钟"，不硬编内容
    ///
    /// 中位数而不是平均数：一次三小时的马拉松会把平均值拉飞，中位数不会。
    /// </summary>
    public static class NextStepRules
    {
        /// <summary>与服务端校验保持一致：超过这个长度就不算"今天能做完的动作"。</summary>
        private const int MaxActionLen = 20;

        public static List<AiStepOption> Build(
            IReadOnlyList<LocalBlock> blocks30d,
            IReadOnlyList<LocalIdea> planIdeas,
            IReadOnlyCollection<string> todayPieces,
            Func<string, string> displayName)
        {
            var result = new List<AiStepOption>();

            var pieces = blocks30d
                .Where(b => !string.IsNullOrEmpty(b.PieceCode))
                .GroupBy(b => b.PieceCode)
                .OrderByDescending(g => g.Count())
                .ThenByDescending(g => g.Sum(b => Math.Max(0, b.DurationMin)))
                .ToList();

            if (pieces.Count == 0)
            {
                // 这个目标一条块都没有 = 没有任何依据。此时给建议只能是编，
                // 所以宁可空手：界面会说"先自己写一句，或者记一笔再回来"。
                return result;
            }

            var (topCode, topMinutes) = (pieces[0].Key, Median(pieces[0]));

            var lessons = blocks30d
                .Where(b => !string.IsNullOrWhiteSpace(b.Lesson))
                .OrderByDescending(b => b.OccurredAt)
                .Select(b => b.Lesson.Trim())
                .Distinct()
                .ToList();

            void Add(string action, string code, int minutes)
            {
                action = Clip(action, MaxActionLen);
                if (action.Length == 0) return;
                if (result.Count >= 3) return;
                if (result.Any(r => r.Action == action)) return;
                if (!string.IsNullOrEmpty(code) && todayPieces.Contains(code)) return;

                result.Add(new AiStepOption(action, code, NearestDuration(minutes)));
            }

            // 1. 用户自己写过的那句话 —— 它比任何建议都准，只是他自己忘了
            foreach (string lesson in lessons.Take(2))
            {
                Add(lesson, topCode, topMinutes);
            }

            // 2. 最常用的片：换一个说法给"继续"
            Add($"{displayName(topCode)}再来一段", topCode, topMinutes);

            // 3. 想法库里那些还没变成目标的「计划/行动」
            foreach (var idea in planIdeas.Take(2))
            {
                Add(idea.Text, topCode, 30);
            }

            // 4. 兜底：最小可执行的一段。写不出内容就写"开始",这也是有效的一步。
            Add("先做 15 分钟，看看卡在哪", topCode, 15);

            return result;
        }

        /// <summary>这个目标最常用的片（排计划、跑规则版都要用；空 = 没有历史）。</summary>
        public static string TopPiece(IReadOnlyList<LocalBlock> blocks30d) =>
            blocks30d
                .Where(b => !string.IsNullOrEmpty(b.PieceCode))
                .GroupBy(b => b.PieceCode)
                .OrderByDescending(g => g.Count())
                .ThenByDescending(g => g.Sum(b => Math.Max(0, b.DurationMin)))
                .Select(g => g.Key)
                .FirstOrDefault() ?? string.Empty;

        private static int Median(IGrouping<string, LocalBlock> group)
        {
            var d = group.Select(b => b.DurationMin).Where(m => m > 0).OrderBy(m => m).ToList();
            return d.Count == 0 ? 30 : d[d.Count / 2];
        }

        /// <summary>吸到最近的固定档（5/15/30/45/60/90/120/180）。</summary>
        public static int NearestDuration(int minutes)
        {
            int best = Pieces.Durations[0];
            foreach (int d in Pieces.Durations)
            {
                if (Math.Abs(d - minutes) < Math.Abs(best - minutes)) best = d;
            }
            return best;
        }

        /// <summary>按字数截断（中文没有空格，只能按字符数）。截断后去掉尾巴上的标点。</summary>
        private static string Clip(string? s, int max)
        {
            s = (s ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            if (s.Length <= max) return s;
            return s.Substring(0, max).TrimEnd('，', '。', ',', '；', ';', ' ');
        }
    }
}
