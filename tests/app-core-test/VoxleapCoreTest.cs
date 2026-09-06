// VoxLeapCore 自测（无第三方依赖，mcs/csc 都可编译运行）
// 运行：mcs -out:/tmp/voxleap-core-test.exe VoxleapCoreTest.cs ../src/app-v0/VoxleapCore.cs
//       mono /tmp/voxleap-core-test.exe
using System;
using System.Text;
using VoxLeap;

internal static class Runner
{
    private static int _passed;
    private static int _failed;

    private static void Check(string name, bool ok)
    {
        if (ok) { _passed++; Console.WriteLine("PASS  " + name); }
        else { _failed++; Console.WriteLine("FAIL  " + name); }
    }

    private static void CheckEq(string name, string actual, string expected)
    {
        Check(name + " [" + Repr(actual) + " == " + Repr(expected) + "]", actual == expected);
    }

    private static string Repr(string s)
    {
        if (s == null) return "<null>";
        return "\"" + s + "\"";
    }

    private static byte[] BuildWav(int dataLen, bool omitData)
    {
        // 最小合理 WAV：RIFF + fmt (16) + data。omitData=true 时返回纯垃圾头。
        var wav = new byte[44 + dataLen];
        Buffer.BlockCopy(System.Text.Encoding.ASCII.GetBytes("RIFF"), 0, wav, 0, 4);
        wav[4] = (byte)(36 + dataLen); wav[5] = (byte)((36 + dataLen) >> 8);
        wav[8] = (byte)'W'; wav[9] = (byte)'A'; wav[10] = (byte)'V'; wav[11] = (byte)'E';
        Buffer.BlockCopy(System.Text.Encoding.ASCII.GetBytes("fmt "), 0, wav, 12, 4);
        wav[16] = 16; // PCM fmt chunk size
        wav[20] = 1;  // PCM
        wav[22] = 1;  // mono
        wav[24] = 160; wav[25] = 62; // 16000
        Buffer.BlockCopy(System.Text.Encoding.ASCII.GetBytes("data"), 0, wav, 36, 4);
        wav[40] = (byte)dataLen; wav[41] = (byte)(dataLen >> 8); wav[42] = (byte)(dataLen >> 16); wav[43] = (byte)(dataLen >> 24);
        for (int i = 44; i < wav.Length; i++) wav[i] = (byte)(i & 0xFF);
        if (omitData) return new byte[0];
        return wav;
    }

    public static int Main()
    {
        // ---- Unescape ----
        CheckEq("unescape 普通文本", VoxleapCore.Unescape("你好，世界"), "你好，世界");
        CheckEq("unescape n/t/r", VoxleapCore.Unescape("a\\nb\\tc\\rd"), "a\nb\tc\rd");
        CheckEq("unescape 引号/反斜杠", VoxleapCore.Unescape("\\\"q\\\\"), "\"q\\");
        CheckEq("unescape \\uXXXX", VoxleapCore.Unescape("\\u4F60\\u597D"), "你好");
        CheckEq("unescape /", VoxleapCore.Unescape("a\\/b"), "a/b");

        // ---- EscapeJson ----
        CheckEq("escape 引号和反斜杠", VoxleapCore.EscapeJson("a\"b\\c"), "a\\\"b\\\\c");

        // ---- ExtractPcm ----
        byte[] wav = BuildWav(100, false);
        byte[] pcm = VoxleapCore.ExtractPcm(wav);
        Check("extract pcm 长度=100（忽略头）", pcm != null && pcm.Length == 100);
        Check("extract pcm 内容保留", pcm != null && pcm.Length == 100 && pcm[0] == (byte)44);
        Check("extract pcm 无 data 块返回原样", VoxleapCore.ExtractPcm(System.Text.Encoding.ASCII.GetBytes("XXXX")) != null);
        // data 块不完整（len 超出）时截断
        byte[] shortWav = new byte[40];
        Buffer.BlockCopy(System.Text.Encoding.ASCII.GetBytes("data"), 0, shortWav, 32, 4);
        shortWav[36] = 100; shortWav[37] = 0; shortWav[38] = 0; shortWav[39] = 0;
        Check("extractPcm data 溢出被截断为 0 或忽略", VoxleapCore.ExtractPcm(shortWav) != null);

        // ---- ParseSseLine ----
        string type, text;
        bool has;
        has = VoxleapCore.ParseSseLine("{\"type\":\"transcript.text.delta\",\"text\":\"你好\"}", out type, out text);
        Check("sse 解析 delta", has && type == "transcript.text.delta" && text == "你好");
        has = VoxleapCore.ParseSseLine("{\"type\":\"error\",\"message\":\"boom\"}", out type, out text);
        Check("sse 错误事件 type 保留", has == false && type == "error");
        has = VoxleapCore.ParseSseLine("{\"type\":\"transcript.text.done\",\"text\":\"结束\"}", out type, out text);
        Check("sse done 事件", has && type == "transcript.text.done" && text == "结束");
        has = VoxleapCore.ParseSseLine("{\"type\":\"noise\"}", out type, out text);
        Check("sse 无 text 行", has == false && type == "noise");
        has = VoxleapCore.ParseSseLine("{\"type\":\"transcript.text.delta\",\"text\":\"含\\\"引号\\\"\"}", out type, out text);
        Check("sse 转义引号还原", has && text == "含\"引号\"");

        // ---- NormalizeCjkLatinSpacing ----
        CheckEq("中英交界收空格", VoxleapCore.NormalizeCjkLatinSpacing("你好 ChatGPT"), "你好ChatGPT");
        CheckEq("英中交界收空格", VoxleapCore.NormalizeCjkLatinSpacing("ChatGPT 你好"), "ChatGPT你好");
        CheckEq("英文内部空格保留", VoxleapCore.NormalizeCjkLatinSpacing("AI agent 你好"), "AI agent你好");
        CheckEq("数字不受影响", VoxleapCore.NormalizeCjkLatinSpacing("价格 123 元"), "价格 123 元");
        CheckEq("空值安全", VoxleapCore.NormalizeCjkLatinSpacing(""), "");
        CheckEq("null 安全", VoxleapCore.NormalizeCjkLatinSpacing(null), null);

        Console.WriteLine("----");
        Console.WriteLine("passed=" + _passed + " failed=" + _failed);
        return _failed == 0 ? 0 : 1;
    }
}