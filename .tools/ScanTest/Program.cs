using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

internal static class Program
{
    private const int Port = 5000;
    private const int ProbeTimeoutMs = 250;
    private const int Concurrency = 128;
    private const int MaxHostsPerSubnet = 512;

    private static async Task Main()
    {
        var targets = new List<string>();

        Console.WriteLine("=== 枚举到的活跃网卡 ===");
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback
                                         or NetworkInterfaceType.Tunnel
                                         or NetworkInterfaceType.Wman
                                         or NetworkInterfaceType.Wwanpp
                                         or NetworkInterfaceType.Wwanpp2) continue;

            IPInterfaceProperties props;
            try { props = nic.GetIPProperties(); } catch { continue; }

            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var ipBytes = ua.Address.GetAddressBytes();
                if (ipBytes.Length != 4) continue;
                if (ipBytes[0] == 127) continue;
                if (ipBytes[0] == 169 && ipBytes[1] == 254) continue;

                uint ip = ToUInt(ipBytes);
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
                int added = 0;
                for (uint h = network + 1; h < broadcast; h++)
                {
                    targets.Add(UIntToIp(h));   // 故意不跳过自己，便于本机验证
                    added++;
                }
                Console.WriteLine($"  {nic.Name} [{nic.NetworkInterfaceType}] ip={ua.Address} mask={ua.IPv4Mask}");
                Console.WriteLine($"      -> 网段 {UIntToIp(network)}/{UIntToIp(mask)}，追加 {added} 个候选");
            }
        }

        Console.WriteLine($"\n=== 待扫描主机 {targets.Count} 个，并发 {Concurrency}，超时 {ProbeTimeoutMs}ms ===");

        string? found = null;
        var sw = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource();
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Concurrency,
            CancellationToken = cts.Token,
        };
        try
        {
            await Parallel.ForEachAsync(targets, options, async (host, ct) =>
            {
                if (Volatile.Read(ref found) != null) { cts.Cancel(); return; }
                if (await ProbeOneAsync(host, ct))
                {
                    Volatile.Write(ref found, host);
                    cts.Cancel();
                }
            });
        }
        catch (OperationCanceledException) { }
        sw.Stop();

        Console.WriteLine("\n=== 结果 ===");
        Console.WriteLine(found != null
            ? $"找到服务端: http://{found}:{Port}（耗时 {sw.ElapsedMilliseconds} ms）"
            : $"未找到任何监听 {Port} 的主机（耗时 {sw.ElapsedMilliseconds} ms）");
    }

    private static async Task<bool> ProbeOneAsync(string host, CancellationToken ct)
    {
        if (!IPAddress.TryParse(host, out var ip)) return false;
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(ProbeTimeoutMs);
            await socket.ConnectAsync(new IPEndPoint(ip, Port), timeoutCts.Token);
            return true;
        }
        catch { return false; }
    }

    private static uint ToUInt(byte[] b) =>
        ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];

    private static string UIntToIp(uint v) =>
        $"{(v >> 24) & 0xFF}.{(v >> 16) & 0xFF}.{(v >> 8) & 0xFF}.{v & 0xFF}";
}
