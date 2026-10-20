using Microsoft.Maui.Controls.Shapes;

namespace PersonalHub.Client.Controls
{
    /// <summary>
    /// 编辑一段文字（替代 DisplayPromptAsync）。
    ///
    /// 为什么换掉 DisplayPromptAsync：Android 上它给的是一个**单行小输入框**，
    /// 几十个字就只能看到尾巴，改一段旧文字等于盲改。这里给的是多行、自动长高、可滚动的编辑面。
    ///
    /// 两种形态，按内容长短选：
    ///   compact = false → 整页编辑（模式四段、事件反思、想法这种要写一会儿的）
    ///   compact = true  → 浮一层卡片（备注、下一步、改名字这种一句话的）
    ///
    /// 返回值：保存 → 新文字（去掉首尾空白）；取消/返回 → null。
    /// 调用方靠 null 判断"没改"，不要把 null 当空字符串写回数据库。
    /// </summary>
    public sealed class TextEditorPage : ContentPage
    {
        private static bool _open;

        private readonly TaskCompletionSource<string?> _result =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly Editor _editor;
        private readonly Label _count;
        private readonly string _title;
        private readonly int _maxLength;
        private bool _closing;

        /// <summary>
        /// 打开编辑器并等用户操作完。取消返回 null。
        /// </summary>
        public static async Task<string?> ShowAsync(
            string title,
            string? initial = null,
            string? placeholder = null,
            bool compact = false,
            int maxLength = 0)
        {
            if (_open) return null;
            _open = true;

            var page = new TextEditorPage(title, initial, placeholder, compact, maxLength);
            try
            {
                await Shell.Current.Navigation.PushModalAsync(page, true);
            }
            catch
            {
                _open = false;
                return null;
            }

            return await page._result.Task;
        }

        private TextEditorPage(string title, string? initial, string? placeholder, bool compact, int maxLength)
        {
            _title = title;
            _maxLength = maxLength;

            _editor = new Editor
            {
                Text = initial ?? string.Empty,
                Placeholder = placeholder ?? string.Empty,
                FontSize = 15,
                AutoSize = EditorAutoSizeOption.TextChanges,
                MinimumHeightRequest = compact ? 96 : 220,
                BackgroundColor = Colors.Transparent,
                TextColor = Color.FromArgb("#2C2C2A"),
                PlaceholderColor = Color.FromArgb("#B4B2A9"),
            };
            _editor.TextChanged += OnTextChanged;

            _count = new Label
            {
                Text = string.Empty,
                FontSize = 11,
                TextColor = Color.FromArgb("#8A8A8E"),
                VerticalOptions = LayoutOptions.Center,
            };

            if (compact)
            {
                BackgroundColor = Color.FromArgb("#66000000");
                Content = BuildCompact();
            }
            else
            {
                Title = title;
                Content = BuildFullPage();
            }

            UpdateCount();
        }

        // ============================================================
        // 两种形态
        // ============================================================

        private View BuildFullPage()
        {
            var grid = new Grid
            {
                RowDefinitions =
                {
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Star),
                    new RowDefinition(GridLength.Auto),
                },
                Padding = new Thickness(20, 12),
                RowSpacing = 10,
            };

            grid.Add(new Label
            {
                Text = _title,
                FontSize = 13,
                TextColor = Color.FromArgb("#5F5E5A"),
            }, 0, 0);

            grid.Add(new ScrollView { Content = _editor }, 0, 1);

            var bar = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(GridLength.Auto),
                },
            };

            // 整页模式也必须给「取消」：它被 PushModalAsync 推出来，没有返回箭头，
            // 只有一个 Android 物理返回键 —— 而很多人根本不会去按它。
            var cancel = new Button
            {
                Text = "取消",
                FontSize = 14,
                HeightRequest = 44,
                Padding = new Thickness(10, 0),
                BackgroundColor = Colors.Transparent,
                TextColor = Color.FromArgb("#5F5E5A"),
            };
            cancel.Clicked += async (_, _) => await CancelAsync();

            bar.Add(cancel, 0, 0);
            bar.Add(_count, 1, 0);
            bar.Add(MakeSaveButton(full: true), 2, 0);
            grid.Add(bar, 0, 2);

            return grid;
        }

        private View BuildCompact()
        {
            var stack = new VerticalStackLayout { Spacing = 10 };
            stack.Children.Add(new Label
            {
                Text = _title,
                FontSize = 13,
                TextColor = Color.FromArgb("#5F5E5A"),
            });

            stack.Children.Add(new ScrollView
            {
                MaximumHeightRequest = CompactMaxHeight(),
                Content = _editor,
            });

            var bar = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Auto) } };
            bar.Add(_count, 0, 0);

            var cancel = new Button
            {
                Text = "取消",
                FontSize = 14,
                HeightRequest = 40,
                BackgroundColor = Colors.Transparent,
                TextColor = Color.FromArgb("#5F5E5A"),
            };
            cancel.Clicked += async (_, _) => await CancelAsync();
            bar.Add(cancel, 1, 0);
            bar.Add(MakeSaveButton(full: false), 2, 0);
            stack.Children.Add(bar);

            var card = new Border
            {
                BackgroundColor = Color.FromArgb("#FFFFFF"),
                Stroke = Color.FromArgb("#E3E0D8"),
                StrokeThickness = 1,
                StrokeShape = new RoundRectangle { CornerRadius = 14 },
                Padding = new Thickness(16, 14),
                Margin = new Thickness(18, 0),
                VerticalOptions = LayoutOptions.Center,
                Content = stack,
            };

            var top = new BoxView { Color = Colors.Transparent };
            var bottom = new BoxView { Color = Colors.Transparent };
            top.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(async () => await CancelAsync()) });
            bottom.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(async () => await CancelAsync()) });

            var grid = new Grid
            {
                RowDefinitions =
                {
                    new RowDefinition(GridLength.Star),
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Star),
                },
            };
            grid.Add(top, 0, 0);
            grid.Add(card, 0, 1);
            grid.Add(bottom, 0, 2);
            return grid;
        }

        private Button MakeSaveButton(bool full)
        {
            var save = new Button
            {
                Text = "保存",
                FontSize = 14,
                HeightRequest = full ? 44 : 40,
                Padding = new Thickness(18, 0),
                BackgroundColor = Color.FromArgb("#534AB7"),
                TextColor = Colors.White,
            };
            save.Clicked += async (_, _) => await SaveAsync();
            return save;
        }

        private static double CompactMaxHeight()
        {
            try
            {
                var info = DeviceDisplay.Current.MainDisplayInfo;
                if (info.Height > 0 && info.Density > 0)
                {
                    return Math.Max(140, info.Height / info.Density * 0.42);
                }
            }
            catch
            {
                // 取不到就用保守值
            }

            return 260;
        }

        // ============================================================
        // 字数与保存
        // ============================================================

        private void OnTextChanged(object? sender, TextChangedEventArgs e)
        {
            if (_maxLength > 0)
            {
                // 直接截断而不是弹提示：在输入到一半时弹窗会打断输入，而且打断得很频繁。
                string text = e.NewTextValue ?? string.Empty;
                if (text.Length > _maxLength)
                {
                    _editor.Text = text.Substring(0, _maxLength);
                    return;
                }
            }

            UpdateCount();
        }

        private void UpdateCount()
        {
            int len = (_editor.Text ?? string.Empty).Length;
            _count.Text = _maxLength > 0 ? $"{len} / {_maxLength}" : $"{len} 字";
        }

        private async Task SaveAsync()
        {
            if (_closing) return;
            _closing = true;

            _result.TrySetResult((_editor.Text ?? string.Empty).Trim());
            await PopAsync();
        }

        private async Task CancelAsync()
        {
            if (_closing) return;
            _closing = true;

            _result.TrySetResult(null);
            await PopAsync();
        }

        private async Task PopAsync()
        {
            try
            {
                await Shell.Current.Navigation.PopModalAsync(true);
            }
            catch
            {
                _open = false;
            }
        }

        protected override bool OnBackButtonPressed()
        {
            _ = CancelAsync();
            return true;
        }

        protected override void OnDisappearing()
        {
            base.OnDisappearing();
            _open = false;

            // 手势下滑关闭等路径不会走 Save/Cancel，这里兜底成"取消"，
            // 否则调用方会永远 await 一个不会完成的 Task。
            _result.TrySetResult(null);
        }
    }
}
