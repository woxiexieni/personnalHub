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

        /// <summary>探针表：钱包通知原文，只留最近 40 条（排查"到底有没有通知"用）。</summary>
        private static LocalRecordStore<LocalWalletProbe>? _probeStore;

        public static void Initialize(
            LocalRecordStore<LocalPendingPay> store,
            LocalRecordStore<LocalWalletProbe>? probeStore = null)
        {
            _store = store;
            _probeStore = probeStore;
        }

        // 包名：支付宝 / 微信。硬编码 —— 它们十年没变过，变了也只是抓不到而已（fail silent）。
        private const string AlipayPkg = "com.eg.android.AlipayGphone";
        private const string WechatPkg = "com.tencent.mm";

        /// <summary>
        /// 支付类动词。支付宝的通知不一定会写自己的名字
        /// （「碰一碰」和扫码的通知标题常常只有"支付成功"），
        /// 所以除了"支付宝"三个字，也认这一组词 —— 有它们 + 有金额，基本不会错。
        /// </summary>
        private static readonly string[] PayKeys =
            { "支付", "付款", "收款", "转账", "消费", "到账", "退款", "账单", "已付", "扣款" };

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
        /// 非支付通知丢掉（包名对不上 → 直接返回）。
        ///
        /// 包名对上的（支付宝 / 微信）**一律先记一条探针**，不管认没认出来：
        /// 有些支付路径根本不发通知，有些只发标题不发正文 —— 没有探针，
        /// 「没抓到」和「没通知」在 app 里长得一模一样，没法判断是谁的问题。
        ///
        /// 这个方法绝不能抛异常 —— 监听服务里抛了，系统可能解绑服务，
        /// 自动记账就整个静默失效了。
        /// </summary>
        public static void HandleNotification(string packageName, string title, string text)
        {
            try
            {
                bool wallet = packageName == AlipayPkg || packageName == WechatPkg;
                if (!wallet) return;

                _ = RecordAsync(packageName, title ?? string.Empty, text ?? string.Empty);
            }
            catch
            {
                // 吞掉：见方法注释。宁可漏一条，不可挂服务。
            }
        }

        /// <summary>记探针 + 尝试解析入库。两条都失败也不抛（监听服务的铁律）。</summary>
        private static async Task RecordAsync(string packageName, string title, string text)
        {
            PaymentCandidate? candidate = null;

            try { candidate = Parse(packageName, title, text); } catch { }

            await SaveProbeAsync(packageName, title, text, candidate is not null).ConfigureAwait(false);

            if (candidate is not null) await SaveAsync(candidate).ConfigureAwait(false);
        }

        // ============================================================
        // 解析（纯函数，Windows 上也能单测）
        // ============================================================

        /// <summary>
        /// 认不出（不是支付通知 / 没金额）返回 null。
        ///
        /// 来源判定：
        ///   支付宝 —— 包名对 + （文本含"支付宝" 或 含支付类动词）
        ///              以前要求必须有"支付宝"三个字，但「碰一碰」和部分扫码通知
        ///              标题只有"支付成功"、正文只有"你已付款 12.00元"，
        ///              于是明明抓到了却被第一道闸挡回去 —— 这就是"支付宝不能识别"。
        ///   微信   —— 包名对 + 文本含"微信支付"/"微信支付凭证"/"收款到账"。
        ///              微信聊天里朋友说"帮我付¥50"不含这些词，天然被挡在外面；
        ///              这条不能放宽，放宽了每条聊天记录都会变成待确认流水。
        /// </summary>
        public static PaymentCandidate? Parse(string packageName, string title, string text)
        {
            string full = $"{title} {text}";

            if (string.IsNullOrWhiteSpace(full)) return null;

            string source;
            if (packageName == AlipayPkg && (full.Contains("支付宝", StringComparison.Ordinal)
                                             || ContainsAny(full, PayKeys)))
            {
                source = "alipay";
            }
            else if (packageName == WechatPkg
                     && (full.Contains("微信支付", StringComparison.Ordinal)
                         || full.Contains("收款到账", StringComparison.Ordinal)))
            {
                source = "wechat";
            }
            else
            {
                return null;
            }

            // 金额从标题 + 正文一起找：有的通知把金额写在标题里（"支付成功 ¥12.00"）
            double amount = ParseAmount(full);
            if (amount <= 0 || amount > 100_000) return null;   // 越界 = 八成抓错了

            return new PaymentCandidate
            {
                Amount = amount,
                Direction = ParseDirection(full),
                Source = source,
                RawText = text,
            };
        }

        private static bool ContainsAny(string text, string[] keys)
        {
            foreach (string k in keys)
            {
                if (text.Contains(k, StringComparison.Ordinal)) return true;
            }

            return false;
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

        /// <summary>
        /// 记一条探针（钱包通知原文）。只留最近 40 条 —— 这个表的用途是排查
        /// "通知到底有没有来"，不是日志，攒多了没意义。
        /// </summary>
        private static async Task SaveProbeAsync(string packageName, string title, string text, bool parsed)
        {
            var store = _probeStore;
            if (store is null) return;

            try
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                await store.EnsureReadyAsync().ConfigureAwait(false);
                await store.InsertAsync(new LocalWalletProbe
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Package = packageName,
                    Source = packageName == AlipayPkg ? "alipay" : "wechat",
                    Title = Cut(title, 80),
                    Text = Cut(text, 300),
                    Parsed = parsed,
                    NotifiedAt = now,
                }).ConfigureAwait(false);

                await store.ExecuteAsync(
                    "DELETE FROM wallet_probes WHERE Id NOT IN " +
                    "(SELECT Id FROM wallet_probes ORDER BY NotifiedAt DESC LIMIT 40)")
                    .ConfigureAwait(false);
            }
            catch
            {
                // 探针失败不影响主链路
            }
        }

        private static string Cut(string s, int max)
            => string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= max ? s : s.Substring(0, max));
    }
}
