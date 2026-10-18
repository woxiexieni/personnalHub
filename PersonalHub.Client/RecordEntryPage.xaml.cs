using PersonalHub.Client.Data;
using PersonalHub.Client.Services;

namespace PersonalHub.Client
{
    /// <summary>
    /// 账单录入页（本地优先）。
    ///
    /// 数据流：
    ///   保存  → 只写本地 SQLite（Synced=false），界面立刻刷新，不依赖网络；
    ///   列表  → 一律从本地读，离线也完整可看；
    ///   同步  → RecordSyncService 把待同步记录按天打包上传（进页面自动触发一次）。
    ///
    /// 约定：spent_at / created_at 均为 Unix 毫秒时间戳。
    /// </summary>
    public partial class RecordEntryPage : ContentPage
    {
        private readonly LocalRecordStore<LocalRecord> _store;
        private readonly RecordSyncService<LocalRecord> _sync;

        // 分类目前写死在客户端；将来接 CategoryService.GetCategories 后换成服务端下发。
        private static readonly (int Id, string Name)[] Categories =
        {
            (1, "餐饮"),
            (2, "交通"),
            (3, "购物"),
            (4, "居住"),
            (5, "娱乐"),
            (6, "医疗"),
            (7, "其他"),
        };

        private static readonly string[] PayMethods = { "微信", "支付宝", "现金", "银行卡" };

        private static readonly Color PendingColor = Color.FromArgb("#D97706");
        private static readonly Color SyncedColor = Color.FromArgb("#8A8A8E");

        public RecordEntryPage(LocalRecordStore<LocalRecord> store, RecordSyncService<LocalRecord> sync)
        {
            InitializeComponent();
            _store = store;
            _sync = sync;

            CategoryPicker.ItemsSource = Categories.Select(c => c.Name).ToList();
            CategoryPicker.SelectedIndex = 0;

            PayPicker.ItemsSource = PayMethods.ToList();
            PayPicker.SelectedIndex = 0;

            var now = DateTime.Now;
            DatePicker.Date = now.Date;
            TimePicker.Time = now.TimeOfDay;
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();

            // 先给用户看到本地数据（秒开，不等网络）
            await RefreshLocalAsync();
            await RefreshSyncStatusAsync();

            // 再在后台试一次同步（"到家了"就自动传）
            _ = SyncInBackgroundAsync();
        }

        // ============ 同步按钮 ============
        private async void OnSyncClicked(object sender, EventArgs e)
        {
            SyncBtn.IsEnabled = false;
            StatusLabel.Text = "正在同步……";
            try
            {
                var result = await _sync.SyncAsync();
                StatusLabel.Text = result.Message;
            }
            catch (Exception ex)
            {
                StatusLabel.Text = "同步失败：" + ex.Message;
            }
            finally
            {
                SyncBtn.IsEnabled = true;
            }

            await RefreshLocalAsync();
            await RefreshSyncStatusAsync();
        }

        // ============ 保存（只写本地） ============
        private async void OnSaveClicked(object sender, EventArgs e)
        {
            if (!double.TryParse(AmountEntry.Text, out double amount) || amount <= 0)
            {
                StatusLabel.Text = "请输入正确的金额（大于 0 的数字）";
                return;
            }

            SaveBtn.IsEnabled = false;
            try
            {
                // 消费时间 → Unix 毫秒
                var spentLocal = DateTime.SpecifyKind(
                    DatePicker.Date.Date + TimePicker.Time, DateTimeKind.Unspecified);
                long spentAtMs = new DateTimeOffset(
                    spentLocal, TimeZoneInfo.Local.GetUtcOffset(spentLocal))
                    .ToUnixTimeMilliseconds();

                var record = new LocalRecord
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Amount = amount,
                    CategoryId = Categories[CategoryPicker.SelectedIndex].Id,
                    Note = NoteEditor.Text ?? string.Empty,
                    SpentAt = spentAtMs,
                    PayMethod = PayPicker.SelectedItem as string ?? string.Empty,
                    CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Synced = false,
                    SyncedAt = 0,
                };

                await _store.InsertAsync(record);

                StatusLabel.Text = "已保存到本地。回家连上服务端后会自动同步。";

                // 清空，方便连续录入
                AmountEntry.Text = string.Empty;
                NoteEditor.Text = string.Empty;
                var now = DateTime.Now;
                DatePicker.Date = now.Date;
                TimePicker.Time = now.TimeOfDay;

                await RefreshLocalAsync();
                await RefreshSyncStatusAsync();

                // 顺手试一次同步（在局域网里就立刻传上去）
                _ = SyncInBackgroundAsync();
            }
            catch (Exception ex)
            {
                StatusLabel.Text = "保存失败：" + ex.Message;
            }
            finally
            {
                SaveBtn.IsEnabled = true;
            }
        }

        /// <summary>后台静默同步：不打扰用户，只在成功时刷新列表与状态。</summary>
        private async Task SyncInBackgroundAsync()
        {
            try
            {
                var result = await _sync.SyncAsync();

                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    if (result.UploadedRecords > 0)
                    {
                        await RefreshLocalAsync();
                    }
                    await RefreshSyncStatusAsync();
                });
            }
            catch
            {
                // 后台同步失败不弹错，保持"待同步"状态等下次触发。
            }
        }

        // ============ 从本地读今日账单 ============
        private async Task RefreshLocalAsync()
        {
            var local = await _store.GetByLocalDateAsync(DateTime.Now.Date);

            var rows = new List<RecordRow>();
            foreach (var r in local)
            {
                var localTime = DateTimeOffset.FromUnixTimeMilliseconds(r.SpentAt).LocalDateTime;
                var cat = Categories.FirstOrDefault(c => c.Id == r.CategoryId);
                string catName = !string.IsNullOrEmpty(cat.Name) ? cat.Name : "未分类";
                var pay = string.IsNullOrEmpty(r.PayMethod) ? "未选支付方式" : r.PayMethod;
                var note = string.IsNullOrEmpty(r.Note) ? string.Empty : " · " + r.Note;

                rows.Add(new RecordRow
                {
                    Title = $"{catName} · {pay}{note}",
                    Time = localTime.ToString("HH:mm"),
                    Amount = $"¥ {r.Amount:F2}",
                    SyncText = r.Synced ? "已同步" : "待同步",
                    SyncColor = r.Synced ? SyncedColor : PendingColor,
                });
            }

            TodayList.ItemsSource = rows;
        }

        // ============ 刷新同步状态条 ============
        private async Task RefreshSyncStatusAsync()
        {
            int pending = await _store.CountUnsyncedAsync();
            long last = _sync.LastSyncAtMs;

            SyncTitleLabel.Text = pending > 0 ? $"待同步 {pending} 条" : "全部已同步";
            SyncInfoLabel.Text = last > 0
                ? "最后同步 " + DateTimeOffset.FromUnixTimeMilliseconds(last).LocalDateTime.ToString("MM-dd HH:mm")
                : "尚未同步过（回家连网后自动上传）";
        }
    }

    /// <summary>今日账单列表的展示行（只用于界面绑定）。</summary>
    public class RecordRow
    {
        public string Title { get; set; } = string.Empty;
        public string Time { get; set; } = string.Empty;
        public string Amount { get; set; } = string.Empty;
        public string SyncText { get; set; } = string.Empty;
        public Color SyncColor { get; set; } = Colors.Transparent;
    }
}
