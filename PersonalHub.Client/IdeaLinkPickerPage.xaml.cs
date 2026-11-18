using PersonalHub.Client.Data;
using PersonalHub.Client.Services;

namespace PersonalHub.Client
{
    /// <summary>
    /// 想法选择器（R9）：「关联到…」的第二个动作 —— 从库里挑一条想法。
    ///
    /// 交互：进来就是最近 200 条，打字即筛（复用想法库的搜索语法：空格 AND / -排除），
    /// 点一条就带着 id 回去。没有多选、没有确认按钮。
    ///
    /// 为什么不做成 Shell 路由：它只在 IdeaListPage 一处使用，直接 new + Navigation.PushAsync
    /// 最省事 —— 省掉 MauiProgram 注册和路由表两行，少两个忘记注册就崩的位置。
    /// </summary>
    public partial class IdeaLinkPickerPage : ContentPage
    {
        private readonly LocalRecordStore<LocalIdea> _ideas;

        /// <summary>发起关联的那条。它自己不出现在候选里 —— 一条想法不跟自己建关系。</summary>
        private readonly string _excludeId;

        /// <summary>这次选的是什么关系（0 衍生 / 1 矛盾），只用来把标题写清楚。</summary>
        private readonly int _kind;

        private TaskCompletionSource<string?>? _tcs;

        /// <summary>渲染出来的行，tap 时从 CommandParameter 拿 id。</summary>
        private readonly List<(string Id, View Row)> _rows = new();

        public IdeaLinkPickerPage(LocalRecordStore<LocalIdea> ideas, string excludeId, int kind)
        {
            InitializeComponent();

            _ideas = ideas;
            _excludeId = excludeId;
            _kind = kind;

            Title = kind == 1 ? "选一条 · 矛盾" : "选一条 · 衍生";
        }

        /// <summary>
        /// 推进选择器并等结果。返回选中的想法 id；null = 用户取消（含左滑返回）。
        /// </summary>
        public async Task<string?> PickAsync(INavigation nav)
        {
            _tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

            await LoadAsync(string.Empty);
            await nav.PushAsync(this);

            return await _tcs.Task;
        }

        protected override void OnDisappearing()
        {
            base.OnDisappearing();

            // 左滑返回 / 点"算了"离开时还没选：把等待方放出来，别让它永远挂着。
            _tcs?.TrySetResult(null);
        }

        private async void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
        {
            await LoadAsync(e.NewTextValue ?? string.Empty);
        }

        private async void OnSearchCompleted(object? sender, EventArgs e)
        {
            // 回车在移动端会收起键盘，列表已经在 TextChanged 里实时刷过了，这里无事可做。
            await Task.CompletedTask;
        }

        private async Task LoadAsync(string query)
        {
            var all = await _ideas.GetByRangeAsync(0, long.MaxValue);

            // 活的想法 + 排掉发起方。作废的不进候选 —— 跟"废案不产生新动作"同一个口径。
            var pool = all
                .Where(x => !x.Deleted && !x.IsVoided && x.Id != _excludeId)
                .OrderByDescending(x => x.CreatedAt)
                .ToList();

            // 搜索语法照想法库：空格 AND、- 排除。这里不复用 IdeaListPage 的解析
            // （那是它的私有静态），就地实现一个同样三条规则的精简版。
            var (terms, excludes) = ParseSearch(query);

            if (terms.Count > 0 || excludes.Count > 0)
            {
                pool = pool.Where(x => Matches(x.Text, terms, excludes)).ToList();
            }

            // 一次最多 200 行：这是选一条的弹窗，不是浏览库 —— 列表越长选择越累。
            var show = pool.Take(200).ToList();

            Rows.Children.Clear();
            _rows.Clear();

            foreach (var idea in show)
            {
                var row = BuildRow(idea);
                _rows.Add((idea.Id, row));
                Rows.Children.Add(row);
            }

            HintLabel.Text = show.Count == 0
                ? (terms.Count > 0 ? "没搜到。试试少一个词。" : "库里还没有别的想法 —— 先回想法库记一条。")
                : $"点一条就选它。{pool.Count} 条里显示最近 {show.Count} 条，搜词能翻全库。";
        }

        private View BuildRow(LocalIdea idea)
        {
            var text = new Label
            {
                Text = idea.Text ?? string.Empty,
                FontSize = 14,
                MaxLines = 3,
                LineBreakMode = LineBreakMode.TailTruncation,
            };

            var meta = new Label
            {
                Text = $"{IdeaKinds.LabelOf(idea.Kind)} · {FormatDay(idea.CreatedAt)}",
                FontSize = 11,
                TextColor = IdeaKinds.ColorOf(idea.Kind),
            };

            var stack = new VerticalStackLayout { Spacing = 2 };
            stack.Children.Add(text);
            stack.Children.Add(meta);

            // 高亮 Span 必须显式给 TextColor（深色主题陷阱）；这里不做高亮，
            // 原样给 Border 挂 tap —— sender 是 Border 本身，参数从 TappedEventArgs.Parameter 取。
            var border = new Border
            {
                Stroke = Color.FromArgb("#E3E0D8"),
                StrokeThickness = 1,
                Padding = new Thickness(12, 8),
                Content = stack,
            };

            var tap = new TapGestureRecognizer { CommandParameter = idea.Id };
            tap.Tapped += OnRowTapped;
            border.GestureRecognizers.Add(tap);

            return border;
        }

        private async void OnRowTapped(object? sender, EventArgs e)
        {
            // sender 是被点的 Border，不是识别器 —— id 从 TappedEventArgs.Parameter 取。
            if (e is not TappedEventArgs t || t.Parameter is not string id) return;

            _tcs?.TrySetResult(id);
            await Navigation.PopAsync();
        }

        private async void OnCancelClicked(object? sender, EventArgs e)
        {
            _tcs?.TrySetResult(null);
            await Navigation.PopAsync();
        }

        // ============================================================
        // 搜索（与想法库同一套三条规则的精简版）
        // ============================================================

        private static bool Matches(string? text, List<string> terms, List<string> excludes)
        {
            string s = text ?? string.Empty;

            foreach (var t in excludes)
                if (s.Contains(t, StringComparison.OrdinalIgnoreCase)) return false;

            foreach (var t in terms)
                if (!s.Contains(t, StringComparison.OrdinalIgnoreCase)) return false;

            return true;
        }

        private static (List<string> Terms, List<string> Excludes) ParseSearch(string raw)
        {
            var terms = new List<string>();
            var excludes = new List<string>();

            if (string.IsNullOrWhiteSpace(raw)) return (terms, excludes);

            foreach (string token in raw.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.Length > 1 && token[0] == '-') excludes.Add(token[1..].Trim());
                else if (token.Length > 0 && token != "-") terms.Add(token);
            }

            return (terms, excludes);
        }

        private static string FormatDay(long ms)
        {
            if (ms <= 0) return "时间未知";
            return DateTimeOffset.FromUnixTimeMilliseconds(ms).ToOffset(Period.Cst).ToString("M月d日");
        }
    }
}
