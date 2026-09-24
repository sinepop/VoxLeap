// ------------------------------------------------------------------
// LatencyTrace.cs — 会话级延迟分解（纯逻辑，不依赖 WinForms / 网络 / 音频）
//
// 目的：回答一个目前无人能回答的问题——「松手之后的等待，到底花在哪？」
//   保存 / 准备(VAD+base64+JSON) / 连接握手 / 真实上传 / 服务端首字节 / SSE 回传 / 整理 / 注入
//
// 隐私约束（AGENTS.md 第 5 条）：只记录阶段名、毫秒、字节数、字数与状态码；
// 绝不记录转写正文、音频内容、剪贴板内容或凭据。
//
// 可测性：构造函数允许注入时钟，因此判定逻辑与格式化完全可确定性单测。
// ------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace VoxLeap
{
    internal sealed class LatencyTrace
    {
        // 阶段键：数组顺序即时间顺序。
        public const string Release = "release";       // 松开热键 = 用户开始等待的起点
        public const string Saved = "saved";           // 录音落盘完成
        public const string Prepared = "prepared";     // VAD + 重打包 + base64 + JSON 组装完成
        public const string Connected = "connected";   // TCP + TLS 连接建立
        public const string Uploaded = "uploaded";     // 请求体真实写入网络完成
        public const string Ttfb = "ttfb";             // 收到响应头（服务端已拿到完整音频）
        public const string FirstDelta = "firstdelta"; // 收到第一条识别增量
        public const string Done = "done";             // 收到最终文本
        public const string Organized = "organized";   // AI 整理返回（关闭时不存在）
        public const string Injected = "injected";     // 写入目标窗口结束

        private static readonly string[] Order = new string[]
        { Release, Saved, Prepared, Connected, Uploaded, Ttfb, FirstDelta, Done, Organized, Injected };

        // 与 Order 对齐的分段名（段 = 该标记 - 上一个存在的标记）。
        private static readonly string[] SegmentNames = new string[]
        { "", "保存", "准备", "连接", "上传", "首字节", "首delta", "回传", "整理", "注入" };

        private readonly Stopwatch _watch;
        private readonly Func<long> _clock;
        private readonly Dictionary<string, long> _marks = new Dictionary<string, long>();
        private readonly Dictionary<string, long> _facts = new Dictionary<string, long>();
        private readonly Dictionary<string, string> _texts = new Dictionary<string, string>();

        public LatencyTrace()
            : this(null)
        {
        }

        // clock 为空时用真实秒表；测试可注入假时钟。
        public LatencyTrace(Func<long> clockMs)
        {
            _clock = clockMs;
            _watch = clockMs == null ? Stopwatch.StartNew() : null;
        }

        private long Now()
        {
            return _clock != null ? _clock() : _watch.ElapsedMilliseconds;
        }

        // 只记录首次出现：首 delta 之后不断到来的 delta 不得覆盖第一次的时间。
        public void Mark(string stage)
        {
            if (stage == null || _marks.ContainsKey(stage)) return;
            _marks[stage] = Now();
        }

        public void Note(string name, long value)
        {
            if (name != null) _facts[name] = value;
        }

        public void NoteText(string name, string value)
        {
            if (name != null) _texts[name] = value ?? "";
        }

        public bool Has(string stage)
        {
            return stage != null && _marks.ContainsKey(stage);
        }

        public long At(string stage)
        {
            long value;
            return _marks.TryGetValue(stage, out value) ? value : -1;
        }

        // 最后出现的阶段（时间轴末端）。
        private long LastMark(out string stage)
        {
            long best = -1;
            stage = "";
            for (int i = 0; i < Order.Length; i++)
            {
                long value = At(Order[i]);
                if (value > best)
                {
                    best = value;
                    stage = Order[i];
                }
            }
            return best;
        }

        // 松手后的总等待毫秒；未标记松手时返回 -1。
        public long PostReleaseMs()
        {
            long release = At(Release);
            if (release < 0) return -1;
            string ignored;
            long last = LastMark(out ignored);
            return last < 0 ? -1 : last - release;
        }

        private long SegmentMs(int orderIndex, out bool present)
        {
            present = false;
            if (orderIndex <= 0 || orderIndex >= Order.Length) return -1;
            long end = At(Order[orderIndex]);
            if (end < 0) return -1;
            // 起点：向前找最近一个存在的标记，避免缺段时把间隔算错。
            for (int i = orderIndex - 1; i >= 0; i--)
            {
                long start = At(Order[i]);
                if (start >= 0)
                {
                    present = true;
                    return end - start;
                }
            }
            return -1;
        }

        // ---- 四个主分量：本地 / 握手 / 上传 / 服务端 ----
        private long LocalMs()
        {
            long release = At(Release);
            long prepared = At(Prepared);
            if (release < 0 || prepared < 0) return -1;
            return prepared - release;
        }

        private long HandshakeMs()
        {
            long prepared = At(Prepared);
            long connected = At(Connected);
            if (prepared < 0 || connected < 0) return -1;
            return connected - prepared;
        }

        private long UploadMs()
        {
            long connected = At(Connected);
            long uploaded = At(Uploaded);
            if (connected < 0 || uploaded < 0) return -1;
            return uploaded - connected;
        }

        // 服务端：从音频收全到最终文本到手（含首字节等待与 SSE 回传）。
        private long ServerMs()
        {
            long uploaded = At(Uploaded);
            long done = At(Done);
            if (uploaded < 0 || done < 0) return -1;
            return done - uploaded;
        }

        // 真实上行速率（KB/s）：直接回答「是链路慢还是服务端慢」。
        // 若远低于链路标称速度，说明不是被本机带宽卡住。
        public double EffectiveUploadKBps()
        {
            long bytes;
            if (!_facts.TryGetValue("reqBytes", out bytes) || bytes <= 0) return -1;
            long upload = UploadMs();
            if (upload <= 0) return -1;
            return bytes / 1024.0 / (upload / 1000.0);
        }

        // 判定：松手后等待由哪一段主导。返回如 "上传主导 655/1523ms(43%)"。
        public string Verdict()
        {
            long post = PostReleaseMs();
            if (post <= 0) return "数据不足";
            long[] values = new long[] { LocalMs(), HandshakeMs(), UploadMs(), ServerMs() };
            string[] names = new string[] { "本地处理", "连接握手", "上传", "服务端" };
            int best = -1;
            for (int i = 0; i < values.Length; i++)
            {
                if (values[i] < 0) continue;
                if (best < 0 || values[i] > values[best]) best = i;
            }
            if (best < 0) return "数据不足";
            int share = (int)Math.Round(values[best] * 100.0 / post);
            return names[best] + "主导 " + values[best] + "/" + post + "ms(" + share + "%)";
        }

        private static string Ms(long value)
        {
            if (value < 0) return "-";
            if (value >= 10000) return (value / 1000.0).ToString("0.0") + "s";
            return value + "ms";
        }

        private static string Kb(long bytes)
        {
            if (bytes <= 0) return "0KB";
            if (bytes < 1024) return bytes + "B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0") + "KB";
            return (bytes / 1024.0 / 1024.0).ToString("0.00") + "MB";
        }

        // 单行、无正文的日志摘要。
        public string ToLogLine()
        {
            var sb = new StringBuilder();
            // 录音时长 = 按下热键到松开（trace 起点即热键按下），这段是用户说话时间，不属于等待。
            long recordMs = At(Release);
            if (recordMs > 0)
            {
                sb.Append("录音=").Append((recordMs / 1000.0).ToString("0.0")).Append("s ");
            }
            long post = PostReleaseMs();
            sb.Append("松手后=").Append(post < 0 ? "-" : post + "ms");
            sb.Append(" | ");
            for (int i = 1; i < Order.Length; i++)
            {
                bool present;
                long ms = SegmentMs(i, out present);
                if (!present) continue;
                sb.Append(SegmentNames[i]).Append('=').Append(Ms(ms)).Append(' ');
            }
            sb.Append("| ");
            long reqBytes;
            if (_facts.TryGetValue("reqBytes", out reqBytes))
            {
                sb.Append("请求=").Append(Kb(reqBytes)).Append(' ');
                double rate = EffectiveUploadKBps();
                if (rate > 0) sb.Append("上行=").Append(rate.ToString("0")).Append("KB/s ");
            }
            long vadIn, vadOut;
            if (_facts.TryGetValue("vadInBytes", out vadIn) && _facts.TryGetValue("vadOutBytes", out vadOut))
            {
                sb.Append("VAD=").Append(Kb(vadIn)).Append("->").Append(Kb(vadOut)).Append(' ');
            }
            string text;
            if (_texts.TryGetValue("status", out text)) sb.Append("状态=").Append(text).Append(' ');
            if (_texts.TryGetValue("outcome", out text)) sb.Append("结果=").Append(text).Append(' ');
            long chars;
            if (_facts.TryGetValue("chars", out chars)) sb.Append("字数=").Append(chars).Append(' ');
            if (_texts.TryGetValue("path", out text)) sb.Append("路径=").Append(text).Append(' ');
            sb.Append("| 判定=").Append(Verdict());
            return sb.ToString();
        }

        // 审阅卡片上的一行短摘要（复用现有 meta 行，不改布局）。
        public string ToMetaSuffix()
        {
            long post = PostReleaseMs();
            // post == 0 表示松手后没有任何实测阶段（例如只有松手标记），显示"0.0s"是噪音。
            if (post <= 0) return "";
            var sb = new StringBuilder();
            sb.Append("松手后 ").Append((post / 1000.0).ToString("0.0")).Append("s");
            long upload = UploadMs();
            long server = ServerMs();
            if (upload >= 0) sb.Append(" · 上传 ").Append(Ms(upload));
            if (server >= 0) sb.Append(" · 服务端 ").Append(Ms(server));
            return sb.ToString();
        }
    }
}
