namespace PersonalHub.Client.Controls
{
    /// <summary>
    /// 给被截断的文字加「点一下看全文」。两档：
    ///
    ///   ≤100 字 → **就地展开**：MaxLines 放开、换成 WordWrap，行自己长高，再点收回。
    ///             没有浮层、不遮挡上下文，点完就在原地看，这是列表里最舒服的做法。
    ///   &gt;100 字 → **半屏阅读卡**（<see cref="PeekPage"/>）：长文本摊在列表里会把页面撑爆。
    ///
    /// 为什么不统一弹气泡：
    ///   手机没有 hover，气泡只能靠点/长按触发，而这两个手势在本 App 里都已经有主人
    ///   （行点击进详情、长按改时长/改名）；而且气泡会压在手指下面，用户还得挪开手才看得见。
    ///
    /// 和「点行进详情」的冲突怎么解：见 <see cref="TapArbiter"/>。
    /// 行的手势处理器开头也要调一次 TryClaim，谁先到谁生效。
    /// </summary>
    public static class TextPeek
    {
        /// <summary>超过这个字数就不在列表里展开，改走阅读卡。</summary>
        public const int LongTextThreshold = 100;

        /// <summary>给一个 Label 挂上「点一下看全文」，返回它自己便于链式书写。</summary>
        public static Label Attach(Label label) => Attach(label, () => label.Text);

        /// <summary>
        /// forceCard = true 时永远走阅读卡、绝不在原地展开。
        /// 给**不能变高**的位置用：典型是统计条里定宽 96px 的片名列 ——
        /// 那里展开会把整行图表撑歪，只能浮起来看。
        /// </summary>
        public static Label Attach(Label label, bool forceCard) => Attach(label, () => label.Text, forceCard);

        /// <summary>
        /// 给一个 Label 挂上「点一下看全文」，文字来源由外部提供。
        /// 用委托而不是快照，是因为很多 Label 的 Text 是后来才填的（异步加载）。
        /// </summary>
        public static Label Attach(Label label, Func<string?> textProvider, bool forceCard = false)
        {
            if (label is null || textProvider is null) return label!;

            // 原始状态必须在这里就记下来：展开后再读 label.MaxLines 已经是改过的值，
            // 收回时会一路变成"永远展开"。
            int collapsedMaxLines = label.MaxLines;
            var collapsedBreak = label.LineBreakMode;
            bool expanded = false;

            var tap = new TapGestureRecognizer();
            tap.Tapped += async (_, _) =>
            {
                if (!TapArbiter.TryClaim()) return;

                string text = (textProvider() ?? string.Empty).Trim();
                if (text.Length == 0) return;

                if (forceCard || text.Length > LongTextThreshold)
                {
                    await PeekPage.ShowAsync(null, text);
                    return;
                }

                expanded = !expanded;
                label.MaxLines = expanded ? -1 : collapsedMaxLines;
                label.LineBreakMode = expanded ? LineBreakMode.WordWrap : collapsedBreak;
            };

            label.GestureRecognizers.Add(tap);
            return label;
        }
    }
}
