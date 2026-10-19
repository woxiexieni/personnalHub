using PersonalHub.Client.Data;
using PersonalHub.Client.Services;

namespace PersonalHub.Client
{
    /// <summary>
    /// 「概览」Tab 的根页面 —— 它是一个目录，不是某一页统计。
    ///
    /// 为什么连"只有一页"的时候也要做成目录：
    /// 概览迟早会长出第二、第三页（时间资产、周复盘、月报）。如果现在把唯一那一页
    /// 直接当 Tab，等到第二页出现就得改导航、改 Shell、改发版；结构先立起来，后面是纯增量。
    /// </summary>
    public partial class OverviewHubPage : ContentPage
    {
        private readonly List<DirectoryItem> _items;

        public OverviewHubPage(
            LocalRecordStore<LocalRecord> expenses,
            LocalRecordStore<LocalBlock> blocks,
            LocalRecordStore<LocalGoal> goals)
        {
            InitializeComponent();

            _items = OverviewCatalog.Build(expenses, blocks, goals);
            BindableLayout.SetItemsSource(ViewRows, _items);
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();

            // 纯本地查询（今日合计），不依赖网络
            foreach (var item in _items)
            {
                await item.RefreshAsync();
            }
        }
    }
}
