using System.Text.RegularExpressions;
using PersonalHub.Client.Data;

namespace PersonalHub.Client.Services
{
    /// <summary>解析出来的候选流水（还没进账本，等用户确认）。</summary>
    public sealed class PaymentCandidate
    {
        public double Amount { get; init; }
        public int Direction { get; init; }     // 0 支出 / 1 收入
        public string Source { get; init; } = string.Empty;   // alipay / wechat
        public string RawText { get; init; } = string.Empty;
    }

    /// <summary>
    /// 自动记账的捕获桥（R12）：通知文本 → 待确认流水。
    ///
    /// 为什么是静态类：Android 的 NotificationListenerService 由**系统**直接绑定，
    /// 不走 MAUI 的 DI 容器，MauiProgram 启动时把 store 塞进来（见 Initialize），
    /// 服务进程内直接调 HandleNotification —— 同一个进程，没有跨进程通信。
    ///
    /// 铁律：**解析必须纯本地**。监听器能看到用户手机上的全部通知
    /// （包括短信和聊天预览），原始文本只存进 pending_pays 的 RawText
    /// 供本地排查，绝不上传服务端、绝不写任何日志。
    /// </summary>
    public static class PaymentCapture
    {
        /// <summary>落了一条新的待确认流水后触发（后台线程）。UI 层订阅它刷新卡片。</summary>
        public static event Action? CandidateCaptured;

        private static LocalRecordStore<LocalPendingPay>? _store;

        public static void Initialize(LocalRecordStore<LocalPendingPay> store) => _store = store;

        // 包名：支付宝 / 微信。硬编码 —— 它们十年没变过，变了也只是抓不到而已（fail silent）。
        private const string AlipayPkg = "com.eg.android.AlipayGphone";
        private const string WechatPkg = "com.tencent.mm";

        /// <summary>收入方向的关键词。先查收、再查付 —— "转账收款"要归收入。</summary>
        private static readonly string[] IncomeKeys =
            { "收款", "收入", "进账", "入账", "到账", "收到" };

        /// <summary>
        /// 金额：必须带货币标记（¥/￥ 前缀，或"元"后缀）才认。
        /// 没有这个约束，通知里的验证码、楼层号、日期数字全会被误抓。
        /// </summary>
        private static readonly Regex AmountRegex = new(
            @"[¥￥]\s*(?<a>[0-9][0-9,]*(?:\.[0-9]{1,2})?)|(?<a>[0-9][0-9,]*(?:\.[0-9]{1,2})?)\s*元",
            RegexOptions.Compiled);

        // ============================================================
        // 入口（Android 监听服务调用）
        // ============================================================

        /// <summary>
        /// Android 监听服务的主入口。所有通知都进这里，第一道闸就把
        /// 非支付通知丢掉（包名对不上 / 文本不含来源标识 → 直接返回）。
        /// 这个方法绝不能抛异常 —— 监听服务里抛了，系统可能解绑服务，
        /// 自动记账就整个静默失效了。
        /// </summary>
        public static void HandleNotification(string packageName, string title, string text)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(text)) return;

                var candidate = Parse(packageName, title, text);
                if (candidate is null) return;

                _ = SaveAsync(candidate);
            }
            catch
            {
                // 吞掉：见方法注释。宁可漏一条，不可挂服务。
            }
        }

        // ============================================================
        // 解析（纯函数，Windows 上也能单测）
        // ============================================================

        /// <summary>
        /// 认不出（不是支付通知 / 没金额）返回 null。
        ///
        /// 来源判定比金额判定更严：
        ///   支付宝 —— 包名对 + 文本含"支付宝"（它家通知都带这个标）
        ///   微信   —— 包名对 + 文本含"微信支付"。聊天消息里朋友说"帮我付¥50"
        ///            这种，因为不含"微信支付"四个字，天然被挡在外面。
        /// </summary>
        public static PaymentCandidate? Parse(string packageName, string title, string text)
        {
            string full = $"{title} {text}";

            string source;
            if (packageName == AlipayPkg && full.Contains("支付宝", StringComparison.Ordinal))
            {
                source = "alipay";
            }
            else if (packageName == WechatPkg && full.Contains("微信支付", StringComparison.Ordinal))
            {
                source = "wechat";
            }
            else
            {
                return null;
            }

            double amount = ParseAmount(text);
            if (amount <= 0 || amount > 100_000) return null;   // 越界 = 八成抓错了

            return new PaymentCandidate
            {
                Amount = amount,
                Direction = ParseDirection(text),
                Source = source,
                RawText = text,
            };
        }

        private static double ParseAmount(string text)
        {
            var m = AmountRegex.Match(text);
            if (!m.Success) return 0;

            string digits = m.Groups["a"].Value.Replace(",", string.Empty);
            return double.TryParse(digits, out double v) ? v : 0;
        }

        private static int ParseDirection(string text)
        {
            foreach (string k in IncomeKeys)
            {
                if (text.Contains(k, StringComparison.Ordinal)) return 1;
            }
            // 带金额又没有"收"字样的，默认按支出 —— 支付通知绝大多数是花钱。
            return 0;
        }

        // ============================================================
        // 落库（去重 + 过期清理）
        // ============================================================

        private static async Task SaveAsync(PaymentCandidate c)
        {
            var store = _store;
            if (store is null) return;

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await store.EnsureReadyAsync().ConfigureAwait(false);

            // 去重：同来源、同金额、2 分钟内已有待确认 → 当成同一条（通知可能重发）。
            int dup = await store.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM pending_pays WHERE Status = 0 AND Source = ? AND ABS(Amount - ?) < 0.001 AND NotifiedAt BETWEEN ? AND ?",
                c.Source, c.Amount, now - 120_000, now + 10_000).ConfigureAwait(false);
            if (dup > 0) return;

            await store.InsertAsync(new LocalPendingPay
            {
                Id = Guid.NewGuid().ToString("N"),
                Amount = c.Amount,
                Direction = c.Direction,
                Source = c.Source,
                RawText = c.RawText,
                NotifiedAt = now,
                Status = 0,
            }).ConfigureAwait(false);

            // 过期清理：超过 7 天的一律物理删（不管处没处理）。待确认的价值只有几分钟。
            long cutoff = now - 7 * 86_400_000L;
            await store.ExecuteAsync("DELETE FROM pending_pays WHERE NotifiedAt < ?", cutoff)
                .ConfigureAwait(false);

            CandidateCaptured?.Invoke();
        }
    }
}
