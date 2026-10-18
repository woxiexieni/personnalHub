using PersonalHub.Client.Data;
using PersonalHub.Client.Services;

namespace PersonalHub.Client
{
    /// <summary>
    /// 账单录入页（离线优先）。
    ///
    /// 数据流（三条，别混）：
    ///   1. 保存 → 只写本地 SQLite（Synced=false）→ 界面立刻刷新，不等网络；
    ///   2. 今日总额 → 先本地 SQL 算（0ms、含未同步），再用服务端 GetTodaySummary 覆盖校正；
    ///   3. 同步 → 上传待同步（含删除墓碑）→ 拉 30 天镜像 → 清理超窗口副本。
    ///
    /// 编辑窗口的规则（需求 v1.0 第 4.4 节）：
    ///   **今天（含已同步）可以改/删；昨天及更早只读，去电脑端改。**
    ///   判定"今天"用固定 UTC+8，不用设备时区。
    /// </summary>
    public partial class RecordEntryPage : ContentPage
    {
        /// <summary>支付方式仍是固定列表（不在本轮改造范围）。</summary>
        private static readonly string[] PayMethods = { "微信", "支付宝", "现金", "银行卡" };

        private static readonly Color PendingColor = Color.FromArgb("#D97706");
        private static readonly Color SyncedColor = Color.FromArgb("#8A8A8E");
        private static readonly Color ChipBg = Color.FromArgb("#F1EFE8");
        private static readonly Color ChipText = Color.FromArgb("#2C2C2A");
        private static readonly Color ChipActiveBg = Color.FromArgb("#185FA5");

        private readonly LocalRecordStore<LocalRecord> _store;
        private readonly RecordSyncService<LocalRecord> _sync;
        private readonly LocalCacheService _cache;

        private IReadOnlyList<CategoryDto> _categories = Array.Empty<CategoryDto>();
        private bool _categoriesLoaded;

        private int _direction;          // 0 支出 / 1 收入
        private int _selectedCategoryId;
        private string? _editingId;      // 非空 = 正在修改这条今日记录

        public RecordEntryPage(
            LocalRecordStore<LocalRecord> store,
            RecordSyncService<LocalRecord> sync,
            LocalCacheService cache)
        {
            InitializeComponent();
            _store = store;
            _sync = sync;
            _cache = cache;

            PayPicker.ItemsSource = PayMethods.ToList();
            PayPicker.SelectedIndex = 0;

            ResetTimeToNow();
            ApplyDirectionStyle();
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();

            // 1) 分类：先用兜底渲染（离线也能记），拿到服务端的再重建
            if (!_categoriesLoaded)
            {
                _categories = LocalCacheService.FallbackCategories;
                BuildCategoryChips();
            }

            // 2) 今日总额：本地即时值（不等网络）
            await RefreshTodayAsync();

            // 3) 拿服务端分类 + 今日校正值（失败就保持本地值）
            await LoadCategoriesAsync();
            await RefreshTodayFromServerAsync();
            await RefreshTodayListAsync();

            // 4) 后台把待同步传上去，再对齐 30 天镜像
            _ = SyncInBackgroundAsync();
        }

        // ============================================================
        // 分类
        // ============================================================

        private async Task LoadCategoriesAsync()
        {
            var list = await _cache.GetCategoriesAsync();
            if (list.Count == 0) return;

            // 内容没变就不重建（重建会让已选中的高亮闪一下）
            bool changed = list.Count != _categories.Count ||
                           list.Where(c => c.Enabled).Select(c => c.Id)
                               .Except(_categories.Where(c => c.Enabled).Select(c => c.Id)).Any();

            _categories = list;
            _categoriesLoaded = true;

            if (changed || CategoryChips.Children.Count == 0)
            {
                BuildCategoryChips();
            }
        }

        private void BuildCategoryChips()
        {
            CategoryChips.Children.Clear();

            var items = _categories
                .Where(c => c.Enabled && c.Direction == _direction)
                .OrderBy(c => c.SortOrder)
                .ThenBy(c => c.Id)
                .ToList();

            // 当前选中的分类不在这个方向里了（比如刚从支出切到收入）→ 默认选第一个
            if (items.Count > 0 && items.All(c => c.Id != _selectedCategoryId))
            {
                _selectedCategoryId = items[0].Id;
            }

            foreach (var c in items)
            {
                var btn = new Button
                {
                    Text = c.Name,
                    FontSize = 13,
                    HeightRequest = 36,
                    Padding = new Thickness(14, 0),
                    CornerRadius = 18,
                    Margin = new Thickness(0, 0, 8, 8),
                    CommandParameter = c.Id,
                    BackgroundColor = c.Id == _selectedCategoryId ? ChipActiveBg : ChipBg,
                    TextColor = c.Id == _selectedCategoryId ? Colors.White : ChipText,
                };
                btn.Clicked += OnCategoryChipClicked;
                CategoryChips.Children.Add(btn);
            }
        }

        private void OnCategoryChipClicked(object? sender, EventArgs e)
        {
            if (sender is Button b && b.CommandParameter is int id)
            {
                _selectedCategoryId = id;
                StyleChips();
            }
        }

        private void StyleChips()
        {
            foreach (var child in CategoryChips.Children)
            {
                if (child is not Button b || b.CommandParameter is not int id) continue;

                bool active = id == _selectedCategoryId;
                b.BackgroundColor = active ? ChipActiveBg : ChipBg;
                b.TextColor = active ? Colors.White : ChipText;
            }
        }

        // ============================================================
        // 支出 / 收入
        // ============================================================

        private void OnExpenseTabClicked(object sender, EventArgs e) => SwitchDirection(0);

        private void OnIncomeTabClicked(object sender, EventArgs e) => SwitchDirection(1);

        private void SwitchDirection(int direction)
        {
            if (_direction == direction) return;

            _direction = direction;
            _selectedCategoryId = 0;      // 强制回落到该方向的第一个分类
            ApplyDirectionStyle();
            BuildCategoryChips();
        }

        private void ApplyDirectionStyle()
        {
            bool expense = _direction == 0;

            ExpenseTabBtn.BackgroundColor = expense ? ChipActiveBg : ChipBg;
            ExpenseTabBtn.TextColor = expense ? Colors.White : ChipText;
            IncomeTabBtn.BackgroundColor = expense ? ChipBg : ChipActiveBg;
            IncomeTabBtn.TextColor = expense ? ChipText : Colors.White;
        }

        // ============================================================
        // 今日总额
        // ============================================================

        /// <summary>
        /// 本地即时值。**这一步不等网络** —— 记完一笔要立刻看到数字变了，
        /// 否则"记了没反应"会让人再记一次（然后账就重了）。
        /// </summary>
        private async Task RefreshTodayAsync()
        {
            var (from, to) = Period.Today();

            long expense = await _store.SumExpenseCentsAsync(from, to);
            long income = await _store.SumIncomeCentsAsync(from, to);
            int count = await _store.CountExpenseAsync(from, to);
            int pending = await _store.CountPendingAsync(from, to);

            RenderToday(expense, income, count, pending);
        }

        /// <summary>服务端校正值：补上电脑端补录 / 另一台设备刚记的，直接覆盖，不弹窗。</summary>
        private async Task RefreshTodayFromServerAsync()
        {
            var server = await _cache.GetTodaySummaryAsync();
            if (server is null) return;

            var (_, to) = Period.Today();
            int pending = await _store.CountPendingAsync(Period.Today().From, to);

            RenderToday(server.ExpenseCents, server.IncomeCents, server.ExpenseCount, pending);
        }

        private void RenderToday(long expenseCents, long incomeCents, int count, int pending)
        {
            TodayTotalLabel.Text = Money(expenseCents);
            TodaySubLabel.Text = pending > 0
                ? $"{count} 笔 · {pending} 笔待同步"
                : $"{count} 笔 · 已全部同步";

            TodayIncomeLabel.Text = "收入 " + Money(incomeCents);
            SyncStateLabel.Text = "今天 " + DateTimeOffset.UtcNow.ToOffset(Period.Cst).ToString("MM-dd");
        }

        private static string Money(long cents) => "¥ " + (cents / 100.0).ToString("F2");

        // ============================================================
        // 今日列表
        // ============================================================

        private async Task RefreshTodayListAsync()
        {
            var (from, to) = Period.Today();
            var records = await _store.GetByRangeAsync(from, to);

            var rows = records.Select(r => new RecordRow
            {
                Id = r.Id,
                Title = BuildTitle(r),
                Time = Period.ToLocalTime(r.SpentAt).ToString("HH:mm"),
                Amount = (r.Direction == 1 ? "+ " : "") + Money((long)Math.Round(r.Amount * 100)),
                SyncText = r.Synced ? "已同步" : "待同步",
                SyncColor = r.Synced ? SyncedColor : PendingColor,
                // 只有"今天"能改；判定用固定 UTC+8，与服务端一致
                CanEdit = Period.IsToday(r.SpentAt),
            }).ToList();

            TodayList.ItemsSource = rows;
        }

        private string BuildTitle(LocalRecord r)
        {
            var cat = _categories.FirstOrDefault(c => c.Id == r.CategoryId);
            string catName = cat is null ? $"分类{r.CategoryId}" : cat.Name;

            var parts = new List<string> { catName };
            if (!string.IsNullOrWhiteSpace(r.PayMethod)) parts.Add(r.PayMethod);
            if (!string.IsNullOrWhiteSpace(r.Note)) parts.Add(r.Note);

            return string.Join(" · ", parts);
        }

        // ============================================================
        // 保存 / 修改
        // ============================================================

        private async void OnSaveClicked(object sender, EventArgs e)
        {
            if (!TryParseAmount(out double amount))
            {
                StatusLabel.Text = "金额需大于 0，且最多两位小数";
                return;
            }

            if (_selectedCategoryId <= 0)
            {
                StatusLabel.Text = "请先选择一个分类";
                return;
            }

            SaveBtn.IsEnabled = false;
            try
            {
                long spentAtMs = BuildSpentAtMs();
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                if (_editingId is not null)
                {
                    var existing = await _store.FindAsync(_editingId);
                    if (existing is null)
                    {
                        ExitEditMode();
                        StatusLabel.Text = "这条记录已经不在了，请刷新后重试";
                        return;
                    }

                    existing.Amount = amount;
                    existing.CategoryId = _selectedCategoryId;
                    existing.Direction = _direction;
                    existing.Note = NoteEntry.Text ?? string.Empty;
                    existing.SpentAt = spentAtMs;
                    existing.PayMethod = PayPicker.SelectedItem as string ?? string.Empty;
                    existing.UpdatedAt = now;
                    existing.Synced = false;      // 改了就要重新上传

                    await _store.UpdateAsync(existing);
                    StatusLabel.Text = "已修改，正在上传……";
                }
                else
                {
                    var record = new LocalRecord
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Amount = amount,
                        CategoryId = _selectedCategoryId,
                        Direction = _direction,
                        Note = NoteEntry.Text ?? string.Empty,
                        SpentAt = spentAtMs,
                        PayMethod = PayPicker.SelectedItem as string ?? string.Empty,
                        CreatedAt = now,
                        UpdatedAt = now,
                        Deleted = false,
                        Synced = false,
                        SyncedAt = 0,
                    };

                    await _store.InsertAsync(record);
                    StatusLabel.Text = "已保存到本地，正在上传……";
                }

                ExitEditMode();
                ResetForm();

                await RefreshTodayAsync();
                await RefreshTodayListAsync();

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

        private bool TryParseAmount(out double amount)
        {
            amount = 0;
            var text = (AmountEntry.Text ?? string.Empty).Trim();
            if (!double.TryParse(text, out amount)) return false;
            if (amount <= 0) return false;

            // 与本地库的取整规则对齐：多于两位小数会被静默改数，不如在这里拦住
            double scaled = amount * 100;
            return Math.Abs(scaled - Math.Round(scaled)) < 1e-6;
        }

        /// <summary>日期 + 时间 → Unix 毫秒。这里按设备本地时区换算"这一刻"，归属天由服务端/Period 决定。</summary>
        private long BuildSpentAtMs()
        {
            var local = DateTime.SpecifyKind(DatePicker.Date.Date + TimePicker.Time, DateTimeKind.Unspecified);
            return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)).ToUnixTimeMilliseconds();
        }

        private async void OnEditRowClicked(object? sender, EventArgs e)
        {
            if (sender is not Button b || b.CommandParameter is not string id) return;

            var row = await _store.FindAsync(id);
            if (row is null) return;

            if (!Period.IsToday(row.SpentAt))
            {
                StatusLabel.Text = "只有今天的记录可以在手机上修改，更早的记录请到电脑端改";
                return;
            }

            _editingId = row.Id;
            _direction = row.Direction;
            _selectedCategoryId = row.CategoryId;

            ApplyDirectionStyle();
            BuildCategoryChips();

            AmountEntry.Text = row.Amount.ToString("F2");
            NoteEntry.Text = row.Note;
            PayPicker.SelectedItem = PayMethods.Contains(row.PayMethod) ? row.PayMethod : PayMethods[0];

            var local = Period.ToLocalTime(row.SpentAt);
            DatePicker.Date = local.Date;
            TimePicker.Time = local.TimeOfDay;

            SaveBtn.Text = "保存修改";
            CancelEditBtn.IsVisible = true;
            StatusLabel.Text = "正在修改这条记录";
        }

        private void OnCancelEditClicked(object sender, EventArgs e)
        {
            ExitEditMode();
            ResetForm();
            StatusLabel.Text = "已取消修改";
        }

        private void ExitEditMode()
        {
            _editingId = null;
            SaveBtn.Text = "保存";
            CancelEditBtn.IsVisible = false;
        }

        private void ResetForm()
        {
            AmountEntry.Text = string.Empty;
            NoteEntry.Text = string.Empty;
            ResetTimeToNow();
        }

        private void ResetTimeToNow()
        {
            var now = DateTime.Now;
            DatePicker.Date = now.Date;
            TimePicker.Time = now.TimeOfDay;
        }

        // ============================================================
        // 删除
        // ============================================================

        private async void OnDeleteRowClicked(object? sender, EventArgs e)
        {
            if (sender is not Button b || b.CommandParameter is not string id) return;

            var row = await _store.FindAsync(id);
            if (row is null) return;

            if (!Period.IsToday(row.SpentAt))
            {
                StatusLabel.Text = "只有今天的记录可以在手机上删除，更早的记录请到电脑端删";
                return;
            }

            bool ok = await DisplayAlert("删除这条记录？",
                $"{BuildTitle(row)}　{Money((long)Math.Round(row.Amount * 100))}",
                "删除", "取消");
            if (!ok) return;

            if (row.Synced)
            {
                // 已同步：留一个墓碑，让服务端也删掉。直接本地删会被下次镜像同步拉回来。
                row.Deleted = true;
                row.Synced = false;
                row.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                await _store.UpdateAsync(row);
                StatusLabel.Text = "已删除，正在同步到服务端……";
            }
            else
            {
                // 从没同步过：服务端根本没有它，本地物理删掉最干净
                await _store.DeleteAsync(row.Id);
                StatusLabel.Text = "已删除（这条尚未上传，本地直接移除）";
            }

            await RefreshTodayAsync();
            await RefreshTodayListAsync();

            _ = SyncInBackgroundAsync();
        }

        // ============================================================
        // 同步
        // ============================================================

        private async void OnSyncClicked(object sender, EventArgs e)
        {
            SyncBtn.IsEnabled = false;
            StatusLabel.Text = "正在同步……";
            try
            {
                var result = await _sync.SyncAsync();
                var mirror = await _cache.MirrorAsync();

                StatusLabel.Text = result.Message + "；" + mirror.Message;

                await RefreshTodayAsync();
                await RefreshTodayFromServerAsync();
                await RefreshTodayListAsync();
            }
            catch (Exception ex)
            {
                StatusLabel.Text = "同步失败：" + ex.Message;
            }
            finally
            {
                SyncBtn.IsEnabled = true;
            }
        }

        /// <summary>
        /// 后台静默：先上传待同步，再对齐 30 天镜像。
        ///
        /// 顺序不能反 —— 先镜像会把"本地刚改、还没上传"的行当成过期数据吗？
        /// 不会（Synced=false 的行永远保留），但先上传能让镜像立刻看到刚传上去的记录，
        /// 少一次"本地有、服务端没有"的中间状态。
        /// </summary>
        private async Task SyncInBackgroundAsync()
        {
            try
            {
                await _sync.SyncAsync();
                await _cache.MirrorAsync();

                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    await RefreshTodayAsync();
                    await RefreshTodayListAsync();
                });
            }
            catch
            {
                // 后台失败不弹错：保持"待同步"状态，等下次进页面 / 网络恢复再试。
            }
        }
    }

    /// <summary>今日账单列表的展示行（只用于界面绑定）。</summary>
    public class RecordRow
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Time { get; set; } = string.Empty;
        public string Amount { get; set; } = string.Empty;
        public string SyncText { get; set; } = string.Empty;
        public Color SyncColor { get; set; } = Colors.Transparent;
        public bool CanEdit { get; set; }
    }
}
