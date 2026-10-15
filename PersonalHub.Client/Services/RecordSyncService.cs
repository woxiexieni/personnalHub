using Grpc.Core;
using Microsoft.Maui.Networking;
using Microsoft.Maui.Storage;
using PersonalHub.Client.Data;

namespace PersonalHub.Client.Services
{
    /// <summary>一次同步的结果，供界面展示。</summary>
    public sealed class SyncResult
    {
        /// <summary>服务端是否可达（"到家了"的判定结果）。</summary>
        public bool Reachable { get; init; }

        /// <summary>本次成功同步的记录条数。</summary>
        public int UploadedRecords { get; init; }

        /// <summary>本次实际推送的"天"数。</summary>
        public int UploadedDays { get; init; }

        /// <summary>给用户看的一句话。</summary>
        public string Message { get; init; } = string.Empty;
    }

    /// <summary>
    /// 同步器：把本地"待同步"的账单按天打包，逐天推送到服务端。
    ///
    /// 触发时机（仅 App 前台场景）：
    ///   1. 记账页出现时（启动 / 切回该 Tab）；
    ///   2. 网络状态恢复时（Connectivity.ConnectivityChanged）；
    ///   3. 用户手动点"同步"。
    ///
    /// "到家了"如何判定：**可达即同步** —— 直接 Ping 服务端，通了就传。
    /// 不判断 SSID/网段，跨平台统一，将来接公网/Tailscale 也不用改。
    ///
    /// 幂等前提：服务端按 id 去重（见服务端 RecordStore.Upsert）。
    /// 因此本服务重复触发、同一天重复上传，都不会导致账单翻倍。
    /// </summary>
    public class RecordSyncService
    {
        private const string LastSyncKey = "ph.lastSyncAtMs";

        private readonly LocalRecordStore _store;
        private readonly ServerConnection _conn;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private bool _connectivityHooked;

        public RecordSyncService(LocalRecordStore store, ServerConnection conn)
        {
            _store = store;
            _conn = conn;
        }

        /// <summary>上一次同步成功的时间（Unix 毫秒），0 = 从未同步。</summary>
        public long LastSyncAtMs
        {
            get => Preferences.Default.Get(LastSyncKey, 0L);
            private set => Preferences.Default.Set(LastSyncKey, value);
        }

        /// <summary>
        /// 订阅网络恢复事件（幂等，只会挂一次）。由 App 启动时调用。
        /// </summary>
        public void Start()
        {
            if (_connectivityHooked) return;
            _connectivityHooked = true;

            try
            {
                Connectivity.Current.ConnectivityChanged += OnConnectivityChanged;
            }
            catch
            {
                // 个别平台不支持该事件，忽略即可，不影响 OnAppearing 触发。
            }
        }

        private void OnConnectivityChanged(object? sender, ConnectivityChangedEventArgs e)
        {
            // 家用 Wi-Fi 有时只给到 Local（无外网），两种都算"网络可用"。
            if (e.NetworkAccess is NetworkAccess.Internet or NetworkAccess.Local)
            {
                _ = SyncAsync();
            }
        }

        /// <summary>
        /// 执行一次同步。可被并发调用，内部用信号量保证同一时刻只有一次真正在跑。
        /// </summary>
        public async Task<SyncResult> SyncAsync()
        {
            // 拿不到锁 → 已有同步在跑，直接返回，不排队。
            if (!await _gate.WaitAsync(0).ConfigureAwait(false))
            {
                return new SyncResult { Reachable = true, Message = "已有同步任务正在进行" };
            }

            try
            {
                await _store.EnsureReadyAsync().ConfigureAwait(false);

                int pending = await _store.CountUnsyncedAsync().ConfigureAwait(false);
                if (pending == 0)
                {
                    return new SyncResult { Reachable = true, Message = "没有待同步的记录" };
                }

                // === 第一步：可达探测（"到家了"的判定）===
                // 先确保有通道；连不上就强制重新定位一次（PC 换 IP / 手机换网络都会让老地址失效）。
                await _conn.EnsureAsync().ConfigureAwait(false);

                var err = await TryPingAsync().ConfigureAwait(false);
                if (err != null && await _conn.EnsureAsync(force: true).ConfigureAwait(false))
                {
                    err = await TryPingAsync().ConfigureAwait(false);
                }

                if (err != null)
                {
                    var where = _conn.Address ?? "尚未定位到服务端";
                    return new SyncResult
                    {
                        Reachable = false,
                        Message = $"暂时连不上服务端（{where}），{pending} 条记录继续留在本地等待下次同步。"
                    };
                }

                // === 第二步：取未同步，按本地日期分组 ===
                var unsynced = await _store.GetUnsyncedAsync().ConfigureAwait(false);
                var groups = unsynced
                    .GroupBy(r => r.DayKey)
                    .OrderBy(g => g.Key, StringComparer.Ordinal)
                    .ToList();

                var client = _conn.NewRecordClient();
                int uploadedRecords = 0;
                int uploadedDays = 0;
                var notes = new List<string>();

                // === 第三步：逐天打包上传（一天一个请求）===
                foreach (var group in groups)
                {
                    var request = new Personalhub.UploadRecordsRequest();
                    foreach (var local in group)
                    {
                        request.Records.Add(ToProto(local));
                    }

                    try
                    {
                        var resp = await client
                            .UploadRecordsAsync(request, deadline: DateTime.UtcNow.AddSeconds(10))
                            .ConfigureAwait(false);

                        // 只有服务端确认接收的 id 才标记为已同步；被拒的继续留本地。
                        var okIds = group
                            .Select(x => x.Id)
                            .Where(id => !resp.FailedIds.Contains(id))
                            .ToList();

                        if (okIds.Count > 0)
                        {
                            await _store
                                .MarkSyncedAsync(okIds, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                                .ConfigureAwait(false);
                            uploadedRecords += okIds.Count;
                            uploadedDays++;
                        }

                        if (resp.FailedIds.Count > 0)
                        {
                            notes.Add($"{group.Key} 有 {resp.FailedIds.Count} 条被服务端拒绝");
                        }
                    }
                    catch (Exception ex)
                    {
                        notes.Add($"{group.Key} 上传失败：{ex.Message}");
                    }
                }

                if (uploadedRecords > 0)
                {
                    LastSyncAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                }

                string message = uploadedRecords > 0
                    ? $"已同步 {uploadedDays} 天 / {uploadedRecords} 条"
                    : "本次没有成功同步的记录";

                if (notes.Count > 0)
                {
                    message += "；" + string.Join("；", notes);
                }

                return new SyncResult
                {
                    Reachable = true,
                    UploadedRecords = uploadedRecords,
                    UploadedDays = uploadedDays,
                    Message = message
                };
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>对当前通道发一次短超时 Ping；成功返回 null，失败返回异常。</summary>
        private async Task<Exception?> TryPingAsync()
        {
            try
            {
                await _conn.NewSystemClient()
                    .PingAsync(new Personalhub.PingRequest(), deadline: DateTime.UtcNow.AddSeconds(2))
                    .ConfigureAwait(false);
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        private static Personalhub.Record ToProto(LocalRecord r) => new Personalhub.Record
        {
            Id = r.Id,
            Amount = r.Amount,
            CategoryId = r.CategoryId,
            Note = r.Note,
            SpentAt = r.SpentAt,
            PayMethod = r.PayMethod,
            CreatedAt = r.CreatedAt,
        };
    }
}
