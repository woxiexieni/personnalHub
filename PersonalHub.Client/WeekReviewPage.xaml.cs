using System.Text;
using PersonalHub.Client.Controls;
using PersonalHub.Client.Data;
using PersonalHub.Client.Services;
using Personalhub;

namespace PersonalHub.Client
{
    /// <summary>
    /// 周回顾：把"这周"和"上周"放在一起，让人看见变化。
    ///
    /// 为什么要有这一页：反馈周期是三个月、记录周期是一天，中间那 89 天系统什么都不说，
    /// 人就会觉得"这个软件对我没用"。它要解决的问题只有一个 —— **让变化在一周内可见**。
    ///
    /// 这一页最刻意的设计是**没有一个输入框**。所有数字都是算出来的，
    /// 符合项目那条原则：需要用户维护的状态一律改算。打开十秒看完，关掉。
    ///
    /// 同样刻意没有的东西：
    ///   没有综合评分 / 没有完成率 / 没有连续天数 —— 有分数就会开始优化分数
    ///   没有建议清单 —— 一旦给出建议，它就变成第三个待办列表
    ///   没有"退步"这个词 —— 少记了可能是砍对了，系统不该替人判断好坏
    /// </summary>
    public partial class WeekReviewPage : ContentPage
    {
        private static readonly Color Ink = Color.FromArgb("#2C2C2A");
        private static readonly Color Muted = Color.FromArgb("#78766E");
        private static readonly Color Faint = Color.FromArgb("#8A8A8E");
        private static readonly Color Up = Color.FromArgb("#185FA5");
        private static readonly Color Down = Color.FromArgb("#8A8A8E");
        private static readonly Color Warn = Color.FromArgb("#A5701A");

        /// <summary>本地只镜像 30 天，所以最多往前翻 3 周（再往前就是"本地没有数据"，不是"你没记"）。</summary>
        private const int MaxBackWeeks = 3;

        /// <summary>一周至少记了这么多个块，才认为"这一周有数据"。</summary>
        private const int MinBlocksForTrend = 1;

        private readonly LocalRecordStore<LocalBlock> _blocks;
        private readonly LocalRecordStore<LocalGoal> _goals;
        private readonly BlockMirrorService _mirror;
        private readonly PieceSettings _settings;
        private readonly AiAssistService _ai;

        /// <summary>0 = 本周，-1 = 上周，-2 = 两周前…</summary>
        private int _offset;

        private bool _loading;

        // ---- AI 那一句话要用的数字：在渲染各段时顺手记下来，避免第二次遍历 ----
        private int _thisMin, _lastMin, _days, _blockCount, _activeGoals, _movedGoals;
        private string _topShift = string.Empty;
        private string _repeat = string.Empty;

        public WeekReviewPage(
            LocalRecordStore<LocalBlock> blocks,
            LocalRecordStore<LocalGoal> goals,
            BlockMirrorService mirror,
            PieceSettings settings,
            AiAssistService ai)
        {
            InitializeComponent();
            _blocks = blocks;
            _goals = goals;
            _mirror = mirror;
            _settings = settings;
            _ai = ai;
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();
            await LoadAsync();

            // 先用本地画完，再去服务端补一次。周中打开时服务端不会有更多数据，
            // 但月初/换机后的第一次打开会缺上周 —— 那时候这一次拉取才有价值。
            _ = PullThenReloadAsync();
        }

        private async Task PullThenReloadAsync()
        {
            try
            {
                var result = await _mirror.MirrorAsync();
                if (!result.Reachable || _loading) return;
                await LoadAsync();
            }
            catch
            {
                // 拉不到就保持本地那一份。这一页永远不能因为没网而空白。
            }
        }

        private async void OnPrevWeekClicked(object? sender, EventArgs e)
        {
            if (_loading) return;

            if (_offset <= -MaxBackWeeks)
            {
                // 到头了就回本周，而不是灰掉按钮 —— 灰按钮会让人以为坏了
                _offset = 0;
            }
            else
            {
                _offset--;
            }

            await LoadAsync();
        }

        // ============================================================
        // 区间
        // ============================================================

        private (long From, long To) RangeOf(int weekOffset)
        {
            var (from, to) = Period.ThisWeek();
            long shift = weekOffset * 7L * 24 * 60 * 60 * 1000;
            return (from + shift, to + shift);
        }

        // ============================================================
        // 加载
        // ============================================================

        private async Task LoadAsync()
        {
            if (_loading) return;
            _loading = true;

            try
            {
                var (from, to) = RangeOf(_offset);
                var (prevFrom, prevTo) = RangeOf(_offset - 1);

                // 换周之后，上一句 AI 的话就不对应当前这一周了：收起来，要用再点
                AiCommentCard.IsVisible = false;
                AiCommentBtn.IsVisible = true;

                // 一次读足够大的范围，省掉多次查询。本地最多 30 天，多读的部分是空的，无害。
                var (wideFrom, wideTo) = Period.LastDays(45);
                var all = await _blocks.GetByRangeAsync(wideFrom, wideTo);

                var thisWeek = all.Where(b => b.OccurredAt >= from && b.OccurredAt < to).ToList();
                var lastWeek = all.Where(b => b.OccurredAt >= prevFrom && b.OccurredAt < prevTo).ToList();

                RenderRange(from, to);
                RenderInvestment(thisWeek, lastWeek);
                await RenderGoalsAsync(all, thisWeek, lastWeek);
                RenderRepeats(thisWeek, lastWeek);
                RenderShift(thisWeek, lastWeek);

                PrevWeekBtn.Text = _offset <= -MaxBackWeeks ? "回到本周" : "上一周";
                StatusLabel.Text = _offset <= -MaxBackWeeks
                    ? "手机只存最近 30 天，再往前要在电脑上看。"
                    : string.Empty;
            }
            catch (Exception ex)
            {
                StatusLabel.Text = "读取失败：" + ex.Message;
            }
            finally
            {
                _loading = false;
            }
        }

        private void RenderRange(long from, long to)
        {
            var start = DateTimeOffset.FromUnixTimeMilliseconds(from).ToOffset(Period.Cst);
            var end = DateTimeOffset.FromUnixTimeMilliseconds(to).ToOffset(Period.Cst).AddDays(-1);

            RangeLabel.Text = _offset == 0 ? "这周" : $"{start:M月d日} 那一周";
            RangeSubLabel.Text = $"{start:M月d日} — {end:M月d日}"
                + (start.Year != DateTimeOffset.UtcNow.ToOffset(Period.Cst).Year ? $" · {start.Year}" : string.Empty);
        }

        // ============================================================
        // 一、投入
        // ============================================================

        private void RenderInvestment(List<LocalBlock> thisWeek, List<LocalBlock> lastWeek)
        {
            int nowMin = thisWeek.Sum(b => Math.Max(0, b.DurationMin));
            int prevMin = lastWeek.Sum(b => Math.Max(0, b.DurationMin));

            ThisWeekLabel.Text = nowMin > 0 ? BlockEntryPage.FormatMinutes(nowMin) : "—";
            LastWeekLabel.Text = prevMin > 0 ? BlockEntryPage.FormatMinutes(prevMin) : "—";

            // 上周是 0 的时候不算百分比：除以 0 会得到 ∞ 或 100%，两个都是假数字。
            if (prevMin == 0)
            {
                DeltaLabel.Text = nowMin > 0 ? "新增" : "—";
                DeltaLabel.TextColor = nowMin > 0 ? Up : Faint;
            }
            else
            {
                int diff = nowMin - prevMin;
                if (diff == 0)
                {
                    DeltaLabel.Text = "持平";
                    DeltaLabel.TextColor = Faint;
                }
                else
                {
                    DeltaLabel.Text = (diff > 0 ? "+" : "−") + BlockEntryPage.FormatMinutes(Math.Abs(diff));
                    DeltaLabel.TextColor = diff > 0 ? Up : Down;
                }
            }

            int days = thisWeek.Select(b => Period.DayStartOf(b.OccurredAt).ToUnixTimeMilliseconds())
                               .Distinct().Count();
            int prevDays = lastWeek.Select(b => Period.DayStartOf(b.OccurredAt).ToUnixTimeMilliseconds())
                                   .Distinct().Count();

            // 只报"留下记录的天数"，不报连续天数。连续天数会让人为了不断签而记假数据。
            DaysLabel.Text = nowMin == 0
                ? (_offset == 0 ? "这周还没有记录。记第一条就会出现在这里。" : "这一周没有记录。")
                : $"这周的 7 天里有 {days} 天留下了记录"
                  + (prevMin > 0 ? $"，上周是 {prevDays} 天。" : "。")
                  + $"共 {thisWeek.Count} 条。";

            _thisMin = nowMin;
            _lastMin = prevMin;
            _days = days;
            _blockCount = thisWeek.Count;
        }

        // ============================================================
        // 二、目标：动了没动
        // ============================================================

        private async Task RenderGoalsAsync(
            List<LocalBlock> all, List<LocalBlock> thisWeek, List<LocalBlock> lastWeek)
        {
            GoalPanel.Children.Clear();

            var goals = await _goals.GetByRangeAsync(0, long.MaxValue);
            var active = goals.Where(g => g.Status == 0).ToList();

            if (active.Count == 0)
            {
                GoalSummaryLabel.Text = " ";
                GoalEmptyLabel.IsVisible = true;
                GoalEmptyLabel.Text = goals.Count == 0
                    ? "还没有目标。目标不是必须的 —— 但有了它，这些时间才能归到一条线上。"
                    : "没有进行中的目标（都暂停或完成了）。";
                _activeGoals = 0;
                _movedGoals = 0;
                return;
            }

            GoalEmptyLabel.IsVisible = false;

            var rows = active.Select(g =>
            {
                int now = thisWeek.Where(b => b.GoalId == g.Id).Sum(b => Math.Max(0, b.DurationMin));
                int prev = lastWeek.Where(b => b.GoalId == g.Id).Sum(b => Math.Max(0, b.DurationMin));
                long? lastAt = all.Where(b => b.GoalId == g.Id)
                                  .Select(b => (long?)b.OccurredAt)
                                  .DefaultIfEmpty(null)
                                  .Max();
                return (Goal: g, Now: now, Prev: prev, LastAt: lastAt);
            })
            .OrderByDescending(x => x.Now)
            .ThenBy(x => x.Goal.Title)
            .ToList();

            // 本周动了的目标数：这是"这周有几条线在动"的唯一硬数字
            int movedCount = rows.Count(x => x.Now > 0);

            foreach (var (goal, now, prev, lastAt) in rows)
            {
                GoalPanel.Children.Add(BuildGoalRow(goal, now, prev, lastAt));
            }

            // 「几条线在动」只说事实，不说"完成 2/3"。分数一旦出现，人就会去凑它。
            GoalSummaryLabel.Text = movedCount == rows.Count
                ? $"{rows.Count} 条进行中的线，这周全部动过。"
                : $"{rows.Count} 条进行中的线，这周动了 {movedCount} 条。";

            _activeGoals = rows.Count;
            _movedGoals = movedCount;
        }

        private View BuildGoalRow(LocalGoal goal, int nowMin, int prevMin, long? lastAt)
        {
            bool moved = nowMin > 0;

            var row = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = GridLength.Star },
                    new ColumnDefinition { Width = GridLength.Auto },
                },
                ColumnSpacing = 8,
            };

            var left = new VerticalStackLayout { Spacing = 1 };
            left.Children.Add(TextPeek.Attach(new Label
            {
                Text = goal.Title,
                FontSize = 14,
                TextColor = moved ? Ink : Faint,
                LineBreakMode = LineBreakMode.TailTruncation,
            }));

            string sub;
            if (moved)
            {
                sub = prevMin > 0
                    ? $"上周 {BlockEntryPage.FormatMinutes(prevMin)}"
                    : "上周没动，这周动了";
            }
            else
            {
                sub = lastAt is null
                    ? "还没有任何记录"
                    : $"上次动是 {DaysAgo(lastAt.Value)} 天前";
            }

            left.Children.Add(new Label
            {
                Text = sub,
                FontSize = 11,
                TextColor = Muted,
            });

            row.Add(left, 0);

            var right = new Label
            {
                Text = moved ? BlockEntryPage.FormatMinutes(nowMin) : "没动",
                FontSize = 14,
                TextColor = moved ? Ink : Faint,
                VerticalOptions = LayoutOptions.Center,
                HorizontalTextAlignment = TextAlignment.End,
            };
            row.Add(right, 1);

            return row;
        }

        private static int DaysAgo(long unixMs)
        {
            var then = Period.DayStartOf(unixMs);
            var today = Period.StartOfDay(DateTimeOffset.UtcNow);
            return (int)Math.Max(0, (today - then).TotalDays);
        }

        // ============================================================
        // 三、重复的话
        // ============================================================
        //
        // 这是"AI 之前"最该由系统做的一件事：它不需要理解语义，只需要数。
        // 人看不见自己说了 12 遍同一句话，但计数看得见。

        private void RenderRepeats(List<LocalBlock> thisWeek, List<LocalBlock> lastWeek)
        {
            RepeatPanel.Children.Clear();
            _repeat = string.Empty;

            var entries = thisWeek.Concat(lastWeek)
                .Where(b => !string.IsNullOrWhiteSpace(b.Lesson))
                .Select(b => (Raw: b.Lesson!.Trim(), Key: Normalize(b.Lesson!), At: b.OccurredAt))
                .Where(x => x.Key.Length > 0)
                .ToList();

            if (entries.Count < 2)
            {
                RepeatEmptyLabel.IsVisible = true;
                RepeatHint.Text = entries.Count == 0
                    ? "评「值得」时可以写一句「下次怎么做更快」，写过的句子会在这里汇总。"
                    : "这两周只写过一句。再攒几句才有得比。";
                return;
            }

            // 先按归一化后的完整句子分组，再把"是别人前缀"的短句并进长句
            // （「先做25分钟」和「先做25分钟就好」是同一句，但字面不相等）。
            var groups = entries
                .GroupBy(x => x.Key)
                .Select(g => new RepeatGroup(g.Key, g.Select(x => x.Raw).OrderByDescending(r => r.Length).First(), g.Count()))
                .ToList<RepeatGroup?>();

            for (int i = 0; i < groups.Count; i++)
            {
                if (groups[i] is null) continue;
                for (int j = 0; j < groups.Count; j++)
                {
                    if (i == j || groups[i] is null || groups[j] is null) continue;
                    var a = groups[i]!;
                    var b = groups[j]!;

                    if (a.Key.Length < 6) continue;
                    if (b.Key.StartsWith(a.Key, StringComparison.Ordinal) && b.Key.Length - a.Key.Length <= 8)
                    {
                        groups[j] = new RepeatGroup(b.Key, b.Display, a.Count + b.Count);
                        groups[i] = null;
                        break;
                    }
                }
            }

            var repeats = groups.OfType<RepeatGroup>()
                .Where(g => g.Count >= 2)
                .OrderByDescending(g => g.Count)
                .Take(5)
                .ToList();

            if (repeats.Count == 0)
            {
                RepeatEmptyLabel.IsVisible = true;
                RepeatHint.Text = $"最近两周写了 {entries.Count} 句「下次怎么做」，没有重复的。";
                return;
            }

            RepeatEmptyLabel.IsVisible = false;
            RepeatHint.Text = $"最近两周写了 {entries.Count} 句「下次怎么做」，下面是说过不止一次的。";

            _repeat = $"{repeats[0].Display}（{repeats[0].Count} 次）";

            foreach (var r in repeats)
            {
                var card = new Border
                {
                    Stroke = Color.FromArgb("#E6C68A"),
                    StrokeThickness = 1,
                    Padding = new Thickness(12, 8),
                    Content = new VerticalStackLayout
                    {
                        Spacing = 2,
                        Children =
                        {
                            TextPeek.Attach(new Label
                            {
                                Text = r.Display,
                                FontSize = 13,
                                MaxLines = 2,
                                LineBreakMode = LineBreakMode.TailTruncation,
                            }),
                            new Label
                            {
                                Text = $"说过 {r.Count} 次",
                                FontSize = 11,
                                TextColor = Warn,
                            },
                        },
                    },
                };

                RepeatPanel.Children.Add(card);
            }
        }

        private sealed record RepeatGroup(string Key, string Display, int Count);

        /// <summary>
        /// 只用"空格 + 标点 + 大小写"做归一化，不做同义词/语义。
        ///
        /// 刻意不做得更聪明：一旦开始判断"这两句意思一样"，就会把不一样的句子合并，
        /// 而合并错的代价是让人以为"我没改过"，比不做更糟。
        /// </summary>
        private static string Normalize(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (char.IsWhiteSpace(c)) continue;
                if (char.IsPunctuation(c)) continue;
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        // ============================================================
        // 四、结构变化
        // ============================================================

        private void RenderShift(List<LocalBlock> thisWeek, List<LocalBlock> lastWeek)
        {
            ShiftPanel.Children.Clear();
            _topShift = string.Empty;

            if (lastWeek.Count < MinBlocksForTrend)
            {
                ShiftEmptyLabel.IsVisible = true;
                ShiftEmptyLabel.Text = "上周还没有记录，这一栏没有对比对象 —— 下周就有了。";
                return;
            }

            var now = thisWeek.GroupBy(b => b.PieceCode ?? string.Empty)
                .ToDictionary(g => g.Key, g => g.Sum(b => Math.Max(0, b.DurationMin)));

            var prev = lastWeek.GroupBy(b => b.PieceCode ?? string.Empty)
                .ToDictionary(g => g.Key, g => g.Sum(b => Math.Max(0, b.DurationMin)));

            var diffs = now.Keys.Union(prev.Keys)
                .Select(code => (Code: code, Now: now.TryGetValue(code, out var n) ? n : 0,
                                 Prev: prev.TryGetValue(code, out var p) ? p : 0))
                .Select(x => (x.Code, x.Now, x.Prev, Diff: x.Now - x.Prev))
                .Where(x => Math.Abs(x.Diff) >= 5)      // 5 分钟以内算噪声，不值得占一行
                .OrderByDescending(x => Math.Abs(x.Diff))
                .Take(4)
                .ToList();

            if (diffs.Count == 0)
            {
                ShiftEmptyLabel.IsVisible = true;
                ShiftEmptyLabel.Text = "这周和上周的时间分布几乎一样，没有哪件事明显多或少。";
                return;
            }

            ShiftEmptyLabel.IsVisible = false;

            var top = diffs[0];
            _topShift = $"{_settings.DisplayName(top.Code)} {(top.Diff > 0 ? "+" : "−")}"
                + BlockEntryPage.FormatMinutes(Math.Abs(top.Diff));

            foreach (var (code, nowMin, prevMin, diff) in diffs)
            {
                var row = new Grid
                {
                    ColumnDefinitions =
                    {
                        new ColumnDefinition { Width = GridLength.Star },
                        new ColumnDefinition { Width = GridLength.Auto },
                        new ColumnDefinition { Width = new GridLength(64) },
                    },
                    ColumnSpacing = 8,
                };

                row.Add(TextPeek.Attach(new Label
                {
                    Text = _settings.DisplayName(code),
                    FontSize = 13,
                    VerticalOptions = LayoutOptions.Center,
                    LineBreakMode = LineBreakMode.TailTruncation,
                }), 0);

                row.Add(new Label
                {
                    Text = $"{BlockEntryPage.FormatMinutes(nowMin)} / {BlockEntryPage.FormatMinutes(prevMin)}",
                    FontSize = 11,
                    TextColor = Muted,
                    VerticalOptions = LayoutOptions.Center,
                    HorizontalTextAlignment = TextAlignment.End,
                }, 1);

                row.Add(new Label
                {
                    Text = (diff > 0 ? "+" : "−") + BlockEntryPage.FormatMinutes(Math.Abs(diff)),
                    FontSize = 13,
                    TextColor = diff > 0 ? Up : Down,
                    VerticalOptions = LayoutOptions.Center,
                    HorizontalTextAlignment = TextAlignment.End,
                }, 2);

                ShiftPanel.Children.Add(row);
            }
        }

        // ============================================================
        // 五、AI 补一句（§27-C）
        // ============================================================
        //
        // 四个数已经在这一页上了。这一句的作用不是"总结"，是**指出差别**：
        // 「投入没变，但时间从写代码挪到了开会」这种事，人自己一眼看不出来。
        //
        // 三条约束都落在服务端的提示词里，这里只负责把事实交上去：
        //   只给数字（分钟/天数/条数/片名），不给原始记录 —— 没有账单、没有备注正文。
        //   数据太少时服务端会直接拒绝，不硬编。
        //   失败只在状态栏说一句，不弹窗。

        private async void OnAiCommentClicked(object? sender, EventArgs e)
        {
            if (!AiCommentBtn.IsEnabled) return;

            AiCommentBtn.IsEnabled = false;
            AiCommentBtn.Text = "想着…";

            try
            {
                var (from, _) = RangeOf(_offset);
                string weekKey = DateTimeOffset.FromUnixTimeMilliseconds(from)
                    .ToOffset(Period.Cst).ToString("yyyyMMdd");

                var facts = new WeekFacts
                {
                    WeekLabel = RangeLabel.Text,
                    ThisMinutes = _thisMin,
                    LastMinutes = _lastMin,
                    Days = _days,
                    Blocks = _blockCount,
                    ActiveGoals = _activeGoals,
                    MovedGoals = _movedGoals,
                    TopShift = _topShift,
                    Repeat = _repeat,
                };

                var r = await _ai.CommentWeekAsync(facts, weekKey);

                if (r.Ok)
                {
                    AiCommentLabel.Text = r.Text;
                    AiCommentCard.IsVisible = true;
                    AiCommentBtn.IsVisible = false;
                    StatusLabel.Text = string.Empty;
                }
                else
                {
                    // 不弹窗：这句话本身常常有信息量（"数据还不够，不猜" / "AI 没接上"）
                    StatusLabel.Text = r.Message;
                }
            }
            catch (Exception ex)
            {
                StatusLabel.Text = "出错了：" + ex.Message;
            }
            finally
            {
                AiCommentBtn.IsEnabled = true;
                AiCommentBtn.Text = "让 AI 补一句";
            }
        }
    }
}
