using System.ComponentModel;
using PersonalHub.Client.Data;
using PersonalHub.Client.Services;

namespace PersonalHub.Client
{
    /// <summary>
    /// 问题卡库：进行中的在前（欠的债），关掉的归档（战绩）。
    ///
    /// 评估三选一（有效继续 / 换招 / 解决了）就是控制环的"评估 → 反馈"出口：
    ///   换招   → 环再转一圈，只审判这一招，不审判人；
    ///   解决了 → 按判据关卡，记录用时和换招次数 —— 那是模型真正的回报。
    ///
    /// 证据计数说明：本地 ThingsRecords 只保留最近 30 天 + 未同步（离线优先的清理策略），
    /// 所以"证据 ×N"是近期可见证据，不是全历史 —— 全历史在服务端，v2 再统计。
    /// </summary>
    public partial class ProblemListPage : ContentPage
    {
        private readonly LocalRecordStore<LocalProblem> _problems;
        private readonly LocalRecordStore<ThingRecord> _things;
        private readonly LocalRecordStore<LocalPattern> _patterns;
        private readonly AiAssistService _ai;
        private bool _reviewBusy;

        /// <summary>当前分类筛选（空 = 全部）。只影响显示，不改数据。</summary>
        private string _filterTag = string.Empty;

        private bool _activeExpanded = true;    // 欠的债要天天看见
        private bool _closedExpanded;           // 战绩偶尔翻，默认收起

        public ProblemListPage(
            LocalRecordStore<LocalProblem> problems,
            LocalRecordStore<ThingRecord> things,
            LocalRecordStore<LocalPattern> patterns,
            AiAssistService ai)
        {
            InitializeComponent();
            _problems = problems;
            _things = things;
            _patterns = patterns;
            _ai = ai;
            BuildFilterChips();
        }

        /// <summary>筛选胶囊：全部 + 共用词表。没打标签的卡只出现在「全部」里。</summary>
        private void BuildFilterChips()
        {
            FilterChips.Children.Clear();
            var tags = new List<string> { "全部" };
            tags.AddRange(RecordTags.All);
            foreach (var tag in tags)
            {
                var btn = new Button
                {
                    Text = tag,
                    FontSize = 12,
                    Padding = new Thickness(10, 2),
                    CornerRadius = 12,
                    MinimumHeightRequest = 30,
                    Margin = new Thickness(0, 0, 8, 8),
                };
                btn.Clicked += async (_, _) =>
                {
                    string value = tag == "全部" ? string.Empty : tag;
                    if (_filterTag == value) return;
                    _filterTag = value;
                    PaintFilterChips();
                    await RefreshAsync();
                };
                FilterChips.Children.Add(btn);
            }
            PaintFilterChips();
        }

        private void PaintFilterChips()
        {
            bool dark = Application.Current?.RequestedTheme == AppTheme.Dark;
            int i = 0;
            foreach (var child in FilterChips.Children)
            {
                if (child is not Button b) continue;
                bool on = i == 0
                    ? _filterTag.Length == 0
                    : i - 1 < RecordTags.All.Length && RecordTags.All[i - 1] == _filterTag;
                b.BackgroundColor = on ? Color.FromArgb("#4A6FA5") : Color.FromArgb("#3A3A40");
                b.TextColor = on ? Colors.White : (dark ? Colors.White : Colors.Black);
                i++;
            }
        }

        private void OnActiveHeaderTapped(object? sender, TappedEventArgs e)
        {
            _activeExpanded = !_activeExpanded;
            ActiveList.IsVisible = _activeExpanded;
            ActiveArrow.Text = _activeExpanded ? "▾" : "▸";
        }

        private void OnClosedHeaderTapped(object? sender, TappedEventArgs e)
        {
            _closedExpanded = !_closedExpanded;
            ClosedList.IsVisible = _closedExpanded;
            ClosedArrow.Text = _closedExpanded ? "▾" : "▸";
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();
            await RefreshAsync();
        }

        private async Task RefreshAsync()
        {
            var all = await _problems.GetAllAsync();
            var things = await _things.GetAllAsync();

            // 证据索引：ProblemId → 事件卡（时间倒序）
            var evidence = things
                .Where(t => !t.Deleted && !string.IsNullOrEmpty(t.ProblemId))
                .GroupBy(t => t.ProblemId)
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderByDescending(t => t.OccurredAt).ToList());

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var activeRows = new List<ProblemRow>();
            var closedRows = new List<ProblemRow>();

            foreach (var p in all
                         .Where(p => !p.Deleted)
                         .Where(p => _filterTag.Length == 0 ||
                                     string.Equals(p.Tag, _filterTag, StringComparison.Ordinal))
                         .OrderByDescending(p => p.UpdatedAt))
            {
                evidence.TryGetValue(p.Id, out var evList);
                evList ??= new List<ThingRecord>();

                var row = new ProblemRow
                {
                    Id = p.Id,
                    Title = p.Title,
                    Status = p.Status,
                };

                string tagPrefix = string.IsNullOrWhiteSpace(p.Tag) ? string.Empty : $"[{p.Tag}] ";

                if (p.Status == 0)
                {
                    bool observing = string.IsNullOrWhiteSpace(p.Attempt);
                    int days = Math.Max(1, (int)((now - p.AttemptStartedAt) / 86400000L));
                    row.Meta = tagPrefix + (observing
                        ? $"卡龄 {DaysText(now - p.CreatedAt)} · 观察中（还没定试法）"
                        : p.AttemptCount > 0
                            ? $"卡龄 {DaysText(now - p.CreatedAt)} · 换过 {p.AttemptCount} 次招 · 当前这招跑了 {days} 天"
                            : $"卡龄 {DaysText(now - p.CreatedAt)}");
                    row.EvidenceBadge = evList.Count > 0 ? $"证据 ×{evList.Count}" : "证据 0";
                    row.CriteriaText = "判据：" + p.Criteria;
                    row.AttemptText = observing
                        ? "试法留空（观察中）：先攒证据——招常常是证据里长出来的，不是想出来的。AI 回看也会帮你建议。"
                        : $"当前试法：{p.Attempt}";
                    row.EvidenceText = evList.Count == 0
                        ? "还没有证据。翻车时打事件卡，AI 命中会自动挂上来。"
                        : "最近证据：\n" + string.Join("\n", evList.Take(5).Select(t =>
                            "  " + DateTimeOffset.FromUnixTimeMilliseconds(t.OccurredAt)
                                .LocalDateTime.ToString("MM-dd") + "  " + t.Title));
                    activeRows.Add(row);
                }
                else
                {
                    string closedAt = DateTimeOffset.FromUnixTimeMilliseconds(
                        p.ClosedAt == 0 ? p.UpdatedAt : p.ClosedAt).LocalDateTime.ToString("MM-dd");
                    row.Meta = tagPrefix + closedAt + (p.AttemptCount > 0 ? $" · 换招 {p.AttemptCount} 次" : string.Empty) +
                               (string.IsNullOrWhiteSpace(p.ClosingNote) ? string.Empty : " · " + p.ClosingNote);
                    row.StatusBadge = p.Status == 1 ? "✓" : "✕";
                    closedRows.Add(row);
                }
            }

            ActiveList.ItemsSource = activeRows;
            ClosedList.ItemsSource = closedRows;
            ActiveHeader.Text = activeRows.Count > 0 ? $"进行中（{activeRows.Count}）" : "进行中";
        }

        private static string DaysText(long ms)
        {
            int days = Math.Max(1, (int)(ms / 86400000L));
            return days >= 30 ? $"{days / 30} 个月" : $"{days} 天";
        }

        // ============ 行交互 ============
        private void OnRowTapped(object? sender, TappedEventArgs e)
        {
            if (e.Parameter is ProblemRow row)
            {
                row.IsExpanded = !row.IsExpanded;
            }
        }

        private async void OnAddClicked(object sender, EventArgs e)
            => await Shell.Current.GoToAsync(RecordCatalog.ProblemEditRoute);

        // ============ AI 周回看（防盲区） ============
        private async void OnReviewClicked(object? sender, EventArgs e)
        {
            if (_reviewBusy) return;
            _reviewBusy = true;
            ReviewBtn.IsEnabled = false;
            ReviewBtn.Text = "回看中……";
            ReviewPanel.IsVisible = false;
            ReviewRows.Children.Clear();

            try
            {
                long since = DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeMilliseconds();
                var events = (await _things.GetAllAsync())
                    .Where(t => !t.Deleted
                                && t.OccurredAt >= since
                                && !string.IsNullOrWhiteSpace(t.ObjectiveDescription)
                                && !string.Equals(t.Title, "（待整理）", StringComparison.Ordinal))
                    .OrderByDescending(t => t.OccurredAt)
                    .Take(50)
                    .Select(t => (
                        Day: DateTimeOffset.FromUnixTimeMilliseconds(t.OccurredAt).LocalDateTime.ToString("MM-dd"),
                        Title: t.Title,
                        Facts: t.ObjectiveDescription))
                    .ToList();

                var problems = (await _problems.GetAllAsync())
                    .Where(p => !p.Deleted && p.Status == 0)
                    .Select(p => (p.Id, p.Title))
                    .ToList();
                var patterns = (await _patterns.GetAllAsync())
                    .Where(p => !p.Deleted && p.Status == 0 && !string.IsNullOrWhiteSpace(p.Title))
                    .Select(p => (p.Id, p.Title))
                    .ToList();

                var (ok, message, findings) = await _ai.ReviewEventsAsync(events, problems, patterns);

                if (!ok)
                {
                    StatusLabel.Text = "回看没出结果：" + message;
                    return;
                }

                foreach (var f in findings)
                {
                    ReviewRows.Children.Add(BuildFindingRow(f));
                }
                ReviewPanel.IsVisible = true;
            }
            catch (Exception ex)
            {
                StatusLabel.Text = "回看失败：" + ex.Message;
            }
            finally
            {
                _reviewBusy = false;
                ReviewBtn.IsEnabled = true;
                ReviewBtn.Text = "AI 回看 30 天";
            }
        }

        /// <summary>一条回看发现：现象 + 依据 +（建议立卡时）一键转建卡页。</summary>
        private VerticalStackLayout BuildFindingRow(AiReviewFinding f)
        {
            var rows = new VerticalStackLayout { Spacing = 4 };

            rows.Children.Add(new Label
            {
                Text = (f.SuggestProblem ? "⚠ " : "· ") + f.Title,
                FontSize = 14,
                FontAttributes = FontAttributes.Bold,
                LineBreakMode = LineBreakMode.WordWrap,
            });
            rows.Children.Add(new Label
            {
                Text = f.Reason,
                FontSize = 12,
                TextColor = Color.FromArgb("#9A9A9A"),
                LineBreakMode = LineBreakMode.WordWrap,
            });

            if (f.SuggestProblem)
            {
                if (!string.IsNullOrWhiteSpace(f.SuggestedAttempt))
                {
                    rows.Children.Add(new Label
                    {
                        Text = "建议试法：" + f.SuggestedAttempt,
                        FontSize = 12,
                        TextColor = Color.FromArgb("#B45309"),
                        LineBreakMode = LineBreakMode.WordWrap,
                    });
                }

                var createBtn = new Button
                {
                    Text = "为它建一张问题卡",
                    FontSize = 12,
                    Padding = new Thickness(10, 4),
                    HorizontalOptions = LayoutOptions.Start,
                };
                createBtn.Clicked += async (_, _) =>
                {
                    // 事实栏带的是"依据"（AI 点了哪几天的事）——建卡页看得到 AI 为什么提议
                    ProblemEditPage.PendingDraft = (f.Title, f.Reason, f.SuggestedAttempt, f.SuggestedCriteria);
                    ReviewPanel.IsVisible = false;
                    await Shell.Current.GoToAsync(RecordCatalog.ProblemEditRoute);
                };
                rows.Children.Add(createBtn);
            }
            else
            {
                rows.Children.Add(new Label
                {
                    Text = "先攒样本，不急着立卡——样本多了会进模式页。",
                    FontSize = 11,
                    TextColor = Color.FromArgb("#9A9A9A"),
                });
            }

            return rows;
        }

        private void OnReviewDismissClicked(object? sender, EventArgs e)
        {
            ReviewPanel.IsVisible = false;
            ReviewRows.Children.Clear();
        }

        private async void OnEditClicked(object? sender, EventArgs e)
        {
            if ((sender as BindableObject)?.BindingContext is ProblemRow row)
            {
                await Shell.Current.GoToAsync($"{RecordCatalog.ProblemEditRoute}?id={row.Id}");
            }
        }

        // ============ 评估三选一 ============
        private void OnKeepClicked(object? sender, EventArgs e)
        {
            if ((sender as BindableObject)?.BindingContext is not ProblemRow row) return;
            StatusLabel.Text = $"「{row.Title}」继续跑。下次评估时看看证据有没有变少。";
            row.IsExpanded = false;
        }

        private async void OnNewAttemptClicked(object? sender, EventArgs e)
        {
            if ((sender as BindableObject)?.BindingContext is not ProblemRow row) return;

            string? next = await DisplayPromptAsync(
                "换一招",
                "这招没用不等于你没用——只是这一圈失败了。下一招打算怎么试？",
                placeholder: "例：不追求不犯错，翻车 24 小时内补一句修正");
            if (string.IsNullOrWhiteSpace(next)) return;

            var p = (await _problems.GetAllAsync()).FirstOrDefault(x => x.Id == row.Id);
            if (p is null) return;

            p.Attempt = next.Trim();
            p.AttemptStartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            p.AttemptCount++;
            p.UpdatedAt = p.AttemptStartedAt;
            await _problems.UpdateAsync(p);
            await RefreshAsync();
            StatusLabel.Text = "换招完成。环再转一圈。";
        }

        private async void OnSolvedClicked(object? sender, EventArgs e)
        {
            if ((sender as BindableObject)?.BindingContext is not ProblemRow row) return;

            string? note = await DisplayPromptAsync(
                "关卡",
                $"对照判据确认解决了？写一句收尾（可空）：\n{row.CriteriaText}",
                placeholder: "例：最近三次都没后悔记录了");
            if (note is null) return;   // 取消 = 不关

            await CloseAsync(row.Id, status: 1, note);
            StatusLabel.Text = "关卡完成。这是你改掉一个毛病的书面证据。";
        }

        private async void OnGiveUpClicked(object? sender, EventArgs e)
        {
            if ((sender as BindableObject)?.BindingContext is not ProblemRow row) return;

            bool ok = await DisplayAlert(
                "放弃这张",
                $"「{row.Title}」标记为放弃？它会进归档区，不算战绩但也不算失败——认了它不是真问题也是收货。",
                "放弃", "再跑跑");
            if (!ok) return;

            await CloseAsync(row.Id, status: 2, note: string.Empty);
        }

        private async Task CloseAsync(string id, int status, string note)
        {
            var p = (await _problems.GetAllAsync()).FirstOrDefault(x => x.Id == id);
            if (p is null) return;

            p.Status = status;
            p.ClosedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            p.UpdatedAt = p.ClosedAt;
            if (!string.IsNullOrWhiteSpace(note)) p.ClosingNote = note.Trim();
            await _problems.UpdateAsync(p);
            await RefreshAsync();
        }
    }

    /// <summary>问题卡列表的展示行（点行头展开）。</summary>
    public class ProblemRow : INotifyPropertyChanged
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Meta { get; set; } = string.Empty;
        public string EvidenceBadge { get; set; } = string.Empty;
        public string StatusBadge { get; set; } = string.Empty;
        public string CriteriaText { get; set; } = string.Empty;
        public string AttemptText { get; set; } = string.Empty;
        public string EvidenceText { get; set; } = string.Empty;

        /// <summary>归档行的状态标（进行中行不用）。</summary>
        public int Status { get; set; }

        private bool _isExpanded;
        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded == value) return;
                _isExpanded = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Arrow));
            }
        }

        public string Arrow => _isExpanded ? "▾" : "▸";

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
