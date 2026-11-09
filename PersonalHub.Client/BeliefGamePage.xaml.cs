using System.Text;
using Microsoft.Maui.Controls.Shapes;
using PersonalHub.Client.Data;

namespace PersonalHub.Client
{
    /// <summary>
    /// 信念拼词（玩具，不是考核）。
    ///
    /// 规则：目标句 + 另一条信念的块混在一起打乱，点选拼回目标句。
    /// 干扰块是真的从你自己库里借的 —— 不是随机造词，所以"哪些块属于这句"这关有点意思。
    ///
    /// 三条刻意不做：
    ///  1. **不计时、不积分、不排行**。做成考核就会开始怕输，然后就不玩了。
    ///  2. **不排期**（没有 1/3/7/21 天到期）。想练就点开，不想练就放着。
    ///  3. **不弹"恭喜"**。拼对了只说"第 N 次"，把注意力留在句子上。
    ///
    /// 唯一落库的东西：IsBelief / PracticeCount / LastPracticedAt（纯本地，见 LocalIdea）。
    /// </summary>
    [QueryProperty(nameof(IdeaId), "id")]
    public partial class BeliefGamePage : ContentPage
    {
        /// <summary>切块时认作"断句点"的字符。中英文标点都算，空格也算。</summary>
        private const string BreakChars = "，。；！？、：,.!?;:…—\n\r\t（）()《》【】<>\"'“”‘’ ";

        /// <summary>
        /// 长段再切时，优先在这些虚词后面断（读起来不会劈开一个词）。
        /// 「不」「很」「太」故意不在里面 —— 切在它们后面会把一个词撕成两半（「别人不 / 自信」）。
        /// </summary>
        private const string SoftCharBreaks = "的了是在就都也和把被对";

        private readonly LocalRecordStore<LocalIdea> _ideas;

        private LocalIdea? _idea;
        private List<string> _target = new();
        private List<string> _pool = new();
        private List<string> _placed = new();

        private bool _loaded;
        private bool _solved;

        /// <summary>由 Shell 的 ?id= 注入。</summary>
        public string IdeaId { get; set; } = string.Empty;

        public BeliefGamePage(LocalRecordStore<LocalIdea> ideas)
        {
            InitializeComponent();
            _ideas = ideas;
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();

            if (_loaded) return;
            _loaded = true;

            await StartAsync();
        }

        // ============================================================
        // 开局
        // ============================================================

        private async Task StartAsync()
        {
            _idea = string.IsNullOrEmpty(IdeaId) ? null : await _ideas.FindAsync(IdeaId);

            if (_idea is null)
            {
                HintLabel.Text = "没找到这条想法";
                SubHintLabel.Text = "可能它已经被删了，回去再挑一条。";
                return;
            }

            _target = Chunk(_idea.Text);

            if (_target.Count < 2)
            {
                HintLabel.Text = "这句太短，拆不开";
                SubHintLabel.Text = "至少两个字才拆得出两块。回去换一条，或者把想法写长一点。";
                return;
            }

            HintLabel.Text = "把你要的那句拼回来";
            SubHintLabel.Text = $"池子里混了另一条信念的块，别把别人的词用上。这句一共 {_target.Count} 块。";

            await BuildRoundAsync();
        }

        private async Task BuildRoundAsync()
        {
            var decoys = await PickDecoysAsync();

            _placed = new List<string>();
            _pool = _target.Concat(decoys)
                           .OrderBy(_ => Random.Shared.Next())
                           .ToList();
            _solved = false;

            FeedbackLabel.Text = "点下面的块，按顺序拼到上面。拼错了点上面的块可以拿下来。";
            FeedbackLabel.TextColor = Color.FromArgb("#8A8A8E");
            RetryBtn.Text = "重来";

            Render();
        }

        /// <summary>
        /// 借另一条信念的块当干扰。
        ///
        /// 优先借「信念」（同类才有混淆价值）；库里只有这一条信念时，退而借普通想法的块 ——
        /// 否则第一句永远没干扰，玩起来就是纯排序。
        /// </summary>
        private async Task<List<string>> PickDecoysAsync()
        {
            if (_idea is null) return new List<string>();

            var all = await _ideas.GetAllAsync();
            var candidates = all
                .Where(x => x.Id != _idea.Id && !string.IsNullOrWhiteSpace(x.Text))
                .ToList();

            if (candidates.Count == 0) return new List<string>();

            var beliefs = candidates.Where(x => x.IsBelief).ToList();

            var source = beliefs.Count > 0 && Random.Shared.Next(3) != 0
                ? beliefs[Random.Shared.Next(beliefs.Count)]
                : candidates[Random.Shared.Next(candidates.Count)];

            return Chunk(source.Text)
                .Where(c => !_target.Contains(c))
                .Distinct()
                .OrderBy(_ => Random.Shared.Next())
                .Take(3)
                .ToList();
        }

        // ============================================================
        // 渲染
        // ============================================================

        private void Render()
        {
            // 空槽：固定数量 = 目标句的块数，所以"拼满"就是判定时机
            SlotArea.Children.Clear();

            for (int i = 0; i < _target.Count; i++)
            {
                bool filled = i < _placed.Count;

                var slot = new Border
                {
                    StrokeShape = new RoundRectangle { CornerRadius = 8 },
                    StrokeThickness = filled ? 1.5 : 1,
                    Stroke = Color.FromArgb(filled ? "#1D9E75" : "#C9C5BA"),
                    StrokeDashArray = filled ? new DoubleCollection() : new DoubleCollection { 4, 3 },
                    BackgroundColor = filled ? Color.FromArgb("#E1F5EE") : Colors.Transparent,
                    Padding = new Thickness(12, 7),
                    Margin = new Thickness(0, 0, 8, 8),
                    MinimumWidthRequest = 46,
                    Content = new Label
                    {
                        Text = filled ? _placed[i] : "?",
                        FontSize = 15,
                        TextColor = Color.FromArgb(filled ? "#04342C" : "#B4B2A9"),
                        HorizontalTextAlignment = TextAlignment.Center,
                        VerticalTextAlignment = TextAlignment.Center,
                    },
                };

                if (filled)
                {
                    // 注意：Tapped 的 sender 是被点的 View，参数只能从 e.Parameter 取
                    var tap = new TapGestureRecognizer { CommandParameter = i };
                    tap.Tapped += OnSlotTapped;
                    slot.GestureRecognizers.Add(tap);
                }

                SlotArea.Children.Add(slot);
            }

            // 词块池
            PoolArea.Children.Clear();

            for (int i = 0; i < _pool.Count; i++)
            {
                var chunk = new Button
                {
                    Text = _pool[i],
                    FontSize = 15,
                    HeightRequest = 40,
                    Padding = new Thickness(12, 0),
                    CornerRadius = 8,
                    BackgroundColor = Color.FromArgb("#EEE9DC"),
                    TextColor = Color.FromArgb("#2C2C2A"),
                    Margin = new Thickness(0, 0, 8, 8),
                    CommandParameter = i,
                };
                chunk.Clicked += OnChunkClicked;
                PoolArea.Children.Add(chunk);
            }
        }

        // ============================================================
        // 交互
        // ============================================================

        private void OnChunkClicked(object? sender, EventArgs e)
        {
            if (_solved) return;
            if (sender is not Button b || b.CommandParameter is not int index) return;
            if (index < 0 || index >= _pool.Count) return;

            _placed.Add(_pool[index]);
            _pool.RemoveAt(index);
            Render();

            if (_placed.Count == _target.Count) _ = JudgeAsync();
        }

        private void OnSlotTapped(object? sender, TappedEventArgs e)
        {
            if (_solved) return;
            if (e.Parameter is not int index) return;
            if (index < 0 || index >= _placed.Count) return;

            _pool.Add(_placed[index]);
            _placed.RemoveAt(index);
            Render();
        }

        private async Task JudgeAsync()
        {
            if (_placed.SequenceEqual(_target))
            {
                _solved = true;

                if (_idea is not null)
                {
                    _idea.IsBelief = true;
                    _idea.PracticeCount += 1;
                    _idea.LastPracticedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    await _ideas.UpdateAsync(_idea);
                }

                FeedbackLabel.Text = $"拼上了 · 这是第 {_idea?.PracticeCount ?? 1} 次";
                FeedbackLabel.TextColor = Color.FromArgb("#3B6D11");
                RetryBtn.Text = "再来一遍";

                await SlotArea.ScaleTo(1.05, 90, Easing.CubicOut);
                await SlotArea.ScaleTo(1, 110, Easing.CubicIn);
                return;
            }

            FeedbackLabel.Text = "顺序不对 · 点上面的块可以拿下来重排";
            FeedbackLabel.TextColor = Color.FromArgb("#A32D2D");

            const uint step = 60;
            await SlotArea.TranslateTo(-7, 0, step, Easing.Linear);
            await SlotArea.TranslateTo(7, 0, step, Easing.Linear);
            await SlotArea.TranslateTo(0, 0, step, Easing.Linear);
        }

        private async void OnRetryClicked(object? sender, EventArgs e)
        {
            if (_target.Count < 2)
            {
                await Shell.Current.GoToAsync("..");
                return;
            }

            await BuildRoundAsync();
        }

        private async void OnBackClicked(object? sender, EventArgs e)
        {
            await Shell.Current.GoToAsync("..");
        }

        // ============================================================
        // 切块
        // ============================================================

        /// <summary>
        /// 本地切块，零成本，不调 AI。
        ///
        /// 三步：
        ///  1. 按标点断句（「我不用赢，我要的是关系」→ 两段）；
        ///  2. 每段超过 5 个字就按长度**均衡**切成若干块（不是从头上切，避免最后剩俩字）；
        ///  3. 切点尽量挪到虚词后面，读起来不会劈开一个词。
        ///
        /// 切得不够漂亮也无所谓 —— 这是玩具，不是分词器。
        /// </summary>
        private static List<string> Chunk(string text)
        {
            const int maxLen = 5;

            var chunks = new List<string>();

            foreach (var seg in SplitByPunctuation(text))
            {
                if (seg.Length <= maxLen)
                {
                    chunks.Add(seg);
                    continue;
                }

                // 需要几块 → 每块大概多长（向上取整，宁可前面几块短一点）
                int pieces = (seg.Length + maxLen - 1) / maxLen;
                int size = (int)Math.Ceiling(seg.Length / (double)pieces);

                int start = 0;

                while (start < seg.Length)
                {
                    int remain = seg.Length - start;

                    // 剩下的不多了就整段收尾，别再切出一个字的碎片
                    if (remain <= size + 1)
                    {
                        chunks.Add(seg.Substring(start));
                        break;
                    }

                    int cut = FindCut(seg, start, size);
                    chunks.Add(seg.Substring(start, cut - start));
                    start = cut;
                }
            }

            var result = chunks.Where(c => c.Length > 0).ToList();

            // 一句话没有任何断点（「做就是了」）就从中劈两半，保证至少有两块可拼
            if (result.Count == 1 && result[0].Length >= 2)
            {
                int mid = result[0].Length / 2;
                result = new List<string> { result[0].Substring(0, mid), result[0].Substring(mid) };
            }

            return result;
        }

        private static List<string> SplitByPunctuation(string text)
        {
            var segments = new List<string>();
            var buffer = new StringBuilder();

            foreach (char ch in text.Trim())
            {
                if (BreakChars.IndexOf(ch) >= 0)
                {
                    if (buffer.Length > 0)
                    {
                        segments.Add(buffer.ToString());
                        buffer.Clear();
                    }
                }
                else
                {
                    buffer.Append(ch);
                }
            }

            if (buffer.Length > 0) segments.Add(buffer.ToString());

            return segments;
        }

        /// <summary>
        /// 在理想切点附近 ±2 个字里找"虚词后面"落刀；找不到就按理想位置切。
        /// 距离相同优先往左（前面短一点，后面完整一点，读着更顺）。
        /// </summary>
        private static int FindCut(string seg, int start, int size)
        {
            int ideal = start + size;

            for (int d = 0; d <= 2; d++)
            {
                int left = ideal - d;
                if (left > start + 1 && left < seg.Length - 1 && SoftCharBreaks.IndexOf(seg[left - 1]) >= 0)
                    return left;

                int right = ideal + d;
                if (right > start + 1 && right < seg.Length - 1 && SoftCharBreaks.IndexOf(seg[right - 1]) >= 0)
                    return right;
            }

            return Math.Min(ideal, seg.Length - 1);
        }
    }
}
