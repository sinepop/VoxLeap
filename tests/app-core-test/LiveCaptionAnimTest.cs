using System;
using VoxLeap;

// 录音期字幕动画的纯逻辑测试。
//
// 为什么单独测它：分层窗口的动画本身没法在测试里跑（要真窗口、要 30fps），但它依赖的
// 算术全是可离线验证的不变量 —— 胶囊宽度只增不减、绝不超过上限、缓动不过冲且收敛、
// 逐字展开按到达速率铺开（不再"吐一阵停一阵"）且滞后有界、积压时追得上、收尾一次补齐、
// 结果与帧率无关。这些一旦破了，用户看到的就是浮层在屏幕底部来回缩、字一个个弹出来、
// 或者字吐到一半停住。
internal static class LiveCaptionAnimTest
{
    private static int _passed;
    private static int _failed;

    private static void CheckEq(string name, object want, object got)
    {
        bool ok = (want == null && got == null) || (want != null && want.Equals(got));
        if (ok) { _passed++; Console.WriteLine("PASS  " + name); }
        else { _failed++; Console.WriteLine("FAIL  " + name + " [" + got + " != " + want + "]"); }
    }

    private static void Check(string name, bool ok)
    {
        if (ok) { _passed++; Console.WriteLine("PASS  " + name); }
        else { _failed++; Console.WriteLine("FAIL  " + name); }
    }

    public static int Main()
    {
        TestResetState();
        TestNoTextKeepsBaseSize();
        TestWidthOnlyGrows();
        TestWidthCappedAndNeverExceeds();
        TestMaxWidthClampedByWorkArea();
        TestWidthEasesWithoutOvershoot();
        TestWidthIndependentOfFrameRate();
        TestRevealMonotonicAndNoOvershoot();
        TestRevealPacedBySpeechDuration();
        TestSpeechDurationBeatsArrivalGap();
        TestArrivalRateGuards();
        TestPacingLagStaysBounded();
        TestRevealCatchesUpWithBacklog();
        TestRowOnlyGrows();
        TestCompleteRevealFillsInstantly();
        Console.WriteLine("----");
        Console.WriteLine("passed=" + _passed + " failed=" + _failed);
        return _failed == 0 ? 0 : 1;
    }

    // 每次录音开始必须回到 256×76、零字幕，否则上一轮的宽胶囊会残留到新一轮。
    private static void TestResetState()
    {
        var a = new LiveCaptionAnim();
        a.Reset(1920);
        CheckEq("复位宽度", 256, a.Width);
        CheckEq("复位高度", 76, a.Height);
        CheckEq("复位宽度目标", 256, a.WidthTarget);
        CheckEq("复位已吐字数", 0, a.RevealedCount);
        Check("复位后字幕行未开始", !a.RowStarted);
    }

    // 还没有任何字时，即使 30fps 循环在跑，尺寸也必须一动不动。
    private static void TestNoTextKeepsBaseSize()
    {
        var a = new LiveCaptionAnim();
        a.Reset(1920);
        for (int i = 0; i < 30; i++)
        {
            a.AdvanceReveal(33);
            a.AdvanceLayout(33, 0);
        }
        CheckEq("无字时宽度不变", 256, a.Width);
        CheckEq("无字时高度不变", 76, a.Height);
        CheckEq("无字时字幕行为 0 像素", 0, a.RowPixels);
    }

    // 宽度只增不减：字幕是逐段追加的，文本变短时缩回去会让浮层在屏幕底部来回跳。
    private static void TestWidthOnlyGrows()
    {
        var a = new LiveCaptionAnim();
        a.Reset(1920);
        int[] measured = { 300, 380, 350, 300, 340 };
        int last = 0;
        bool monotonic = true;
        for (int i = 0; i < measured.Length; i++)
        {
            for (int f = 0; f < 10; f++)
            {
                a.AdvanceLayout(33, measured[i]);
                if (a.Width < last) monotonic = false;
                last = a.Width;
            }
        }
        Check("宽度单调不减（中途变短也不缩）", monotonic);
        CheckEq("宽度目标取历史最大值", 380, a.WidthTarget);
        CheckEq("宽度最终收敛到目标", 380, a.Width);
    }

    // 超长文本必须停在宽度上限，不许横跨屏幕；行高停在 106。
    private static void TestWidthCappedAndNeverExceeds()
    {
        var a = new LiveCaptionAnim();
        a.Reset(1920);
        CheckEq("1920 工作区的宽度上限", 480, a.MaxWidth);
        a.SetTextLength(400, 1000, 0); // 400 字，远超一行
        for (int f = 0; f < 120; f++)
        {
            a.AdvanceReveal(33);
            a.AdvanceLayout(33, 9000);
            if (a.Width > 480) { Check("宽度不得越过上限", false); return; }
        }
        CheckEq("宽度停在上限", 480, a.Width);
        CheckEq("宽度目标停在上限", 480, a.WidthTarget);
        CheckEq("有字时高度长到 106", 106, a.Height);
    }

    // 上限必须先被工作区宽度压一次：胶囊水平居中，而 BottomCenterOf 只居中、不钳制。
    private static void TestMaxWidthClampedByWorkArea()
    {
        CheckEq("宽工作区取首选上限", 480, LiveCaptionAnim.ClampMaxWidth(1920));
        CheckEq("工作区够宽也不超首选值", 480, LiveCaptionAnim.ClampMaxWidth(4000));
        CheckEq("窄工作区按宽度压低", 320, LiveCaptionAnim.ClampMaxWidth(400));
        CheckEq("极窄工作区不低于基础宽度", 256, LiveCaptionAnim.ClampMaxWidth(200));
    }

    // 指数缓动：不过冲、能收敛、且吸附到整数目标（不能永远差 0.04px 让浮层一直重绘）。
    private static void TestWidthEasesWithoutOvershoot()
    {
        var a = new LiveCaptionAnim();
        a.Reset(1920);
        bool overshoot = false;
        for (int f = 0; f < 60; f++)
        {
            a.AdvanceLayout(33, 400);
            if (a.Width > 400) overshoot = true;
        }
        Check("缓动不过冲", !overshoot);
        CheckEq("缓动收敛到目标", 400, a.Width);
    }

    // 缓动必须按**时间**而不是按帧数：Timer 被节流时帧长会变，动画速度不能跟着变。
    private static void TestWidthIndependentOfFrameRate()
    {
        var a = new LiveCaptionAnim(); a.Reset(1920);
        var b = new LiveCaptionAnim(); b.Reset(1920);
        var c = new LiveCaptionAnim(); c.Reset(1920);
        for (int i = 0; i < 5; i++) a.AdvanceLayout(30, 480);   // 30fps
        for (int i = 0; i < 3; i++) b.AdvanceLayout(50, 480);   // 20fps
        c.AdvanceLayout(150, 480);                              // 单帧 150ms
        Check("同样时长下 30fps 与 20fps 宽度一致（±1px）", Math.Abs(a.Width - b.Width) <= 1);
        Check("同样时长下单帧推进与多帧一致（±1px）", Math.Abs(a.Width - c.Width) <= 1);
        Check("缓动确实在动（不是一步到位）", a.Width > 256 && a.Width < 480);
    }

    // 逐字展开：单调、不超调、绝对不超过文本长度；而且**不再一口气吐光**。
    // 这条以前断言"40 帧吐完 10 字"，那正是被用户否掉的"一个一个弹出来"：10 字 0.42 秒吐完。
    private static void TestRevealMonotonicAndNoOvershoot()
    {
        var a = new LiveCaptionAnim();
        a.Reset(1920);
        a.SetTextLength(10, 1000, 0);
        int last = 0;
        bool monotonic = true;
        bool overshoot = false;
        for (int f = 0; f < 40; f++)
        {
            int n = a.AdvanceReveal(33);
            if (n < last) monotonic = false;
            if (n > 10) overshoot = true;
            last = n;
        }
        Check("已吐字数单调不减", monotonic);
        Check("已吐字数不超文本长度", !overshoot);
        Check("1320ms 内没有一口气吐光 10 字", last < 10);
        for (int f = 0; f < 80; f++) a.AdvanceReveal(33);
        CheckEq("最终一定吐完", 10, a.RevealedCount);
    }

    // 铺开速率的分母是**说话时长**（静音不计），不是到达间隔。实测同一段录音：
    // 41 字 / 说话 7.9s = 5.2 字/秒，而按"8 字 / 2.6s 到达间隔"只有 3.1 字/秒 —— 估低四成，
    // 滞后就会一路累积，最后被积压加速一次性吐出来（"停顿一下之后出来一堆字"）。
    private static void TestRevealPacedBySpeechDuration()
    {
        var a = new LiveCaptionAnim();
        a.Reset(1920);
        a.SetTextLength(12, 2000, 2300); // 12 字，其中说话 2.3 秒 ⇒ 5.2 字/秒
        Check("速率按说话时长算", Math.Abs(a.RatePerSecond - 12.0 * 1000.0 / 2300.0) < 0.01);
        Check("速率落在常人语速量级（2~6 字/秒）", a.RatePerSecond >= 2.0 && a.RatePerSecond <= 6.0);

        // 12 字按 5.2×1.2 = 6.26 字/秒铺开 ⇒ 约 1.9 秒吐完（滞后 1.9s < 3s，不该被额外加速）。
        for (int f = 0; f < 30; f++) a.AdvanceReveal(33); // 990ms
        Check("1 秒后仍在按速率铺开（没有一口气吐光）", a.RevealedCount >= 3 && a.RevealedCount <= 9);
        for (int f = 0; f < 28; f++) a.AdvanceReveal(33); // 累计 1914ms
        Check("约 1.9 秒吐完 12 字（≈ 12 / 5.2 秒）", a.RevealedCount >= 11);
    }

    // 关键回归：到达间隔里含着长停顿，速率也不得被估低（分母必须是说话时长）。
    private static void TestSpeechDurationBeatsArrivalGap()
    {
        var a = new LiveCaptionAnim();
        a.Reset(1920);
        a.SetTextLength(10, 1000, 2000); // 10 字 / 说话 2.0s ⇒ 5.0 字/秒
        a.SetTextLength(20, 5000, 4000); // 又 10 字；到达间隔 4 秒（中间停了 2 秒），说话合计 4.0s
        Check("速率取说话时长而不是 4 秒的到达间隔", Math.Abs(a.RatePerSecond - 5.0) < 0.01);
        Check("说话速率本身被记下来", Math.Abs(a.SpeechRatePerSecond - 5.0) < 0.01);
    }

    // 速率估计的护栏：同一批被拆成两次到达（间隔过小）、或中间长时间没人说话（间隔过大），
    // 都不许污染备用估计；说话时长太短时也不算速率，一律退回起步值。
    private static void TestArrivalRateGuards()
    {
        var small = new LiveCaptionAnim();
        small.Reset(1920);
        small.SetTextLength(5, 1000, 0);
        small.SetTextLength(6, 1100, 0); // 间隔 100ms < 300ms：不参与估计
        Check("过小间隔不参与速率估计（仍在起步值）", small.RatePerSecond == 4.5);

        var big = new LiveCaptionAnim();
        big.Reset(1920);
        big.SetTextLength(5, 1000, 0);
        big.SetTextLength(6, 21000, 0); // 间隔 20s > 8s：不参与估计
        Check("过大间隔不参与速率估计（仍在起步值）", big.RatePerSecond == 4.5);

        var grow = new LiveCaptionAnim();
        grow.Reset(1920);
        grow.SetTextLength(3, 1000, 0);
        grow.SetTextLength(8, 2000, 0); // 间隔 1s、到 5 字 ⇒ 5 字/秒
        Check("正常间隔建立备用估计", grow.RatePerSecond > 4.5);

        var tiny = new LiveCaptionAnim();
        tiny.Reset(1920);
        tiny.SetTextLength(3, 1000, 200); // 说话仅 200ms < 500ms：不算速率
        Check("说话时长太短不算速率（仍在起步值）", tiny.RatePerSecond == 4.5);
    }

    // "铺开"不等于"永远落后"：真实节拍（每 2.6 秒一批、每批 8 字、说话 5.2 字/秒）连续 10 批，
    // 滞后必须有界。松手时只会 CompleteReveal 补最后那几个字。
    private static void TestPacingLagStaysBounded()
    {
        var a = new LiveCaptionAnim();
        a.Reset(1920);
        int t = 1000;
        int speech = 0;
        int total = 0;
        int maxBacklog = 0;
        for (int batch = 0; batch < 10; batch++)
        {
            total += 8;
            speech += 1540; // 每批 8 字按 5.2 字/秒 ⇒ 说话约 1.54 秒
            a.SetTextLength(total, t, speech);
            int peak = total - a.RevealedCount; // 刚到达那一刻的滞后（本批的峰值）
            if (peak > maxBacklog) maxBacklog = peak;
            for (int f = 0; f < 79; f++) { a.AdvanceReveal(33); t += 33; } // 约 2.6 秒
        }
        Check("每批到达时的滞后不超过一批", maxBacklog <= 8);
        Check("每批都能在这批到下一批之前吐完（没有越落越远）", total - a.RevealedCount <= 2);
    }

    // 一次到达一大段时必须追得上，但**不按字数加速**：只有滞后超过 3 秒才加速排空。
    // 上一版按"积压 >10 字就按 1.5 秒排空"加速，把正常的十几字批次也变成"一口气吐光"。
    private static void TestRevealCatchesUpWithBacklog()
    {
        var normal = new LiveCaptionAnim();
        normal.Reset(1920);
        normal.SetTextLength(5, 1000, 0); // 5 字：滞后 5/5.4 < 3s ⇒ 不加速
        var huge = new LiveCaptionAnim();
        huge.Reset(1920);
        huge.SetTextLength(30, 1000, 0); // 30 字：滞后 5.6s > 3s ⇒ 加速到 10 字/秒
        for (int f = 0; f < 10; f++)
        {
            normal.AdvanceReveal(33);
            huge.AdvanceReveal(33);
        }
        Check("滞后超过 3 秒才加速", huge.RevealedCount > normal.RevealedCount);

        // 回归：正常的二十字批次不得被加速（上一版会以 13 字/秒吐出来）。
        var twenty = new LiveCaptionAnim();
        twenty.Reset(1920);
        twenty.SetTextLength(20, 1000, 0);
        for (int f = 0; f < 30; f++) twenty.AdvanceReveal(33); // 990ms
        Check("二十字批次按铺开速率走（990ms 不超过 8 字）", twenty.RevealedCount <= 8);

        bool caughtUp = false;
        for (int f = 0; f < 160 && !caughtUp; f++)
        {
            caughtUp = huge.AdvanceReveal(33) >= 30;
        }
        Check("30 字积压能在 5 秒内追平", caughtUp);
    }

    // 字幕行只在有字要吐之后才开始长，且本次录音内只增不减。
    private static void TestRowOnlyGrows()
    {
        var a = new LiveCaptionAnim();
        a.Reset(1920);
        a.SetTextLength(20, 1000, 0);
        CheckEq("未开始时长 0 像素", 0, a.RowPixels);
        int last = 0;
        bool monotonic = true;
        for (int f = 0; f < 40; f++)
        {
            a.AdvanceReveal(33);
            a.AdvanceLayout(33, 300);
            if (a.RowPixels < last) monotonic = false;
            last = a.RowPixels;
        }
        Check("字幕行单调不减", monotonic);
        CheckEq("字幕行长满 30 像素", 30, a.RowPixels);
        CheckEq("整高 106", 106, a.Height);
    }

    // 收尾立刻补齐，避免"字还没吐完录音就结束了"。
    private static void TestCompleteRevealFillsInstantly()
    {
        var a = new LiveCaptionAnim();
        a.Reset(1920);
        a.SetTextLength(30, 1000, 0);
        a.AdvanceReveal(33); // 让字幕行开始
        a.AdvanceLayout(33, 300);
        a.CompleteReveal();
        CheckEq("收尾补齐到全文", 30, a.RevealedCount);
        CheckEq("收尾时字幕行已满高", 30, a.RowPixels);
    }
}
