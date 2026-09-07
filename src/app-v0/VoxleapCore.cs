// ------------------------------------------------------------------
// VoxleapCore.cs — 纯逻辑层（不依赖 WinForms/系统 API），从 App.cs 抽取，
// 保证转录/解析/文本规范化的核心逻辑可被控制台测试直接验证。
// 编译：mcs/csc 均可；测试见 tests/app-core-test/VoxleapCoreTest.cs
// 注意：保持与 App.cs 原有行为完全一致（同一函数的语义迁移，不做改动）。
// ------------------------------------------------------------------
using System;
using System.Text;
using System.Text.RegularExpressions;

namespace VoxLeap
{
    internal static class VoxleapCore
    {
        // ---- JSON 字段值转义（用于请求体组装）----
        public static string EscapeJson(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        // ---- JSON 字符串反转义：与 AsrClient 原 JsonUtil.Unescape 语义一致 ----
        public static string Unescape(string s)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < s.Length)
                {
                    char n = s[++i];
                    if (n == 'n') sb.Append('\n');
                    else if (n == 't') sb.Append('\t');
                    else if (n == 'r') sb.Append('\r');
                    else if (n == '"') sb.Append('"');
                    else if (n == '\\') sb.Append('\\');
                    else if (n == '/') sb.Append('/');
                    else if (n == 'u' && i + 4 < s.Length)
                    {
                        int code;
                        if (int.TryParse(s.Substring(i + 1, 4), System.Globalization.NumberStyles.HexNumber, null, out code))
                        {
                            sb.Append((char)code);
                            i += 4;
                        }
                    }
                    else sb.Append(n);
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        // ---- SSE 单行 payload 解析：提取事件 type 与首段 text ----
        // 返回是否命中 "text" 字段（type 无匹配时为空串，与旧实现一致）。
        public static bool ParseSseLine(string payload, out string type, out string text)
        {
            type = "";
            text = "";
            if (payload == null) return false;
            Match tm = Regex.Match(payload, "\"type\"\\s*:\\s*\"([^\"]+)\"");
            if (tm.Success) type = tm.Groups[1].Value;
            Match xm = Regex.Match(payload, "\"text\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (xm.Success) text = Unescape(xm.Groups[1].Value);
            return xm.Success;
        }

        // ---- 从 WAV 中取出 data 块的裸 PCM；解析失败时原样返回 ----
        public static byte[] ExtractPcm(byte[] wav)
        {
            if (wav == null) return null;
            int i = 12;
            while (i + 8 <= wav.Length)
            {
                // 对抗审查 P2：size 由字节拼接，需防恶意/损坏 WAV 中 0x7FFFFFF8..0x7FFFFFFF
                // 的正大值导致 8+size 整数溢出。用 long 计算并钳制到剩余长度，避免死循环或越界。
                long size = (long)wav[i + 4] | ((long)wav[i + 5] << 8) | ((long)wav[i + 6] << 16) | ((long)wav[i + 7] << 24);
                if (size < 0) break;
                if (size > wav.Length - i - 8L) size = wav.Length - i - 8L; // 超出剩余数据视为损坏，钳制后继续
                if (wav[i] == (byte)'d' && wav[i + 1] == (byte)'a' && wav[i + 2] == (byte)'t' && wav[i + 3] == (byte)'a')
                {
                    int start = i + 8;
                    int len = (int)Math.Min(size, wav.Length - start);
                    if (len > 0)
                    {
                        var pcm = new byte[len];
                        Buffer.BlockCopy(wav, start, pcm, 0, len);
                        return pcm;
                    }
                }
                long next = i + 8L + size + (size & 1);
                if (next > wav.Length || next <= i) break; // 不再前进则终止，防死循环
                i = (int)next;
                if (size == 0) break;
            }
            return wav;
        }

        // ---- 中英文交界空格收拢（英文短语内部空格保留）----
        public static string NormalizeCjkLatinSpacing(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            const string cjk = "\\u3400-\\u4DBF\\u4E00-\\u9FFF\\uF900-\\uFAFF";
            text = Regex.Replace(text, @"(?<=[" + cjk + @"])[\t \u3000]+(?=[A-Za-z])", "");
            return Regex.Replace(text, @"(?<=[A-Za-z])[\t \u3000]+(?=[" + cjk + @"])", "");
        }
    }
}