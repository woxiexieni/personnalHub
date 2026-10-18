using PersonalHub.Client.Data;
using PersonalHub.Client.Services;

namespace PersonalHub.Client
{
    /// <summary>
    /// 事件反思记录页（本地优先）。
    ///
    /// 数据流与记账页完全一致，只是换了一套本地模型与 proto：
    ///   保存  → 只写本地 SQLite（ThingRecord，Synced=false），不依赖网络；
    ///   列表  → 一律从本地读，只看"事件发生日 = 今天"的记录；
    ///   同步  → RecordSyncService&lt;ThingRecord&gt; 把待同步记录按天打包上传。
    ///
    /// 同步逻辑与记账页共用同一个 RecordSyncService&lt;T&gt;，差异全在 ThingUploader 里。
    /// </summary>
    public partial class RecordDothingPage : ContentPage
    {
        private readonly LocalRecordStore<ThingRecord> _store;
        private readonly RecordSyncService<ThingRecord> _sync;

        /// <summary>事件标签（顺序即 Picker 顺序）。传上去存的是文字，便于以后直接加项、不用改服务端枚举。</summary>
        private static readonly string[] Tags =
        {
            "工作", "人际", "技术学习", "情绪内耗", "生活", "其他",
        };

        /// <summary>事发时情绪。</summary>
        private static readonly string[] Emotions =
        {
            "平静", "焦虑", "烦躁", "激动", "委屈", "兴奋", "失落",
        };

        private static readonly Color PendingColor = Color.FromArgb("#D97706");
        private static readonly Color SyncedColor = Color.FromArgb("#8A8A8E");

        public RecordDothingPage(LocalRecordStore<ThingRecord> store, RecordSyncService<ThingRecord> sync)
        {
            InitializeComponent();
            _store = store;
            _sync = sync;

            TagPicker.ItemsSource = Tags.ToList();
            EmotionPicker.ItemsSource = Emotions.ToList();

            var now = DateTime.Now;
            EventDatePicker.Date = now.Date;
            EventTimePicker.Time = now.TimeOfDay;
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();

            // 先展示本地数据（秒开，不等网络），再后台试一次同步
            await RefreshLocalAsync();
            await RefreshSyncStatusAsync();

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
            // ---- 必填校验：一次只提示第一个问题，避免一屏红字 ----
            string? problem = ValidateInputs();
            if (problem != null)
            {
                StatusLabel.Text = problem;
                return;
            }

            SaveBtn.IsEnabled = false;
            try
            {
                // 事件发生时间 → Unix 毫秒（按本地时区换算，和记账页同一套算法）
                var occurredLocal = DateTime.SpecifyKind(
                    EventDatePicker.Date.Date + EventTimePicker.Time, DateTimeKind.Unspecified);
                long occurredAtMs = new DateTimeOffset(
                    occurredLocal, TimeZoneInfo.Local.GetUtcOffset(occurredLocal))
                    .ToUnixTimeMilliseconds();

                var thing = new ThingRecord
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Title = TitleEntry.Text!.Trim(),
                    Tag = Tags[TagPicker.SelectedIndex],
                    Emotion = Emotions[EmotionPicker.SelectedIndex],
                    OccurredAt = occurredAtMs,
                    ObjectiveDescription = FactEditor.Text!.Trim(),
                    InitialThought = ThoughtEditor.Text!.Trim(),
                    ActionTaken = ActionEditor.Text!.Trim(),
                    FinalResult = ResultEditor.Text!.Trim(),
                    Reflection = ReviewEditor.Text!.Trim(),
                    ActionPlan = PlanEditor.Text!.Trim(),
                    CreatedTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Synced = false,
                    SyncedAt = 0,
                };

                await _store.InsertAsync(thing);

                StatusLabel.Text = "已保存到本地。回家连上服务端后会自动同步。";

                // 清空正文，方便连续录入（标签/情绪保留上次选择）
                TitleEntry.Text = string.Empty;
                FactEditor.Text = string.Empty;
                ThoughtEditor.Text = string.Empty;
                ActionEditor.Text = string.Empty;
                ResultEditor.Text = string.Empty;
                ReviewEditor.Text = string.Empty;
                PlanEditor.Text = string.Empty;

                var now = DateTime.Now;
                EventDatePicker.Date = now.Date;
                EventTimePicker.Time = now.TimeOfDay;

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

        /// <summary>返回第一个没填好的字段提示；全部合法返回 null。</summary>
        private string? ValidateInputs()
        {
            if (string.IsNullOrWhiteSpace(TitleEntry.Text))
                return "请填写事件标题";

            if (TagPicker.SelectedIndex < 0)
                return "请选择事件标签";

            if (EmotionPicker.SelectedIndex < 0)
                return "请选择事发时的情绪";

            if (string.IsNullOrWhiteSpace(FactEditor.Text))
                return "请填写事件简述（客观事实）";

            if (string.IsNullOrWhiteSpace(ThoughtEditor.Text))
                return "请填写事发那一刻的想法";

            if (string.IsNullOrWhiteSpace(ActionEditor.Text))
                return "请填写当时采取的行动";

            if (string.IsNullOrWhiteSpace(ResultEditor.Text))
                return "请填写事件最终结果";

            if (string.IsNullOrWhiteSpace(ReviewEditor.Text))
                return "请填写复盘反思";

            if (string.IsNullOrWhiteSpace(PlanEditor.Text))
                return "请填写后续行动计划";

            return null;
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

        // ============ 从本地读今日事件 ============
        private async Task RefreshLocalAsync()
        {
            var local = await _store.GetByLocalDateAsync(DateTime.Now.Date);

            var rows = new List<ThingRow>();
            foreach (var r in local)
            {
                var localTime = DateTimeOffset.FromUnixTimeMilliseconds(r.OccurredAt).LocalDateTime;

                rows.Add(new ThingRow
                {
                    Title = string.IsNullOrWhiteSpace(r.Title) ? "(无标题)" : r.Title,
                    EventTime = localTime.ToString("HH:mm"),
                    Tag = string.IsNullOrWhiteSpace(r.Tag) ? "未分类" : r.Tag,
                    SyncText = r.Synced ? "已同步" : "待同步",
                    SyncColor = r.Synced ? SyncedColor : PendingColor,
                });
            }

            TodayReflectList.ItemsSource = rows;
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

    /// <summary>今日事件列表的展示行（只用于界面绑定）。</summary>
    public class ThingRow
    {
        public string Title { get; set; } = string.Empty;
        public string EventTime { get; set; } = string.Empty;
        public string Tag { get; set; } = string.Empty;
        public string SyncText { get; set; } = string.Empty;
        public Color SyncColor { get; set; } = Colors.Transparent;
    }
}
