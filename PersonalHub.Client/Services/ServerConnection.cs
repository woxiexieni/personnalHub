using System.Diagnostics;
using Grpc.Net.Client;
using Personalhub;
namespace PersonalHub.Client.Services
{
    /// <summary>
    /// 服务端连接管理器 —— 持有当前的 gRPC 通道，并支持"重新定位"。
    ///
    /// 为什么需要它：GrpcChannel 一旦创建，地址就固定了；而 ServerLocator 的探测结果
    /// 会随环境变化（PC 换了 IP、手机从 4G 回到家 Wi-Fi）。把通道集中在这里管理，
    /// 连不上 / 网络变化时调 <see cref="EnsureAsync"/>（force=true）重建通道即可。
    ///
    /// 复核机制：ServerLocator 只能判断"5000 端口通"，而局域网里别的设备也可能占着 5000。
    /// 所以这里对每个候选发一次真正的 gRPC Ping，只有应答的才是我们的服务端；
    /// 不是就跳过、继续找下一个。确认可用后才写进地址缓存。
    ///
    /// 注意：gRPC 客户端（SystemClient / RecordServiceClient）要**每次从当前通道新建**，
    /// 不能做成单例缓存 —— 否则通道换了，客户端还指着旧地址。
    /// </summary>
    public sealed class ServerConnection
    {
        /// <summary>一次定位最多复核几个候选（避免在满是"假服务端"的网段里耗太久）。</summary>
        private const int MaxCandidatesToVerify = 3;

        private readonly SemaphoreSlim _gate = new(1, 1);
        private GrpcChannel? _channel;



        /// <summary>当前使用的服务端基地址（形如 http://192.168.11.168:5000）；尚未定位时为 null。</summary>
        public string? Address { get; private set; }

        /// <summary>当前通道。未定位时会抛异常，调用前请先 await <see cref="EnsureAsync"/>。</summary>
        public GrpcChannel Channel =>
            _channel ?? throw new InvalidOperationException("尚未定位到服务端地址");

        //对应proto 的service,
        
        public Personalhub.System.SystemClient NewSystemClient() => new(Channel);

        //对应Rpc方法
        public Personalhub.RecordService.RecordServiceClient NewRecordClient() => new(Channel);

        // 事件反思服务（ThingService）
        public Personalhub.ThingService.ThingServiceClient NewThingClient() => new(Channel);

        /// <summary>
        /// 确保已定位到服务端并建好通道。
        /// force=false：已有通道就直接复用；
        /// force=true ：强制重新探测（网络变化 / 连不上时用）。
        /// 返回是否拿到了可用通道。
        /// </summary>
        public async Task<bool> EnsureAsync(bool force = false)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!force && _channel != null) return true;

                var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < MaxCandidatesToVerify; i++)
                {
                    string? addr = await ServerLocator.FindServerAsync(tried).ConfigureAwait(false);
                    if (addr == null)
                    {
                        Debug.WriteLine("[ServerConnection] 没有更多候选了");
                        break;
                    }

                    tried.Add(HostOf(addr));

                    var channel = BuildChannel(addr);
                    if (await VerifyAsync(channel).ConfigureAwait(false))
                    {
                        var old = _channel;
                        _channel = channel;
                        Address = addr;
                        ServerLocator.Remember(addr);

                        // 旧通道可能还有在途请求，Dispose 会中断它们；只在确实换了地址时释放。
                        try { old?.Dispose(); } catch { /* 忽略 */ }

                        Debug.WriteLine($"[ServerConnection] 已连接 {addr}");
                        return true;
                    }

                    // 端口通但 Ping 不应答 → 是别的设备占着 5000，换下一个
                    Debug.WriteLine($"[ServerConnection] {addr} 端口可达但不是 PersonalHub 服务端，继续找");
                    try { channel.Dispose(); } catch { /* 忽略 */ }
                }

                return _channel != null;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>对候选通道发一次短超时 Ping，确认它确实是我们的服务端。</summary>
        private static async Task<bool> VerifyAsync(GrpcChannel channel)
        {
            try
            {
                var client = new Personalhub.System.SystemClient(channel);
                var resp = await client
                    .PingAsync(new Personalhub.PingRequest(), deadline: DateTime.UtcNow.AddSeconds(2))
                    .ConfigureAwait(false);
                return resp.Ok;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>按服务端基地址创建一个 gRPC 通道。</summary>
        private static GrpcChannel BuildChannel(string address)
        {
            // 【关键】必须显式使用 SocketsHttpHandler：
            //   Android 上 .NET 默认走 AndroidMessageHandler，它不支持 HTTP/2，
            //   而 gRPC 强制要求 HTTP/2 —— 不换 handler 会直接报「HTTP/2 is not supported」。
            //   Windows 桌面默认本就是 SocketsHttpHandler，指定它不改变行为。
            var handler = new SocketsHttpHandler
            {
                PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
                KeepAlivePingDelay = TimeSpan.FromSeconds(60),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(30),
                EnableMultipleHttp2Connections = true,
                ConnectTimeout = TimeSpan.FromSeconds(5),
            };

            return GrpcChannel.ForAddress(address, new GrpcChannelOptions
            {
                HttpClient = new HttpClient(handler),
            });
        }

        private static string HostOf(string url)
        {
            var s = url.Replace("http://", string.Empty);
            var idx = s.LastIndexOf(':');
            return idx > 0 ? s.Substring(0, idx) : s;
        }



    }
}
