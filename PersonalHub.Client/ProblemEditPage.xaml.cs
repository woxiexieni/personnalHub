using PersonalHub.Client.Data;
using PersonalHub.Client.Services;

namespace PersonalHub.Client
{
    /// <summary>
    /// 建卡 / 编辑问题卡。
    ///
    /// 三个入口共用这一页：
    ///   1. 问题卡列表「建一张」（手动）；
    ///   2. 事件卡 AI 建议「为它建一张」—— 标题和事件事实已带好，判据仍要你写；
    ///   3. 列表行「编辑」（?id= 进来）。
    ///
    /// 闸 1 的落点：判据空着，保存不放行。AI 能替你跑腿，不能替你想"怎么算解决"。
    /// </summary>
    [QueryProperty(nameof(ProblemId), "id")]
    public partial class ProblemEditPage : ContentPage
    {
        private readonly LocalRecordStore<LocalProblem> _store;

        /// <summary>
        /// AI 建议立卡时带的草稿（RecordDothingPage / 问题卡列表的周回看 导航前塞进来）。
        /// 静态属性传参是偷懒但可控的做法：Shell 路由带不了对象，
        /// 查询串塞长文本要转义、还得防注入，不如一次性的静态交接。
        /// 判据草案只进 placeholder（占位提示），不进正文——闸 1 不许 AI 替你过。
        /// </summary>
        public static (string Title, string Facts, string Attempt, string CriteriaHint)? PendingDraft { get; set; }

        /// <summary>?id= 进来时 Shell 塞的值（编辑模式）。</summary>
        public string ProblemId { get; set; } = string.Empty;

        private LocalProblem? _existing;
        private bool _loaded;

        /// <summary>当前选中的分类（空 = 不分类）。词表与事件卡共用。</summary>
        private string _tag = string.Empty;

        public ProblemEditPage(LocalRecordStore<LocalProblem> store)
        {
            InitializeComponent();
            _store = store;
            BuildTagChips();
        }

        /// <summary>分类胶囊：单选，再点一下取消。跟草稿卡同款交互。</summary>
        private void BuildTagChips()
        {
            TagChips.Children.Clear();
            foreach (var tag in RecordTags.All)
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
                btn.Clicked += (_, _) =>
                {
                    _tag = _tag == tag ? string.Empty : tag;
                    PaintTagChips();
                };
                TagChips.Children.Add(btn);
            }
            PaintTagChips();
        }

        private void PaintTagChips()
        {
            bool dark = Application.Current?.RequestedTheme == AppTheme.Dark;
            int i = 0;
            foreach (var child in TagChips.Children)
            {
                if (child is not Button b) continue;
                bool on = i < RecordTags.All.Length && RecordTags.All[i] == _tag;
                b.BackgroundColor = on ? Color.FromArgb("#4A6FA5") : Color.FromArgb("#3A3A40");
                b.TextColor = on ? Colors.White : (dark ? Colors.White : Colors.Black);
                i++;
            }
        }

        protected override async void OnNavigatedTo(NavigatedToEventArgs args)
        {
            base.OnNavigatedTo(args);
            if (_loaded) return;
            _loaded = true;

            if (!string.IsNullOrEmpty(ProblemId))
            {
                // 编辑模式：把现有卡填进表单（await 后要碰 UI，不能 ConfigureAwait(false)）
                _existing = (await _store.GetAllAsync())
                    .FirstOrDefault(p => p.Id == ProblemId);
                if (_existing is { } p)
                {
                    Title = "编辑问题卡";
                    TitleEntry.Text = p.Title;
                    CriteriaEditor.Text = p.Criteria;
                    AttemptEditor.Text = p.Attempt;
                    _tag = p.Tag ?? string.Empty;
                    PaintTagChips();
                    SaveBtn.Text = "保存修改";
                }
                return;
            }

            if (PendingDraft is { } draft)
            {
                // AI 建议立卡：标题/事实/试法草案带好，判据只给占位提示（闸 1 仍要你亲手写）
                TitleEntry.Text = draft.Title;
                DraftContext.IsVisible = true;
                DraftFactsLabel.Text = draft.Facts;
                if (!string.IsNullOrWhiteSpace(draft.Attempt)) AttemptEditor.Text = draft.Attempt;
                if (!string.IsNullOrWhiteSpace(draft.CriteriaHint))
                {
                    CriteriaEditor.Placeholder = "AI 草案（可参考，但判据要你自己写）：\n" + draft.CriteriaHint;
                }
                PendingDraft = null;
            }
        }

        private async void OnSaveClicked(object sender, EventArgs e)
        {
            string title = TitleEntry.Text?.Trim() ?? string.Empty;
            string criteria = CriteriaEditor.Text?.Trim() ?? string.Empty;
            string attempt = AttemptEditor.Text?.Trim() ?? string.Empty;

            // ---- 闸 1：判据必填 ----
            if (criteria.Length == 0)
            {
                StatusLabel.Text = "判据没写：写不出『怎么算解决』的，不是问题是情绪——先想想它能不能验证。";
                return;
            }
            if (title.Length == 0)
            {
                StatusLabel.Text = "给这张卡起个名——一句话说清什么事老出问题。";
                return;
            }

            SaveBtn.IsEnabled = false;
            try
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                if (_existing is { } p)
                {
                    p.Title = title;
                    p.Criteria = criteria;
                    p.CriteriaConfirmed = true;   // 过了你的手，闸 1 算过
                    p.Attempt = attempt;
                    p.Tag = _tag;
                    p.UpdatedAt = now;
                    await _store.UpdateAsync(p);   // 不能 ConfigureAwait(false)：后面要回 UI
                }
                else
                {
                    await _store.InsertAsync(new LocalProblem
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Title = title,
                        Criteria = criteria,
                        CriteriaConfirmed = true,
                        Attempt = attempt,
                        AttemptStartedAt = now,
                        Status = 0,
                        Source = DraftContext.IsVisible ? "event" : "manual",
                        Tag = _tag,
                        CreatedAt = now,
                        UpdatedAt = now,
                        Synced = false,
                    });                            // 同上，UI 事件处理器里禁止 ConfigureAwait(false)
                }

                await Shell.Current.GoToAsync("..");
            }
            catch (Exception ex)
            {
                StatusLabel.Text = "保存失败：" + ex.Message;
                SaveBtn.IsEnabled = true;
            }
        }
    }
}
