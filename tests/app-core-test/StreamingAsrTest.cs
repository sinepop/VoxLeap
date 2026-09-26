using System;
using VoxLeap;

// 边说边送的结果账本测试。
//
// 为什么单独测它：分段请求在线程池上并发完成，**完成顺序与段序无关**，而分段的正确性
// 完全依赖"按段序落位 + 不完整就不交出文本"。这一段是纯逻辑，可以完全离线验证；
// 端到端（真麦克风 + 真 API）无法在测试里覆盖，所以这里必须测严。
internal static class StreamingAsrTest
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
        TestOrderIndependentOfCompletion();
        TestFailureBlocksStitch();
        TestUnsettledIsNotComplete();
        TestEmptySegmentSettles();
        Console.WriteLine("----");
        Console.WriteLine("passed=" + _passed + " failed=" + _failed);
        return _failed == 0 ? 0 : 1;
    }

    // 乱序完成必须按段序拼接。
    private static void TestOrderIndependentOfCompletion()
    {
        var l = new SegmentLedger();
        int a = l.Dispatch();
        int b = l.Dispatch();
        int c = l.Dispatch();
        l.NoteText(c, "丙");
        l.NoteText(a, "甲");
        l.NoteText(b, "乙");
        CheckEq("乱序完成按段序拼接", "甲乙丙", l.Stitch());
        Check("全部结算", l.AllSettled);
        CheckEq("派发数", 3, l.Dispatched);
        CheckEq("结算数", 3, l.Settled);
    }

    // 任一段失败 ⇒ 整次流式作废，必须回退整段上传。
    private static void TestFailureBlocksStitch()
    {
        var l = new SegmentLedger();
        int a = l.Dispatch();
        int b = l.Dispatch();
        l.NoteText(a, "前半句");
        l.NoteFailure(b, "HTTP 500");
        Check("有失败时 Stitch 返回 null", l.Stitch() == null);
        Check("AnyFailed 为真", l.AnyFailed);
        Check("失败也算结算", l.AllSettled);
        Check("日志含失败数", l.ToLogLine().Contains("失败=1"));
        Check("日志含首个失败原因", l.ToLogLine().Contains("HTTP 500"));
    }

    // 有段始终没结算（请求卡住/超时未归）时**绝不能**把其余段拼起来交出去——
    // 那会得到中间缺一段、读不通的句子。这是最容易写错的一条。
    private static void TestUnsettledIsNotComplete()
    {
        var l = new SegmentLedger();
        int a = l.Dispatch();
        l.Dispatch();
        l.NoteText(a, "前半句");
        Check("有段未结算时 Stitch 返回 null", l.Stitch() == null);
        Check("有段未结算时不算全部结算", !l.AllSettled);
        CheckEq("未结算时结算数", 1, l.Settled);
    }

    // 无语音段（本地 VAD 已判定）必须结算，否则等待方会一直等到超时。
    private static void TestEmptySegmentSettles()
    {
        var l = new SegmentLedger();
        int a = l.Dispatch();
        int b = l.Dispatch();
        l.NoteText(a, "有字");
        l.NoteEmpty(b);
        Check("无语音段也结算", l.AllSettled);
        CheckEq("无语音段不贡献文本", "有字", l.Stitch());
        CheckEq("无语音段计数", 1, l.EmptySegments);
        Check("无语音段不算失败", !l.AnyFailed);
    }
}
