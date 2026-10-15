using PersonalHub.Client.Services;

namespace PersonalHub.Client
{
    /// <summary>
    /// 「状态」页：Ping 服务端并显示连接情况。
    ///
    /// 地址不是写死的：先复用已定位的通道；连不上就强制重新扫描局域网再试一次
    /// （PC 换 IP、手机从 4G 回到家 Wi-Fi 都能自动适应）。见 ServerConnection / ServerLocator。
    /// </summary>
    public partial class MainPage : ContentPage
    {
        private readonly ServerConnection _conn;

        public MainPage(ServerConnection conn)
        {
            InitializeComponent();
            _conn = conn;
        }

        // 页面显示时自动 Ping
        protected override async void OnAppearing()
        {
            base.OnAppearing();
            await PingServerAsync();
        }

        // 手动重连
        private async void OnReconnectClicked(object sender, EventArgs e)
        {
            await PingServerAsync();
        }

        private async Task PingServerAsync()
        {
            SetStatus("正在查找服务端...");

            if (await TryPingAsync(force: false))
            {
                return;
            }

            // 连不上：强制重新扫描一遍局域网再试（换 IP / 换网络的情况）
            SetStatus("未连通，正在重新扫描局域网...");
            if (await TryPingAsync(force: true))
            {
                return;
            }

            SetStatus(
                "与服务器连接状态：失败\n" +
                $"已尝试的地址：{_conn.Address ?? "未定位到任何服务端"}\n\n" +
                "排查建议：\n" +
                "1）手机和电脑是否连同一个 Wi-Fi\n" +
                "2）手机关掉「WLAN+/智能网络切换」，别让流量走 4G/5G\n" +
                "3）电脑上服务端窗口是否还开着（双击 start-server.bat）");
        }

        /// <summary>发一次 Ping；force=true 时先强制重新定位地址。</summary>
        private async Task<bool> TryPingAsync(bool force)
        {
            try
            {
                if (!await _conn.EnsureAsync(force))
                {
                    return false;
                }

                var resp = await _conn.NewSystemClient()
                    .PingAsync(new Personalhub.PingRequest(), deadline: DateTime.UtcNow.AddSeconds(3));

                SetStatus(
                    "与服务器连接状态：成功\n" +
                    $"服务器：{_conn.Address}\n" +
                    $"ServerVersion = \"{resp.ServerVersion}\"");
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Ping] 失败: {ex.Message}");
                return false;
            }
        }

        private void SetStatus(string text) =>
            MainThread.BeginInvokeOnMainThread(() => StatusLabel.Text = text);
    }
}
