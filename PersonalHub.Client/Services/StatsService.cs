using Personalhub;

namespace PersonalHub.Client.Services
{
    /// <summary>分类构成（金额单位：分）。</summary>
    public sealed record CategorySliceDto(
        int CategoryId, string Name, string Icon,
        long AmountCents, double Ratio, int Count,
        long PrevAmountCents, double DeltaRatio, bool HasPrev);

    /// <summary>支付方式构成（金额单位：分）。</summary>
    public sealed record PayMethodSliceDto(string Name, long AmountCents, double Ratio, int Count);

    /// <summary>一页统计所需的全部数据（金额单位：分）。</summary>
    public sealed record StructureDto(
        string Period,
        long RangeStart, long RangeEnd,
        long ExpenseCents, long IncomeCents, int ExpenseCount, long DailyAvgCents,
        long PrevExpenseCents, double DeltaRatio, bool HasPrev,
        long PrevRangeStart, long PrevRangeEnd, long GeneratedAt,
        IReadOnlyList<CategorySliceDto> Categories,
        IReadOnlyList<PayMethodSliceDto> PayMethods);

    /// <summary>一次取数的结果：<see cref="IsStale"/> = true 表示这是上次的缓存（当前没连上服务端）。</summary>
    public sealed record StatsFetch(StructureDto? Data, string? Error, bool IsStale);

    /// <summary>
    /// 统计页的数据来源。
    ///
    /// 三个刻意为之的选择：
    ///  1. **只传档位，不传时间区间**。区间由服务端算并回传，否则手机和网页各算一遍，
    ///     迟早出现"手机说本月 3200、网页说 3100"，而且两边都能自圆其说。
    ///  2. **按档位缓存上一次结果**（内存，随进程消失）。断网时显示缓存 + 明确的"离线"标识，
    ///     不做假装的实时。不落盘是因为这些数字随记账随时变，缓存过期了反而误导。
    ///  3. **失败不抛异常**。统计页失败就退化成空态/缓存，不应该让页面崩。
    /// </summary>
    public sealed class StatsService
    {
        private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(8);
        private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

        private readonly ServerConnection _conn;
        private readonly Dictionary<string, (StructureDto Data, DateTime At)> _cache = new();
        private readonly SemaphoreSlim _gate = new(1, 1);

        public StatsService(ServerConnection conn)
        {
            _conn = conn;
        }

        public async Task<StatsFetch> GetAsync(string period, bool force = false, CancellationToken ct = default)
        {
            // 同一时刻只允许一个请求：切档位连点时，后面的会等前一个结束再判断缓存
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!force && _cache.TryGetValue(period, out var hit) &&
                    DateTime.UtcNow - hit.At < CacheTtl)
                {
                    return new StatsFetch(hit.Data, null, false);
                }

                // force 只表示"用户手动刷新"，顺带允许重新扫描一次地址；
                // 平时切档位不该每次都重扫局域网，那会把 0.1 秒的操作变成 2 秒。
                bool ok = await _conn.EnsureAsync().ConfigureAwait(false);
                if (!ok && force) ok = await _conn.EnsureAsync(force: true).ConfigureAwait(false);
                if (!ok)
                {
                    return Offline(period, "连不上服务端");
                }

                var r = await _conn.NewAnalysisClient()
                    .GetSpendingStructureAsync(
                        new StructureRequest { Period = period },
                        deadline: DateTime.UtcNow.Add(CallTimeout),
                        cancellationToken: ct)
                    .ConfigureAwait(false);

                var dto = new StructureDto(
                    r.Period,
                    r.RangeStart, r.RangeEnd,
                    r.ExpenseCents, r.IncomeCents, r.ExpenseCount, r.DailyAvgCents,
                    r.PrevExpenseCents, r.DeltaRatio, r.HasPrev,
                    r.PrevRangeStart, r.PrevRangeEnd, r.GeneratedAt,
                    r.Categories.Select(c => new CategorySliceDto(
                        c.CategoryId, c.Name, c.Icon, c.AmountCents, c.Ratio, c.Count,
                        c.PrevAmountCents, c.DeltaRatio, c.HasPrev)).ToList(),
                    r.PayMethods.Select(p => new PayMethodSliceDto(
                        p.Name, p.AmountCents, p.Ratio, p.Count)).ToList());

                _cache[period] = (dto, DateTime.UtcNow);
                return new StatsFetch(dto, null, false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Offline(period, ex.Message);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>失败时尽量给缓存，而不是一片空白。</summary>
        private StatsFetch Offline(string period, string error)
            => _cache.TryGetValue(period, out var hit)
                ? new StatsFetch(hit.Data, error, true)
                : new StatsFetch(null, error, false);

        /// <summary>服务端地址（用于在页面上显示"数据来自哪台机器"）。</summary>
        public string? Address => _conn.Address;
    }
}
