// SpeechSegmenter 自测（无第三方依赖，mcs/csc 都可编译运行）
// 运行：csc /out:SpeechSegmenterTest.exe SpeechSegmenterTest.cs ..\..\src\app-v0\SpeechSegmenter.cs
//       SpeechSegmenterTest.exe
using System;
using VoxLeap;

internal static class SpeechSegmenterRunner
{
    private static int _passed;
    private static int _failed;

    private const double SpeechRms = 450 / 32768.0; // 既有 vadThreshold 默认值换算成归一化 RMS
    private const double Voiced = 0.05;
    private const double Silent = 0.001;

    private static void Check(string name, bool ok)
    {
        if (ok) { _passed++; Console.WriteLine("PASS  " + name); }
        else { _failed++; Console.WriteLine("FAIL  " + name); }
    }

    private static void CheckEq(string name, long actual, long expected)
    {
        Check(name + " [" + actual + " == " + expected + "]", actual == expected);
    }

    // counts: [0]=SpeechStarted [1]=SegmentBoundary [2]=AutoStop
    private static void Feed(SpeechSegmenter seg, int frames, double rms, int[] counts)
    {
        for (int i = 0; i < frames; i++)
        {
            SpeechSegmenter.Signal s = seg.Feed(rms);
            if (s == SpeechSegmenter.Signal.SpeechStarted) counts[0]++;
            else if (s == SpeechSegmenter.Signal.SegmentBoundary) counts[1]++;
            else if (s == SpeechSegmenter.Signal.AutoStop) counts[2]++;
        }
    }

    private static SpeechSegmenter New()
    {
        return new SpeechSegmenter(100, SpeechRms, 300, 600, 1200);
    }

    public static int Main()
    {
        TestAllSilence();
        TestBoundaryOnly();
        TestAutoStopAfterBoundary();
        TestShortNoiseDoesNotStop();
        TestTwoSegments();
        TestThresholdInclusive();
        TestEdgeTriggeredOncePerRun();
        TestByteAccounting();
        TestEndThresholdClamped();
        TestFinishRecordsTrailingRun();
        TestTailSpeech();
        TestLogLine();
        TestShouldAutoStop();

        Console.WriteLine("----");
        Console.WriteLine("passed=" + _passed + " failed=" + _failed);
        return _failed == 0 ? 0 : 1;
    }

    private static void TestAllSilence()
    {
        var seg = New();
        var c = new int[3];
        Feed(seg, 20, Silent, c);
        CheckEq("全静音无任何事件", c[0] + c[1] + c[2], 0);
        CheckEq("全静音累计说话为 0", seg.SpeechMsTotal, 0);
        CheckEq("全静音静音游程 = 2000ms", seg.SilenceRunMs, 2000);
        Check("全静音未进入说话态", !seg.InSpeech);
    }

    private static void TestBoundaryOnly()
    {
        var seg = New();
        var c = new int[3];
        Feed(seg, 10, Voiced, c);   // 说话 1000ms
        Feed(seg, 7, Silent, c);    // 静音 700ms > 分界 600ms，但 < 收尾 1200ms
        CheckEq("静音 700ms 只报一次分界", c[1], 1);
        CheckEq("静音 700ms 不触发自动收尾", c[2], 0);
        CheckEq("语音起始报一次", c[0], 1);
    }

    private static void TestAutoStopAfterBoundary()
    {
        var seg = New();
        var c = new int[3];
        Feed(seg, 10, Voiced, c);
        Feed(seg, 30, Silent, c); // 静音 3000ms：先过 600ms 分界，再过 1200ms 收尾
        CheckEq("长静音报一次分界", c[1], 1);
        CheckEq("长静音报一次自动收尾", c[2], 1);
        Check("自动收尾后退出说话态", !seg.InSpeech);
    }

    private static void TestShortNoiseDoesNotStop()
    {
        var seg = New();
        var c = new int[3];
        Feed(seg, 1, Voiced, c);    // 仅 100ms 短促噪声（低于 minSpeech 300ms）
        Feed(seg, 20, Silent, c);
        CheckEq("短噪声不触发分界", c[1], 0);
        CheckEq("短噪声不触发自动收尾", c[2], 0);
        CheckEq("短噪声仍报告语音起始", c[0], 1);
    }

    private static void TestTwoSegments()
    {
        var seg = New();
        var c = new int[3];
        Feed(seg, 10, Voiced, c);  // 段 1：1000ms
        Feed(seg, 8, Silent, c);   // 静音 800ms → 分界
        Feed(seg, 10, Voiced, c);  // 段 2：1000ms
        CheckEq("两段只报一次分界", c[1], 1);
        CheckEq("分界后重新说话算新段起点", c[0], 2);
        CheckEq("两段未触发自动收尾", c[2], 0);
        CheckEq("静音游程 800ms 被记账", seg.SilenceRuns.Count, 1);
        CheckEq("游程长度正确", seg.SilenceRuns[0], 800);
    }

    private static void TestThresholdInclusive()
    {
        var seg = new SpeechSegmenter(100, 0.5, 300, 600, 1200);
        var c = new int[3];
        Feed(seg, 5, 0.5, c); // 恰好等于阈值应算语音（>=）
        CheckEq("RMS 等于阈值算语音", seg.SpeechMsTotal, 500);
        CheckEq("RMS 等于阈值报告语音起始", c[0], 1);
    }

    private static void TestEdgeTriggeredOncePerRun()
    {
        var seg = New();
        var c = new int[3];
        Feed(seg, 10, Voiced, c);
        Feed(seg, 100, Silent, c); // 10s 静音
        CheckEq("10s 静音只报一次分界", c[1], 1);
        CheckEq("10s 静音只报一次自动收尾", c[2], 1);
    }

    private static void TestByteAccounting()
    {
        var seg = New();
        for (int i = 0; i < 10; i++) seg.NoteAudioBytes(3200); // 每帧 100ms 应为 3200 字节
        CheckEq("无丢帧时收发字节相等", seg.ReceivedBytes - seg.ExpectedBytes, 0);
        seg.NoteAudioBytes(1600); // 一帧只交付一半 → 丢帧
        CheckEq("丢帧时差值为负", seg.ReceivedBytes - seg.ExpectedBytes, -1600);
        CheckEq("期望字节数按帧长推算", seg.ExpectedBytes, 11L * 100 * SpeechSegmenter.BytesPerMs);
    }

    private static void TestEndThresholdClamped()
    {
        // 收尾阈值小于分界阈值时，应被钳制为分界值，避免出现"先收尾却报不出分界"的乱序。
        var seg = new SpeechSegmenter(100, SpeechRms, 300, 1200, 600);
        var c = new int[3];
        Feed(seg, 10, Voiced, c);
        Feed(seg, 13, Silent, c);
        CheckEq("钳制后分界仍然报出", c[1], 1);
        CheckEq("钳制后收尾仍然报出", c[2], 1);
    }

    private static void TestFinishRecordsTrailingRun()
    {
        var seg = New();
        var c = new int[3];
        Feed(seg, 10, Voiced, c);
        Feed(seg, 4, Silent, c); // 静音 400ms，未达分界阈值
        CheckEq("结束前游程尚未记账", seg.SilenceRuns.Count, 0);
        seg.Finish();
        CheckEq("Finish 补记末尾游程", seg.SilenceRuns.Count, 1);
        CheckEq("末尾游程长度正确", seg.SilenceRuns[0], 400);
    }

    private static void TestTailSpeech()
    {
        // 结束时仍在说话 → 尾段 = 当前连续语音
        var speaking = New();
        var c1 = new int[3];
        Feed(speaking, 10, Voiced, c1);
        CheckEq("说话中结束的尾段", speaking.Finish(), 1000);

        // 结束时在静音中且未达分界 → 尾段 = 静音前那段语音
        var shortPause = New();
        var c2 = new int[3];
        Feed(shortPause, 10, Voiced, c2);
        Feed(shortPause, 4, Silent, c2);
        CheckEq("短停顿结束的尾段", shortPause.Finish(), 1000);

        // 已经报过分界 → 该段已发走，尾段为空
        var afterSplit = New();
        var c3 = new int[3];
        Feed(afterSplit, 10, Voiced, c3);
        Feed(afterSplit, 8, Silent, c3);
        CheckEq("分界已发走后尾段为空", afterSplit.Finish(), 0);
    }

    private static void TestShouldAutoStop()
    {
        // 一句话都没说过：即使长时间静音也不能收尾，否则误按热键会立刻结束。
        var mute = New();
        var cm = new int[3];
        Feed(mute, 50, Silent, cm);
        Check("从未说话时不自动收尾", !mute.ShouldAutoStop(1200));

        // 正常节奏：说话 1s 后静音 500ms。
        var seg = New();
        var c = new int[3];
        Feed(seg, 10, Voiced, c);
        Feed(seg, 5, Silent, c);
        Check("静音 500ms 未达 1200ms 阈值", !seg.ShouldAutoStop(1200));
        Check("静音 500ms 已达 500ms 阈值", seg.ShouldAutoStop(500));

        // 正在说话时永不收尾。
        var talking = New();
        var ct = new int[3];
        Feed(talking, 10, Voiced, ct);
        Check("正在说话时不自动收尾", !talking.ShouldAutoStop(300));

        // 阈值 <= 0 视为关闭。
        Check("阈值 0 视为关闭", !seg.ShouldAutoStop(0));

        // 查询必须无副作用：宿主每 100ms 调一次，绝不能污染影子记录。
        int runsBefore = seg.SilenceRuns.Count;
        int autoStopsBefore = seg.AutoStops;
        for (int i = 0; i < 20; i++) seg.ShouldAutoStop(1200);
        CheckEq("查询不改变静音游程记录", seg.SilenceRuns.Count, runsBefore);
        CheckEq("查询不改变自动收尾计数", seg.AutoStops, autoStopsBefore);

        // 关键回归：影子 Feed 已按构造阈值置位 _endFired，但宿主阈值可能更大。
        // 若查询复用了 _endFired，配置调大后就会永远收不了尾。
        var late = New();
        var cl = new int[3];
        Feed(late, 10, Voiced, cl);
        Feed(late, 12, Silent, cl); // 1200ms：影子已报 AutoStop
        CheckEq("影子已按 1200ms 收尾", cl[2], 1);
        Check("影子的收尾不阻挡更大的阈值", !late.ShouldAutoStop(1500));
        Feed(late, 4, Silent, cl);  // 静音累计 1600ms
        Check("静音继续增长后更大阈值也能收尾", late.ShouldAutoStop(1500));
    }

    private static void TestLogLine()
    {
        var seg = New();
        var c = new int[3];
        Feed(seg, 10, Voiced, c);
        Feed(seg, 8, Silent, c);
        // NoteAudioBytes 是每帧一次的回调语义，必须逐帧上报（每次累加一帧的期望字节）。
        for (int i = 0; i < 18; i++) seg.NoteAudioBytes(3200);
        seg.Finish();
        string line = seg.ToLogLine();
        Check("摘要含帧数", line.IndexOf("帧=18") >= 0);
        Check("摘要含说话时长", line.IndexOf("说话=1.0s") >= 0);
        Check("摘要含段数", line.IndexOf("段=1") >= 0);
        Check("摘要含字节核对", line.IndexOf("字节=57600/57600") >= 0);
        Check("摘要含尾段", line.IndexOf("尾段=0ms") >= 0);
        Check("摘要含阈值参数", line.IndexOf("分界=600ms") >= 0 && line.IndexOf("收尾=1200ms") >= 0);
    }
}
