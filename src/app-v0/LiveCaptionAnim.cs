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
        // 逐字展开的速度不再是一个固定值，而是**跟着说话的速率走**。
        //
        // 为什么（2026-09-26 实机反馈）：分段识别是"每 2.6 秒到一批、每批约 8 字"（用户实测
        // 13.4s 录音里 5 批共 41 字）。原来的"基础 16 字/秒 + 按积压加速"会把这 8 个字在
        // 0.42 秒内吐光（每 62ms 蹦一个字，看着就是"一个一个弹出来"），然后空等 2.2 秒 ——
        // 占空比只有 16%，所以观感永远是"吐一阵、停一阵"，调快调慢都摆脱不了这个节奏。
        //
        // 速率的分母必须是**说话时长**（静音不计），不能是"距上批的间隔"：后者含着用户停顿的
        // 沉默，会把速率估低 —— 实测同一段录音，按说话时长是 41 字 / 7.9s = 5.2 字/秒，按到达
        // 间隔只有 8 字 / 2.6s = 3.1 字/秒。估低会让滞后一路累积，最后被"积压加速"一次性吐出来，
        // 那正是第二次实机反馈的"停顿一下之后，出来一堆字"。
        private const double DefaultRevealPerSecond = 4.5; // 还没有速率估计时的起步值（接近常人语速）
        private const double RevealHeadroom = 1.2;         // 比说话速率略快，积压才能慢慢排空
        private const double RevealMinPerSecond = 2.0;
        private const double RevealMaxPerSecond = 45.0;
        // 滞后按**秒**兜底，而不是按字数：正常一批十几到二十字本来就要好几秒才吐完，
        // 按字数兜底（上一版是"积压 >10 字就按 1.5 秒排空"）会把正常批次也加速成"一口气吐光"。
        private const double MaxLagSeconds = 3.0;
        // 说话时长至少要累计到这里，算出来的速率才可信（太短会被一两个字的抖动放大）。
        private const int MinRateSpeechMs = 500;
        // 备用速率估计（说话时长还不够时顶一下）：按"距上批的间隔"做指数平滑。
        private const double RateSmoothing = 0.45;
        private const int MinRateGapMs = 300;
        private const int MaxRateGapMs = 8000;
        // 最新吐出来那个字的淡入时长。按说话速率铺开时约每 180ms 才出一个字，所以 140ms 的
        // 淡入窗口里同一时刻最多一两个字在渐变；一次到达一大段（追赶）时多出来的字直接全亮。
        public const int FadeInMs = 140;
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
        private double _speechRatePerSecond; // 首选估计：累计字数 / 累计说话时长（不受停顿影响）
        private double _gapRatePerSecond;    // 备用估计：按到达间隔平滑（只在会话开头几秒用）
        private int _lastArrivalAt;          // 上一批文本到达的时刻（TickCount 毫秒），0 表示还没到过
        // 内部单调时钟：只由 AdvanceReveal 的帧间隔累加。用它记"最新那个字是什么时候吐出来的"，
        // 这样淡入进度完全不依赖宿主传进来的 TickCount，离线测试也能照样推进。
        private double _clockMs;
        private int _newestIndex;            // 最新吐出来的那个字的下标，-1 表示没有字在淡入
        private double _newestAtMs;

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
            _speechRatePerSecond = 0.0;
            _gapRatePerSecond = 0.0;
            _lastArrivalAt = 0;
            _clockMs = 0.0;
            _newestIndex = -1;
            _newestAtMs = 0.0;
        }

        // 分段账本累计出来的**全文**长度、这一批到达的时刻（TickCount 毫秒）、以及当时累计的
        // **说话时长**（毫秒，静音不计，来自 SpeechSegmenter.SpeechMsTotal）。
        // 只增不减：字幕是逐段追加的，若允许变短，胶囊会在屏幕底部来回缩，比不长大更糟。
        public void SetTextLength(int textLength, int nowMs, int speechMsTotal)
        {
            if (textLength < 0) textLength = 0;
            if (textLength <= _textLength) return;
            int arrived = textLength - _textLength;
            if (_lastArrivalAt != 0)
            {
                // 备用估计：这一批多少字 / 距上批多少毫秒。含停顿的沉默，会估低，所以只当兜底。
                int gap = unchecked(nowMs - _lastArrivalAt);
                if (gap >= MinRateGapMs && gap <= MaxRateGapMs)
                {
                    double instant = arrived * 1000.0 / gap;
                    _gapRatePerSecond = _gapRatePerSecond <= 0.0
                        ? instant
                        : _gapRatePerSecond + RateSmoothing * (instant - _gapRatePerSecond);
                }
            }
            _lastArrivalAt = nowMs;
            _textLength = textLength;
            // 首选估计：累计字数 / 累计说话时长。用户停顿不会把它估低。
            if (speechMsTotal >= MinRateSpeechMs && textLength > 0)
            {
                double bySpeech = textLength * 1000.0 / speechMsTotal;
                if (bySpeech > 0.0) _speechRatePerSecond = bySpeech;
            }
        }

        // 当前用于铺开的速率（字/秒）：优先说话速率，其次到达间隔，最后是起步值。
        private double EffectiveRatePerSecond()
        {
            if (_speechRatePerSecond > 0.0) return _speechRatePerSecond;
            if (_gapRatePerSecond > 0.0) return _gapRatePerSecond;
            return DefaultRevealPerSecond;
        }

        // 收尾时立即补齐，避免"字还没吐完录音就结束了"。
        // 同时取消未完成的淡入：收尾后动画循环就停了，时钟不再前进，留着会让最后一个字
        // 永远停在半透明状态。
        public void CompleteReveal()
        {
            _revealed = _textLength;
            _newestIndex = -1;
            if (_rowStarted) _rowProgress = 1.0;
        }

        // 推进"已吐字数"。返回本帧应当显示的字数（截断到整数）。
        public int AdvanceReveal(int deltaMs)
        {
            if (deltaMs > 0)
            {
                _clockMs += deltaMs;
                double backlog = _textLength - _revealed;
                if (backlog > 0)
                {
                    double speed = EffectiveRatePerSecond() * RevealHeadroom;
                    // 只有真的落下超过 MaxLagSeconds 才加速排空；正常一批（十到二十字）不加速，
                    // 否则又会回到"一口气吐光"。
                    if (speed > 0.0 && backlog / speed > MaxLagSeconds)
                    {
                        speed = backlog / MaxLagSeconds;
                    }
                    if (speed < RevealMinPerSecond) speed = RevealMinPerSecond;
                    if (speed > RevealMaxPerSecond) speed = RevealMaxPerSecond;
                    double before = _revealed;
                    _revealed += speed * (deltaMs / 1000.0);
                    if (_revealed > _textLength) _revealed = _textLength;
                    // 记下这一帧跨过的最后一个整数边界：那个字刚刚露出来，开始淡入。
                    // 一帧跨过多个边界时只记最后一个 —— 追赶时多出来的字直接全亮，看不出来。
                    int crossed = (int)_revealed;
                    if (crossed > (int)before && crossed >= 1)
                    {
                        _newestIndex = crossed - 1;
                        _newestAtMs = _clockMs;
                    }
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
        // 当前用于铺开的速率（字/秒）。给测试与排障用：确认"铺开"确实跟着说话速率走。
        public double RatePerSecond { get { return EffectiveRatePerSecond(); } }
        // 首选估计本身（0 = 说话时长还不够，正在用备用估计或起步值）。
        public double SpeechRatePerSecond { get { return _speechRatePerSecond; } }
        // 最新那个字的淡入进度：0 = 刚露头（全透明），1 = 已经全亮。
        // 宿主把它用在**整行最后一个字**上：那一定是刚刚露出来的那个（文本只追加、裁剪只砍左边）。
        public double NewestCharAlpha
        {
            get
            {
                if (_newestIndex < 0) return 1.0;
                double age = _clockMs - _newestAtMs;
                if (age <= 0.0) return 0.0;
                if (age >= FadeInMs) return 1.0;
                return age / FadeInMs;
            }
        }

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
