using Microsoft.Extensions.Hosting;
using personalHub.Server.Data;

namespace personalHub.Server.Services
{
    /// <summary>
    /// 每天补做一次数据库快照。
    ///
    /// 为什么需要定时器：服务端常常连开好几天，只在启动时备份会漏掉后面每天的数据。
    /// 这里每 30 分钟检查一次"今天的文件在不在"，不在就做 —— 便宜且不会重复。
    /// </summary>
    public sealed class BackupWorker : BackgroundService
    {
        private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);

        private readonly BackupService _backup;

        public BackupWorker(BackupService backup) => _backup = backup;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // 启动就做一次：昨天关机了一整天，今天一开机就能补上
            RunOnce();

            using var timer = new PeriodicTimer(Interval);
            try
            {
                while (await timer.WaitForNextTickAsync(stoppingToken))
                {
                    RunOnce();
                }
            }
            catch (OperationCanceledException)
            {
                // 正常关闭
            }
        }

        private void RunOnce()
        {
            try
            {
                if (_backup.BackupToday())
                {
                    Console.WriteLine($"[Backup] 已生成今日快照 → {_backup.Directory}");
                }
                else if (_backup.LastError is not null)
                {
                    Console.WriteLine($"[Backup] 失败：{_backup.LastError}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[Backup] 异常：" + ex.Message);
            }
        }
    }
}
