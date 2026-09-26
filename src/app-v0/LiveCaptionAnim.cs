using System;

namespace VoxLeap
{
    // 录音期字幕的生长动画 —— 纯逻辑，无 WinForms / 字体 / 绘制依赖。
    //
    // 为什么单独一个类：录音胶囊原来写死 256×76，字幕只在第一次出现时把高度从 76 跳到
    // 106 然后锁死，所以看上去就是"一小块不动"。要让它在说话时慢慢变长，每帧要算三件事：
    // 已经吐出几个字、胶囊该有多宽、字幕行该占多高。这三件都是纯算术（只增不减、封顶、
    // 指数缓动、按积压追字），与窗口和绘制无关，抽出来就能离线单测 ——
    // 分层窗口的动画没法在测试里跑，但它依赖的算术可以测严。
    internal sealed class LiveCaptionAnim
    {
        // 无字幕时的录音胶囊尺寸，与 OverlayForm 的初始尺寸同源。
        public const int BaseWidth = 256;
        public const int BaseHeight = 76;
        // 字幕行高度，与 OverlayForm.RecCaptionRowHeight 一致。
        public const int RowHeight = 30;
        // 首选上限：10pt 下约 31 个汉字一行。
        public const int PreferredMaxWidth = 480;
        // 绝对上限：即使首选值将来被调大，也不许横跨整个屏幕。
        public const int AbsMaxWidth = 560;
        // 工作区两侧各留的安全边距：胶囊是水平居中的，太宽会顶出屏幕（BottomCenterOf
        // 只做居中、不钳制），所以上限必须先被工作区宽度压一次。
        public const int WorkAreaReserve = 80;

        // 时间常数（指数缓动，越小越快）。按毫秒计，所以与帧率无关：
        // 33ms 一帧时约 10 帧（330ms）收敛到 5% 以内。
        private const double WidthTauMs = 110.0;
        private const double RowTauMs = 80.0;
        // 逐字展开速度：基础 16 字/秒，并按积压加速（上限 80 字/秒）。
        // 基础值略低于常人语速（约 4~6 字/秒）就够用；积压加速是为了"永远追得上"——
        // 一段识别结果一次到达十几个字时，不会出现"字还在慢慢吐、人已经说下一句"的滞后。
        private const double RevealBasePerSecond = 16.0;
        private const double RevealBacklogGain = 0.40;
        private const double RevealMaxPerSecond = 80.0;
        // 收敛吸附阈值：差值小于它就贴死目标。浮层每帧都要重绘一张 32 位位图，
        // 不能为了 0.04px 的差值让动画永远不结束。
        private const double SettleEpsilon = 0.05;

        private int _maxWidth;
        private double _width;
        private int _widthTarget;
        private double _rowProgress;
        private bool _rowStarted;
        private double _revealed;
        private int _textLength;

        public LiveCaptionAnim()
        {
            Reset(PreferredMaxWidth + WorkAreaReserve);
        }

        // 胶囊宽度上限：先取首选值，再被工作区宽度压低，最后钳到绝对上限与基础宽度之间。
        public static int ClampMaxWidth(int workAreaWidth)
        {
            int max = PreferredMaxWidth;
            int byArea = workAreaWidth - WorkAreaReserve;
            if (max > byArea) max = byArea;
            if (max > AbsMaxWidth) max = AbsMaxWidth;
            if (max < BaseWidth) max = BaseWidth;
            return max;
        }

        // 每次录音开始复位：胶囊回到 256×76，字幕一格都没吐。
        public void Reset(int workAreaWidth)
        {
            _maxWidth = ClampMaxWidth(workAreaWidth);
            _width = BaseWidth;
            _widthTarget = BaseWidth;
            _rowProgress = 0.0;
            _rowStarted = false;
            _revealed = 0.0;
            _textLength = 0;
        }

        // 分段账本累计出来的**全文**长度。只增不减：字幕是逐段追加的，
        // 若允许变短，胶囊会在屏幕底部来回缩，比不长大更糟。
        public void SetTextLength(int textLength)
        {
            if (textLength < 0) textLength = 0;
            if (textLength > _textLength) _textLength = textLength;
        }

        // 收尾时立即补齐，避免"字还没吐完录音就结束了"。
        public void CompleteReveal()
        {
            _revealed = _textLength;
            if (_rowStarted) _rowProgress = 1.0;
        }

        // 推进"已吐字数"。返回本帧应当显示的字数（截断到整数）。
        public int AdvanceReveal(int deltaMs)
        {
            if (deltaMs > 0)
            {
                double backlog = _textLength - _revealed;
                if (backlog > 0)
                {
                    double speed = RevealBasePerSecond + backlog * RevealBacklogGain;
                    if (speed > RevealMaxPerSecond) speed = RevealMaxPerSecond;
                    _revealed += speed * (deltaMs / 1000.0);
                    if (_revealed > _textLength) _revealed = _textLength;
                    // 真正有字要吐了，字幕行才开始让位（此前保持 256×76）。
                    _rowStarted = true;
                }
            }
            return RevealedCount;
        }

        // 推进胶囊宽度与字幕行高度。
        // measuredRevealedWidth = 宿主按**本帧已吐出的字**实测的文本宽 + 边框内缩；
        // 传 0 表示这一帧没有可测的文本（宽度目标保持不变）。
        public void AdvanceLayout(int deltaMs, int measuredRevealedWidth)
        {
            if (measuredRevealedWidth > 0)
            {
                int want = measuredRevealedWidth;
                if (want < BaseWidth) want = BaseWidth;
                if (want > _maxWidth) want = _maxWidth;
                // 本次录音内只增不减：字变短（例如刚吐完一段、下一段还没到）不许缩回去。
                if (want > _widthTarget) _widthTarget = want;
            }
            _width = Ease(_width, _widthTarget, deltaMs, WidthTauMs);
            if (_rowStarted) _rowProgress = Ease(_rowProgress, 1.0, deltaMs, RowTauMs);
        }

        public int Width { get { return (int)Math.Round(_width, MidpointRounding.AwayFromZero); } }
        public int WidthTarget { get { return _widthTarget; } }
        public int MaxWidth { get { return _maxWidth; } }
        public int RowPixels { get { return (int)Math.Round(_rowProgress * RowHeight, MidpointRounding.AwayFromZero); } }
        public double RowProgress { get { return _rowProgress; } }
        public int Height { get { return BaseHeight + RowPixels; } }
        public bool RowStarted { get { return _rowStarted; } }
        public int RevealedCount { get { return (int)_revealed; } }
        public int TextLength { get { return _textLength; } }

        private static double Ease(double current, double target, int deltaMs, double tauMs)
        {
            if (deltaMs <= 0) return current;
            double k = Math.Exp(-deltaMs / tauMs);
            double next = target + (current - target) * k;
            if (Math.Abs(next - target) <= SettleEpsilon) return target;
            return next;
        }
    }
}
