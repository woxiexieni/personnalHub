namespace PersonalHub.Client.Controls
{
    /// <summary>
    /// 「同一次点击只允许一个处理器生效」的仲裁器。
    ///
    /// 为什么需要它：列表行的结构是「Border 上挂 tap（进详情）+ 里面的 Label 上挂 tap（看全文）」。
    /// MAUI 对**嵌套**手势没有统一的冒泡/捕获语义，各平台实现不一样，
    /// 结果是点一下文字可能同时触发「看全文」和「进详情」——或者干脆只触发其中一个，
    /// 而且顺序不保证。往任何一个方向写死判断都会在某个平台上错。
    ///
    /// 所以不猜顺序，改成**时间窗仲裁**：谁先到谁赢，后到的那一个在 400ms 内被吞掉。
    /// 两边都先调用 <see cref="TryClaim"/>，先到者拿到 true，后到者拿到 false 直接 return。
    /// 这样无论平台先派发给子控件还是父控件，行为都一样。
    ///
    /// 400ms 的取值：正常点按间隔远大于它（连点两次通常也在 500ms 以上），
    /// 而一次点击里两个手势的派发间隔只有几毫秒，必然落在窗内。
    /// </summary>
    public static class TapArbiter
    {
        private const int WindowMs = 400;

        private static long _lastClaimTicks;

        /// <summary>本次点击我是否该响应。同一时刻只有第一个调用者拿到 true。</summary>
        public static bool TryClaim()
        {
            long now = Environment.TickCount64;
            long prev = Interlocked.Exchange(ref _lastClaimTicks, now);
            return now - prev >= WindowMs;
        }
    }
}
