using PersonalHub.Client.Data;
using PersonalHub.Client.Services;

namespace PersonalHub.Client
{
    /// <summary>
    /// 行为模式列表：在练的几个 + 已暂停/已自动化的折叠区。
    ///
    /// 刻意不显示任何成功率、连续天数。这一页只回答两个问题：
    ///   "我现在在练什么" 和 "最近有没有在做"。
    /// </summary>
    public partial class PatternListPage : ContentPage
    {
        private const int MaxActive = 3;

        private readonly LocalRecordStore<LocalPattern> _patterns;
        private readonly LocalRecordStore<LocalPractice> _practices;
        private readonly RecordSyncService<LocalPattern> _patternSync;
        private readonly RecordSyncService<LocalPractice> _practiceSync;

        private bool _othersOpen;

        public PatternListPage(
            LocalRecordStore<LocalPattern> patterns,
            LocalRecordStore<LocalPractice> practices,
            RecordSyncService<LocalPattern> patternSync,
            RecordSyncService<LocalPractice> practiceSync)
        {
            InitializeComponent();

            _patterns = patterns;
            _practices = practices;
            _patternSync = patternSync;
            _practiceSync = practiceSync;
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();
            await LoadAsync();
        }

        private async Task LoadAsync()
        {
            var all = await _patterns.GetByRangeAsync(0, long.MaxValue);
            var (from, to) = Period.LastDays(30);
            var practices = await _practices.GetByRangeAsync(from, to);

            var active = all.Where(p => p.Status == 0).OrderByDescending(p => p.UpdatedAt).ToList();
            var others = all.Where(p => p.Status != 0).OrderByDescending(p => p.UpdatedAt).ToList();

            ActivePanel.Children.Clear();
            foreach (var p in active)
            {
                ActivePanel.Children.Add(BuildCard(p, practices));
            }

            ActiveHeader.IsVisible = active.Count > 0;
            EmptyLabel.IsVisible = active.Count == 0;
            EmptyLabel.Text = "还没有在练的模式。上面填两行就能建一个。";

            OthersPanel.Children.Clear();
            foreach (var p in others)
            {
                OthersPanel.Children.Add(BuildCard(p, practices));
            }

            OthersHeader.IsVisible = others.Count > 0;
            OthersHeader.Text = $"已暂停 / 已自动化 {others.Count} 个 {(_othersOpen ? "▴" : "▾")}";
            OthersPanel.IsVisible = _othersOpen && others.Count > 0;
        }

        private View BuildCard(LocalPattern p, List<LocalPractice> practices)
        {
            int good = practices.Count(x => x.PatternId == p.Id && x.Result == 1);
            int back = practices.Count(x => x.PatternId == p.Id && x.Result == 0);

            var stack = new VerticalStackLayout { Spacing = 2 };
            stack.Children.Add(new Label
            {
                Text = p.Title,
                FontSize = 15,
                LineBreakMode = LineBreakMode.TailTruncation,
            });
            stack.Children.Add(new Label
            {
                Text = string.IsNullOrEmpty(p.NewResponse) ? "还没写新反应" : "练：" + p.NewResponse,
                FontSize = 12,
                TextColor = Color.FromArgb("#5F5E5A"),
                LineBreakMode = LineBreakMode.TailTruncation,
            });

            // 只给次数，不给比率 —— 有比率就会开始不记失败那次
            stack.Children.Add(new Label
            {
                Text = $"最近 30 天：守住 {good} 次 · 又回去 {back} 次",
                FontSize = 11,
                TextColor = Color.FromArgb("#8A8A8E"),
            });

            if (p.Status == 2)
            {
                stack.Children.Add(new Label
                {
                    Text = "已自动化",
                    FontSize = 11,
                    TextColor = Color.FromArgb("#3B6D11"),
                });
            }

            var border = new Border
            {
                Stroke = Color.FromArgb("#E3E0D8"),
                StrokeThickness = 1,
                Padding = new Thickness(14, 10),
                Content = stack,
            };

            var tap = new TapGestureRecognizer();
            tap.Tapped += async (_, _) =>
                await Shell.Current.GoToAsync($"{RecordCatalog.PatternDetailRoute}?id={p.Id}");
            border.GestureRecognizers.Add(tap);

            return border;
        }

        private async void OnCreateClicked(object? sender, EventArgs e)
        {
            string title = (TitleEntry.Text ?? string.Empty).Trim();
            string newResponse = (NewResponseEntry.Text ?? string.Empty).Trim();

            if (title.Length == 0)
            {
                StatusLabel.Text = "先给它起个名字（一个具体的行为，不是「我要更自律」）";
                return;
            }
            if (newResponse.Length == 0)
            {
                StatusLabel.Text = "新反应必填：写下你要练的那一个动作";
                return;
            }

            var all = await _patterns.GetByRangeAsync(0, long.MaxValue);
            if (all.Count(p => p.Status == 0) >= MaxActive)
            {
                StatusLabel.Text = $"已有 {MaxActive} 个模式在练，先暂停一个";
                return;
            }

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var pattern = new LocalPattern
            {
                Id = Guid.NewGuid().ToString("N"),
                Title = title,
                NewResponse = newResponse,
                Status = 0,
                CreatedAt = now,
                UpdatedAt = now,
                Deleted = false,
                Synced = false,
                SyncedAt = 0,
            };

            await _patterns.InsertAsync(pattern);
            TitleEntry.Text = string.Empty;
            NewResponseEntry.Text = string.Empty;
            StatusLabel.Text = $"已建「{title}」，其余三段在详情里补";

            await LoadAsync();
            _ = _patternSync.SyncAsync();
        }

        private void OnToggleOthersClicked(object? sender, EventArgs e)
        {
            _othersOpen = !_othersOpen;
            OthersPanel.IsVisible = _othersOpen && OthersPanel.Children.Count > 0;
            OthersHeader.Text = OthersHeader.Text.Replace("▾", _othersOpen ? "▴" : "▾");
        }
    }
}
