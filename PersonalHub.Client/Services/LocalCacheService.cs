using PersonalHub.Client.Data;
using Personalhub;

namespace PersonalHub.Client.Services
{
    /// <summary>一次镜像同步的结果（给界面看的一句话 + 便于自测的数字）。</summary>
    public sealed record MirrorResult(bool Reachable, int Applied, int Removed, int Pruned, string Message);

    /// <summary>今日汇总（单位：分）。</summary>
    public sealed record TodaySummaryDto(long ExpenseCents, long IncomeCents, int ExpenseCount, long From, long To);

    /// <summary>分类项（服务端下发）。</summary>
    public sealed record CategoryDto(int Id, string Name, string Icon, int Direction, int SortOrder, bool Enabled);

    /// <summary>
    /// 服务端数据的本地缓存层：30 天镜像 + 今日汇总 + 分类。
    ///
    /// 这里刻意**不做上传**（那是 <see cref="RecordSyncService{T}"/> 的活），
    /// 只做"从服务端拉下来，把本地副本对齐"：
    ///   拉 30 天全量（含墓碑）→ 覆盖已同步行 → 删掉本地多余行 → 清理超窗口副本。
    ///
    /// 为什么不用增量同步：一个月最多几百条、不到 100KB。全量镜像简单、幂等、能自愈，
    /// 不需要增量游标，也不需要冲突合并。个人单用户局域网场景，复杂度花在这里不划算。
    /// </summary>
    public sealed class LocalCacheService
    {
        /// <summary>本地保留的自然日天数（含今天）。</summary>
        public const int WindowDays = 30;

        private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);

        private readonly ServerConnection _conn;
        private readonly Data.LocalRecordStore<Data.LocalRecord> _store;
        private readonly SemaphoreSlim _gate = new(1, 1);

        public LocalCacheService(ServerConnection conn, Data.LocalRecordStore<Data.LocalRecord> store)
        {
            _conn = conn;
            _store = store;
        }

        /// <summary>
        /// 拉最近 30 天做镜像覆盖，然后清理超窗口的已同步副本。
        /// 可被并发调用，内部只允许一个真正在跑（第二个直接返回，不排队）。
        /// </summary>
        public async Task<MirrorResult> MirrorAsync(CancellationToken ct = default)
        {
            if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false))
            {
                return new MirrorResult(true, 0, 0, 0, "已有镜像任务在进行");
            }

            try
            {
                if (!await _conn.EnsureAsync().ConfigureAwait(false))
                {
                    return new MirrorResult(false, 0, 0, 0, "暂时连不上服务端，本地保留最近 30 天的数据");
                }

                var (from, to) = Period.LastDays(WindowDays);

                var resp = await _conn.NewRecordClient()
                    .ListRecordsAsync(
                        new ListRecordsRequest
                        {
                            From = from,
                            To = to,
                            Direction = -1,
                            // 必须显式 true：镜像要靠"墓碑"把本地已同步的副本删掉
                            IncludeDeleted = true,
                        },
                        deadline: DateTime.UtcNow.Add(CallTimeout),
                        cancellationToken: ct)
                    .ConfigureAwait(false);

                var stats = await _store.MergeServerWindowAsync(resp.Records, from, to).ConfigureAwait(false);

                // 窗口是"最近 30 天"，所以 cutoff 是今天往前 30 天
                long cutoff = Period.StartOfDay(DateTimeOffset.UtcNow)
                    .AddDays(-WindowDays)
                    .ToUnixTimeMilliseconds();
                int pruned = await _store.PruneLocalCacheAsync(cutoff).ConfigureAwait(false);

                return new MirrorResult(true, stats.Applied, stats.Removed, pruned,
                    $"已对齐最近 {WindowDays} 天（更新 {stats.Applied}，移除 {stats.Removed}，清理 {pruned}）");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new MirrorResult(false, 0, 0, 0, "镜像同步失败：" + ex.Message);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>服务端算的今日汇总。失败返回 null（调用方保持本地值，不显示错误）。</summary>
        public async Task<TodaySummaryDto?> GetTodaySummaryAsync(CancellationToken ct = default)
        {
            try
            {
                if (!await _conn.EnsureAsync().ConfigureAwait(false)) return null;

                var r = await _conn.NewAnalysisClient()
                    .GetTodaySummaryAsync(
                        new Empty(),
                        deadline: DateTime.UtcNow.Add(CallTimeout),
                        cancellationToken: ct)
                    .ConfigureAwait(false);

                return new TodaySummaryDto(r.ExpenseCents, r.IncomeCents, r.ExpenseCount, r.RangeStart, r.RangeEnd);
            }
            catch
            {
                // 今日总额的校正失败不该打扰用户：本地那个数字本来就是可用的
                return null;
            }
        }

        /// <summary>
        /// 分类列表。拿不到时返回内置兜底，保证录入页在离线状态下依然能用。
        /// </summary>
        public async Task<IReadOnlyList<CategoryDto>> GetCategoriesAsync(CancellationToken ct = default)
        {
            try
            {
                if (await _conn.EnsureAsync().ConfigureAwait(false))
                {
                    var resp = await _conn.NewCategoryClient()
                        .GetCategoriesAsync(
                            new Empty(),
                            deadline: DateTime.UtcNow.Add(CallTimeout),
                            cancellationToken: ct)
                        .ConfigureAwait(false);

                    if (resp.Categories.Count > 0)
                    {
                        return resp.Categories
                            .Select(c => new CategoryDto(c.Id, c.Name, c.Icon, c.Direction, c.SortOrder, c.Enabled))
                            .ToList();
                    }
                }
            }
            catch
            {
                // 落到下面的内置兜底
            }

            return FallbackCategories;
        }

        /// <summary>
        /// 内置兜底分类。**只用于离线**，正常路径一律用服务端下发的。
        /// id 必须和服务端 seed 一致，否则离线记的账联网后会因为"分类不存在"被拒收。
        /// </summary>
        public static readonly IReadOnlyList<CategoryDto> FallbackCategories = new List<CategoryDto>
        {
            new(1,   "餐饮",     "", 0, 10, true),
            new(2,   "交通",     "", 0, 20, true),
            new(3,   "购物",     "", 0, 30, true),
            new(4,   "居住",     "", 0, 40, true),
            new(5,   "娱乐",     "", 0, 50, true),
            new(6,   "医疗",     "", 0, 60, true),
            new(7,   "其他",     "", 0, 99, true),
            new(101, "工资",     "", 1, 10, true),
            new(102, "奖金",     "", 1, 20, true),
            new(103, "理财收益", "", 1, 30, true),
            new(104, "退款",     "", 1, 40, true),
            new(105, "其他收入", "", 1, 99, true),
        };
    }
}
