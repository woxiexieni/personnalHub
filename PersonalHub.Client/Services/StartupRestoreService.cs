namespace PersonalHub.Client.Services
{
    /// <summary>
    /// 「本机没有，就自己从服务端捞回来」—— 启动时跑一次，没有任何界面。
    ///
    /// 为什么不给一个「数据与恢复」按钮页（2026-10-21 撤掉的那个）：
    /// 那个页面把"数据会不会丢"变成了一件**要用户记得去做**的事。
    /// 而这件事的判断条件其实非常确定：本机一条都没有 + 服务端有一份，
    /// 那这台设备要么是新装的、要么被清过 —— 不需要问，直接补回来就对了。
    /// 需要用户维护的状态越少，系统越不可能因为"我忘了点那个按钮"而丢数据。
    ///
    /// 安全边界（三条，缺一条就会有反效果）：
    ///   1. **只跑一次**（偏好键 <see cref="FlagKey"/>）。跑过之后再也不自动写库，
    ///      否则用户在别的设备上删掉的东西会一直复活。
    ///   2. **合并规则由 <see cref="RestoreService"/> 决定**：本地没同步过的行永远优先，
    ///      所以"刚重装完先记了一笔"这种顺序也不会被服务端的旧版本盖掉。
    ///   3. **失败不落标记**。连不上服务端（最常见：手机不在那个 Wi-Fi）就下次启动再试，
    ///      而不是"试过一次没成功，以后都不试了"。
    /// </summary>
    public sealed class StartupRestoreService
    {
        private const string FlagKey = "ph.restore.once";

        private readonly RestoreService _restore;

        /// <summary>同一个进程只进一次（Android 上 OnStart 可能被多次触发）。</summary>
        private bool _entered;

        public StartupRestoreService(RestoreService restore)
        {
            _restore = restore;
        }

        /// <summary>后台跑，不阻塞启动。异常一律吞掉：这一步失败不该影响开机。</summary>
        public async Task TryAsync()
        {
            if (_entered) return;
            _entered = true;

            try
            {
                if (Preferences.Default.Get(FlagKey, false)) return;

                // 本机有数据而且从没标记过（老版本升上来）→ 说明不需要补，直接打标记收工
                int local = await _restore.CountLocalAsync().ConfigureAwait(false);
                if (local > 0)
                {
                    Preferences.Default.Set(FlagKey, true);
                    return;
                }

                var report = await _restore.RestoreAllAsync().ConfigureAwait(false);
                if (!report.Ok) return;      // 见边界 3：不落标记，下次启动再试

                Preferences.Default.Set(FlagKey, true);

                System.Diagnostics.Debug.WriteLine($"[StartupRestore] {report.Message}");
            }
            catch
            {
                // 恢复失败不影响启动。用户下次开 App 会自动再试一次。
            }
        }
    }
}
