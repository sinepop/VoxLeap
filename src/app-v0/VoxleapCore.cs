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
        public sealed class TextSafetyReport
        {
            public bool Safe;
            public string Reason = "";
        }

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

        public static byte[] TrimSilencePcm(byte[] pcm, int sampleRate, int threshold, int paddingMs)
        {
            if (pcm == null || pcm.Length < 2 || sampleRate <= 0) return pcm;
            int samples = pcm.Length / 2;
            int frame = Math.Max(80, sampleRate / 100);
            int first = -1;
            int last = -1;
            for (int start = 0; start < samples; start += frame)
            {
                int end = Math.Min(samples, start + frame);
                long sum = 0;
                for (int i = start; i < end; i++)
                {
                    short value = BitConverter.ToInt16(pcm, i * 2);
                    sum += (long)value * value;
                }
                double rms = Math.Sqrt(sum / (double)Math.Max(1, end - start));
                if (rms >= threshold)
                {
                    if (first < 0) first = start;
                    last = end;
                }
            }
            if (first < 0) return new byte[0];
            int padding = sampleRate * Math.Max(0, paddingMs) / 1000;
            first = Math.Max(0, first - padding);
            last = Math.Min(samples, last + padding);
            int length = (last - first) * 2;
            var trimmed = new byte[length];
            Buffer.BlockCopy(pcm, first * 2, trimmed, 0, length);
            return CompactInternalSilencePcm(trimmed, sampleRate, threshold, 800, Math.Min(180, paddingMs));
        }

        public static byte[] CompactInternalSilencePcm(byte[] pcm, int sampleRate, int threshold, int maxGapMs, int keepGapMs)
        {
            if (pcm == null || pcm.Length < 2 || sampleRate <= 0 || maxGapMs <= 0) return pcm;
            int samples = pcm.Length / 2;
            int frame = Math.Max(80, sampleRate / 100);
            int maxSilentFrames = Math.Max(1, maxGapMs * sampleRate / 1000 / frame);
            int keepSamples = Math.Max(0, keepGapMs) * sampleRate / 1000;
            var output = new System.Collections.Generic.List<byte>(pcm.Length);
            int silentStart = -1;
            for (int start = 0; start < samples; start += frame)
            {
                int end = Math.Min(samples, start + frame);
                long sum = 0;
                for (int i = start; i < end; i++)
                {
                    short value = BitConverter.ToInt16(pcm, i * 2);
                    sum += (long)value * value;
                }
                bool voiced = Math.Sqrt(sum / (double)Math.Max(1, end - start)) >= threshold;
                if (voiced)
                {
                    if (silentStart >= 0)
                    {
                        int silentSamples = start - silentStart;
                        int copySamples = silentSamples > maxSilentFrames * frame
                            ? Math.Min(keepSamples, silentSamples)
                            : silentSamples;
                        AppendPcm(output, pcm, silentStart, copySamples);
                        silentStart = -1;
                    }
                    AppendPcm(output, pcm, start, end - start);
                }
                else if (silentStart < 0)
                {
                    silentStart = start;
                }
            }
            if (silentStart >= 0)
            {
                int silentSamples = samples - silentStart;
                AppendPcm(output, pcm, silentStart, silentSamples > maxSilentFrames * frame
                    ? Math.Min(keepSamples, silentSamples)
                    : silentSamples);
            }
            return output.ToArray();
        }

        private static void AppendPcm(System.Collections.Generic.List<byte> output, byte[] pcm, int sampleStart, int sampleCount)
        {
            if (sampleCount <= 0) return;
            int offset = sampleStart * 2;
            int length = Math.Min(sampleCount * 2, pcm.Length - offset);
            for (int i = 0; i < length; i++) output.Add(pcm[offset + i]);
        }

        public static byte[] CreatePcmWav(byte[] pcm, int sampleRate)
        {
            pcm = pcm ?? new byte[0];
            var wav = new byte[44 + pcm.Length];
            Buffer.BlockCopy(Encoding.ASCII.GetBytes("RIFF"), 0, wav, 0, 4);
            WriteInt32(wav, 4, 36 + pcm.Length);
            Buffer.BlockCopy(Encoding.ASCII.GetBytes("WAVEfmt "), 0, wav, 8, 8);
            WriteInt32(wav, 16, 16);
            WriteInt16(wav, 20, 1);
            WriteInt16(wav, 22, 1);
            WriteInt32(wav, 24, sampleRate);
            WriteInt32(wav, 28, sampleRate * 2);
            WriteInt16(wav, 32, 2);
            WriteInt16(wav, 34, 16);
            Buffer.BlockCopy(Encoding.ASCII.GetBytes("data"), 0, wav, 36, 4);
            WriteInt32(wav, 40, pcm.Length);
            Buffer.BlockCopy(pcm, 0, wav, 44, pcm.Length);
            return wav;
        }

        private static void WriteInt16(byte[] bytes, int offset, int value)
        {
            bytes[offset] = (byte)value;
            bytes[offset + 1] = (byte)(value >> 8);
        }

        private static void WriteInt32(byte[] bytes, int offset, int value)
        {
            bytes[offset] = (byte)value;
            bytes[offset + 1] = (byte)(value >> 8);
            bytes[offset + 2] = (byte)(value >> 16);
            bytes[offset + 3] = (byte)(value >> 24);
        }

        // ---- 中英文交界空格收拢（英文短语内部空格保留）----
        public static string NormalizeCjkLatinSpacing(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            const string cjk = "\\u3400-\\u4DBF\\u4E00-\\u9FFF\\uF900-\\uFAFF";
            text = Regex.Replace(text, @"(?<=[" + cjk + @"])[\t \u3000]+(?=[A-Za-z])", "");
            return Regex.Replace(text, @"(?<=[A-Za-z])[\t \u3000]+(?=[" + cjk + @"])", "");
        }

        public static TextSafetyReport ValidateOrganizedText(string original, string candidate)
        {
            var report = new TextSafetyReport();
            if (string.IsNullOrWhiteSpace(candidate))
            {
                report.Reason = "整理结果为空";
                return report;
            }
            if (original == null) original = "";
            string source = NormalizeProtectedText(original);
            string output = NormalizeProtectedText(candidate);
            MatchCollection sourceTokens = Regex.Matches(source, @"https?://[^\s，。！？,!?]+|(?<![A-Za-z])\d+(?:\.\d+)*(?!\d)|[A-Za-z_][A-Za-z0-9_./:-]*[_./:-][A-Za-z0-9_./:-]*");
            foreach (Match token in sourceTokens)
            {
                if (output.IndexOf(token.Value, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    report.Reason = "高风险 Token 未保留: " + token.Value;
                    return report;
                }
            }
            string[] negatives = { "不", "没", "无", "未", "禁止", "不要", "不能", "不会", "not", "no", "never", "cannot", "don't" };
            for (int i = 0; i < negatives.Length; i++)
            {
                int sourceCount = CountToken(source, negatives[i]);
                if (sourceCount > 0 && CountToken(output, negatives[i]) < sourceCount)
                {
                    report.Reason = "否定关系可能被改变: " + negatives[i];
                    return report;
                }
            }
            report.Safe = true;
            return report;
        }

        private static string NormalizeProtectedText(string text)
        {
            return (text ?? "").Replace("，", ",").Replace("。", ".").Replace("：", ":").Replace("／", "/");
        }

        private static int CountToken(string text, string token)
        {
            int count = 0;
            int index = 0;
            while ((index = text.IndexOf(token, index, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                count++;
                index += token.Length;
            }
            return count;
        }
    }
}