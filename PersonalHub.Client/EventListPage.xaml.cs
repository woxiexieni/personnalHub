using System.ComponentModel;
using PersonalHub.Client.Data;
using PersonalHub.Client.Services;

namespace PersonalHub.Client
{
    /// <summary>
    /// 事件卡库：全部历史。想法库的模子（R7.2）——
    /// 录入页底下的"今日事件卡"只能看当天，这里是唯一能翻全量的地方。
    ///
    /// 筛选三档：全部 / 待整理（AI 兜底卡，攒着能批量重跑） / 各标签（与问题卡共用词表）。
    /// 每张卡如果挂了问题卡，展开区显示归属——证据链从这头也走得通。
    /// </summary>
    public partial class EventListPage : ContentPage
    {
        private readonly LocalRecordStore<ThingRecord> _things;
        private readonly LocalRecordStore<LocalProblem> _problems;

        /// <summary>空 = 全部；"（待整理）" = 只看兜底卡；其他值 = 标签。</summary>
        private string _filter = string.Empty;

        public EventListPage(
            LocalRecordStore<ThingRecord> things,
            LocalRecordStore<LocalProblem> problems)
        {
            InitializeComponent();
            _things = things;
            _problems = problems;
            BuildFilterChips();
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();
            await RefreshAsync();
        }

        private void BuildFilterChips()
        {
            FilterChips.Children.Clear();
            var values = new List<string> { string.Empty, RecordTags.PendingTitle };
            values.AddRange(RecordTags.All);

            foreach (var value in values)
            {
                string label = value.Length == 0 ? "全部" : value;
                var btn = new Button
                {
                    Text = label,
                    FontSize = 12,
                    Padding = new Thickness(10, 2),
                    CornerRadius = 12,
                    MinimumHeightRequest = 30,
                    Margin = new Thickness(0, 0, 8, 8),
                };
                btn.Clicked += async (_, _) =>
                {
                    if (_filter == value) return;
                    _filter = value;
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
            var values = new List<string> { string.Empty, RecordTags.PendingTitle };
            values.AddRange(RecordTags.All);

            int i = 0;
            foreach (var child in FilterChips.Children)
            {
                if (child is not Button b) continue;
                bool on = i < values.Count && values[i] == _filter;
                b.BackgroundColor = on ? Color.FromArgb("#4A6FA5") : Color.FromArgb("#3A3A40");
                b.TextColor = on ? Colors.White : (dark ? Colors.White : Colors.Black);
                i++;
            }
        }

        private async Task RefreshAsync()
        {
            var all = await _things.GetAllAsync();
            var problemTitles = (await _problems.GetAllAsync())
                .Where(p => !p.Deleted)
                .ToDictionary(p => p.Id, p => p.Title);

            var rows = new List<EventRow>();
            foreach (var r in all
                         .Where(r => !r.Deleted)
                         .Where(r => _filter.Length == 0 ||
                                     (_filter == RecordTags.PendingTitle
                                         ? string.Equals(r.Title, RecordTags.PendingTitle, StringComparison.Ordinal)
                                         : string.Equals(r.Tag, _filter, StringComparison.Ordinal)))
                         .OrderByDescending(r => r.OccurredAt)
                         .Take(200))
            {
                var localTime = DateTimeOffset.FromUnixTimeMilliseconds(r.OccurredAt).LocalDateTime;
                bool pending = string.Equals(r.Title, RecordTags.PendingTitle, StringComparison.Ordinal);

                var detail = new List<string>();
                if (pending)
                {
                    detail.Add(r.ObjectiveDescription ?? string.Empty);
                }
                else
                {
                    void Add(string label, string? v)
                    {
                        if (!string.IsNullOrWhiteSpace(v)) detail.Add(label + "：" + v.Trim());
                    }
                    Add("事实", r.ObjectiveDescription);
                    Add("想法", r.InitialThought);
                    Add("行动", r.ActionTaken);
                    Add("结果", r.FinalResult);
                    Add("复盘", r.Reflection);
                }

                bool hasProblem = !string.IsNullOrWhiteSpace(r.ProblemId);
                rows.Add(new EventRow
                {
                    Id = r.Id,
                    Day = localTime.ToString("MM-dd"),
                    Title = string.IsNullOrWhiteSpace(r.Title) ? "（无标题）" : r.Title,
                    Meta = localTime.ToString("HH:mm") + (r.Synced ? " · 已同步" : " · 待同步"),
                    Tag = string.IsNullOrWhiteSpace(r.Tag) ? string.Empty : r.Tag,
                    Detail = string.Join("\n", detail),
                    HasProblem = hasProblem,
                    ProblemLine = hasProblem
                        ? "属于问题卡：" + (problemTitles.TryGetValue(r.ProblemId, out var pt) ? pt : "（已删除的卡）")
                        : string.Empty,
                    IsPending = pending,
                    RawText = pending ? (r.ObjectiveDescription ?? string.Empty) : string.Empty,
                });
            }

            EventList.ItemsSource = rows;
            SummaryLabel.Text = rows.Count == 0
                ? string.Empty
                : _filter.Length == 0
                    ? $"共 {rows.Count} 张（最近的在前）"
                    : $"筛选出 {rows.Count} 张";
        }

        private void OnRowTapped(object? sender, TappedEventArgs e)
        {
            if (e.Parameter is EventRow row)
            {
                row.IsExpanded = !row.IsExpanded;
            }
        }

        private async void OnRefillClicked(object? sender, EventArgs e)
        {
            if (sender is not Button btn || btn.BindingContext is not EventRow row) return;

            // 交接给录入页：填回原文 + 删掉这张待整理卡（在 OnAppearing 里消费）
            RecordDothingPage.PendingRefill = (row.Id, row.RawText);
            await Shell.Current.GoToAsync(RecordCatalog.ThingRoute);
        }
    }

    /// <summary>事件卡库的展示行（点行头展开明细）。</summary>
    public class EventRow : INotifyPropertyChanged
    {
        public string Id { get; set; } = string.Empty;
        public string Day { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Meta { get; set; } = string.Empty;
        public string Tag { get; set; } = string.Empty;
        public string Detail { get; set; } = string.Empty;
        public string ProblemLine { get; set; } = string.Empty;
        public bool HasProblem { get; set; }
        public bool IsPending { get; set; }
        public string RawText { get; set; } = string.Empty;

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
