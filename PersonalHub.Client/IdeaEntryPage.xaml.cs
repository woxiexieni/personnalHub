using PersonalHub.Client.Data;
using PersonalHub.Client.Services;

namespace PersonalHub.Client
{
    /// <summary>
    /// 想法录入（R6）。
    ///
    /// 设计上只有一个要求：**从想到到存下，中间不能有任何决策**。
    /// 所以这一页没有必填项、没有标题栏、没有分类必选、没有保存前的确认。
    /// 类型五个 chip 是可选的，默认「未分类」—— 选它不会丢任何东西，
    /// 而强迫人分类的代价是"这条想法干脆不记了"。
    ///
    /// 为什么不在这里放"转成目标"：那是**加工**动作，发生在想法库（列表页）里，
    /// 需要先看一堆想法才知道哪条值得做。录入页只管一件事：别让想法跑掉。
    /// </summary>
    public partial class IdeaEntryPage : ContentPage
    {
        private static readonly Color ChipBg = Color.FromArgb("#F1EFE8");
        private static readonly Color ChipText = Color.FromArgb("#2C2C2A");
        private static readonly Color ChipActiveBg = Color.FromArgb("#185FA5");

        /// <summary>
        /// 四种类型 + 未分类。判据写在 Hint 里，因为光给四个词，没人知道自己该点哪个 ——
        /// 而选错类型比不选更糟（它会给后续的"评价类念头矫正"喂错数据）。
        /// 定义放在 IdeaKinds 里，与想法库共用同一份，避免两边文案漂移。
        /// </summary>
        private static readonly (int Kind, string Label, string Hint)[] Kinds = IdeaKinds.All;

        private readonly LocalRecordStore<LocalIdea> _ideas;
        private readonly RecordSyncService<LocalIdea> _ideaSync;

        private int _kind;

        public IdeaEntryPage(
            LocalRecordStore<LocalIdea> ideas,
            RecordSyncService<LocalIdea> ideaSync)
        {
            InitializeComponent();

            _ideas = ideas;
            _ideaSync = ideaSync;

            BuildKindChips();
            ApplyKindHint();
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();

            // 只读一次本地条数，纯 SQLite 查询，不会拖慢进入这一页
            await RefreshLibraryButtonAsync();
        }

        // ============================================================
        // 类型 chips
        // ============================================================

        private void BuildKindChips()
        {
            KindChips.Children.Clear();

            foreach (var k in Kinds)
            {
                var chip = new Button
                {
                    Text = k.Label,
                    FontSize = 12,
                    HeightRequest = 34,
                    Padding = new Thickness(14, 0),
                    CornerRadius = 17,
                    Margin = new Thickness(0, 0, 8, 8),
                    CommandParameter = k.Kind,
                };

                StyleKindChip(chip, k.Kind);
                chip.Clicked += OnKindClicked;
                KindChips.Children.Add(chip);
            }
        }

        private void StyleKindChip(Button chip, int kind)
        {
            bool active = kind == _kind;
            chip.BackgroundColor = active ? ChipActiveBg : ChipBg;
            chip.TextColor = active ? Colors.White : ChipText;
            chip.BorderWidth = 0;
        }

        private void OnKindClicked(object? sender, EventArgs e)
        {
            if (sender is not Button b || b.CommandParameter is not int kind) return;

            _kind = kind;

            // 全部重刷一遍而不是只改两个：chip 只有 5 个，重刷比维护"上一个是谁"更不容易错
            foreach (var child in KindChips.Children)
            {
                if (child is Button c && c.CommandParameter is int k) StyleKindChip(c, k);
            }

            ApplyKindHint();
        }

        private void ApplyKindHint()
        {
            KindHintLabel.Text = IdeaKinds.HintOf(_kind);
        }

        // ============================================================
        // 保存
        // ============================================================

        private async void OnSaveClicked(object? sender, EventArgs e)
        {
            string text = (IdeaEditor.Text ?? string.Empty).Trim();

            if (text.Length == 0)
            {
                StatusLabel.Text = "先写点什么。一句话就够，不用组织语言。";
                return;
            }

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var idea = new LocalIdea
            {
                Id = Guid.NewGuid().ToString("N"),
                Text = text,
                Kind = _kind,
                GoalId = string.Empty,
                CreatedAt = now,
                UpdatedAt = now,
                Deleted = false,
                Synced = false,
                SyncedAt = 0,
            };

            await _ideas.InsertAsync(idea);

            // 清空正文，**不清空类型**：连着记几条同类想法是常见情况，
            // 每记一条都要重新点类型，就等于每条都多一次点击。
            IdeaEditor.Text = string.Empty;

            await RefreshLibraryButtonAsync();
            StatusLabel.Text = _kind == 0
                ? "已记下（未分类）。想分类可以到想法库里改。"
                : $"已记下，标成了「{IdeaKinds.LabelOf(_kind)}」。";

            // 后台传，不阻塞下一次记录
            _ = _ideaSync.SyncAsync();
        }

        private async Task RefreshLibraryButtonAsync()
        {
            try
            {
                var all = await _ideas.GetByRangeAsync(0, long.MaxValue);
                LibraryBtn.Text = all.Count == 0 ? "想法库（还空着）▸" : $"想法库 {all.Count} 条 ▸";
            }
            catch
            {
                LibraryBtn.Text = "想法库 ▸";
            }
        }

        private async void OnLibraryClicked(object? sender, EventArgs e)
        {
            await Shell.Current.GoToAsync(RecordCatalog.IdeaListRoute);
        }
    }
}
