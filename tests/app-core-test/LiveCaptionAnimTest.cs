using System;
using VoxLeap;

// 录音期字幕动画的纯逻辑测试。
//
// 为什么单独测它：分层窗口的动画本身没法在测试里跑（要真窗口、要 30fps），但它依赖的
// 算术全是可离线验证的不变量 —— 胶囊宽度只增不减、绝不超过上限、缓动不过冲且收敛、
// 逐字展开不超调且积压时追得上、收尾一次补齐、结果与帧率无关。
// 这些一旦破了，用户看到的就是浮层在屏幕底部来回缩、或者字吐到一半停住。
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
        a.SetTextLength(400); // 400 字，远超一行
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

    // 逐字展开：单调、不超调、绝对不超过文本长度。
    private static void TestRevealMonotonicAndNoOvershoot()
    {
        var a = new LiveCaptionAnim();
        a.Reset(1920);
        a.SetTextLength(10);
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
        CheckEq("最终吐完", 10, a.RevealedCount);
    }

    // 一次到达一大段时必须靠积压加速追得上，否则字永远落在人说话后面。
    private static void TestRevealCatchesUpWithBacklog()
    {
        var small = new LiveCaptionAnim();
        small.Reset(1920);
        small.SetTextLength(5);
        var big = new LiveCaptionAnim();
        big.Reset(1920);
        big.SetTextLength(50);
        for (int f = 0; f < 10; f++)
        {
            small.AdvanceReveal(33);
            big.AdvanceReveal(33);
        }
        Check("积压越大吐得越快", big.RevealedCount > small.RevealedCount);

        bool caughtUp = false;
        for (int f = 0; f < 100 && !caughtUp; f++)
        {
            caughtUp = big.AdvanceReveal(33) >= 50;
        }
        Check("50 字积压能在 3.3 秒内追平", caughtUp);
    }

    // 字幕行只在有字要吐之后才开始长，且本次录音内只增不减。
    private static void TestRowOnlyGrows()
    {
        var a = new LiveCaptionAnim();
        a.Reset(1920);
        a.SetTextLength(20);
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
        a.SetTextLength(30);
        a.AdvanceReveal(33); // 让字幕行开始
        a.AdvanceLayout(33, 300);
        a.CompleteReveal();
        CheckEq("收尾补齐到全文", 30, a.RevealedCount);
        CheckEq("收尾时字幕行已满高", 30, a.RowPixels);
    }
}
