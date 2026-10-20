using Microsoft.Maui.Controls.Shapes;

namespace PersonalHub.Client.Controls
{
    /// <summary>
    /// 半屏阅读卡：把一段长文本摊开来看，点任意处关掉。
    ///
    /// 为什么长文本不用「就地展开」：事件反思、模式四段、想法这类动辄几百上千字，
    /// 在列表里展开会把页面撑成好几屏，用户看完还得往上翻回去 —— 比截断还难用。
    /// 这类统一走这个卡片：屏幕中间浮一层，可滚动，读完即走。
    ///
    /// 为什么要自绘而不是用 MAUI 的 ToolTipProperties：
    /// Android 上 ToolTip 是**长按**触发（松手后几秒消失），而本 App 的长按已经占给了
    /// 「改时长 / 改名」菜单，直接撞车；而且原生提示框是小黑框，字多了照样截断。
    /// </summary>
    public sealed class PeekPage : ContentPage
    {
        /// <summary>同一时刻只允许一张阅读卡（防止连点叠出好几层）。</summary>
        private static bool _open;

        public static async Task ShowAsync(string? caption, string? text)
        {
            string body = (text ?? string.Empty).Trim();
            if (body.Length == 0 || _open) return;

            _open = true;
            try
            {
                await Shell.Current.Navigation.PushModalAsync(new PeekPage(caption ?? string.Empty, body), true);
            }
            catch
            {
                // 推不出去（正在转场等）就复位，不要让开关永久卡死
                _open = false;
            }
        }

        private PeekPage(string caption, string body)
        {
            BackgroundColor = Color.FromArgb("#66000000");

            var card = new Border
            {
                BackgroundColor = Color.FromArgb("#FFFFFF"),
                Stroke = Color.FromArgb("#E3E0D8"),
                StrokeThickness = 1,
                StrokeShape = new RoundRectangle { CornerRadius = 14 },
                Padding = new Thickness(18, 16),
                Margin = new Thickness(18, 0),
                VerticalOptions = LayoutOptions.Center,
                Content = BuildCard(caption, body),
            };

            // 上下两块透明区吃掉点击 = "点空白处关闭"。
            // 不用给整页挂手势：那样点在卡片上也会冒泡到页面，一碰卡片就关了。
            var top = new BoxView { Color = Colors.Transparent };
            var bottom = new BoxView { Color = Colors.Transparent };
            top.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(async () => await CloseAsync()) });
            bottom.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(async () => await CloseAsync()) });

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

            Content = grid;
        }

        private View BuildCard(string caption, string body)
        {
            var stack = new VerticalStackLayout { Spacing = 10 };

            if (caption.Length > 0)
            {
                stack.Children.Add(new Label
                {
                    Text = caption,
                    FontSize = 12,
                    TextColor = Color.FromArgb("#8A8A8E"),
                });
            }

            stack.Children.Add(new ScrollView
            {
                MaximumHeightRequest = MaxCardHeight(),
                Content = new Label
                {
                    Text = body,
                    FontSize = 14,
                    LineHeight = 1.45,
                    LineBreakMode = LineBreakMode.WordWrap,
                    TextColor = Color.FromArgb("#2C2C2A"),
                },
            });

            var close = new Button
            {
                Text = "知道了",
                FontSize = 14,
                HeightRequest = 40,
                BackgroundColor = Color.FromArgb("#F1EFE8"),
                TextColor = Color.FromArgb("#2C2C2A"),
            };
            close.Clicked += async (_, _) => await CloseAsync();
            stack.Children.Add(close);

            return stack;
        }

        /// <summary>卡片最多占屏幕高度的 62%，留出上下文，让人知道是从哪儿点开的。</summary>
        private static double MaxCardHeight()
        {
            try
            {
                var info = DeviceDisplay.Current.MainDisplayInfo;
                if (info.Height > 0 && info.Density > 0)
                {
                    return Math.Max(180, info.Height / info.Density * 0.62);
                }
            }
            catch
            {
                // 取不到就退回一个保守值
            }

            return 380;
        }

        private async Task CloseAsync()
        {
            if (!_open) return;

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
            _ = CloseAsync();
            return true;
        }

        protected override void OnDisappearing()
        {
            base.OnDisappearing();
            _open = false;
        }
    }
}
