// LatencyTrace 自测（无第三方依赖，mcs/csc 都可编译运行）
// 运行：csc /out:LatencyTraceTest.exe LatencyTraceTest.cs ..\..\src\app-v0\LatencyTrace.cs
//       LatencyTraceTest.exe
// 注入假时钟，因此全部断言可确定性复现，不依赖真实耗时。
using System;
using VoxLeap;

internal static class LatencyTraceRunner
{
    private static int _passed;
    private static int _failed;

    private static void Check(string name, bool ok)
    {
        if (ok) { _passed++; Console.WriteLine("PASS  " + name); }
        else { _failed++; Console.WriteLine("FAIL  " + name); }
    }

    private static void CheckEq(string name, long actual, long expected)
    {
        Check(name + " [" + actual + " == " + expected + "]", actual == expected);
    }

    private static void CheckStr(string name, string actual, string expected)
    {
        Check(name + " [" + actual + " == " + expected + "]", actual == expected);
    }

    public static int Main()
    {
        TestFullSessionSegments();
        TestMarkKeepsFirst();
        TestServerDominated();
        TestMissingStagesFallBack();
        TestInsufficientData();
        TestUploadThroughput();
        TestMetaSuffix();
        TestPrivacyWhitelist();

        Console.WriteLine("----");
        Console.WriteLine("passed=" + _passed + " failed=" + _failed);
        return _failed == 0 ? 0 : 1;
    }

    // 一个完整会话：录音 9s，松手后共 1500ms，其中上传 650ms 最大 → 上传主导。
    private static void TestFullSessionSegments()
    {
        long now = 0;
        var t = new LatencyTrace(delegate() { return now; });
        now = 9000; t.Mark(LatencyTrace.Release);     // 录音 9.0s
        now = 9010; t.Mark(LatencyTrace.Saved);       // 保存 10ms
        now = 9020; t.Mark(LatencyTrace.Prepared);    // 准备 10ms
        now = 9250; t.Mark(LatencyTrace.Connected);   // 连接 230ms
        now = 9900; t.Mark(LatencyTrace.Uploaded);    // 上传 650ms
        now = 10400; t.Mark(LatencyTrace.Ttfb);       // 首字节 500ms
        now = 10450; t.Mark(LatencyTrace.FirstDelta); // 首 delta 50ms
        now = 10500; t.Mark(LatencyTrace.Done);       // 回传 50ms

        CheckEq("松手后总等待 = 最后标记 - 松手", t.PostReleaseMs(), 1500);
        CheckStr("判定为上传主导", t.Verdict(), "上传主导 650/1500ms(43%)");

        string line = t.ToLogLine();
        Check("日志含录音时长", line.IndexOf("录音=9.0s") >= 0);
        Check("日志含松手后总等待", line.IndexOf("松手后=1500ms") >= 0);
        Check("日志含上传分段", line.IndexOf("上传=650ms") >= 0);
        Check("日志含服务端首字节", line.IndexOf("首字节=500ms") >= 0);
        Check("日志以判定结尾", line.IndexOf("判定=上传主导 650/1500ms(43%)") >= 0);
    }

    // Mark 只记首次：后续 delta 不得覆盖第一条 delta 的时间。
    private static void TestMarkKeepsFirst()
    {
        long now = 0;
        var t = new LatencyTrace(delegate() { return now; });
        now = 100; t.Mark(LatencyTrace.FirstDelta);
        now = 200; t.Mark(LatencyTrace.FirstDelta);
        now = 300; t.Mark(LatencyTrace.FirstDelta);
        CheckEq("Mark 只记首次", t.At(LatencyTrace.FirstDelta), 100);
        Check("Has 对已标记返回 true", t.Has(LatencyTrace.FirstDelta));
        Check("Has 对未标记返回 false", !t.Has(LatencyTrace.Done));
        CheckEq("未标记阶段返回 -1", t.At(LatencyTrace.Done), -1);
    }

    // 服务端慢：上传 1000ms，服务端 3980ms → 服务端主导。
    private static void TestServerDominated()
    {
        long now = 0;
        var t = new LatencyTrace(delegate() { return now; });
        t.Mark(LatencyTrace.Release);
        now = 10; t.Mark(LatencyTrace.Prepared);
        now = 20; t.Mark(LatencyTrace.Connected);
        now = 1020; t.Mark(LatencyTrace.Uploaded);
        now = 3000; t.Mark(LatencyTrace.Ttfb);
        now = 5000; t.Mark(LatencyTrace.Done);
        CheckStr("判定为服务端主导", t.Verdict(), "服务端主导 3980/5000ms(80%)");
    }

    // 缺段容错：中间阶段缺失时，分段向前回退到最近存在的标记，并且不崩溃。
    private static void TestMissingStagesFallBack()
    {
        long now = 0;
        var t = new LatencyTrace(delegate() { return now; });
        t.Mark(LatencyTrace.Release);
        now = 1010; t.Mark(LatencyTrace.Uploaded); // 保存/准备/连接全部缺失
        now = 2000; t.Mark(LatencyTrace.Done);
        CheckEq("松手后总等待正确", t.PostReleaseMs(), 2000);
        string v = t.Verdict();
        Check("缺段时仍给出判定且不崩溃: " + v, v.IndexOf("数据不足") < 0);
        // 分段回退：Uploaded 段吸收前面所有缺失段 → 1010
        Check("缺段分段向前回退", t.ToLogLine().IndexOf("上传=1010ms") >= 0);
    }

    // 无数据 / 只有一个标记时不得给出误导性判定。
    private static void TestInsufficientData()
    {
        var empty = new LatencyTrace(delegate() { return 0; });
        CheckEq("空 trace 松手后为 -1", empty.PostReleaseMs(), -1);
        CheckStr("空 trace 判定为数据不足", empty.Verdict(), "数据不足");

        long now = 0;
        var only = new LatencyTrace(delegate() { return now; });
        only.Mark(LatencyTrace.Release);
        CheckEq("仅松手时总等待为 0", only.PostReleaseMs(), 0);
        CheckStr("仅松手时判定为数据不足", only.Verdict(), "数据不足");
        CheckStr("仅松手时 meta 为空", only.ToMetaSuffix(), "");
    }

    // 真实上行速率：直接回答"是链路慢还是服务端慢"。
    private static void TestUploadThroughput()
    {
        long now = 0;
        var t = new LatencyTrace(delegate() { return now; });
        t.Mark(LatencyTrace.Release);
        now = 10; t.Mark(LatencyTrace.Prepared);
        now = 20; t.Mark(LatencyTrace.Connected);
        now = 670; t.Mark(LatencyTrace.Uploaded); // 上传 650ms
        now = 1000; t.Mark(LatencyTrace.Done);
        t.Note("reqBytes", 417000); // 10s 录音的 base64 请求体
        // 417000/1024 = 407.2 KB / 0.65s = 626.5 KB/s
        CheckEq("上行速率 KB/s 计算", (long)t.EffectiveUploadKBps(), 626);
        Check("无 reqBytes 时速率返回负数", new LatencyTrace(delegate() { return 0; }).EffectiveUploadKBps() < 0);
    }

    // 审阅卡片摘要。
    private static void TestMetaSuffix()
    {
        long now = 0;
        var t = new LatencyTrace(delegate() { return now; });
        now = 9000; t.Mark(LatencyTrace.Release);
        now = 9010; t.Mark(LatencyTrace.Prepared);
        now = 9250; t.Mark(LatencyTrace.Connected);
        now = 9900; t.Mark(LatencyTrace.Uploaded);
        now = 10500; t.Mark(LatencyTrace.Done);
        CheckStr("meta 摘要格式", t.ToMetaSuffix(), "松手后 1.5s · 上传 650ms · 服务端 600ms");
    }

    // 隐私白名单：只有已知键会被打印，任何未列入的键（例如误传的转写正文）不得出现在日志里。
    private static void TestPrivacyWhitelist()
    {
        long now = 0;
        var t = new LatencyTrace(delegate() { return now; });
        now = 1000; t.Mark(LatencyTrace.Release);
        now = 2000; t.Mark(LatencyTrace.Done);
        t.NoteText("transcript", "这是转写正文，绝不能进日志");
        t.NoteText("apiKey", "sk-abcdef0123456789");
        string line = t.ToLogLine();
        Check("未列入白名单的转写正文不进日志", line.IndexOf("绝不能进日志") < 0);
        Check("未列入白名单的密钥不进日志", line.IndexOf("sk-abcdef0123456789") < 0);
        Check("白名单键仍然输出", line.IndexOf("松手后=1000ms") >= 0);
    }
}
