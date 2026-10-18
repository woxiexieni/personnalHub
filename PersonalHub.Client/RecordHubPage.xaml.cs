using PersonalHub.Client.Data;
using PersonalHub.Client.Services;

namespace PersonalHub.Client
{
    /// <summary>
    /// 「记录」页 —— 底部第二个 Tab，是**所有录入入口的目录**。
    ///
    /// 为什么不做成"三个 Tab 各一个记录类型"：
    /// 底部 Tab 应该按「读 / 写」分（概览 / 记录），不能按"类型"分。
    /// 类型会一直加（习惯、体重、阅读、复盘…），按类型分迟早放不下，
    /// 而且每加一种都要改导航、改 Shell、重新发版。
    ///
    /// 现在的结构：目录项来自 <see cref="RecordCatalog"/>，加类型 = 加一条数据 + 一个路由。
    ///
    /// 这一页还额外承担两件事：
    ///   1. 每项显示"今日已记 / 待同步"，回到这里就能知道有没有漏记（比进页面才发现更早）；
    ///   2. 进页面顺手静默同步一次，徽标数字才不会一直挂着旧的。
    /// </summary>
    public partial class RecordHubPage : ContentPage
    {
        private readonly List<RecordTypeItem> _types;
        private readonly RecordSyncService<LocalRecord> _expenseSync;
        private readonly RecordSyncService<ThingRecord> _thingSync;

        /// <summary>防止 OnAppearing 连续触发时叠起两次同步（同步器内部也有锁，这里只是少绕一圈）。</summary>
        private bool _syncing;

        public RecordHubPage(
            LocalRecordStore<LocalRecord> expenses,
            LocalRecordStore<ThingRecord> things,
            RecordSyncService<LocalRecord> expenseSync,
            RecordSyncService<ThingRecord> thingSync)
        {
            InitializeComponent();

            _expenseSync = expenseSync;
            _thingSync = thingSync;

            // 目录只有几项、且是固定集合 → 用 BindableLayout 而不是 CollectionView。
            // （CollectionView 套在 ScrollView 里测量高度会出问题，这里根本不需要虚拟化。）
            _types = RecordCatalog.Build(expenses, things);
            BindableLayout.SetItemsSource(TypeRows, _types);
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();

            // 1) 先把本地的小字/徽标刷出来（纯本地查询，不依赖网络）
            await RefreshAllAsync();

            if (_syncing) return;
            _syncing = true;

            try
            {
                // 2) 静默同步：SyncAsync 内部先 Ping 再决定传不传，连不上只返回 Reachable=false，不抛异常。
                var expense = await _expenseSync.SyncAsync();
                var thing = await _thingSync.SyncAsync();

                int uploaded = expense.UploadedRecords + thing.UploadedRecords;

                if (uploaded > 0)
                {
                    SyncLabel.Text = $"已同步 {uploaded} 条";
                    await RefreshAllAsync();   // 上传完待同步数变了，重刷一遍
                }
                else if (!expense.Reachable || !thing.Reachable)
                {
                    SyncLabel.Text = "离线";
                }
                else
                {
                    SyncLabel.Text = string.Empty;
                }
            }
            catch
            {
                // 同步失败不影响录入：记录本来就先落本地
                SyncLabel.Text = "离线";
            }
            finally
            {
                _syncing = false;
            }
        }

        private async Task RefreshAllAsync()
        {
            foreach (var item in _types)
            {
                await item.RefreshAsync();
            }
        }

        private async void OnTypeTapped(object? sender, TappedEventArgs e)
        {
            if (sender is not Element element || element.BindingContext is not RecordTypeItem item)
            {
                return;
            }

            try
            {
                // 相对路由 → 推进导航栈，自带返回按钮；底部两个 Tab 保持可见
                await Shell.Current.GoToAsync(item.Route);
            }
            catch (Exception ex)
            {
                // 路由没注册是开发期错误：说清楚是哪条路由，而不是静默无反应
                await DisplayAlert("打不开", $"路由 {item.Route} 未注册。\n{ex.Message}", "好");
            }
        }
    }
}
