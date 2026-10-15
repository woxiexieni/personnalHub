using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PersonalHub.Client.Services
{
    /// <summary>
    /// 服务端地址定位器 —— 自动找到同一局域网里的 PersonalHub 服务端，无需写死 IP。
    ///
    /// 探测顺序（前者命中即返回）：
    ///   1. 上次成功的地址（本地缓存，命中最快）
    ///   2. 固定候选：10.0.2.2（Android 模拟器宿主机别名）、127.0.0.1（桌面本机）
    ///   3. 本机所在网段的扫描：取本机各网卡 IPv4 + 掩码算出网段，并行探 :5000
    ///      —— 这一步让 PC 换了 DHCP 地址也照样能找到。
    ///
    /// 注意：本类只能判断"端口通了"，不能确认"那就是我们的服务端" ——
    /// 局域网里别的设备也可能占着 5000 端口。所以命中后由 <see cref="ServerConnection"/>
    /// 用一次真正的 gRPC Ping 复核，复核通过才 <see cref="Remember"/> 进缓存。
    /// </summary>
    public static class ServerLocator
    {
        /// <summary>服务端端口。必须与服务端 Program.cs 的 ListenAnyIP 端口一致。</summary>
        public const int Port = 5000;

        /// <summary>单个地址的连接超时（毫秒）。</summary>
        private const int ProbeTimeoutMs = 250;

        /// <summary>并行探测并发度。用真异步 connect，128 路对手机也很轻。</summary>
        private const int Concurrency = 128;

        /// <summary>网段超过这么多台主机就退回 /24 扫，避免在 /16 这类大网上扫爆。</summary>
        private const int MaxHostsPerSubnet = 512;

        /// <summary>上次探测成功地址的缓存键（存 host:port）。</summary>
        private const string PrefKey = "server_address_last_ok";

        /// <summary>
        /// 探测并返回一个"端口可达"的服务端基地址（形如 http://192.168.11.168:5000）；找不到返回 null。
        /// </summary>
        /// <param name="exclude">已经试过并且不成立的地址，跳过它们再找下一个。</param>
        public static async Task<string?> FindServerAsync(ISet<string>? exclude = null)
        {
            // 1) 先试缓存 + 固定候选（一般都很快，命中就不用扫网段了）
            var quick = new List<string>();
            void AddQuick(string h)
            {
                if (exclude != null && exclude.Contains(h)) return;
                if (!quick.Contains(h)) quick.Add(h);
            }

            var cached = ReadCachedHost();
            if (cached != null) AddQuick(cached);
            AddQuick("10.0.2.2");    // Android 模拟器(AVD)宿主机别名
            AddQuick("127.0.0.1");   // Windows 桌面本机

            var hit = await ProbeFirstAsync(quick).ConfigureAwait(false);
            if (hit != null)
            {
                Debug.WriteLine($"[ServerLocator] 命中固定候选: {hit}");
                return ToUrl(hit);
            }

            // 2) 扫本机所在网段（自动适应 DHCP 换 IP）
            var hosts = EnumerateLocalSubnetHosts(exclude);
            Debug.WriteLine($"[ServerLocator] 固定候选未命中，扫描网段主机 {hosts.Count} 个");
            hit = await ProbeFirstAsync(hosts).ConfigureAwait(false);
            if (hit != null)
            {
                Debug.WriteLine($"[ServerLocator] 网段扫描命中: {hit}");
                return ToUrl(hit);
            }

            Debug.WriteLine("[ServerLocator] 所有候选均不可达");
            return null;
        }

        private static string ToUrl(string host) => $"http://{host}:{Port}";

        /// <summary>把确认可用的地址写进缓存（由 ServerConnection 复核通过后调用）。</summary>
        public static void Remember(string url)
        {
            try
            {
                var host = url.Replace("http://", string.Empty);
                var idx = host.LastIndexOf(':');
                if (idx > 0) host = host.Substring(0, idx);
                Preferences.Default.Set(PrefKey, $"{host}:{Port}");
            }
            catch
            {
                // 缓存失败无所谓，下次重新探测
            }
        }

        /// <summary>
        /// 并行探一批主机，返回第一个连通的（无则 null）。
        /// 用真异步 connect（不是阻塞等待），否则并发一多会拖垮线程池 —— 实测阻塞版扫 508 个地址要 10 秒，异步版 26 毫秒。
        /// 命中后立刻取消其余探测。
        /// </summary>
        private static async Task<string?> ProbeFirstAsync(IReadOnlyList<string> hosts)
        {
            if (hosts.Count == 0) return null;

            string? found = null;
            using var cts = new CancellationTokenSource();
            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = Concurrency,
                CancellationToken = cts.Token,
            };

            try
            {
                await Parallel.ForEachAsync(hosts, options, async (host, ct) =>
                {
                    if (Volatile.Read(ref found) != null)
                    {
                        cts.Cancel();
                        return;
                    }

                    if (await ProbeOneAsync(host, ct).ConfigureAwait(false))
                    {
                        Volatile.Write(ref found, host);
                        cts.Cancel();   // 找到就走人，剩下的不用探了
                    }
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 找到后主动取消，属正常路径
            }

            return Volatile.Read(ref found);
        }

        /// <summary>异步 TCP 连接测试：超时内连上端口即视为可达。</summary>
        private static async Task<bool> ProbeOneAsync(string host, CancellationToken ct)
        {
            if (!IPAddress.TryParse(host, out var ip))
            {
                return false;
            }

            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(ProbeTimeoutMs);

                await socket
                    .ConnectAsync(new IPEndPoint(ip, Port), timeoutCts.Token)
                    .ConfigureAwait(false);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 枚举本机所在各网段的主机地址（排除自己与 exclude）。
        /// 只看 Wi-Fi / 有线 / 类型未知的网卡，跳过回环、隧道和蜂窝，避免在移动数据上白扫。
        /// </summary>
        private static List<string> EnumerateLocalSubnetHosts(ISet<string>? exclude = null)
        {
            var result = new List<string>();

            NetworkInterface[] nics;
            try { nics = NetworkInterface.GetAllNetworkInterfaces(); }
            catch { return result; }

            foreach (var nic in nics)
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback
                                                 or NetworkInterfaceType.Tunnel
                                                 or NetworkInterfaceType.Wman
                                                 or NetworkInterfaceType.Wwanpp
                                                 or NetworkInterfaceType.Wwanpp2) continue;

                IPInterfaceProperties props;
                try { props = nic.GetIPProperties(); }
                catch { continue; }

                foreach (var ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;

                    var ipBytes = ua.Address.GetAddressBytes();
                    if (ipBytes.Length != 4) continue;
                    if (ipBytes[0] == 127) continue;                       // 回环
                    if (ipBytes[0] == 169 && ipBytes[1] == 254) continue;  // APIPA 未获取到 IP

                    uint ip = ToUInt(ipBytes);

                    // 掩码：优先用实际掩码；太宽（>/24 级别 512 台）就退回 /24
                    uint mask = 0xFFFFFF00u;
                    if (ua.IPv4Mask != null)
                    {
                        var mb = ua.IPv4Mask.GetAddressBytes();
                        if (mb.Length == 4)
                        {
                            uint m = ToUInt(mb);
                            if ((~m) <= MaxHostsPerSubnet) mask = m;
                        }
                    }

                    uint network = ip & mask;
                    uint broadcast = network | ~mask;

                    for (uint h = network + 1; h < broadcast; h++)
                    {
                        if (h == ip) continue;
                        var candidate = UIntToIp(h);
                        if (exclude != null && exclude.Contains(candidate)) continue;
                        result.Add(candidate);
                    }
                }
            }

            return result;
        }

        private static uint ToUInt(byte[] b) =>
            ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];

        private static string UIntToIp(uint v) =>
            $"{(v >> 24) & 0xFF}.{(v >> 16) & 0xFF}.{(v >> 8) & 0xFF}.{v & 0xFF}";

        private static string? ReadCachedHost()
        {
            try
            {
                var val = Preferences.Default.Get(PrefKey, string.Empty);
                if (string.IsNullOrWhiteSpace(val)) return null;
                var idx = val.LastIndexOf(':');
                return idx > 0 ? val.Substring(0, idx) : val;
            }
            catch
            {
                return null;
            }
        }
    }
}
