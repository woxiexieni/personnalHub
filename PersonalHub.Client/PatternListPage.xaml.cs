using PersonalHub.Client.Controls;
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
        private readonly AiAssistService _ai;

        private bool _othersOpen;

        public PatternListPage(
            LocalRecordStore<LocalPattern> patterns,
            LocalRecordStore<LocalPractice> practices,
            RecordSyncService<LocalPattern> patternSync,
            RecordSyncService<LocalPractice> practiceSync,
            AiAssistService ai)
        {
            InitializeComponent();

            _patterns = patterns;
            _practices = practices;
            _patternSync = patternSync;
            _practiceSync = practiceSync;
            _ai = ai;
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
            stack.Children.Add(TextPeek.Attach(new Label
            {
                Text = p.Title,
                FontSize = 15,
                LineBreakMode = LineBreakMode.TailTruncation,
            }));
            stack.Children.Add(TextPeek.Attach(new Label
            {
                Text = string.IsNullOrEmpty(p.NewResponse) ? "还没写新反应" : "练：" + p.NewResponse,
                FontSize = 12,
                TextColor = Color.FromArgb("#5F5E5A"),
                LineBreakMode = LineBreakMode.TailTruncation,
            }));

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
            {
                // 点文字 = 看全文，点行 = 进详情。先到先得，后到的被吞掉。
                if (!TapArbiter.TryClaim()) return;

                await Shell.Current.GoToAsync($"{RecordCatalog.PatternDetailRoute}?id={p.Id}");
            };
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

        // ============================================================
        // 从想法里找模式（§27-B）
        // ============================================================
        //
        // 为什么放在「行为模式」页而不是「想法库」页：
        //   这个功能的产出是一个**模式**，不是"整理过的想法"。放在想法库会让人以为
        //   "我该去把想法处理一下"，而想法库的根本立场恰恰是"不需要处理"。
        //
        // 为什么每条都必须带原文：
        //   模型说"你总是拖延"是不可接受的；它只能指着你自己写过的几句话。
        //   那几句话由服务端按序号回填，模型碰不到原字 —— 编不出来。

        private async void OnFindPatternClicked(object? sender, EventArgs e)
        {
            if (!FindPatternBtn.IsEnabled) return;

            FindPatternBtn.IsEnabled = false;
            FindPatternBtn.Text = "看着…";
            ProposalPanel.IsVisible = false;
            ProposalRows.Children.Clear();

            try
            {
                var r = await _ai.FindPatternsAsync();

                if (!r.Ok)
                {
                    ProposalHint.Text = r.Message;
                    ProposalDismissBtn.IsVisible = false;
                    ProposalPanel.IsVisible = true;
                    return;
                }

                ProposalHint.Text = "这些是从你的「评价」类想法里找出来的。每条都附了原句 ——"
                    + "对不上就说明它想错了，别硬认。";
                ProposalDismissBtn.IsVisible = true;

                foreach (var p in r.Items) ProposalRows.Children.Add(BuildProposalCard(p));

                ProposalPanel.IsVisible = true;
            }
            catch (Exception ex)
            {
                ProposalHint.Text = "出错了：" + ex.Message;
                ProposalDismissBtn.IsVisible = false;
                ProposalPanel.IsVisible = true;
            }
            finally
            {
                FindPatternBtn.IsEnabled = true;
                FindPatternBtn.Text = "从想法里找找反复出现的 ▸";
            }
        }

        private View BuildProposalCard(AiPattern p)
        {
            var stack = new VerticalStackLayout { Spacing = 3 };

            stack.Children.Add(new Label
            {
                Text = p.Title,
                FontSize = 15,
                FontAttributes = FontAttributes.Bold,
                LineBreakMode = LineBreakMode.WordWrap,
            });

            AddField(stack, "触发", p.Trigger, Color.FromArgb("#5F5E5A"));
            AddField(stack, "旧反应", p.OldResponse, Color.FromArgb("#5F5E5A"));
            AddField(stack, "练", p.NewResponse, Color.FromArgb("#3B6D11"));

            if (p.Evidence.Count > 0)
            {
                stack.Children.Add(new Label
                {
                    Text = "原句：" + string.Join(" ", p.Evidence.Select(e => $"「{e}」")),
                    FontSize = 11,
                    TextColor = Color.FromArgb("#8A8A8E"),
                    Margin = new Thickness(0, 4, 0, 0),
                    LineBreakMode = LineBreakMode.WordWrap,
                });
            }

            var card = new Border
            {
                Stroke = Color.FromArgb("#E6C68A"),
                StrokeThickness = 1,
                Padding = new Thickness(14, 10),
                Content = stack,
            };

            var buttons = new HorizontalStackLayout { Spacing = 8, Margin = new Thickness(0, 6, 0, 0) };

            var use = new Button
            {
                Text = "用这个",
                FontSize = 11,
                HeightRequest = 30,
                Padding = new Thickness(12, 0),
                CornerRadius = 8,
                BackgroundColor = Color.FromArgb("#EEEDFE"),
                TextColor = Color.FromArgb("#534AB7"),
            };
            use.Clicked += async (_, _) => await AdoptAsync(p, card);

            var skip = new Button
            {
                Text = "不要",
                FontSize = 11,
                HeightRequest = 30,
                Padding = new Thickness(12, 0),
                CornerRadius = 8,
                BackgroundColor = Colors.Transparent,
                TextColor = Color.FromArgb("#8A8A8E"),
            };
            skip.Clicked += (_, _) =>
            {
                ProposalRows.Children.Remove(card);
                if (ProposalRows.Children.Count == 0)
                {
                    ProposalPanel.IsVisible = false;
                    StatusLabel.Text = "都看过了。想法不处理也没关系 —— 它们本来就只是原材料。";
                }
            };

            buttons.Children.Add(use);
            buttons.Children.Add(skip);
            stack.Children.Add(buttons);

            return card;
        }

        private static void AddField(VerticalStackLayout stack, string label, string value, Color color)
        {
            if (string.IsNullOrWhiteSpace(value)) return;

            stack.Children.Add(new Label
            {
                Text = $"{label}：{value}",
                FontSize = 12,
                TextColor = color,
                LineBreakMode = LineBreakMode.WordWrap,
            });
        }

        /// <summary>
        /// 「用这个」= 建一个**完整的**模式（四段都填上）。
        /// 不预填到上面的输入框：那会拆成两步，而模式名和"练的那个动作"是同一件事的两面。
        /// 建完可以直接点进去改 —— 原文一字不动，改的只是它的措辞。
        /// </summary>
        private async Task AdoptAsync(AiPattern p, View card)
        {
            var all = await _patterns.GetByRangeAsync(0, long.MaxValue);
            if (all.Any(x => x.Title == p.Title && !x.Deleted))
            {
                StatusLabel.Text = $"已经有一个叫「{p.Title}」的模式了。";
                return;
            }

            int active = all.Count(x => x.Status == 0);
            int status = active >= MaxActive ? 1 : 0;   // 名额满了就先存为暂停，不丢内容

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var pattern = new LocalPattern
            {
                Id = Guid.NewGuid().ToString("N"),
                Title = p.Title,
                Trigger = p.Trigger,
                OldResponse = p.OldResponse,
                NewResponse = p.NewResponse,
                Status = status,
                CreatedAt = now,
                UpdatedAt = now,
                Deleted = false,
                Synced = false,
                SyncedAt = 0,
            };

            await _patterns.InsertAsync(pattern);
            ProposalRows.Children.Remove(card);

            await LoadAsync();

            StatusLabel.Text = status == 0
                ? $"已建「{p.Title}」。点进去补一句「保留的回报」，它就完整了。"
                : $"已建「{p.Title}」，先存为暂停（已有 {MaxActive} 个在练）。";

            if (ProposalRows.Children.Count == 0) ProposalPanel.IsVisible = false;

            _ = _patternSync.SyncAsync();
        }

        private void OnDismissProposalsClicked(object? sender, EventArgs e)
        {
            ProposalPanel.IsVisible = false;
            StatusLabel.Text = "自己写吧。上面那两格填完点「建」就行。";
        }
    }
}
