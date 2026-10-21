using Personalhub;
using PersonalHub.Client.Data;

namespace PersonalHub.Client.Services
{
    /// <summary>一次「把服务端的想法对齐到本机」的结果。</summary>
    public sealed record IdeaPull(bool Reachable, int Added, int Updated, int RemoteTotal)
    {
        public bool Ok => Reachable;
    }

    /// <summary>
    /// 想法库的「服务端 → 本机」对齐。
    ///
    /// 为什么想法需要这个，而账单/时间块不需要：
    ///   账单、时间块有 <see cref="LocalCacheService"/> / <see cref="BlockMirrorService"/>，
    ///   每次进页面都会把服务端最近 30 天镜像回本地 —— 所以换机、重装之后它们自己会长回来。
    ///   **想法没有镜像**，以前只有"上传"一条路，于是本机一旦清空，服务端那一份就永远看不见了。
    ///   这不是"想法没上传"，是"上传了但没人把它拿回来"。
    ///
    /// 和那两个镜像的区别（三条）：
    ///   1. **不裁时间窗**。账单只保留 30 天是对的（看的是近期），想法是长期攒的资产，
    ///      三个月前那句"我好像每次都…"才是最值钱的那种。
    ///   2. **不删任何东西**。没有墓碑清理、没有窗口裁剪，只补和覆盖。
    ///   3. **数据量小到可以整取**。想法是短文本，几年下来也就几十万字节，
    ///      一次拉全比分页简单，而且分页会让"到底还有没有更早的"变成一个猜不准的问题。
    ///
    /// 合并规则复用 <see cref="LocalRecordStore{T}.RestoreAsync"/>：
    ///   本地没有 → 插入；本地有但没同步 → **跳过**（本机这份更新）；都已同步 → 比 UpdatedAt。
    /// 第三条是这套东西能天天跑的原因：它不会把用户刚写、还没传上去的那条冲掉。
    /// </summary>
    public sealed class IdeaHistoryService
    {
        /// <summary>2000-01-01（UTC）。早于系统里可能存在的任何一条数据。</summary>
        private const long EpochFrom = 946684800000L;

        /// <summary>想法页每次进场都会调它，超时给短一点：慢就等于没有。</summary>
        private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(8);

        /// <summary>同一个进程里 60 秒内不重复打服务端（来回切页面不该反复请求）。</summary>
        private static readonly TimeSpan QuietWindow = TimeSpan.FromSeconds(60);

        private readonly ServerConnection _conn;
        private readonly LocalRecordStore<LocalIdea> _ideas;

        private DateTime _lastPullAt = DateTime.MinValue;
        private IdeaPull? _lastResult;

        public IdeaHistoryService(ServerConnection conn, LocalRecordStore<LocalIdea> ideas)
        {
            _conn = conn;
            _ideas = ideas;
        }

        /// <summary>
        /// 拉一次并合并。**不抛异常**：连不上、超时、服务端报错，一律返回 Reachable=false，
        /// 调用方只需要知道"这次没拿到"，界面该显示本机的就显示本机的。
        /// </summary>
        public async Task<IdeaPull> PullAsync(bool force = false, CancellationToken ct = default)
        {
            if (!force && _lastResult is not null && DateTime.UtcNow - _lastPullAt < QuietWindow)
            {
                return _lastResult;
            }

            try
            {
                if (!await _conn.EnsureAsync().ConfigureAwait(false))
                {
                    return Cache(new IdeaPull(false, 0, 0, 0));
                }

                long to = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 24L * 60 * 60 * 1000;

                var resp = await _conn.NewIdeaClient()
                    .ListIdeasAsync(new ListIdeasRequest
                    {
                        Kind = -1,          // 全部类型
                        Done = -1,          // 已落实和没落实都要：标记丢了比多一条更难受
                        From = EpochFrom,
                        To = to,
                        IncludeDeleted = false,
                    }, deadline: DateTime.UtcNow.Add(CallTimeout), cancellationToken: ct)
                    .ConfigureAwait(false);

                var remote = resp.Ideas.Select(x => x.ToLocalIdea()).ToList();
                if (remote.Count == 0) return Cache(new IdeaPull(true, 0, 0, 0));

                var stats = await _ideas.RestoreAsync(remote, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                    .ConfigureAwait(false);

                return Cache(new IdeaPull(true, stats.Added, stats.Updated, remote.Count));
            }
            catch (OperationCanceledException)
            {
                return Cache(new IdeaPull(false, 0, 0, 0));
            }
            catch (Exception)
            {
                // 服务端旧版本没有这个接口 / 没网 / 超时 —— 对用户是同一件事：这次没拿到
                return Cache(new IdeaPull(false, 0, 0, 0));
            }
        }

        private IdeaPull Cache(IdeaPull r)
        {
            _lastPullAt = DateTime.UtcNow;
            _lastResult = r;
            return r;
        }
    }
}
