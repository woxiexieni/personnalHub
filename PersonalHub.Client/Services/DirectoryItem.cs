using System.ComponentModel;
using System.Windows.Input;

namespace PersonalHub.Client.Services
{
    /// <summary>
    /// 目录里的一行 —— 「概览」和「记录」两个 Tab 用的是同一个模型、同一份模板
    /// （<c>Resources/Styles/DirectoryRow.xaml</c>）。
    ///
    /// 为什么导航做成目录而不是一排 Tab：
    /// 底部 Tab 只按「读 / 写」分工，具体有哪几项会一直变（支出、时间、复盘、习惯…）。
    /// 目录是数据驱动的：加一项 = 在对应的 Catalog 里加一条，界面和导航都不用改。
    /// </summary>
    public sealed class DirectoryItem : INotifyPropertyChanged
    {
        private readonly Func<Task<string?>>? _summaryProvider;
        private readonly Func<Task<int>>? _pendingProvider;

        private string _summary = string.Empty;
        private string _badge = string.Empty;
        private ICommand? _openCommand;

        public DirectoryItem(
            string title,
            string subtitle,
            string icon,
            string route,
            Func<Task<string?>>? summaryProvider = null,
            Func<Task<int>>? pendingProvider = null)
        {
            Title = title;
            Subtitle = subtitle;
            Icon = icon;
            Route = route;
            _summaryProvider = summaryProvider;
            _pendingProvider = pendingProvider;
        }

        public string Title { get; }

        /// <summary>一句话说明，让人在目录里就知道点进去是什么。</summary>
        public string Subtitle { get; }

        /// <summary>用 emoji 而不是图片：省一套图标资源，也不用为深色模式准备两套。</summary>
        public string Icon { get; }

        /// <summary>Shell 路由（AppShell 里注册）。</summary>
        public string Route { get; }

        /// <summary>副标题下面那行小字，例如"今日支出 ¥12.30"。null = 不显示。</summary>
        public string SummaryText => _summary;

        public bool HasSummary => _summary.Length > 0;

        /// <summary>右上角徽标，例如"3 条待同步"。</summary>
        public string BadgeText => _badge;

        public bool HasBadge => _badge.Length > 0;

        /// <summary>
        /// 点击整行的命令。
        ///
        /// 导航写在模型里（而不是页面的 Tapped 事件）是**故意的**：
        /// 模板放在 App 级资源字典里，没法绑定到某个页面的 code-behind 事件。
        /// 用命令后，模板对页面零依赖，才能两个目录页共用一份。
        /// </summary>
        public ICommand OpenCommand => _openCommand ??= new Command(async () => await OpenAsync());

        private async Task OpenAsync()
        {
            if (string.IsNullOrWhiteSpace(Route)) return;

            try
            {
                // 相对路由 → 推进导航栈，自带返回按钮；底部两个 Tab 保持可见
                await Shell.Current.GoToAsync(Route);
            }
            catch (Exception ex)
            {
                // 路由没注册是开发期错误：说清楚是哪条路由，而不是点了没反应
                await Shell.Current.DisplayAlert("打不开", $"路由 {Route} 未注册。\n{ex.Message}", "好");
            }
        }

        /// <summary>
        /// 刷新小字与徽标。**故意吞掉异常**：目录是入口，
        /// 某一项的本地库读失败不该让整个目录打不开 —— 那一项只是少行小字。
        /// </summary>
        public async Task RefreshAsync()
        {
            string summary = string.Empty;
            string badge = string.Empty;

            try
            {
                if (_summaryProvider is not null)
                {
                    // ConfigureAwait(true)：改的是绑定到界面的属性，必须回 UI 线程
                    summary = await _summaryProvider().ConfigureAwait(true) ?? string.Empty;
                }

                if (_pendingProvider is not null)
                {
                    int pending = await _pendingProvider().ConfigureAwait(true);
                    badge = pending > 0 ? $"{pending} 条待同步" : string.Empty;
                }
            }
            catch
            {
                // 读不到就当作"没有额外信息"，不影响点进去
            }

            Set(ref _summary, summary, nameof(SummaryText), nameof(HasSummary));
            Set(ref _badge, badge, nameof(BadgeText), nameof(HasBadge));
        }

        private void Set(ref string field, string value, string textProp, string boolProp)
        {
            if (field == value) return;

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(textProp));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(boolProp));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
