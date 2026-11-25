using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Layouts;
using PersonalHub.Client.Data;
using PersonalHub.Client.Services;

namespace PersonalHub.Client.Controls
{
    /// <summary>
    /// 番茄钟卡片（R14.5）：挂在时间块录入页顶部。
    ///
    /// 形态对齐用户取向 —— 不是表单：
    ///   待机态 = 选片胶囊（必选）+ 时长档（25/15/45/60）+ **备注框常开**（用户点名要的，
    ///   不折叠）+ 一个「开始专注」按钮；
    ///   运行态 = 大字倒计时 + 暂停/继续 + 结束并记入。
    /// 结束自动入库（含手动停，按实际分钟），不需要人再确认一次。
    ///
    /// Android 的通知栏倒计时由 PomodoroForegroundService 负责，本卡片只管页内 UI。
    /// </summary>
    public class PomodoroCard : VerticalStackLayout
    {
        private static Color ChipBg => IsDark ? Color.FromArgb("#2E2E33") : Color.FromArgb("#F1EFE8");
        private static Color ChipFg => IsDark ? Color.FromArgb("#E8E6DF") : Color.FromArgb("#2C2C2A");
        private static Color ActiveBg => Color.FromArgb("#185FA5");
        private static Color MutedFg => IsDark ? Color.FromArgb("#9A9891") : Color.FromArgb("#78766E");
        private static Color BorderClr => IsDark ? Color.FromArgb("#3A3A40") : Color.FromArgb("#C9D8EA");

        private static bool IsDark =>
            Application.Current?.RequestedTheme == AppTheme.Dark;

        private static readonly int[] DurationChoices = { 25, 15, 45, 60 };

        private readonly PieceSettings _settings;

        // 待机态控件
        private readonly FlexLayout _pieceRow = new() { Wrap = FlexWrap.Wrap, JustifyContent = FlexJustify.Start };
        private readonly FlexLayout _durationRow = new() { Wrap = FlexWrap.Wrap, JustifyContent = FlexJustify.Start };
        private readonly Editor _noteEditor = new()
        {
            Placeholder = "备注（可选）：这 25 分钟干嘛",
            FontSize = 13,
            HeightRequest = 70,          // Editor 没有 AutoSize 好用的形态，固定高（老坑）
            TextColor = ChipFg,
            PlaceholderColor = MutedFg,
        };
        private readonly Button _startBtn = new()
        {
            FontSize = 14,
            HeightRequest = 42,
            CornerRadius = 21,
            BackgroundColor = ActiveBg,
            TextColor = Colors.White,
        };

        // 运行态控件
        private readonly Label _countdown = new()
        {
            FontSize = 42,
            FontAttributes = FontAttributes.Bold,
            HorizontalTextAlignment = TextAlignment.Center,
        };
        private readonly Label _runningInfo = new()
        {
            FontSize = 13,
            HorizontalTextAlignment = TextAlignment.Center,
            TextColor = MutedFg,
        };
        private readonly Button _pauseBtn = new()
        {
            FontSize = 13,
            HeightRequest = 38,
            CornerRadius = 19,
            HorizontalOptions = LayoutOptions.Center,
        };
        private readonly Button _stopBtn = new()
        {
            Text = "结束并记入",
            FontSize = 13,
            HeightRequest = 38,
            CornerRadius = 19,
            HorizontalOptions = LayoutOptions.Center,
            BackgroundColor = ChipBg,
            TextColor = ChipFg,
        };

        // 选择状态
        private string _selectedPiece = string.Empty;
        private int _selectedMinutes = 25;
        private readonly Dictionary<string, Button> _pieceBtns = new();

        // 完成提示（几秒后自动消失）
        private readonly Label _resultLabel = new()
        {
            FontSize = 12,
            TextColor = MutedFg,
            HorizontalTextAlignment = TextAlignment.Center,
            IsVisible = false,
        };

        /// <summary>
        /// 通知权限没开时的补救入口。
        ///
        /// Android 13+ 不给通知权限，前台服务的常驻通知也不进通知栏 ——
        /// 番茄钟就变成"看不见的计时器"，到点也没有任何提示。
        /// 这个提示只在真没权限时出现，点一下直接进系统设置。
        /// </summary>
        private readonly Label _notifyHint = new()
        {
            Text = "通知被关了（权限或渠道）—— 倒计时和到点提醒都不会出现。点这里去打开。",
            FontSize = 11,
            TextColor = Color.FromArgb("#A32D2D"),
            LineBreakMode = LineBreakMode.WordWrap,
            IsVisible = false,
        };

        private readonly VerticalStackLayout _idlePanel;
        private readonly VerticalStackLayout _runningPanel;

        public PomodoroCard(LocalRecordStore<LocalBlock> blocks, PieceSettings settings)
        {
            _settings = settings;

            PomodoroState.Initialize(blocks, settings);

            Spacing = 8;

            _idlePanel = new VerticalStackLayout
            {
                Spacing = 8,
                Children =
                {
                    new Label
                    {
                        Text = "🍅 番茄钟",
                        FontSize = 14,
                        FontAttributes = FontAttributes.Bold,
                    },
                    new Label
                    {
                        Text = "选片开始，结束自动记入时间块（真实起止时刻）",
                        FontSize = 11,
                        TextColor = MutedFg,
                    },
                    _pieceRow,
                    _durationRow,
                    _noteEditor,
                    _startBtn,
                    _notifyHint,
                    _resultLabel,
                },
            };

            _runningPanel = new VerticalStackLayout
            {
                Spacing = 6,
                IsVisible = false,
                Children =
                {
                    new Label
                    {
                        Text = "🍅 专注中",
                        FontSize = 14,
                        FontAttributes = FontAttributes.Bold,
                    },
                    _countdown,
                    _runningInfo,
                    new HorizontalStackLayout
                    {
                        Spacing = 10,
                        HorizontalOptions = LayoutOptions.Center,
                        Children = { _pauseBtn, _stopBtn },
                    },
                    new Label
                    {
                        Text = "期间自动切片已暂停，结束自动恢复",
                        FontSize = 11,
                        TextColor = MutedFg,
                        HorizontalTextAlignment = TextAlignment.Center,
                    },
                },
            };

            Children.Add(new Border
            {
                Stroke = BorderClr,
                StrokeThickness = 1,
                Padding = new Thickness(14, 12),
                StrokeShape = new RoundRectangle { CornerRadius = 6 },
                Content = new VerticalStackLayout { Spacing = 8, Children = { _idlePanel, _runningPanel } },
            });

            BuildPieceChips();
            BuildDurationChips();

            _startBtn.Clicked += OnStartClicked;
            _pauseBtn.Clicked += OnPauseClicked;
            _stopBtn.Clicked += OnStopClicked;

#if ANDROID
            _notifyHint.GestureRecognizers.Add(NotifyHintTap());
#endif

            PomodoroState.Changed += OnStateChanged;
            PomodoroState.Finished += OnFinished;
            Unloaded += (_, _) =>
            {
                PomodoroState.Changed -= OnStateChanged;
                PomodoroState.Finished -= OnFinished;
            };

            Refresh();

#if ANDROID
            _ = CheckNotifyPermissionAsync();
#endif

            // 清后台导致"到点那一刻进程不在"时，时间块是在下次启动才补记的，
            // 那次 Finished 没有界面订阅，所以在这里补一句，免得用户以为白专注了。
            int recovered = PomodoroState.TakeRecoveredMinutes();
            if (recovered > 0)
            {
                _resultLabel.Text = $"✓ 上次那段 {recovered} 分钟已补记进时间块";
                _resultLabel.IsVisible = true;
                _ = HideResultLaterAsync();
            }
        }

#if ANDROID
        private static TapGestureRecognizer NotifyHintTap()
        {
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => NotificationPermissionHelper.OpenSettings();
            return tap;
        }

        private async Task CheckNotifyPermissionAsync()
        {
            try
            {
                bool granted = await NotificationPermissionHelper.IsGrantedAsync();

                // 权限开着但渠道被关成"没有任何提示"时，通知同样不会出现在通知栏。
                // 这两种情况用户看到的都是"番茄钟没有通知"，所以合并成一个提示。
                bool blocked = NotificationPermissionHelper.IsChannelBlocked(NotificationChannels.Pomodoro);

                _notifyHint.IsVisible = !granted || blocked;
            }
            catch
            {
                // 查不到就当有权限，不吓人
            }
        }
#endif

        // ============================================================
        // 构建胶囊
        // ============================================================

        private void BuildPieceChips()
        {
            foreach (var piece in Pieces.All)
            {
                var btn = new Button
                {
                    Text = $"{piece.Emoji} {_settings.DisplayName(piece.Code)}",
                    FontSize = 12,
                    HeightRequest = 32,
                    Padding = new Thickness(10, 0),
                    CornerRadius = 16,
                };
                // 闭包捕获 piece.Code（手势/CommandParameter 的坑见 memory，闭包不赌版本）
                btn.Clicked += (_, _) =>
                {
                    _selectedPiece = piece.Code;
                    StylePieceChips();
                };
                _pieceBtns[piece.Code] = btn;
                _pieceRow.Children.Add(btn);
            }
            StylePieceChips();
        }

        private void StylePieceChips()
        {
            foreach (var (code, btn) in _pieceBtns)
            {
                bool on = code == _selectedPiece;
                btn.BackgroundColor = on ? ActiveBg : ChipBg;
                btn.TextColor = on ? Colors.White : ChipFg;
            }
            _startBtn.Text = string.IsNullOrEmpty(_selectedPiece)
                ? "先选一件事，再开始专注"
                : $"开始专注 {_selectedMinutes} 分钟";
            _startBtn.IsEnabled = !string.IsNullOrEmpty(_selectedPiece);
            _startBtn.BackgroundColor = _startBtn.IsEnabled ? ActiveBg : ChipBg;
            _startBtn.TextColor = _startBtn.IsEnabled ? Colors.White : MutedFg;
        }

        private void BuildDurationChips()
        {
            foreach (int m in DurationChoices)
            {
                var btn = new Button
                {
                    Text = $"{m} 分",
                    FontSize = 12,
                    HeightRequest = 30,
                    Padding = new Thickness(10, 0),
                    CornerRadius = 15,
                };
                btn.Clicked += (_, _) =>
                {
                    _selectedMinutes = m;
                    StyleDurationChips();
                };
                _durationRow.Children.Add(btn);
            }
            StyleDurationChips();
        }

        private void StyleDurationChips()
        {
            int i = 0;
            foreach (var child in _durationRow.Children)
            {
                if (child is Button btn)
                {
                    bool on = i == Array.IndexOf(DurationChoices, _selectedMinutes);
                    btn.BackgroundColor = on ? ActiveBg : ChipBg;
                    btn.TextColor = on ? Colors.White : ChipFg;
                }
                i++;
            }
            if (!string.IsNullOrEmpty(_selectedPiece))
            {
                _startBtn.Text = $"开始专注 {_selectedMinutes} 分钟";
            }
        }

        // ============================================================
        // 动作
        // ============================================================

        private async void OnStartClicked(object? sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(_selectedPiece)) return;

            try
            {
#if ANDROID
                // 只在真要用番茄钟的时候问一次通知权限 —— 启动流程里弹框八成被随手点掉，
                // 而点掉之后 Android 13+ 一条通知都不显示，番茄钟就成了看不见的计时器。
                bool granted = await NotificationPermissionHelper.EnsureAsync();
                _notifyHint.IsVisible = !granted
                    || NotificationPermissionHelper.IsChannelBlocked(NotificationChannels.Pomodoro);
#endif
                PomodoroState.Start(_selectedPiece, _noteEditor.Text ?? string.Empty, _selectedMinutes);

#if ANDROID
                Platforms.Android.PomodoroServiceHelper.StartService();
#endif
            }
            catch
            {
                // 权限申请/服务启动任何意外都不该让这一下点击变成闪退
            }
        }

        private void OnPauseClicked(object? sender, EventArgs e)
        {
            if (PomodoroState.PausedPhase)
            {
                PomodoroState.ResumePhase();
            }
            else
            {
                PomodoroState.PausePhase();
            }
        }

        private void OnStopClicked(object? sender, EventArgs e) => PomodoroState.StopEarly();

        // ============================================================
        // 状态刷新
        // ============================================================

        private void OnStateChanged()
        {
            // 计时循环在后台线程 1Hz 发事件，UI 必须回主线程
            Dispatcher.Dispatch(Refresh);
        }

        private void OnFinished(int minutes)
        {
            Dispatcher.Dispatch(() =>
            {
                _resultLabel.Text = $"✓ 已记入 {minutes} 分钟（真实起止 {_settings.DisplayName(PomodoroState.PieceCode)}）";
                _resultLabel.IsVisible = true;
                Refresh();
                _ = HideResultLaterAsync();
            });
        }

        private async Task HideResultLaterAsync()
        {
            await Task.Delay(6000);
            Dispatcher.Dispatch(() => _resultLabel.IsVisible = false);
        }

        private void Refresh()
        {
            bool running = PomodoroState.Running;
            _idlePanel.IsVisible = !running;
            _runningPanel.IsVisible = running;

            if (running)
            {
                long remainMs = PomodoroState.RemainingMs;
                var t = TimeSpan.FromMilliseconds(remainMs);
                _countdown.Text = PomodoroState.PausedPhase
                    ? $"已暂停 {t.Minutes:D2}:{t.Seconds:D2}"
                    : $"{t.Minutes:D2}:{t.Seconds:D2}";

                _runningInfo.Text =
                    $"{_settings.DisplayName(PomodoroState.PieceCode)} · 计划 {PomodoroState.PlannedMinutes} 分钟"
                    + (string.IsNullOrEmpty(PomodoroState.Note) ? "" : $" · {PomodoroState.Note}");

                _pauseBtn.Text = PomodoroState.PausedPhase ? "继续" : "暂停";
                _pauseBtn.BackgroundColor = ChipBg;
                _pauseBtn.TextColor = ChipFg;
            }
            else
            {
                StylePieceChips();
                StyleDurationChips();
            }
        }
    }
}
