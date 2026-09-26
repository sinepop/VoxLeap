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
        TestRevealPacedByArrivalRate();
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
        a.SetTextLength(400, 1000); // 400 字，远超一行
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
        a.SetTextLength(10, 1000);
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

    // 按到达速率铺开（2026-09-26 实机反馈："一个一个弹出来"+"过一段时间跳一段"）。
    // 用实测节拍构造两批：每 2.6 秒到 8 字。要点是字被**摊开**吐，且速率估计确实建起来了。
    private static void TestRevealPacedByArrivalRate()
    {
        var a = new LiveCaptionAnim();
        a.Reset(1920);
        int t = 1000;
        a.SetTextLength(8, t); // 第一批 8 字
        for (int f = 0; f < 18; f++) { a.AdvanceReveal(33); t += 33; } // 约 600ms
        Check("第一段 600ms 内吐不出 4 个字以上（不再是 0.42 秒吐光）", a.RevealedCount <= 4);

        while (t < 3600) { a.AdvanceReveal(33); t += 33; } // 推进到第二批该到的时刻
        CheckEq("两批之间把第一批吐完", 8, a.RevealedCount);

        a.SetTextLength(16, t); // 第二批又 8 字（累计 16）⇒ 间隔约 2.6 秒，据此估计速率
        Check("按到达间隔建立起速率估计", Math.Abs(a.RatePerSecond - 8.0 * 1000.0 / (t - 1000)) < 0.01);
        Check("速率估计落在常人语速量级（2~6 字/秒）", a.RatePerSecond >= 2.0 && a.RatePerSecond <= 6.0);

        for (int f = 0; f < 30; f++) a.AdvanceReveal(33); // 约 1 秒
        Check("第二批 1 秒内仍在慢慢吐（不是又一次吐光）", a.RevealedCount < 16);
    }

    // 速率估计的护栏：同一批被拆成两次到达（间隔过小）、或中间长时间没人说话（间隔过大），
    // 都不许污染估计 —— 否则"铺开"会被一次抖动带偏，字要么卡住、要么又变成一口气吐光。
    private static void TestArrivalRateGuards()
    {
        var small = new LiveCaptionAnim();
        small.Reset(1920);
        small.SetTextLength(5, 1000);
        small.SetTextLength(6, 1100); // 间隔 100ms < 300ms：不参与估计
        CheckEq("过小间隔不参与速率估计", 0.0, small.RatePerSecond);

        var big = new LiveCaptionAnim();
        big.Reset(1920);
        big.SetTextLength(5, 1000);
        big.SetTextLength(6, 21000); // 间隔 20s > 8s：不参与估计
        CheckEq("过大间隔不参与速率估计", 0.0, big.RatePerSecond);

        var grow = new LiveCaptionAnim();
        grow.Reset(1920);
        grow.SetTextLength(3, 1000);
        grow.SetTextLength(8, 2000); // 间隔 1s、到 5 字 ⇒ 5 字/秒
        Check("正常间隔建立估计", grow.RatePerSecond > 0.0);
    }

    // "铺开"不等于"永远落后"：真实节拍下连续 10 批，滞后必须有界。
    // 松手时只会 CompleteReveal 补最后那几个字，滞后若是无界的，用户就会看到字幕越落越远。
    private static void TestPacingLagStaysBounded()
    {
        var a = new LiveCaptionAnim();
        a.Reset(1920);
        int t = 1000;
        int total = 0;
        int maxBacklog = 0;
        for (int batch = 0; batch < 10; batch++)
        {
            total += 8;
            a.SetTextLength(total, t);
            for (int f = 0; f < 79; f++) { a.AdvanceReveal(33); t += 33; } // 约 2.6 秒
            int backlog = total - a.RevealedCount;
            if (backlog > maxBacklog) maxBacklog = backlog;
        }
        Check("10 批之间最大滞后不超过一批", maxBacklog <= 8);
        Check("没有越落越远", total - a.RevealedCount <= 8);
    }

    // 一次到达一大段时必须靠积压加速追得上，否则字永远落在人说话后面。
    // 注意时限：积压越大越快（上限 45 字/秒），但积压回落到阈值以内后**故意**回到铺开速率，
    // 所以 50 字不是 1 秒吐完，而是约 4.6 秒吐完（5 秒是留了余量的上界）。
    private static void TestRevealCatchesUpWithBacklog()
    {
        var small = new LiveCaptionAnim();
        small.Reset(1920);
        small.SetTextLength(5, 1000);
        var big = new LiveCaptionAnim();
        big.Reset(1920);
        big.SetTextLength(50, 1000);
        for (int f = 0; f < 10; f++)
        {
            small.AdvanceReveal(33);
            big.AdvanceReveal(33);
        }
        Check("积压越大吐得越快", big.RevealedCount > small.RevealedCount);

        bool caughtUp = false;
        for (int f = 0; f < 160 && !caughtUp; f++)
        {
            caughtUp = big.AdvanceReveal(33) >= 50;
        }
        Check("50 字积压能在 5 秒内追平", caughtUp);
    }

    // 字幕行只在有字要吐之后才开始长，且本次录音内只增不减。
    private static void TestRowOnlyGrows()
    {
        var a = new LiveCaptionAnim();
        a.Reset(1920);
        a.SetTextLength(20, 1000);
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
        a.SetTextLength(30, 1000);
        a.AdvanceReveal(33); // 让字幕行开始
        a.AdvanceLayout(33, 300);
        a.CompleteReveal();
        CheckEq("收尾补齐到全文", 30, a.RevealedCount);
        CheckEq("收尾时字幕行已满高", 30, a.RowPixels);
    }
}
