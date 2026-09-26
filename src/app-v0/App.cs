// ------------------------------------------------------------------
// VoxLeap Personal v0.1 — 单可执行文件语音输入工具（C# 5 / .NET Framework 4.x）
// 编译：Windows 自带 csc.exe（见 build.cmd），无需安装任何 SDK。
// 已验证路径：阶跃星辰 StepFun SSE；高级设置里另提供 OpenAI 兼容转写（未验证）。
// ------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace VoxLeap
{
    internal static class Program
    {
        // 二次启动进程通过该命名事件请运行中的托盘进程打开设置窗口。
        internal const string SettingsSignalName = "Local\\VoxLeapOpenSettings";

        private static void RequestTraySettings()
        {
            try
            {
                using (EventWaitHandle signal = EventWaitHandle.OpenExisting(SettingsSignalName))
                {
                    signal.Set();
                }
            }
            catch { }
        }

        [STAThread]
        private static void Main(string[] args)
        {
            bool openSettings = args != null && Array.Exists(args,
                delegate(string arg) { return string.Equals(arg, "--settings", StringComparison.OrdinalIgnoreCase); });
            bool createdNew;
            using (var mutex = new Mutex(true, "Local\\VoxLeapPersonal", out createdNew))
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                // 主托盘进程和独立设置进程都可能执行连接测试，必须共享 TLS 基线。
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                if (!createdNew)
                {
                    // 托盘进程已在运行：默认静默退出，不弹任何窗口（设置只能从托盘打开）。
                    // 只有显式带 --settings 参数时，才通知运行中的托盘进程弹出设置。
                    if (openSettings) RequestTraySettings();
                    Log.Write("二次启动: 托盘已在运行, 请求打开设置=" + openSettings);
                    return;
                }
                AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
                {
                    Log.Write("未处理异常: " + e.ExceptionObject);
                };
                // 清理历史残留的临时音频（转写中途崩溃时可能遗留），默认不保存音频。
                try
                {
                    foreach (string f in Directory.GetFiles(Path.GetTempPath(), "voxleap_*.wav"))
                    {
                        File.Delete(f);
                    }
                }
                catch { }
                Application.Run(new TrayContext(openSettings));
            }
        }
    }

    internal static class Log
    {
        private static readonly object Gate = new object();

        public static string FilePath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "voxleap.log"); }
        }

        // 隐私约束：日志只记录状态与长度，不记录转写正文。
        public static void Write(string line)
        {
            try
            {
                lock (Gate)
                {
                    File.AppendAllText(FilePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff ") + line + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch { }
        }
    }

    internal sealed class Config
    {
        public const string StepFunBaseUrlDefault = "https://api.stepfun.com/step_plan/v1";
        public const string StepFunEndpointDefault = "/audio/asr/sse";
        public const string StepFunModelDefault = "stepaudio-2.5-asr";

        public string BaseUrl = StepFunBaseUrlDefault;
        public string Endpoint = StepFunEndpointDefault;
        public string Model = StepFunModelDefault;
        public string Api = "sse";
        public string ApiKey = "";
        public bool AutoInsert = false;
        public string Language = "zh";
        public string Hotkey = "RControl";
        public string HotkeyMode = "hold";
        public string Hotwords = "";
        public int ClipboardThreshold = 200;
        public int RequestTimeoutMs = 20000;
        public int MaxRecordMs = 300000; // 单次录音时长上限，0=不限制
        public bool EnableVad = true;
        public int VadThreshold = 450;
        public int VadPaddingMs = 180;
        // 静音自动停止。默认关闭，与"默认审阅后输入""默认不自动输入"同属收紧默认：
        // 开启后不必一直按住热键，连续静音超过 AutoStopSilenceMs 即自动结束并开始识别。
        public bool AutoStopOnSilence = false;
        public int AutoStopSilenceMs = 1200;
        // 边说边送：说话期间就把已完成的分段送出去识别，松手时只剩尾段要传。
        // 默认关闭（默认收紧）；任一段失败即回退整段上传，因此正确性不依赖它。
        public bool StreamingSegments = false;
        public bool AiOrganize = false;
        public string OrganizerBaseUrl = "";
        public string OrganizerEndpoint = "/chat/completions";
        public string OrganizerModel = "";
        public string OrganizerApiKey = "";
        public string ParseIssue = ""; // 配置解析问题描述，供启动时提示
        public bool LoadedLegacyPlaintextKey = false; // 仅用于一次性 DPAPI 迁移，不写回配置

        public static string SettingsPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json"); }
        }

        public bool HasKey
        {
            get { return !string.IsNullOrEmpty((ApiKey ?? "").Trim()) && ApiKey.IndexOf("在这里") < 0; }
        }

        public Config Clone()
        {
            var copy = new Config();
            copy.CopyFrom(this);
            return copy;
        }

        public void CopyFrom(Config other)
        {
            if (other == null) return;
            BaseUrl = other.BaseUrl;
            Endpoint = other.Endpoint;
            Model = other.Model;
            Api = other.Api;
            ApiKey = other.ApiKey;
            AutoInsert = other.AutoInsert;
            Language = other.Language;
            Hotkey = other.Hotkey;
            HotkeyMode = other.HotkeyMode;
            Hotwords = other.Hotwords;
            ClipboardThreshold = other.ClipboardThreshold;
            RequestTimeoutMs = other.RequestTimeoutMs;
            MaxRecordMs = other.MaxRecordMs;
            EnableVad = other.EnableVad;
            VadThreshold = other.VadThreshold;
            VadPaddingMs = other.VadPaddingMs;
            AutoStopOnSilence = other.AutoStopOnSilence;
            StreamingSegments = other.StreamingSegments;
            AutoStopSilenceMs = other.AutoStopSilenceMs;
            AiOrganize = other.AiOrganize;
            OrganizerBaseUrl = other.OrganizerBaseUrl;
            OrganizerEndpoint = other.OrganizerEndpoint;
            OrganizerModel = other.OrganizerModel;
            OrganizerApiKey = other.OrganizerApiKey;
            ParseIssue = other.ParseIssue;
            LoadedLegacyPlaintextKey = other.LoadedLegacyPlaintextKey;
        }

        public static Config Load()
        {
            return ConfigStore.LoadFromPath(new DpapiSecretProtector());
        }
    }

    internal static class JsonUtil
    {
        public static string Unescape(string s)
        {
            return VoxleapCore.Unescape(s);
        }
    }

    internal static class Native
    {
        public const int WH_KEYBOARD_LL = 13;
        public const int WM_KEYDOWN = 0x100;
        public const int WM_KEYUP = 0x101;
        public const int WM_SYSKEYDOWN = 0x104;
        public const int WM_SYSKEYUP = 0x105;
        public const int VK_ESCAPE = 0x1B;
        public const int VK_CAPITAL = 0x14;
        public const int VK_SHIFT = 0x10;
        public const int VK_CONTROL = 0x11;
        public const int VK_MENU = 0x12;
        public const int VK_LSHIFT = 0xA0;
        public const int VK_RSHIFT = 0xA1;
        public const int VK_LCONTROL = 0xA2;
        public const int VK_RCONTROL = 0xA3;
        public const int VK_LMENU = 0xA4;
        public const int VK_RMENU = 0xA5;
        public const int VK_LWIN = 0x5B;
        public const int VK_RWIN = 0x5C;
        public const int LLKHF_INJECTED = 0x10;
        public const uint ES_PASSWORD = 0x0020;
        public const int GWL_STYLE = -16;
        public const uint KEYEVENTF_KEYUP = 0x0002;
        public const uint KEYEVENTF_UNICODE = 0x0004;
        public const uint INPUT_KEYBOARD = 1;
        public const int ULW_ALPHA = 2;
        public const byte AC_SRC_ALPHA = 1;

        public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;

            public POINT(int x, int y)
            {
                X = x;
                Y = y;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE
        {
            public int Width;
            public int Height;

            public SIZE(int width, int height)
            {
                Width = width;
                Height = height;
            }
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFOHEADER
        {
            public uint Size;
            public int Width;
            public int Height;
            public ushort Planes;
            public ushort BitCount;
            public uint Compression;
            public uint ImageSize;
            public int XPelsPerMeter;
            public int YPelsPerMeter;
            public uint ColorsUsed;
            public uint ColorsImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFO
        {
            public BITMAPINFOHEADER Header;
            public uint Colors;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct GUITHREADINFO
        {
            public uint cbSize;
            public uint flags;
            public IntPtr hwndActive;
            public IntPtr hwndFocus;
            public IntPtr hwndCapture;
            public IntPtr hwndMenuOwner;
            public IntPtr hwndMoveSize;
            public IntPtr hwndCaret;
            public RECT rcCaret;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct InputUnion
        {
            [FieldOffset(0)]
            public MOUSEINPUT mi;
            [FieldOffset(0)]
            public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT
        {
            public uint type;
            public InputUnion U;
        }

        [DllImport("user32.dll")] public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("kernel32.dll")] public static extern IntPtr GetModuleHandle(string lpModuleName);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] public static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO pgui);
        [DllImport("user32.dll")] public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UpdateLayeredWindow(
            IntPtr hWnd,
            IntPtr hdcDst,
            ref POINT pptDst,
            ref SIZE psize,
            IntPtr hdcSrc,
            ref POINT pptSrc,
            int crKey,
            ref BLENDFUNCTION pblend,
            int dwFlags);
        [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll", SetLastError = true)] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
        [DllImport("gdi32.dll", SetLastError = true)] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll", SetLastError = true)] public static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll", SetLastError = true)] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);
        [DllImport("gdi32.dll", SetLastError = true)] public static extern bool DeleteObject(IntPtr hgdiobj);
        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern IntPtr CreateDIBSection(
            IntPtr hdc,
            ref BITMAPINFO bitmapInfo,
            uint usage,
            out IntPtr bits,
            IntPtr section,
            uint offset);

        // ---- 进程完整性级别查询（UIPI 识别用）----
        [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool CloseHandle(IntPtr hObject);
        [DllImport("advapi32.dll", SetLastError = true)] public static extern bool OpenProcessToken(IntPtr hProcess, uint dwDesiredAccess, out IntPtr hToken);
        [DllImport("advapi32.dll", SetLastError = true)] public static extern bool GetTokenInformation(IntPtr hToken, int tokenInformationClass, IntPtr tokenInformation, uint tokenInformationLength, out uint returnLength);
        [DllImport("advapi32.dll")] public static extern IntPtr GetSidSubAuthority(IntPtr pSid, uint nSubAuthority);
        [DllImport("advapi32.dll")] public static extern byte GetSidSubAuthorityCount(IntPtr pSid);

        [StructLayout(LayoutKind.Sequential)]
        public struct SID_AND_ATTRIBUTES
        {
            public IntPtr Sid;
            public uint Attributes;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct TOKEN_MANDATORY_LABEL
        {
            public SID_AND_ATTRIBUTES Label;
        }
    }

    // 约束：MCI 设备有线程亲和性，本类的所有调用必须来自同一线程（UI 线程）。
    internal static class Recorder
    {
        private const string Alias = "voxleaprec";
        private static bool _open;
        private static WaveInLevelMonitor _levelMonitor;
        private static int _levelMilli;

        // ---- 实时分段（影子模式）：只观察、只记录，不改变录音/识别/注入的任何行为 ----
        // 分界 600ms / 收尾 1200ms 是待验证的观察参数，不是用户设置。等真实录音日志
        // 确认阈值合理后，再让它真正驱动「静音自动停止」与「边说边送」。
        private const int ShadowMinSpeechMs = 300;
        private const int ShadowSplitSilenceMs = 600;
        private const int ShadowEndSilenceMs = 1200;
        private static SpeechSegmenter _segmenter;
        private static readonly object _segmenterGate = new object();

        // ---- 边说边送：分段 PCM 累积 ------------------------------------------
        // 单段软上限 2 秒、硬上限 3 秒。
        //
        // 为什么不是 5 秒：实机日志显示，用户说话的自然停顿大量落在 100~500ms，远低于
        // 600ms 的分段阈值，而整句通常只有 1~5 秒——两个条件都不满足，于是每次录音只切出
        // 一段（日志里全是"派发=1"），而这一段只能等松手才发得出去，"边说边出字"就永远
        // 不会发生。所以兜底必须由**音频时长**决定，而不是由静音时长决定：到 2 秒后遇到
        // 第一个静音帧就切（尽量不切在词中间），连续说话不停也最迟 3 秒硬切一次。
        private const int SegSoftCloseBytes = 16000 * 2 * 2;
        private const int SegHardCloseBytes = 16000 * 2 * 3;
        private static byte[] _segBuf;
        private static int _segLen;
        private static double _speechRms;
        // 由宿主设置，在 waveIn 回调线程上触发。回调内不得做 I/O——它只负责派发。
        public static Action<byte[]> SegmentReady;

        // 由 waveIn 回调线程逐帧调用（每 100ms 一次）。只做纯内存计算，不做磁盘或网络 I/O，
        // 避免在音频回调里引入阻塞。
        // live=false 表示这是停机回收的半满缓冲：字节要记账，但不能当成一个完整帧。
        private static void OnAudioFrame(double rmsRaw, byte[] frame, int bytesRecorded, bool live)
        {
            byte[] ready = null;
            lock (_segmenterGate)
            {
                if (_segmenter == null) return;
                _segmenter.NoteAudioBytes(bytesRecorded, live);
                // 停机回收的半满缓冲携带的是真实音频（正好是最后一小段），必须补进分段缓冲，
                // 否则尾段会丢掉最后约 100ms；但它不是完整帧，所以不喂 Feed。
                AppendSegmentBytes(frame, bytesRecorded);
                if (live)
                {
                    SpeechSegmenter.Signal signal = _segmenter.Feed(rmsRaw);
                    // 软上限到了之后要等到第一个静音帧才切——不要在词中间切。
                    bool forceClose = _segLen >= SegHardCloseBytes
                        || (_segLen >= SegSoftCloseBytes && rmsRaw < _speechRms);
                    if (signal == SpeechSegmenter.Signal.SegmentBoundary || forceClose) ready = TakeSegment();
                }
            }
            // 锁外触发：宿主在这里派发上传，绝不阻塞音频回调。
            if (ready != null && SegmentReady != null) SegmentReady(ready);
        }

        private static void AppendSegmentBytes(byte[] frame, int count)
        {
            if (frame == null || count <= 0) return;
            if (_segBuf == null) _segBuf = new byte[SegSoftCloseBytes * 2];
            if (_segLen + count > _segBuf.Length)
            {
                int size = _segBuf.Length;
                while (size < _segLen + count) size *= 2;
                byte[] bigger = new byte[size];
                Array.Copy(_segBuf, bigger, _segLen);
                _segBuf = bigger;
            }
            Array.Copy(frame, 0, _segBuf, _segLen, count);
            _segLen += count;
        }

        // 取走当前段。调用方必须已持有 _segmenterGate。
        private static byte[] TakeSegment()
        {
            if (_segLen == 0) return null;
            byte[] pcm = new byte[_segLen];
            Array.Copy(_segBuf, pcm, _segLen);
            _segLen = 0;
            return pcm;
        }

        // 供录音计时器轮询：静音是否已持续到该收尾。只读查询，不改动影子记录状态。
        public static bool ShouldAutoStopOnSilence(int endSilenceMs)
        {
            lock (_segmenterGate)
            {
                if (_segmenter == null) return false;
                return _segmenter.ShouldAutoStop(endSilenceMs);
            }
        }

        // 当前累计说话时长（毫秒，静音不计）。实时字幕的"按说话速率铺开"拿它当分母：
        // 用"距上批的间隔"当分母会把用户的停顿算成"说得慢"，速率被估低约四成，滞后一路累积，
        // 最后被积压加速一次性吐出来（实机反馈的"停顿一下之后出来一堆字"）。
        public static int CurrentSpeechMs()
        {
            lock (_segmenterGate)
            {
                return _segmenter == null ? 0 : _segmenter.SpeechMsTotal;
            }
        }

        [DllImport("winmm.dll", CharSet = CharSet.Auto)]
        private static extern int mciSendString(string command, StringBuilder buffer, int bufferSize, IntPtr callback);

        private static int Send(string cmd)
        {
            var sb = new StringBuilder(512);
            int err = mciSendString(cmd, sb, sb.Capacity, IntPtr.Zero);
            if (err != 0) Log.Write("MCI 错误 " + err + " @ " + cmd);
            return err;
        }

        public static bool Start(int vadThreshold)
        {
            Stop();
            if (Send("open new type waveaudio alias " + Alias) != 0) return false;
            Send("set " + Alias + " time format ms bitspersample 16 channels 1 samplespersec 16000 bytespersec 32000 alignment 2");
            if (Send("record " + Alias) != 0)
            {
                Send("close " + Alias);
                return false;
            }
            _open = true;
            _levelMilli = 0;
            // 与批处理 VAD 同口径：把 vadThreshold 的 16bit RMS 换算成归一化 RMS，
            // 避免实时分段与事后 VAD 出现两套互相矛盾的阈值。
            double speechRms = Math.Max(1, vadThreshold) / 32768.0;
            _speechRms = speechRms;
            lock (_segmenterGate)
            {
                _segmenter = new SpeechSegmenter(100, speechRms, ShadowMinSpeechMs, ShadowSplitSilenceMs, ShadowEndSilenceMs);
                _segLen = 0; // 新会话：丢弃上一段遗留
            }
            try
            {
                _levelMonitor = new WaveInLevelMonitor();
                if (!_levelMonitor.Start())
                {
                    _levelMonitor = null;
                    Log.Write("实时音量监视不可用，浮层使用静默波形");
                }
            }
            catch (Exception ex)
            {
                _levelMonitor = null;
                Log.Write("实时音量监视启动失败: " + ex.Message);
            }
            return true;
        }

        public static bool StopAndSave(string wavPath)
        {
            if (!_open) return false;
            StopLevelMonitor();
            // 尾段：StopLevelMonitor 已等待全部回调回收，因此缓冲里就是完整的最后一段。
            // 这一段是"松手后还需要等"的唯一来源——前面的段在说话期间就送出去了。
            byte[] tail;
            lock (_segmenterGate) { tail = TakeSegment(); }
            if (tail != null && SegmentReady != null) SegmentReady(tail);
            Send("stop " + Alias);
            int err = Send("save " + Alias + " \"" + wavPath + "\"");
            Send("close " + Alias);
            _open = false;
            LogSegmentShadow();
            return err == 0 && File.Exists(wavPath);
        }

        public static void Stop()
        {
            if (!_open) return;
            StopLevelMonitor();
            // 取消路径：丢掉未送出的音频，不派发（已经送出去的段由宿主整体丢弃）。
            lock (_segmenterGate) { _segLen = 0; }
            Send("stop " + Alias);
            Send("close " + Alias);
            _open = false;
            LogSegmentShadow();
        }

        // 停止后调用。StopLevelMonitor 已等待 waveIn 回调全部回收，因此此时摘除分段器
        // 不存在与回调线程的竞态。
        private static void LogSegmentShadow()
        {
            SpeechSegmenter seg;
            lock (_segmenterGate)
            {
                seg = _segmenter;
                _segmenter = null;
            }
            if (seg == null) return;
            try
            {
                seg.Finish(); // 先补记末尾静音游程，再输出摘要
                Log.Write(seg.ToLogLine());
            }
            catch (Exception ex)
            {
                Log.Write("分段影子结算失败: " + ex.Message);
            }
        }

        public static float GetLevel()
        {
            return Thread.VolatileRead(ref _levelMilli) / 1000f;
        }

        private static void SetLevel(float level)
        {
            int milli = (int)(Math.Max(0, Math.Min(1, level)) * 1000f);
            Interlocked.Exchange(ref _levelMilli, milli);
        }

        private static void StopLevelMonitor()
        {
            WaveInLevelMonitor monitor = _levelMonitor;
            _levelMonitor = null;
            if (monitor != null) monitor.Stop();
            SetLevel(0);
        }

        // MCI 负责生成现有 WAV；这个独立的 waveIn 监听器只计算 RMS，不接管
        // 录音文件，也不把原始音频写入磁盘。这样浮层可以跟随真实说话音量起伏。
        private sealed class WaveInLevelMonitor
        {
            private const uint WaveMapper = 0xffffffff;
            private const uint CallbackFunction = 0x00030000;
            private const uint WIM_DATA = 0x3c0;
            private const int BufferBytes = 3200; // 16 kHz / 16 bit / mono / 100 ms

            [StructLayout(LayoutKind.Sequential)]
            private struct WaveFormatEx
            {
                public ushort wFormatTag;
                public ushort nChannels;
                public uint nSamplesPerSec;
                public uint nAvgBytesPerSec;
                public ushort nBlockAlign;
                public ushort wBitsPerSample;
                public ushort cbSize;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct WaveHeader
            {
                public IntPtr lpData;
                public uint dwBufferLength;
                public uint dwBytesRecorded;
                public IntPtr dwUser;
                public uint dwFlags;
                public uint dwLoops;
                public IntPtr lpNext;
                public IntPtr reserved;
            }

            [UnmanagedFunctionPointer(CallingConvention.StdCall)]
            private delegate void WaveInProc(IntPtr hwi, uint message, IntPtr instance, IntPtr parameter1, IntPtr parameter2);

            [DllImport("winmm.dll", CallingConvention = CallingConvention.StdCall)]
            private static extern int waveInOpen(out IntPtr hwi, uint deviceId, ref WaveFormatEx format,
                WaveInProc callback, IntPtr instance, uint flags);
            [DllImport("winmm.dll", CallingConvention = CallingConvention.StdCall)]
            private static extern int waveInPrepareHeader(IntPtr hwi, IntPtr header, uint size);
            [DllImport("winmm.dll", CallingConvention = CallingConvention.StdCall)]
            private static extern int waveInAddBuffer(IntPtr hwi, IntPtr header, uint size);
            [DllImport("winmm.dll", CallingConvention = CallingConvention.StdCall)]
            private static extern int waveInStart(IntPtr hwi);
            [DllImport("winmm.dll", CallingConvention = CallingConvention.StdCall)]
            private static extern int waveInStop(IntPtr hwi);
            [DllImport("winmm.dll", CallingConvention = CallingConvention.StdCall)]
            private static extern int waveInReset(IntPtr hwi);
            [DllImport("winmm.dll", CallingConvention = CallingConvention.StdCall)]
            private static extern int waveInUnprepareHeader(IntPtr hwi, IntPtr header, uint size);
            [DllImport("winmm.dll", CallingConvention = CallingConvention.StdCall)]
            private static extern int waveInClose(IntPtr hwi);

            private readonly WaveInProc _callback;
            private readonly int _headerSize;
            private IntPtr _device;
            private IntPtr[] _headers;
            private IntPtr[] _buffers;
            private int _stopping;
            private int _outstanding; // 仍在队列（等待回调）的缓冲区数量
            private byte[] _frame;    // 复用的一帧托管副本；回调只有一个 waveIn 分发线程，可安全复用

            public WaveInLevelMonitor()
            {
                _callback = OnWave;
                _headerSize = Marshal.SizeOf(typeof(WaveHeader));
            }

            public bool Start()
            {
                var format = new WaveFormatEx();
                format.wFormatTag = 1;
                format.nChannels = 1;
                format.nSamplesPerSec = 16000;
                format.nAvgBytesPerSec = 32000;
                format.nBlockAlign = 2;
                format.wBitsPerSample = 16;
                format.cbSize = 0;

                int err = waveInOpen(out _device, WaveMapper, ref format, _callback, IntPtr.Zero, CallbackFunction);
                if (err != 0)
                {
                    Log.Write("实时音量监视打开失败: " + err);
                    _device = IntPtr.Zero;
                    return false;
                }

                _headers = new IntPtr[3];
                _buffers = new IntPtr[3];
                for (int i = 0; i < _headers.Length; i++)
                {
                    _buffers[i] = Marshal.AllocHGlobal(BufferBytes);
                    _headers[i] = Marshal.AllocHGlobal(_headerSize);
                    var header = new WaveHeader();
                    header.lpData = _buffers[i];
                    header.dwBufferLength = BufferBytes;
                    Marshal.StructureToPtr(header, _headers[i], false);
                    err = waveInPrepareHeader(_device, _headers[i], (uint)_headerSize);
                    if (err == 0) err = waveInAddBuffer(_device, _headers[i], (uint)_headerSize);
                    if (err == 0) Interlocked.Increment(ref _outstanding);
                    if (err != 0)
                    {
                        Log.Write("实时音量监视缓冲区失败: " + err);
                        Stop();
                        return false;
                    }
                }

                err = waveInStart(_device);
                if (err != 0)
                {
                    Log.Write("实时音量监视启动失败: " + err);
                    Stop();
                    return false;
                }
                return true;
            }

            public void Stop()
            {
                IntPtr device = _device;
                if (device == IntPtr.Zero)
                {
                    FreeBuffers();
                    return;
                }

                Interlocked.Exchange(ref _stopping, 1);
                waveInStop(device);
                waveInReset(device);
                // 竞态修复：回调运行在 waveIn 分发线程，可能在 Stop 期间仍持有 header 指针。
                // 必须先等待回调不再指向任何内存，再 Unprepare/Close/Free，否则会释放后访问。
                for (int i = 0; i < 200 && Thread.VolatileRead(ref _outstanding) > 0; i++)
                {
                    Thread.Sleep(5);
                }
                if (Thread.VolatileRead(ref _outstanding) > 0)
                {
                    Log.Write("实时音量监视缓冲区未全部回收: outstanding=" + Thread.VolatileRead(ref _outstanding));
                }
                if (_headers != null)
                {
                    for (int i = 0; i < _headers.Length; i++)
                    {
                        if (_headers[i] != IntPtr.Zero) waveInUnprepareHeader(device, _headers[i], (uint)_headerSize);
                    }
                }
                waveInClose(device);
                _device = IntPtr.Zero;
                FreeBuffers();
            }

            private void FreeBuffers()
            {
                if (_headers != null)
                {
                    for (int i = 0; i < _headers.Length; i++)
                    {
                        if (_headers[i] != IntPtr.Zero) Marshal.FreeHGlobal(_headers[i]);
                        if (_buffers[i] != IntPtr.Zero) Marshal.FreeHGlobal(_buffers[i]);
                    }
                }
                _headers = null;
                _buffers = null;
            }

            private void OnWave(IntPtr hwi, uint message, IntPtr instance, IntPtr parameter1, IntPtr parameter2)
            {
                if (message != WIM_DATA || parameter1 == IntPtr.Zero) return;
                try
                {
                    WaveHeader header = (WaveHeader)Marshal.PtrToStructure(parameter1, typeof(WaveHeader));
                    int bytes = (int)header.dwBytesRecorded;
                    if (bytes < 0) bytes = 0;
                    if (bytes > BufferBytes) bytes = BufferBytes; // 防御：异常驱动可能报超出缓冲区的值
                    // 一次 Marshal.Copy 取代逐样本 Marshal.ReadInt16：快得多，而且顺带把本帧 PCM
                    // 落进托管内存——那正是实时分段（以及后续"边说边送"）需要的字节。
                    if (_frame == null) _frame = new byte[BufferBytes];
                    if (bytes > 0) Marshal.Copy(header.lpData, _frame, 0, bytes);
                    int samples = bytes / 2;
                    double sum = 0;
                    for (int i = 0; i < samples; i++)
                    {
                        short sample = (short)(_frame[i * 2] | (_frame[i * 2 + 1] << 8));
                        sum += (double)sample * sample;
                    }
                    double rms = samples == 0 ? 0 : Math.Sqrt(sum / samples) / 32768.0;
                    SetLevel((float)Math.Min(1, rms * 4)); // 显示用：放大 4 倍，保持既有观感
                    // 判定这个回调是不是"直播帧"：
                    //  - 满缓冲：无论是否正在停机，都是完整的一帧；
                    //  - 残缺且正在停机：waveInReset 回收的在途半满缓冲，不是完整帧；
                    //  - 残缺但仍在运行：异常情况，按整帧记账，让"差"变负把丢帧暴露出来。
                    bool live = bytes >= BufferBytes || Thread.VolatileRead(ref _stopping) == 0;
                    Recorder.OnAudioFrame(rms, _frame, bytes, live); // 分段用：原始 RMS + 本帧 PCM

                    // Reset 会把剩余缓冲区回调回来；停止标记避免在释放前重新入队。
                    // 计数：回调让“一个在途缓冲结束”；若成功重新入队则保持计数，
                    // 否则递减，Stop 等待计数归零后才释放内存（use-after-free 防守）。
                    bool requeued = Thread.VolatileRead(ref _stopping) == 0 && _device != IntPtr.Zero
                        && waveInAddBuffer(hwi, parameter1, (uint)_headerSize) == 0;
                    if (!requeued) Interlocked.Decrement(ref _outstanding);
                }
                catch (Exception ex)
                {
                    Log.Write("实时音量回调失败: " + ex.Message);
                }
            }
        }
    }

    internal sealed class AsrResult
    {
        public bool Ok;
        public bool Cancelled;
        public string Text = "";
        public string Error = "";
        public string OrganizedText = "";
        public string OrganizeError = "";
        public int HttpStatus;
        public double LatencySeconds;
    }

    internal sealed class AsrSession
    {
        public volatile bool Cancelled;
        public volatile HttpWebRequest CurrentRequest;
        public Action<string> OnPartial;
        // 会话级延迟分解。挂在会话上，因此所有 provider 都能记录自己那一段，
        // 无需改动 IStreamingAsrProvider 签名。只记阶段与计数，不记正文。
        public LatencyTrace Trace;

        public void Abort()
        {
            try { Cancelled = true; }
            catch { }
            try
            {
                HttpWebRequest req = CurrentRequest;
                if (req != null) req.Abort();
            }
            catch { }
        }
    }

    internal static class AsrClient
    {
        private static IStreamingAsrProvider CreateProvider(Config cfg)
        {
            if (cfg.Api == "sse") return new StepFunStreamingAsrProvider();
            if (cfg.Api == "transcriptions") return new OpenAiCompatibleAsrProvider();
            throw new InvalidOperationException("未支持的 ASR Provider: " + cfg.Api);
        }

        public static AsrResult Transcribe(Config cfg, string wavPath, AsrSession session = null)
        {
            var result = new AsrResult();
            var sw = Stopwatch.StartNew();
            LatencyTrace trace = session == null ? null : session.Trace;
            try
            {
                byte[] wav = File.ReadAllBytes(wavPath);
                if (trace != null) trace.Note("wavBytes", wav.Length);
                IStreamingAsrProvider provider = CreateProvider(cfg);
                return provider.Transcribe(cfg, PrepareAudio(cfg, wav, trace), session);
            }
            catch (WebException wex)
            {
                HandleWebException(result, wex, session, sw);
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
            }
            finally
            {
                result.LatencySeconds = sw.Elapsed.TotalSeconds;
            }
            return result;
        }

        // 边说边送：分段音频已经在内存里，不必再落盘读回。
        // 与整段上传共用同一条链路（PrepareAudio → provider），所以行为一致，没有第二套实现。
        public static AsrResult TranscribePcm(Config cfg, byte[] pcm, AsrSession session)
        {
            var result = new AsrResult();
            var sw = Stopwatch.StartNew();
            try
            {
                byte[] wav = VoxleapCore.CreatePcmWav(pcm, 16000);
                IStreamingAsrProvider provider = CreateProvider(cfg);
                // 分段不进会话延迟分解：那段账是给整段路径算的，
                // 让分段覆盖它会把整段的 VAD 字节数污染成最后一段的。
                return provider.Transcribe(cfg, PrepareAudio(cfg, wav, null), session);
            }
            catch (WebException wex)
            {
                result.Error = wex.Message;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
            }
            finally
            {
                result.LatencySeconds = sw.Elapsed.TotalSeconds;
            }
            return result;
        }

        // Step Plan 订阅专用：POST /step_plan/v1/audio/asr/sse，JSON + base64 PCM，SSE 流式返回。
        internal static AsrResult TranscribeStepFun(Config cfg, byte[] wav, AsrSession session)
        {
            Stopwatch sw = Stopwatch.StartNew();
            var result = new AsrResult();
            HttpWebRequest req = null;
            LatencyTrace trace = session == null ? null : session.Trace;
            try
            {
                byte[] pcm = VoxleapCore.ExtractPcm(wav);
                string b64 = Convert.ToBase64String(pcm);
                string endpoint = cfg.Endpoint.StartsWith("/") ? cfg.Endpoint : "/" + cfg.Endpoint;
                req = (HttpWebRequest)WebRequest.Create(cfg.BaseUrl + endpoint);
                SetSessionRequest(session, req);
                req.Method = "POST";
                req.Timeout = cfg.RequestTimeoutMs;
                req.ReadWriteTimeout = cfg.RequestTimeoutMs;
                req.ContentType = "application/json";
                req.Accept = "text/event-stream";
                req.Headers["Authorization"] = "Bearer " + cfg.ApiKey;
                // 关闭写缓冲有两个理由：
                //  1) HttpWebRequest 默认会先把整个请求体缓冲在内存里、close 时才发——
                //     这样"上传"与"服务端处理"在计时上无法区分。关闭后 Write 受真实网络
                //     背压阻塞，上传耗时才可测（LatencyTrace.Connected/Uploaded）。
                //  2) 顺带省掉一份请求体的内存副本：300s 录音的请求体约 12.8MB，
                //     默认缓冲会再复制一份。
                // 代价：请求不可被重定向/重鉴权后重放（本端点是固定 HTTPS POST，不重定向）。
                req.AllowWriteStreamBuffering = false;
                var tj = new StringBuilder();
                tj.Append("{\"model\":\"").Append(VoxleapCore.EscapeJson(cfg.Model)).Append("\"");
                // language 留空则省略该字段，由模型自动识别语种。
                if (!string.IsNullOrEmpty(cfg.Language))
                {
                    tj.Append(",\"language\":\"").Append(VoxleapCore.EscapeJson(cfg.Language)).Append("\"");
                }
                tj.Append(",\"enable_itn\":true");
                string[] words = cfg.Hotwords.Split(',');
                var hw = new List<string>();
                foreach (string w in words)
                {
                    string t = w.Trim();
                    if (t.Length > 0) hw.Add("\"" + VoxleapCore.EscapeJson(t) + "\"");
                }
                if (hw.Count > 0)
                {
                    tj.Append(",\"hotwords\":[").Append(string.Join(",", hw.ToArray())).Append("]");
                }
                tj.Append("}");
                string body = "{\"audio\":{\"data\":\"" + b64 + "\",\"input\":{\"transcription\":"
                    + tj.ToString() + ",\"format\":{\"type\":\"pcm\",\"codec\":\"pcm_s16le\",\"rate\":16000,\"bits\":16,\"channel\":1}}}}";
                byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
                req.ContentLength = bodyBytes.Length;
                if (trace != null)
                {
                    trace.Note("reqBytes", bodyBytes.Length);
                    trace.Mark(LatencyTrace.Prepared); // VAD + base64 + JSON 组装完成
                }
                using (var rs = req.GetRequestStream()) // 含 TCP + TLS 握手
                {
                    if (trace != null) trace.Mark(LatencyTrace.Connected);
                    rs.Write(bodyBytes, 0, bodyBytes.Length); // 已关闭写缓冲，这里才是真实网络传输
                }
                if (trace != null) trace.Mark(LatencyTrace.Uploaded);

                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    if (trace != null) trace.Mark(LatencyTrace.Ttfb); // 服务端已收全音频并开始响应
                    result.HttpStatus = (int)resp.StatusCode;
                    var deltas = new StringBuilder();
                    string finalText = null;
                    using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    {
                        string line;
                        while ((line = sr.ReadLine()) != null)
                        {
                            if (!line.StartsWith("data:")) continue;
                            string payload = line.Substring(5).Trim();
                            string type = "";
                            string piece = "";
                            bool hasText = VoxleapCore.ParseSseLine(payload, out type, out piece);
                            if (type.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                result.Error = SummarizeError(result.HttpStatus, payload);
                                return result;
                            }

                            if (!hasText) continue;
                            // Mark 只记首次，因此这里记的是"第一个带文本的事件"到达时间。
                            if (trace != null) trace.Mark(LatencyTrace.FirstDelta);
                            if (type == "transcript.text.done")
                            {
                                finalText = piece;
                                if (trace != null) trace.Mark(LatencyTrace.Done);
                            }
                            else if (type == "transcript.text.delta")
                            {
                                deltas.Append(piece);
                                FirePartial(session, deltas.ToString());
                            }
                            else
                            {
                                deltas.Append(piece);
                            }
                        }
                    }
                    // 兜底：部分实现可能不发 done 事件，用流结束时间收尾（Mark 首次生效，不会覆盖）。
                    if (trace != null) trace.Mark(LatencyTrace.Done);
                    result.Text = finalText != null ? finalText : deltas.ToString();
                    result.Ok = true;
                }
            }
            catch (WebException wex)
            {
                HandleWebException(result, wex, session, sw);
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
            }
            finally
            {
                ClearSessionRequest(session, req);
                result.LatencySeconds = sw.Elapsed.TotalSeconds;
            }
            return result;
        }

        private static byte[] PrepareAudio(Config cfg, byte[] wav, LatencyTrace trace)
        {
            if (!cfg.EnableVad)
            {
                if (trace != null) trace.NoteText("vad", "关闭");
                return wav;
            }
            byte[] pcm = VoxleapCore.ExtractPcm(wav);
            byte[] trimmed = VoxleapCore.TrimSilencePcm(pcm, 16000, cfg.VadThreshold, cfg.VadPaddingMs);
            if (trace != null)
            {
                trace.Note("vadInBytes", pcm == null ? 0 : pcm.Length);
                trace.Note("vadOutBytes", trimmed == null ? 0 : trimmed.Length);
            }
            if (trimmed.Length == 0) return wav;
            Log.Write("VAD: " + pcm.Length + " -> " + trimmed.Length + " bytes");
            return VoxleapCore.CreatePcmWav(trimmed, 16000);
        }

        private static void SetSessionRequest(AsrSession session, HttpWebRequest request)
        {
            try
            {
                if (session != null) session.CurrentRequest = request;
            }
            catch { }
        }

        private static void ClearSessionRequest(AsrSession session, HttpWebRequest request)
        {
            try
            {
                if (session != null && object.ReferenceEquals(session.CurrentRequest, request)) session.CurrentRequest = null;
            }
            catch { }
        }

        private static void FirePartial(AsrSession session, string text)
        {
            try
            {
                if (session != null && session.OnPartial != null) session.OnPartial(text);
            }
            catch { }
        }

        private static void HandleWebException(AsrResult result, WebException wex, AsrSession session, Stopwatch sw)
        {
            var errResp = wex.Response as HttpWebResponse;
            try
            {
                result.HttpStatus = errResp != null ? (int)errResp.StatusCode : 0;
                if (session != null && session.Cancelled)
                {
                    result.Cancelled = true;
                    result.Error = "";
                    return;
                }
                string errBody = errResp != null ? ReadAll(errResp) : wex.Message;
                result.Error = SummarizeError(result.HttpStatus, errBody);
            }
            finally
            {
                try
                {
                    if (errResp != null) errResp.Close();
                }
                catch { }
                result.LatencySeconds = sw.Elapsed.TotalSeconds;
            }
        }

        // 从 WAV 中取出 data 块的裸 PCM；解析失败时原样返回。
        // 实现已迁至 VoxleapCore.ExtractPcm（可单测）。
        private static byte[] BuildMultipart(string boundary, string model, byte[] wav)
        {
            var ms = new MemoryStream();
            string b = "--" + boundary;
            WriteText(ms, b + "\r\nContent-Disposition: form-data; name=\"model\"\r\n\r\n" + model + "\r\n");
            WriteText(ms, b + "\r\nContent-Disposition: form-data; name=\"file\"; filename=\"audio.wav\"\r\nContent-Type: audio/wav\r\n\r\n");
            ms.Write(wav, 0, wav.Length);
            WriteText(ms, "\r\n" + b + "--\r\n");
            return ms.ToArray();
        }

        private static void WriteText(Stream s, string text)
        {
            byte[] b = Encoding.UTF8.GetBytes(text);
            s.Write(b, 0, b.Length);
        }

        private static string ReadAll(HttpWebResponse resp)
        {
            using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            {
                return sr.ReadToEnd();
            }
        }

        private static string SummarizeError(int code, string body)
        {
            string msg = body ?? "";
            Match m = Regex.Match(msg, "\"message\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (m.Success) msg = JsonUtil.Unescape(m.Groups[1].Value);
            if (msg.IndexOf("no speech", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "没有检测到有效语音，请靠近麦克风说话后重试";
            }
            if (msg.Length > 200) msg = msg.Substring(0, 200);
            return (code > 0 ? "HTTP " + code + " " : "") + msg;
        }
    }

    internal static class ScreenLayout
    {
        public static Point BottomCenterOf(IntPtr windowHandle, int width, int height, int margin)
        {
            Rectangle area = windowHandle != IntPtr.Zero
                ? Screen.FromHandle(windowHandle).WorkingArea
                : Screen.PrimaryScreen.WorkingArea;
            int x = area.Left + (area.Width - width) / 2;
            int y = area.Bottom - height - margin;
            return new Point(x, y);
        }
    }

    // 分层浮层使用低 alpha 玻璃色层、内部反射和柔和边缘，模拟系统级液态玻璃；
    // 圆角区域本身不依赖系统默认窗口边框，避免出现黑色矩形和白色残边。
    internal class GlassForm : Form
    {
        protected Color SurfaceTop = Color.FromArgb(250, 253, 252);
        protected Color SurfaceBottom = Color.FromArgb(231, 243, 240);
        protected Color SurfaceAccent = Color.FromArgb(32, 119, 133);
        protected int CornerRadius = 26;

        protected GlassForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.Dpi;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = SurfaceBottom;
        }

        protected virtual bool KeepTargetFocus
        {
            get { return false; }
        }

        protected virtual bool UsesLayeredSurface
        {
            get { return false; }
        }

        protected override bool ShowWithoutActivation
        {
            get { return KeepTargetFocus; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
                if (KeepTargetFocus) cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
                if (UsesLayeredSurface) cp.ExStyle |= 0x00080000; // WS_EX_LAYERED
                else cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (UsesLayeredSurface) RefreshLayeredSurface();
            else UpdateSurfaceRegion();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (UsesLayeredSurface) RefreshLayeredSurface();
            else
            {
                UpdateSurfaceRegion();
                Invalidate();
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (!UsesLayeredSurface) DrawSurface(e.Graphics);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (UsesLayeredSurface) return;
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using (var path = CreateRoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), CornerRadius))
            using (var pen = new Pen(Color.FromArgb(150, 183, 202, 198), 1f))
            {
                g.DrawPath(pen, path);
            }
        }

        protected virtual void RefreshLayeredSurface()
        {
        }

        protected void SetSurfaceAccent(Color accent)
        {
            SurfaceAccent = accent;
            Invalidate();
        }

        protected static GraphicsPath CreateRoundedPath(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
            int diameter = Math.Max(1, radius * 2);
            diameter = Math.Min(diameter, Math.Min(bounds.Width, bounds.Height));
            int x = bounds.Left;
            int y = bounds.Top;
            int right = bounds.Right;
            int bottom = bounds.Bottom;
            path.AddArc(x, y, diameter, diameter, 180, 90);
            path.AddArc(right - diameter, y, diameter, diameter, 270, 90);
            path.AddArc(right - diameter, bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(x, bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        private void UpdateSurfaceRegion()
        {
            if (Width <= 1 || Height <= 1) return;
            Region = new Region(CreateRoundedPath(new Rectangle(0, 0, Width, Height), CornerRadius));
        }

        private void DrawSurface(Graphics g)
        {
            if (Width <= 1 || Height <= 1) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(SurfaceBottom);

            Rectangle bounds = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = CreateRoundedPath(bounds, CornerRadius))
            {
                using (var fill = new LinearGradientBrush(bounds, SurfaceTop, SurfaceBottom, 135f))
                {
                    g.FillPath(fill, path);
                }

                GraphicsState state = g.Save();
                g.SetClip(path);
                using (var glowPath = new GraphicsPath())
                {
                    glowPath.AddEllipse(-Width / 5, -Height / 2, Width * 3 / 4, Height * 3 / 2);
                    using (var glow = new PathGradientBrush(glowPath))
                    {
                        glow.CenterColor = Color.FromArgb(30, SurfaceAccent);
                        glow.SurroundColors = new[] { Color.FromArgb(0, SurfaceAccent) };
                        g.FillPath(glow, glowPath);
                    }
                }
                using (var highlight = new LinearGradientBrush(
                    new Rectangle(0, 0, Width, Math.Max(1, Height / 3)),
                    Color.FromArgb(56, Color.White), Color.FromArgb(0, Color.White), 90f))
                {
                    g.FillRectangle(highlight, 0, 0, Width, Math.Max(1, Height / 3));
                }
                g.Restore(state);
            }
        }
    }

    // 透明窗口不使用 WinForms Region：Region 只能硬裁像素，会在圆角处产生
    // 白边和锯齿。UpdateLayeredWindow 直接提交带 alpha 的位图，让边缘由
    // GDI+ 抗锯齿后再和桌面合成。
    internal abstract class LayeredGlassForm : GlassForm
    {
        // 玻璃面相对窗口的内缩。protected 而非 private：录音态要用它算"胶囊宽度里
        // 与字幕文本无关的那些边"（见 OverlayForm.RecCaptionChromeWidth）。
        protected const int SurfaceInset = 10;
        private readonly System.Windows.Forms.Timer _fadeTimer;
        private int _layeredOpacity = 255;
        private int _fadeFromOpacity;
        private int _fadeTargetOpacity;
        private int _fadeDurationMs;
        private DateTime _fadeStartedAt;
        private bool _hideAfterFade;

        protected LayeredGlassForm()
        {
            BackColor = Color.Transparent;
            _fadeTimer = new System.Windows.Forms.Timer();
            _fadeTimer.Interval = 16;
            _fadeTimer.Tick += delegate { AdvanceFade(); };
        }

        // 动效语义：入场从下方升起，退场向下沉去（与透明度同步，物理直觉）。
        protected float FadeOffsetY
        {
            get
            {
                if (!_fadeTimer.Enabled) return 0f;
                double elapsed = (DateTime.UtcNow - _fadeStartedAt).TotalMilliseconds;
                float p = (float)Math.Max(0, Math.Min(1, elapsed / Math.Max(1, _fadeDurationMs)));
                float eased = p * p * (3f - 2f * p);
                bool rising = _fadeTargetOpacity >= _fadeFromOpacity;
                return (rising ? 1f - eased : eased) * 5f;
            }
        }

        protected override bool UsesLayeredSurface
        {
            get { return true; }
        }

        protected Rectangle SurfaceBounds
        {
            get
            {
                return new Rectangle(
                    SurfaceInset,
                    SurfaceInset,
                    Math.Max(1, Width - SurfaceInset * 2),
                    Math.Max(1, Height - SurfaceInset * 2));
            }
        }

        protected abstract void DrawLayeredContent(Graphics g);

        protected override void RefreshLayeredSurface()
        {
            if (!IsHandleCreated || Width <= SurfaceInset * 2 || Height <= SurfaceInset * 2) return;

            using (var bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb))
            using (var g = Graphics.FromImage(bitmap))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.Clear(Color.Transparent);
                g.CompositingMode = CompositingMode.SourceOver;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                float fadeOffset = FadeOffsetY;
                if (fadeOffset != 0f) g.TranslateTransform(0f, fadeOffset);
                DrawLayeredChrome(g);
                DrawLayeredContent(g);

                Native.POINT destination = new Native.POINT(Left, Top);
                Native.SIZE size = new Native.SIZE(Width, Height);
                Native.POINT source = new Native.POINT(0, 0);
                var blend = new Native.BLENDFUNCTION();
                blend.BlendOp = 0;
                blend.BlendFlags = 0;
                blend.SourceConstantAlpha = (byte)_layeredOpacity;
                blend.AlphaFormat = Native.AC_SRC_ALPHA;

                // 不直接把 GDI+ Graphics.GetHdc() 或 GetHbitmap() 交给
                // UpdateLayeredWindow：前者在部分 Windows 10/DPI 组合下会只留下
                // 空矩形，后者可能把透明像素扁平化。这里复制到显式的 32 位
                // top-down DIB，保留每个像素的 premultiplied alpha 和文字内容。
                g.Flush(System.Drawing.Drawing2D.FlushIntention.Sync);
                BitmapData locked = bitmap.LockBits(
                    new Rectangle(0, 0, Width, Height),
                    ImageLockMode.ReadOnly,
                    PixelFormat.Format32bppPArgb);
                var bitmapInfo = new Native.BITMAPINFO();
                bitmapInfo.Header.Size = (uint)Marshal.SizeOf(typeof(Native.BITMAPINFOHEADER));
                bitmapInfo.Header.Width = Width;
                bitmapInfo.Header.Height = -Height;
                bitmapInfo.Header.Planes = 1;
                bitmapInfo.Header.BitCount = 32;
                bitmapInfo.Header.Compression = 0;
                IntPtr dibBits;
                IntPtr bitmapHandle = Native.CreateDIBSection(
                    IntPtr.Zero,
                    ref bitmapInfo,
                    0,
                    out dibBits,
                    IntPtr.Zero,
                    0);
                if (bitmapHandle == IntPtr.Zero || dibBits == IntPtr.Zero)
                {
                    bitmap.UnlockBits(locked);
                    Log.Write("浮层位图创建失败: " + Marshal.GetLastWin32Error());
                    return;
                }
                byte[] row = new byte[Width * 4];
                for (int y = 0; y < Height; y++)
                {
                    IntPtr sourceRow = new IntPtr(locked.Scan0.ToInt64() + (long)y * locked.Stride);
                    IntPtr targetRow = new IntPtr(dibBits.ToInt64() + (long)y * Width * 4);
                    Marshal.Copy(sourceRow, row, 0, row.Length);
                    Marshal.Copy(row, 0, targetRow, row.Length);
                }
                bitmap.UnlockBits(locked);
                IntPtr sourceHdc = Native.CreateCompatibleDC(IntPtr.Zero);
                IntPtr oldBitmap = Native.SelectObject(sourceHdc, bitmapHandle);
                IntPtr screenHdc = Native.GetDC(IntPtr.Zero);
                try
                {
                    bool updated = Native.UpdateLayeredWindow(
                        Handle,
                        screenHdc,
                        ref destination,
                        ref size,
                        sourceHdc,
                        ref source,
                        0,
                        ref blend,
                        Native.ULW_ALPHA);
                    if (!updated) Log.Write("浮层渲染失败: " + Marshal.GetLastWin32Error());
                }
                finally
                {
                    Native.SelectObject(sourceHdc, oldBitmap);
                    Native.DeleteDC(sourceHdc);
                    Native.DeleteObject(bitmapHandle);
                    Native.ReleaseDC(IntPtr.Zero, screenHdc);
                }
            }
        }

        protected void ShowLayeredAnimated(int durationMs)
        {
            _hideAfterFade = false;
            _fadeTimer.Stop();
            if (!Visible)
            {
                _layeredOpacity = 0;
                Show();
            }
            StartFadeTo(255, durationMs, false);
        }

        protected void HideLayeredAnimated(int durationMs)
        {
            if (!Visible)
            {
                _fadeTimer.Stop();
                _layeredOpacity = 0;
                return;
            }
            StartFadeTo(0, durationMs, true);
        }

        private void StartFadeTo(int targetOpacity, int durationMs, bool hideWhenDone)
        {
            _fadeTimer.Stop();
            _fadeFromOpacity = _layeredOpacity;
            _fadeTargetOpacity = Math.Max(0, Math.Min(255, targetOpacity));
            _fadeDurationMs = Math.Max(1, durationMs);
            _fadeStartedAt = DateTime.UtcNow;
            _hideAfterFade = hideWhenDone;
            if (_fadeFromOpacity == _fadeTargetOpacity)
            {
                AdvanceFade();
                return;
            }
            _fadeTimer.Start();
        }

        private void AdvanceFade()
        {
            double elapsed = (DateTime.UtcNow - _fadeStartedAt).TotalMilliseconds;
            float progress = (float)Math.Max(0, Math.Min(1, elapsed / _fadeDurationMs));
            float eased = progress * progress * (3f - 2f * progress);
            _layeredOpacity = (int)Math.Round(
                _fadeFromOpacity + (_fadeTargetOpacity - _fadeFromOpacity) * eased);
            if (IsHandleCreated && Visible) RefreshLayeredSurface();
            if (progress >= 1f)
            {
                _fadeTimer.Stop();
                if (_hideAfterFade)
                {
                    _hideAfterFade = false;
                    Hide();
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _fadeTimer != null) _fadeTimer.Dispose();
            base.Dispose(disposing);
        }

        private void DrawLayeredChrome(Graphics g)
        {
            Rectangle surface = SurfaceBounds;

            using (var path = CreateRoundedPath(surface, CornerRadius))
            {
                // 阴影只画在玻璃外侧，不能透过半透明表面叠进内部，否则会把底部染灰。
                // 深色 HUD：阴影要更实更近，浮层才有“悬在桌面上”的重量。
                GraphicsState shadowState = g.Save();
                using (var shadowRegion = new Region(path))
                {
                    g.ExcludeClip(shadowRegion);
                    for (int spread = 8; spread >= 1; spread--)
                    {
                        Rectangle shadowBounds = new Rectangle(
                            surface.Left - spread / 2,
                            surface.Top + spread / 2,
                            surface.Width + spread,
                            surface.Height + spread);
                        using (var shadowPath = CreateRoundedPath(shadowBounds, CornerRadius + spread / 2))
                        using (var shadow = new SolidBrush(Color.FromArgb((8 - spread) * 5, 6, 8, 10)))
                        {
                            g.FillPath(shadow, shadowPath);
                        }
                    }
                }
                g.Restore(shadowState);

                using (var fill = new LinearGradientBrush(
                    surface,
                    Color.FromArgb(228, SurfaceTop),
                    Color.FromArgb(204, SurfaceBottom),
                    112f))
                {
                    g.FillPath(fill, path);
                }

                GraphicsState state = g.Save();
                g.SetClip(path);
                // 顶部 1px 亮缘：深色玻璃唯一的高光，克制。
                using (var cap = new SolidBrush(Color.FromArgb(30, Color.White)))
                {
                    g.FillRectangle(cap, surface.Left + CornerRadius / 2, surface.Top + 1,
                        surface.Width - CornerRadius, 1);
                }
                g.Restore(state);
            }

            // 只留一条低对比度内缘高光，不绘制厚重白框。
            Rectangle inner = new Rectangle(
                surface.Left + 1,
                surface.Top + 1,
                Math.Max(1, surface.Width - 2),
                Math.Max(1, surface.Height - 2));
            using (var innerPath = CreateRoundedPath(inner, Math.Max(1, CornerRadius - 1)))
            {
                using (var edge = new Pen(Color.FromArgb(22, Color.White), 1f))
                {
                    g.DrawPath(edge, innerPath);
                }
            }
        }

        protected void DrawLayeredText(Graphics g, string text, Font font, Color color, RectangleF bounds, bool centered)
        {
            if (string.IsNullOrEmpty(text)) return;
            using (var brush = new SolidBrush(color))
            using (var format = new StringFormat())
            {
                format.Alignment = centered ? StringAlignment.Center : StringAlignment.Near;
                format.LineAlignment = StringAlignment.Center;
                format.Trimming = StringTrimming.EllipsisCharacter;
                format.FormatFlags = StringFormatFlags.NoWrap;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                g.TextContrast = 0;
                g.DrawString(text, font, brush, bounds, format);
            }
        }
    }

    internal sealed class OverlayForm : LayeredGlassForm
    {
        // 品牌五块（母版 viewBox 1254×1254，内容框 337,437-914,819）归一化：
        // {顶左x, 顶右x, 底左x, 底右x, 顶y, 底y, 色标}，0=青 1=蓝。
        private static readonly float[][] LogoBlocks = new float[][]
        {
            new[] { 0.040f, 0.177f, 0.000f, 0.137f, 0.408f, 0.689f, 0f },
            new[] { 0.280f, 0.419f, 0.194f, 0.331f, 0.141f, 0.762f, 1f },
            new[] { 0.510f, 0.646f, 0.364f, 0.503f, 0.000f, 1.000f, 1f },
            new[] { 0.636f, 0.770f, 0.586f, 0.719f, 0.581f, 0.898f, 0f },
            new[] { 0.865f, 1.000f, 0.818f, 0.951f, 0.411f, 0.728f, 1f }
        };
        private static readonly Color BlockBlue = Color.FromArgb(0, 70, 241);
        private static readonly Color BlockCyan = Color.FromArgb(0, 226, 241);
        // 声波起伏轮廓：中间主块最高，向两侧递减（呼应母版的节奏）。
        private static readonly float[] WaveShape = { 0.62f, 0.85f, 1.0f, 0.82f, 0.6f };

        private readonly System.Windows.Forms.Timer _timer;
        private readonly System.Windows.Forms.Timer _levelTimer;
        private readonly System.Windows.Forms.Timer _delayShow;
        private DateTime _startedAt;
        private string _mainText = "";
        private string _busyCaption = "";
        private string _elapsedText = "";
        private IntPtr _busyTarget = IntPtr.Zero;
        private bool _busy;
        // 录音期实时字幕（只显示，绝不注入）。原始累计文本与"已经裁到放得下"的显示文本分开存：
        // 裁字要测量字体，只在字幕更新时做一次，而不是每帧（30fps）都做。
        private readonly LiveCaptionAnim _liveAnim = new LiveCaptionAnim();
        private string _liveCaption = "";
        private string _liveCaptionDisplay = "";
        // 动画帧时钟（Environment.TickCount 毫秒）与"显示文本"缓存键。
        // 缓存键能把每帧的逐字测量降到"只有字数或胶囊宽度真的变了"时才做一次。
        private int _liveFrameAt;
        private int _liveFitRevealed = -1;
        private int _liveFitWidth = -1;
        private Font _liveCaptionFont;
        // 录音态布局：第一行（红点 + 时间码 + 声波）固定占用内容盒顶部 56px —— 这恰好是
        // 无字幕时 76px 窗口的内容盒全高（76 - 上下各 10 的内缩），所以第一行中轴仍是
        // surface.Top + 28，与改动前的 surface.Top + surface.Height / 2f 逐像素相同；
        // 有字幕时才在这 56px 下面再加一行，而不是把两行一起居中（那会把声波压到字幕上）。
        private const int RecRowHeight = 56;
        private const int RecCaptionRowHeight = 30;
        private const int RecCaptionLeftInset = 22;  // 与第一行的红点左缘对齐
        private const int RecCaptionRightInset = 20;
        // 胶囊宽度里与字幕文本无关的那些边：两侧玻璃内缩（LayeredGlassForm.SurfaceInset）、
        // 字幕左右内缩，再加 6px 量测余量——宽度用 GDI 的 TextRenderer 量、绘制走 GDI+ 的
        // DrawString，两者字宽有细微差异，宁可少放一个字，也不能让最新吐出的字被裁掉。
        private const int RecCaptionChromeWidth =
            SurfaceInset * 2 + RecCaptionLeftInset + RecCaptionRightInset + 6;
        private readonly float[] _waveLevels = new float[5];
        private readonly float[] _levelHistory = new float[8];
        private int _historyIndex;
        private float _visualLevel;
        private bool _recording;
        public int MaxRecordMs = 0; // 0=不限制；来自配置 maxRecordMs
        // 对抗审查 P1 安全闸：即使 MaxRecordMs=0，录音也不得无限持续（keyup 丢失/卡住场景）。
        // 10 分钟硬顶后自动转入转写（MaxDurationReached），避免麦克风静默常开。
        private static readonly int RecordSafeguardMs = 10 * 60 * 1000;
        public Action<string> MaxDurationReached; // 需要收尾时的回调，参数是收尾原因（供日志区分）
        // 对抗审查 P1 键态探测：宿主注入“热键物理上是否仍按住”，用于 keyup 丢失（Alt+Tab/UAC/安全桌面/ RDP）自动收尾。
        public Func<bool> HoldKeyStillDown;
        // 静音自动停止：宿主注入"此刻是否该因静音而收尾"。返回 true 时走与录音上限/keyup 丢失
        // 完全相同的收尾路径（含 auto-repeat 抑制），因此不新增一条并行状态机。
        public Func<bool> ShouldAutoStopOnSilence;
        private DateTime _holdKeyLiftedAt = DateTime.MinValue;
        private bool _holdKeyWasDownOnce;
        private static readonly int[] WaveLags = new[] { 3, 1, 0, 2, 4 };
        private static readonly float[] WaveGains = new[] { 0.72f, 0.92f, 1.0f, 0.86f, 0.66f };

        public OverlayForm()
        {
            Size = new Size(256, 76); // ● REC + 时间码 + 五块声波
            CornerRadius = 16;
            SurfaceTop = Color.FromArgb(24, 28, 33);
            SurfaceBottom = Color.FromArgb(14, 17, 21);
            SetSurfaceAccent(BlockBlue);
            Font = new Font("Microsoft YaHei UI", 12f, FontStyle.Bold);

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 100; // 1/10 秒精度的时间码
            _timer.Tick += delegate { UpdateText(); };

            _levelTimer = new System.Windows.Forms.Timer();
            _levelTimer.Interval = 33; // 30fps
            _levelTimer.Tick += delegate { UpdateLevel(); };

            // 延迟显示：右 Ctrl 组合键（如 Ctrl+C）会立即取消录音，
            // 避免悬浮层在每次组合键时闪现。
            _delayShow = new System.Windows.Forms.Timer();
            _delayShow.Interval = 150;
            _delayShow.Tick += delegate
            {
                _delayShow.Stop();
                ShowLayeredAnimated(120); // 快速淡入 + 从下方升起
                _timer.Start();
            };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _delayShow.Stop();
                _delayShow.Dispose();
                _timer.Stop();
                _timer.Dispose();
                _levelTimer.Stop();
                _levelTimer.Dispose();
                if (_liveCaptionFont != null)
                {
                    _liveCaptionFont.Dispose();
                    _liveCaptionFont = null;
                }
            }
            base.Dispose(disposing);
        }

        protected override bool KeepTargetFocus
        {
            get { return true; }
        }

        public void ShowRecording(IntPtr targetWindow)
        {
            _startedAt = DateTime.Now;
            _recording = true;
            _holdKeyWasDownOnce = true; // 录音开始即认为热键处于按下态，供 keyup 丢失检测
            _holdKeyLiftedAt = DateTime.MinValue;
            _busyCaption = "";
            _liveCaption = "";        // 每次录音开始清空字幕，上一轮的字不许残留到新一轮
            _liveCaptionDisplay = "";
            _liveFitRevealed = -1;
            _liveFitWidth = -1;
            _liveAnim.Reset(WorkAreaWidthOf(targetWindow));
            _liveFrameAt = Environment.TickCount;
            _busy = false;
            _busyTarget = targetWindow;
            ResetWave();
            Size = new Size(LiveCaptionAnim.BaseWidth, LiveCaptionAnim.BaseHeight);
            Location = ScreenLayout.BottomCenterOf(targetWindow, Width, Height, 28);
            UpdateText();
            _levelTimer.Start();
            _delayShow.Start();
        }

        public void ShowBusy(string text, IntPtr targetWindow)
        {
            _timer.Stop();
            _levelTimer.Stop();
            _recording = false;
            ResetWave();
            _mainText = text;
            _busyCaption = "";
            _busy = true;
            _busyTarget = targetWindow;
            // 收尾态没有 30fps 帧循环了（_levelTimer 已停），按到达速率铺开所欠的那几个字必须在这里
            // 一次补齐，否则滞后会永远留在屏幕上。宽度**不**跳到位（那一下看得见）：字幕只显示
            // 结尾若干字，沿用当前宽度即可（见 UpdateBusyLayout）。
            _liveAnim.CompleteReveal();
            UpdateBusyLayout();
            ShowLayeredAnimated(140);
        }

        public void SetBusyCaption(string text)
        {
            if (!_busy) return; // 迟到的字幕封送不得改用已退场的浮层尺寸。
            // 本次录音已经有实时字幕时，收尾态保留的是**用户刚说的那行字**（见 UpdateBusyLayout），
            // 不让收尾期的增量文本进来抢这一行：流式分段的收尾请求只回最后 2~3 秒的尾段文本，
            // 覆盖上去会让整行突然缩短、前半句当场消失。第一行仍显示"识别中（Esc 取消）"。
            if (_liveCaption.Length > 0) return;
            text = text ?? "";
            if (_busyCaption == text) return;
            _busyCaption = text;
            UpdateBusyLayout();
            if (Visible && IsHandleCreated) RefreshLayeredSurface();
        }

        // 录音期实时字幕：把流式分段吐出来的文字画在浮层上，**只显示、不注入**。
        //
        // 为什么另开一条路径而不是复用 SetBusyCaption：两者是两套尺寸、两套文本来源——
        // 收尾态按状态文字测量宽度、高度 68（本次录音有实时字幕时保住录音胶囊的尺寸与那一行字，
        // 见 UpdateBusyLayout），而录音态是随说话生长的胶囊（见 AdvanceLiveCaption）、
        // 声波居中于 56px 内容盒。若在 _busy == false 时调 UpdateBusyLayout，
        // 录音态的浮层尺寸会被字幕文本长度牵着走，声波和红点当场错位。
        // speechMsTotal = 当时累计的**说话时长**（静音不计）。它是铺开速率的分母：
        // 用"距上批的间隔"当分母会把停顿的沉默算进去、把速率估低（见 LiveCaptionAnim 的常量注释）。
        public void SetLiveCaption(string text, int speechMsTotal)
        {
            // 只有"正在录音且还没进收尾"时才画字幕：收尾（_busy）与等待态走原有 SetBusyCaption，
            // 这里的迟到封送不得改尺寸、也不得覆盖收尾字幕。
            if (!_recording || _busy) return;
            text = text ?? "";
            if (_liveCaption == text) return;
            _liveCaption = text;
            // 这里只登记"全文有多长、这一批是什么时候到的、当时说了多久"，真正的推进交给 30fps
            // 帧循环（UpdateLevel → AdvanceLiveCaption）：字幕到达与动画解耦，一次到达一大段也不会
            // 让浮层瞬间长到位。
            _liveAnim.SetTextLength(text.Length, Environment.TickCount, speechMsTotal);
        }

        // 当前铺开速率（字/秒）。给主流程记一行可核对日志用，不参与绘制。
        public double LiveCaptionRate { get { return _liveAnim.RatePerSecond; } }

        // 字幕只显示**结尾**若干字，理由与忙碌态的 GetBusyDisplayText 一致：用户是在看着字往外冒，
        // 刚说出口的那一截才是新信息，前面说过的是已知内容。
        // 与忙碌态不同的是：录音胶囊会随说话变宽（见 AdvanceLiveCaption），"放不下"的门槛因此
        // 一路后移，直到宽度到上限才在这里丢掉最旧的字。
        // 裁字用逐字测量而不是固定字数：字号、DPI、中英混排字宽都会变，而
        // DrawLayeredText 的省略号是从**右侧**截的——固定字数一旦估大，被吃掉的就正好是
        // 最新吐出来的字，与"字幕"的目的相反。
        // 输入是**已经吐出来的那部分**文本，不是全文：逐字展开还没吐到的字不该提前占位，
        // 否则胶囊会先按全文长到位，字再慢慢往里填（与"随着说话变长"的观感正好相反）。
        private string FitLiveCaption(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            Font font = LiveCaptionFont();
            // 再留 6px 余量：这里用 GDI 的 TextRenderer 量，绘制走 GDI+ 的 DrawString，
            // 两者字宽有细微差异；宁可少显示一个字，也不能让 GDI+ 从右边裁掉最新的字。
            int available = SurfaceBounds.Width - RecCaptionLeftInset - RecCaptionRightInset - 6;
            if (available < 24) available = 24;
            string body = text;
            bool trimmed = false;
            const int maxChars = 60; // 先砍到 60 字再逐字测量，避免超长文本无谓空转
            if (body.Length > maxChars)
            {
                body = body.Substring(body.Length - maxChars);
                trimmed = true;
            }
            while (body.Length > 0
                && TextRenderer.MeasureText(trimmed ? "…" + body : body, font).Width > available)
            {
                body = body.Substring(1); // 从最旧的一侧丢，永远保住刚说出口的字
                trimmed = true;
            }
            if (body.Length == 0) return "";
            return trimmed ? "…" + body : body;
        }

        private Font LiveCaptionFont()
        {
            // 比正文（12pt）小一档，让一行能多放几个字；10pt 在 96dpi 下约 13.3px，
            // 仍满足项目规范里"关键正文不小于 12px"的要求。缓存复用，避免每帧 new Font。
            if (_liveCaptionFont == null)
                _liveCaptionFont = new Font("Microsoft YaHei UI", 10f, FontStyle.Regular);
            return _liveCaptionFont;
        }

        // 录音期布局：只在真的出现了可显示字幕之后才长高一行，并且本次录音内不再缩回
        //（文本变短就跟着缩的话，浮层会在屏幕底部来回跳动）。
        // 定位沿用 BottomCenterOf：底边距 28px 不变，所以浮层是**向上**长高——声波上移一行，
        // 字幕行落在原来那一带；这样底边永远钉在离屏幕底部 28px 处，符合"从屏幕底部出现"。
        // 以上是布局规则；高度与宽度都不是跳变，而是由下面这个每帧函数缓动推进
        //（首次让出字幕行约 160ms 长满，宽度约 330ms 收敛，底边全程不动）。
        // 每帧（30fps）推进字幕动画。顺序不能反：先按真实帧间隔吐出这一帧该显示的字，
        // 再按**这些字**实测的宽度去定胶囊宽度目标；反过来会变成"框先长好、字在后面追"。
        private void AdvanceLiveCaption()
        {
            // 帧间隔取真实时间差，不用假定的 33ms：Timer 会被系统节流，假定帧长会让动画在
            // 卡顿时偷偷跑快。Environment.TickCount 是 int 毫秒，unchecked 相减天然处理回绕。
            int now = Environment.TickCount;
            int dt = unchecked(now - _liveFrameAt);
            _liveFrameAt = now;
            if (dt < 0 || dt > 500) dt = 0; // 挂起/卡顿之后不要一次性把动画跳完

            if (_liveAnim.TextLength == 0) return; // 还没有任何字：保持 256×76
            int revealed = _liveAnim.AdvanceReveal(dt);
            int measured = 0;
            if (revealed > 0)
            {
                string shown = revealed >= _liveCaption.Length
                    ? _liveCaption
                    : _liveCaption.Substring(0, revealed);
                measured = TextRenderer.MeasureText(shown, LiveCaptionFont()).Width + RecCaptionChromeWidth;
            }
            _liveAnim.AdvanceLayout(dt, measured);
        }

        // 算出这一帧真正画出去的一行。缓存键是（已吐字数, 胶囊宽度）：两者都没变就沿用上一帧，
        // 因为逐字测量字体不便宜，而 30fps 的大多数帧只有声波在动。
        private void SyncLiveCaptionDisplay()
        {
            int revealed = _liveAnim.RevealedCount;
            if (revealed > _liveCaption.Length) revealed = _liveCaption.Length;
            if (revealed == _liveFitRevealed && Width == _liveFitWidth) return;
            _liveFitRevealed = revealed;
            _liveFitWidth = Width;
            _liveCaptionDisplay = revealed <= 0 ? "" : FitLiveCaption(_liveCaption.Substring(0, revealed));
        }

        // 胶囊是水平居中的，而 BottomCenterOf 只居中、不钳制，所以宽度上限必须先被工作区宽度压一次。
        private static int WorkAreaWidthOf(IntPtr targetWindow)
        {
            Screen screen = targetWindow != IntPtr.Zero
                ? Screen.FromHandle(targetWindow)
                : Screen.PrimaryScreen;
            return screen.WorkingArea.Width;
        }

        public void HideOverlay()
        {
            _delayShow.Stop();
            _timer.Stop();
            _levelTimer.Stop();
            _recording = false;
            _holdKeyWasDownOnce = false; // keyup 检测状态复位，防止下次录音误触发
            _holdKeyLiftedAt = DateTime.MinValue;
            _busy = false;
            ResetWave();
            HideLayeredAnimated(260); // 渐消 + 向下沉
        }

        private void ResetWave()
        {
            Array.Clear(_waveLevels, 0, _waveLevels.Length);
            Array.Clear(_levelHistory, 0, _levelHistory.Length);
            _historyIndex = 0;
            _visualLevel = 0;
        }

        // 收尾态的尺寸。本次录音有实时字幕时**沿用录音胶囊的宽度与两行高度**：用户要求"松手后
        // 保留刚才那行字"，若这里换回 68 高的小框，那行字就等于被换掉了，而且整块会硬缩一下
        //（实机反馈的"松手那一下会缩回去"）。
        // 宽度取 max(录音胶囊当前宽度, 状态文字所需宽度)：宽度**不**跳到目标值（那一下看得见，
        // 而字幕只显示结尾若干字，沿用当前宽度即可），但不能小到把"识别中（Esc 取消）"挤成省略号。
        private void UpdateBusyLayout()
        {
            string status = GetBusyDisplayText();
            int statusWidth = 26 + 31 + 10 + TextRenderer.MeasureText(status, Font).Width + 26;
            if (_liveCaption.Length > 0)
            {
                int width = _liveAnim.Width > statusWidth ? _liveAnim.Width : statusWidth;
                Size = new Size(width, LiveCaptionAnim.BaseHeight + LiveCaptionAnim.RowHeight);
            }
            else
            {
                Size = new Size(statusWidth, 68);
            }
            Location = ScreenLayout.BottomCenterOf(_busyTarget, Width, Height, 28);
        }

        private string GetBusyDisplayText()
        {
            string text = !string.IsNullOrEmpty(_busyCaption) ? _busyCaption : _mainText;
            if (string.IsNullOrEmpty(text)) return "";
            const int maxChars = 36;
            if (text.Length <= maxChars) return text;
            return "…" + text.Substring(text.Length - (maxChars - 1));
        }

        private void UpdateText()
        {
            // 时间码秒级（00:07）；100ms 刷新保证秒位切换即时。承担录音上限守护。
            int totalSeconds = Math.Max(0, (int)(DateTime.Now - _startedAt).TotalSeconds);
            if (_recording)
            {
                // cap: MaxRecordMs>0 用配置值；0 时也走 10 分钟安全闸，避免 keyup 丢失时无限录音。
                long capMs = MaxRecordMs > 0 ? MaxRecordMs : RecordSafeguardMs;
                // 静音自动停止必须排在第一个分支：后面的 keyup 探测分支只要 _holdKeyWasDownOnce
                // 为真就会进入，写成 else-if 放它后面将永远得不到执行机会。
                if (ShouldAutoStopOnSilence != null && ShouldAutoStopOnSilence())
                {
                    _recording = false; // 防重入
                    if (MaxDurationReached != null) MaxDurationReached("静音自动停止");
                }
                else if (totalSeconds * 1000L >= capMs)
                {
                    _recording = false; // 防重入
                    if (MaxDurationReached != null) MaxDurationReached("录音上限");
                }
                else if (HoldKeyStillDown != null && _holdKeyWasDownOnce)
                {
                    // keyup 丢失检测：录音中探测到热键已物理离开（持续 >=500ms 非 down），
                    // 判定 keyup 事件丢失（Alt+Tab/UAC 弹窗/安全桌面/RDP），自动收尾转写。
                    bool down = HoldKeyStillDown();
                    if (down) _holdKeyLiftedAt = DateTime.MinValue;
                    else if (_holdKeyLiftedAt == DateTime.MinValue) _holdKeyLiftedAt = DateTime.Now;
                    else if ((DateTime.Now - _holdKeyLiftedAt).TotalMilliseconds >= 500)
                    {
                        _recording = false;
                        _holdKeyLiftedAt = DateTime.MinValue;
                        if (MaxDurationReached != null) MaxDurationReached("热键松开丢失");
                    }
                }
            }
            _elapsedText = (totalSeconds / 60).ToString("00") + ":" + (totalSeconds % 60).ToString("00");
            if (IsHandleCreated && Visible) RefreshLayeredSurface();
        }

        private void UpdateLevel()
        {
            if (!_recording) return;
            float voice = Recorder.GetLevel();
            float gated = Math.Max(0, Math.Min(1, (voice - 0.012f) / (0.34f - 0.012f)));
            float eased = gated * gated * (3f - 2f * gated);
            float target = (float)Math.Pow(eased, 0.42);
            _visualLevel = target;
            // 静默呼吸：无声时给波形叠加极轻的正弦起伏，避免基线死板。
            if (target < 0.02f)
            {
                double nowMs = DateTime.UtcNow.TimeOfDay.TotalMilliseconds;
                for (int i = 0; i < _waveLevels.Length; i++)
                {
                    float breath = (float)(0.05 + 0.03 * Math.Sin(nowMs / 420.0 + i * 1.7));
                    _waveLevels[i] += (breath - _waveLevels[i]) * 0.08f;
                }
            }
            _levelHistory[_historyIndex] = target;
            for (int i = 0; i < _waveLevels.Length; i++)
            {
                int sampleIndex = _historyIndex - WaveLags[i];
                if (sampleIndex < 0) sampleIndex += _levelHistory.Length;
                float barTarget = Math.Max(0, Math.Min(1, _levelHistory[sampleIndex] * WaveGains[i]));
                float response = barTarget > _waveLevels[i]
                    ? 0.38f + (i % 3) * 0.05f          // 快起
                    : 0.11f + ((i + 1) % 3) * 0.02f;   // 缓落
                _waveLevels[i] += (barTarget - _waveLevels[i]) * response;
                if (Math.Abs(barTarget - _waveLevels[i]) < 0.005f) _waveLevels[i] = barTarget;
            }
            _historyIndex = (_historyIndex + 1) % _levelHistory.Length;

            AdvanceLiveCaption();
            // 尺寸变化用 SetBounds 一次到位（先改宽再改高会闪一帧错位），重绘交给 OnResize；
            // 尺寸没变才在这里补一次，保证每帧恰好重绘一次。
            int w = _liveAnim.Width;
            int h = _liveAnim.Height;
            if (w != Width || h != Height)
            {
                Point p = ScreenLayout.BottomCenterOf(_busyTarget, w, h, 28);
                SetBounds(p.X, p.Y, w, h);
                return;
            }
            if (IsHandleCreated && Visible) RefreshLayeredSurface();
        }

        protected override void DrawLayeredContent(Graphics g)
        {
            Rectangle surface = SurfaceBounds;
            if (_recording)
            {
                // 录音棚语言：● + 1/10 秒时间码 + 声波。红点是真实录音状态。
                // 第一行钉在内容盒顶部 56px 内：56 / 2 = 28，与改动前的 surface.Height / 2f
                // 逐像素相同（无字幕时 surface.Height 就是 56）；只有让出了字幕行之后，
                // 浮层才比原来高 30px，而第一行仍待在它原来的顶部位置、不跟着一起居中。
                float cy = surface.Top + RecRowHeight / 2f;
                using (var dot = new SolidBrush(Color.FromArgb(255, 69, 58)))
                {
                    g.FillEllipse(dot, surface.Left + 22, cy - 3.5f, 7, 7);
                }
                using (var timerFont = new Font("Consolas", 12f, FontStyle.Bold))
                {
                    // 时间码的包围盒必须钉在 56px 的第一行内：DrawLayeredText 是垂直居中的，
                    // 若沿用 surface.Height，让出字幕行后时间码会独自掉到字幕行上去，
                    // 而红点和声波还在第一行。无字幕时 56 == surface.Height，与改动前一致。
                    DrawLayeredText(
                        g,
                        _elapsedText,
                        timerFont,
                        Color.FromArgb(225, 242, 245, 247),
                        new RectangleF(surface.Left + 37, surface.Top, 56, RecRowHeight),
                        false);
                }
                DrawBrandWave(g, surface.Right - 74.5f, cy);
                DrawLiveCaptionRow(g, surface);
            }
            else
            {
                // 等待/收尾态：声音停了，波形归位成 Logo 的静止形态（休止符）。
                // 本次录音有实时字幕时，第二行**原地保留**刚才那行字（用户要求：松手后不要把它换成
                // "识别中"），第一行照旧是状态文字。因此状态文字必须钉在第一行的 56px 内，不能再用
                // surface.Height 垂直居中 —— 否则它会掉到字幕行上去，而 Logo 还留在第一行。
                bool keepLine = _liveCaption.Length > 0;
                float rowH = keepLine ? RecRowHeight : surface.Height;
                float markH = 20f;
                DrawLogoMark(g, surface.Left + 26, surface.Top + (rowH - markH) / 2f, markH);
                using (var font = new Font("Microsoft YaHei UI", 12f, FontStyle.Bold))
                {
                    DrawLayeredText(
                        g,
                        GetBusyDisplayText(),
                        font,
                        Color.FromArgb(214, 242, 245, 247),
                        new RectangleF(surface.Left + 26 + 31 + 10, surface.Top, surface.Width - 26 - 31 - 10 - 20, rowH),
                        false);
                }
                if (keepLine) DrawLiveCaptionRow(g, surface);
            }
        }

        // 实时字幕行：静音进行中的实时文字，只画在浮层上（本类不持有任何注入能力）。
        // 字幕行长到满高才画：这一行约 160ms 从 0 长到 30px，半高时画字会被玻璃边缘切掉上下两头，
        // 看上去像文字从缝里挤出来。
        // 录音态与收尾态共用它：收尾时录音帧循环已停，这一行必须由收尾这次重绘来同步，
        // 否则 CompleteReveal 补齐的最后几个字根本不会显示。
        private void DrawLiveCaptionRow(Graphics g, Rectangle surface)
        {
            SyncLiveCaptionDisplay();
            if (_liveAnim.RowPixels < RecCaptionRowHeight) return;
            DrawLayeredText(
                g,
                _liveCaptionDisplay,
                LiveCaptionFont(),
                Color.FromArgb(196, 242, 245, 247),
                new RectangleF(
                    surface.Left + RecCaptionLeftInset,
                    surface.Top + RecRowHeight,
                    surface.Width - RecCaptionLeftInset - RecCaptionRightInset,
                    RecCaptionRowHeight),
                false);
        }

        private void DrawBrandWave(Graphics g, float cx, float cy)
        {
            int blockW = 13, gap = 9;
            const float maxH = 38f, minH = 7f;
            float x = cx - (_waveLevels.Length * blockW + (_waveLevels.Length - 1) * gap) / 2f;
            for (int i = 0; i < _waveLevels.Length; i++)
            {
                float lvl = Math.Max(0, Math.Min(1, _waveLevels[i])) * WaveShape[i];
                float bh = minH + (maxH - minH) * Math.Max(0f, Math.Min(1f, lvl));
                float skew = bh * 0.22f; // 前倾：与母版一致的动势
                float half = blockW / 2f;
                var pts = new[]
                {
                    new PointF(x + half + skew, cy - bh / 2),
                    new PointF(x - half + skew, cy - bh / 2),
                    new PointF(x - half - skew, cy + bh / 2),
                    new PointF(x + half - skew, cy + bh / 2)
                };
                Color c = (i == 1 || i == 2 || i == 4) ? BlockBlue : BlockCyan;
                using (var brush = new SolidBrush(c))
                {
                    g.FillPolygon(brush, pts);
                }
                x += blockW + gap;
            }
        }

        private void DrawLogoMark(Graphics g, float left, float top, float height)
        {
            float width = height * 577f / 382f;
            foreach (float[] b in LogoBlocks)
            {
                var pts = new[]
                {
                    new PointF(left + b[0] * width, top + b[4] * height),
                    new PointF(left + b[1] * width, top + b[4] * height),
                    new PointF(left + b[3] * width, top + b[5] * height),
                    new PointF(left + b[2] * width, top + b[5] * height)
                };
                Color c = b[6] < 0.5f ? BlockCyan : BlockBlue;
                using (var brush = new SolidBrush(c))
                {
                    g.FillPolygon(brush, pts);
                }
            }
        }
    }

    internal sealed class ToastForm : LayeredGlassForm
    {
        private readonly System.Windows.Forms.Timer _timer;
        private Color _signalColor = Color.FromArgb(0, 70, 241); // 品牌蓝
        private string _text = "";
        private bool _centered;

        public ToastForm()
        {
            Size = new Size(180, 60);
            CornerRadius = 16; // 与录音 HUD 同一圆角体系
            SurfaceTop = Color.FromArgb(24, 28, 33);
            SurfaceBottom = Color.FromArgb(14, 17, 21);
            Font = new Font("Microsoft YaHei UI", 11.5f, FontStyle.Bold);

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 3500;
            _timer.Tick += delegate
            {
                _timer.Stop();
                HideLayeredAnimated(240); // 渐消更从容
            };
        }

        protected override bool KeepTargetFocus
        {
            get { return true; }
        }

        public void ShowToast(string text)
        {
            ShowToast(text, IntPtr.Zero, false);
        }

        public void ShowToastCentered(string text, IntPtr targetWindow)
        {
            ShowToast(text, targetWindow, true);
        }

        private void ShowToast(string text, IntPtr targetWindow, bool centered)
        {
            _text = text;
            _centered = centered;
            int textWidth = TextRenderer.MeasureText(text, Font).Width;
            int width = centered
                ? GetCenteredToastWidth(text)
                : Math.Max(180, Math.Min(420, textWidth + 76));
            Size = new Size(width, 60);
            _signalColor = text.IndexOf("失败", StringComparison.Ordinal) >= 0
                ? Color.FromArgb(91, 80, 78)
                : Color.FromArgb(0, 70, 241); // 成功提示：品牌蓝光效
            SetSurfaceAccent(_signalColor);
            if (centered)
            {
                Location = ScreenLayout.BottomCenterOf(targetWindow, Width, Height, 28);
            }
            else
            {
                Rectangle area = Screen.PrimaryScreen.WorkingArea;
                Location = new Point(area.Right - Width - 24, area.Bottom - Height - 24);
            }
            ShowLayeredAnimated(140);
            _timer.Stop();
            _timer.Start();
        }

        protected override void DrawLayeredContent(Graphics g)
        {
            Rectangle surface = SurfaceBounds;
            if (_centered)
            {
                DrawCenteredToast(g, surface);
                return;
            }
            // 无装饰点；失败语义用色，其余中性白。
            Color textColor = _text.IndexOf("失败", StringComparison.Ordinal) >= 0
                ? Color.FromArgb(235, 255, 122, 107)
                : Color.FromArgb(235, 242, 245, 247);
            using (var font = new Font("Microsoft YaHei UI", 11.5f, FontStyle.Bold))
            {
                DrawLayeredText(
                    g,
                    _text,
                    font,
                    textColor,
                    new RectangleF(surface.Left + 20, surface.Top + 8, surface.Width - 36, surface.Height - 16),
                    false);
            }
        }

        private int GetCenteredToastWidth(string text)
        {
            Match match = Regex.Match(text, @"^已输入\s*(\d+)\s*字(.*)$");
            if (!match.Success)
            {
                return Math.Max(156, Math.Min(420, TextRenderer.MeasureText(text, Font).Width + 48));
            }

            string suffix = match.Groups[2].Value.Trim();
            using (var leadFont = new Font("Microsoft YaHei UI", 11.5f, FontStyle.Bold))
            using (var numberFont = new Font("Segoe UI Semibold", 18f, FontStyle.Regular))
            using (var unitFont = new Font("Microsoft YaHei UI", 11.5f, FontStyle.Bold))
            using (var suffixFont = new Font("Microsoft YaHei UI", 10f, FontStyle.Regular))
            {
                int contentWidth = TextRenderer.MeasureText("已输入", leadFont).Width;
                contentWidth += 7 + TextRenderer.MeasureText(match.Groups[1].Value, numberFont).Width;
                contentWidth += 5 + TextRenderer.MeasureText("字", unitFont).Width;
                if (suffix.Length > 0)
                {
                    contentWidth += 12 + TextRenderer.MeasureText(suffix, suffixFont).Width;
                }
                return Math.Max(156, Math.Min(420, contentWidth + 48));
            }
        }

        private void DrawCenteredToast(Graphics g, Rectangle surface)
        {
            Match match = Regex.Match(_text, @"^已输入\s*(\d+)\s*字(.*)$");
            if (!match.Success)
            {
                using (var font = new Font("Microsoft YaHei UI", 11.5f, FontStyle.Bold))
                {
                    DrawLayeredText(
                        g,
                        _text,
                        font,
                        Color.FromArgb(255, 24, 33, 38),
                        new RectangleF(surface.Left + 18, surface.Top + 8, surface.Width - 36, surface.Height - 16),
                        true);
                }
                return;
            }

            string number = match.Groups[1].Value;
            string suffix = match.Groups[2].Value.Trim();
            const float leadGap = 7f;
            const float unitGap = 5f;
            const float suffixGap = 12f;
            using (var leadFont = new Font("Microsoft YaHei UI", 11.5f, FontStyle.Bold))
            using (var numberFont = new Font("Segoe UI Semibold", 18f, FontStyle.Regular))
            using (var unitFont = new Font("Microsoft YaHei UI", 11.5f, FontStyle.Bold))
            using (var suffixFont = new Font("Microsoft YaHei UI", 10f, FontStyle.Regular))
            using (var leadBrush = new SolidBrush(Color.FromArgb(185, 242, 245, 247)))
            using (var numberBrush = new SolidBrush(Color.FromArgb(255, 242, 245, 247)))
            using (var unitBrush = new SolidBrush(Color.FromArgb(185, 242, 245, 247)))
            using (var suffixBrush = new SolidBrush(Color.FromArgb(130, 242, 245, 247)))
            {
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                g.TextContrast = 0;
                SizeF leadSize = g.MeasureString("已输入", leadFont);
                SizeF numberSize = g.MeasureString(number, numberFont);
                SizeF unitSize = g.MeasureString("字", unitFont);
                SizeF suffixSize = suffix.Length > 0 ? g.MeasureString(suffix, suffixFont) : SizeF.Empty;
                float totalWidth = leadSize.Width + leadGap + numberSize.Width + unitGap + unitSize.Width;
                if (suffix.Length > 0) totalWidth += suffixGap + suffixSize.Width;
                float x = surface.Left + (surface.Width - totalWidth) / 2f;
                float centerY = surface.Top + surface.Height / 2f;

                g.DrawString("已输入", leadFont, leadBrush,
                    new PointF(x, centerY - leadSize.Height / 2f + 2f));
                x += leadSize.Width + leadGap;
                g.DrawString(number, numberFont, numberBrush,
                    new PointF(x, centerY - numberSize.Height / 2f - 1f));
                x += numberSize.Width + unitGap;
                g.DrawString("字", unitFont, unitBrush,
                    new PointF(x, centerY - unitSize.Height / 2f + 2f));
                if (suffix.Length > 0)
                {
                    x += unitSize.Width + suffixGap;
                    g.DrawString(suffix, suffixFont, suffixBrush,
                        new PointF(x, centerY - suffixSize.Height / 2f + 3f));
                }
            }
        }
    }

    internal sealed class ReviewForm : GlassForm
    {
        private readonly TextBox _box;
        private readonly Label _title;
        private readonly Label _meta;
        private readonly Label _count;
        private readonly Action<string> _onWrite;
        private readonly Action<string> _onCopied;
        private readonly string _original;
        private readonly string _sourceText;
        private bool _finished;

        public ReviewForm(string text, string sourceText, string meta, Action<string> onWrite, Action<string> onCopied)
        {
            _onWrite = onWrite;
            _onCopied = onCopied;
            _original = text ?? "";
            _sourceText = sourceText ?? "";

            Size = new Size(660, 314);
            CornerRadius = 32;
            SurfaceTop = Color.FromArgb(250, 253, 252);
            SurfaceBottom = Color.FromArgb(231, 243, 240);
            SetSurfaceAccent(Color.FromArgb(32, 119, 133));
            Font = new Font("Microsoft YaHei UI", 10.5f);

            _title = new Label();
            _title.Location = new Point(24, 18);
            _title.Size = new Size(390, 26);
            _title.ForeColor = Color.FromArgb(24, 33, 38);
            _title.BackColor = Color.Transparent;
            _title.Font = new Font("Microsoft YaHei UI", 15f, FontStyle.Bold);
            _title.Text = "确认文字，再写入";

            _meta = new Label();
            _meta.Location = new Point(25, 47);
            _meta.Size = new Size(590, 18);
            _meta.ForeColor = Color.FromArgb(91, 112, 113);
            _meta.BackColor = Color.Transparent;
            _meta.Font = new Font("Microsoft YaHei UI", 9f);
            _meta.Text = meta;

            var paper = new Panel();
            paper.Location = new Point(24, 76);
            paper.Size = new Size(612, 140);
            paper.BackColor = Color.FromArgb(248, 252, 250);
            using (var paperPath = CreateRoundedPath(new Rectangle(0, 0, paper.Width, paper.Height), 18))
            {
                paper.Region = new Region(paperPath);
            }

            _box = new TextBox();
            _box.Location = new Point(39, 87);
            _box.Size = new Size(582, 118);
            _box.Multiline = true;
            _box.WordWrap = true;
            _box.ScrollBars = ScrollBars.Vertical;
            _box.BorderStyle = BorderStyle.None;
            _box.BackColor = Color.FromArgb(248, 252, 250);
            _box.ForeColor = Color.FromArgb(24, 33, 38);
            _box.Font = new Font("Microsoft YaHei UI", 11.5f);
            _box.Padding = new Padding(0, 0, 0, 0);
            _box.Text = _original;
            _box.TextChanged += delegate { UpdateCount(); };

            var hint = new Label();
            hint.Location = new Point(25, 228);
            hint.Size = new Size(285, 20);
            hint.ForeColor = Color.FromArgb(108, 130, 130);
            hint.BackColor = Color.Transparent;
            hint.Text = "Ctrl+Enter 写入 · Esc 取消";

            _count = new Label();
            _count.Location = new Point(275, 228);
            _count.Size = new Size(120, 20);
            _count.TextAlign = ContentAlignment.MiddleRight;
            _count.ForeColor = Color.FromArgb(108, 130, 130);
            _count.BackColor = Color.Transparent;

            var sourceBtn = MakeButton("查看原文", Color.FromArgb(245, 250, 248), Color.FromArgb(52, 72, 74), false);
            sourceBtn.Location = new Point(25, 260);
            sourceBtn.Size = new Size(96, 38);
            sourceBtn.Click += delegate { _box.Text = _sourceText; };

            var writeBtn = MakeButton("写入光标处", Color.FromArgb(220, 241, 236), Color.FromArgb(21, 81, 90), true);
            writeBtn.Location = new Point(336, 260);
            writeBtn.Size = new Size(148, 38);
            writeBtn.Click += delegate
            {
                _onWrite(_box.Text);
                _finished = true;
                Close();
            };

            var copyBtn = MakeButton("复制", Color.FromArgb(245, 250, 248), Color.FromArgb(52, 72, 74), false);
            copyBtn.Location = new Point(494, 260);
            copyBtn.Size = new Size(72, 38);
            copyBtn.Click += delegate
            {
                try
                {
                    Clipboard.SetText(_box.Text);
                    _onCopied("已复制 " + _box.Text.Length + " 字");
                    _finished = true;
                    Close();
                }
                catch
                {
                    // 复制失败时不能关窗：内容既未写入也未保留，静默丢弃违反草稿保护承诺。
                    _onCopied("剪贴板被占用，未复制，可再试一次");
                }
            };

            var cancelBtn = MakeButton("取消", Color.FromArgb(245, 250, 248), Color.FromArgb(52, 72, 74), false);
            cancelBtn.Location = new Point(574, 260);
            cancelBtn.Size = new Size(62, 38);
            cancelBtn.Click += delegate { Close(); };

            Controls.Add(paper);
            Controls.Add(_box);
            Controls.Add(_title);
            Controls.Add(_meta);
            Controls.Add(hint);
            Controls.Add(_count);
            Controls.Add(sourceBtn);
            Controls.Add(writeBtn);
            Controls.Add(copyBtn);
            Controls.Add(cancelBtn);

            KeyPreview = true;
            KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape)
                {
                    e.SuppressKeyPress = true;
                    Close();
                }
                else if (e.KeyCode == Keys.Enter && e.Control)
                {
                    e.SuppressKeyPress = true;
                    _onWrite(_box.Text);
                    _finished = true;
                    Close();
                }
            };

            UpdateCount();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!e.Cancel && !_finished)
            {
                string current = _box.Text;
                if (!string.IsNullOrEmpty(current) && current != _original)
                {
                    DialogResult result = MessageBox.Show(
                        this,
                        "修改过的文字还没有写入，确定放弃吗？",
                        "放弃修改",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button2);
                    if (result == DialogResult.No)
                    {
                        e.Cancel = true;
                        return;
                    }
                }
            }
            base.OnFormClosing(e);
        }

        private static Button MakeButton(string text, Color back, Color fore, bool primary)
        {
            var b = new Button();
            b.Text = text;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = primary
                ? Color.FromArgb(157, 204, 198)
                : Color.FromArgb(211, 225, 222);
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.MouseOverBackColor = primary
                ? Color.FromArgb(229, 246, 241)
                : Color.FromArgb(255, 255, 255);
            b.FlatAppearance.MouseDownBackColor = primary
                ? Color.FromArgb(205, 233, 226)
                : Color.FromArgb(233, 243, 240);
            b.UseVisualStyleBackColor = false;
            b.BackColor = back;
            b.ForeColor = fore;
            b.Font = new Font("Microsoft YaHei UI", 10.5f);
            b.Padding = new Padding(4, 0, 4, 0);
            b.Cursor = Cursors.Hand;
            return b;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Activate();
            _box.Focus();
            // 不全选：避免误触一键清空识别结果（无撤销）；光标置于末尾便于续写。
            _box.SelectionStart = _box.Text.Length;
            _box.ScrollToCaret();
        }

        private void UpdateCount()
        {
            _count.Text = _box.Text.Length + " 字";
        }
    }

    // 目标窗口安全判定（第一性原理：注入前必须确认“目标窗口确实接收输入”，
    // 不能依赖 SendInput 返回值——它只反映事件排入输入队列，不反映送达）。
    internal static class TargetSafety
    {
        private const uint ProcessQueryLimitedInformation = 0x1000;
        private const uint TokenQuery = 0x0008;
        private const int TokenIntegrityLevel = 25;
        private const int IntegrityMedium = 4096;

        // 进程完整性级别（SID 最后子权限值）；查询失败返回 -1。
        private static int QueryIntegrity(IntPtr hProcess)
        {
            IntPtr token;
            if (!Native.OpenProcessToken(hProcess, TokenQuery, out token)) return -1;
            try
            {
                uint len;
                if (!Native.GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out len)) return -1;
                if (len < (uint)Marshal.SizeOf(typeof(Native.TOKEN_MANDATORY_LABEL))) return -1;
                IntPtr buf = Marshal.AllocHGlobal((int)len);
                try
                {
                    if (!Native.GetTokenInformation(token, TokenIntegrityLevel, buf, len, out len)) return -1;
                    Native.TOKEN_MANDATORY_LABEL label = (Native.TOKEN_MANDATORY_LABEL)
                        Marshal.PtrToStructure(buf, typeof(Native.TOKEN_MANDATORY_LABEL));
                    if (label.Label.Sid == IntPtr.Zero) return -1;
                    byte count = Native.GetSidSubAuthorityCount(label.Label.Sid);
                    if (count == 0) return -1;
                    IntPtr pSub = Native.GetSidSubAuthority(label.Label.Sid, (uint)(count - 1));
                    if (pSub == IntPtr.Zero) return -1;
                    return Marshal.ReadInt32(pSub);
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            finally { Native.CloseHandle(token); }
        }

        // 目标进程完整性级别；查询失败返回 -1。
        public static int IntegrityLevel(IntPtr window)
        {
            if (window == IntPtr.Zero) return -1;
            uint pid;
            if (Native.GetWindowThreadProcessId(window, out pid) == 0 || pid == 0) return -1;
            IntPtr hProc = Native.OpenProcess(ProcessQueryLimitedInformation, false, pid);
            if (hProc == IntPtr.Zero) return -1;
            try { return QueryIntegrity(hProc); }
            finally { Native.CloseHandle(hProc); }
        }

        // 目标完整性是否高于本进程——高权限窗口会丢弃 SendInput/Ctrl+V（UIPI）。
        public static bool IsHigherIntegrity(IntPtr window)
        {
            int targetLevel = IntegrityLevel(window);
            if (targetLevel <= 0) return false;
            int selfLevel = QueryIntegrity(Native.GetCurrentProcess());
            if (selfLevel <= 0) selfLevel = IntegrityMedium;
            return targetLevel > selfLevel;
        }
    }

    internal sealed class TrayContext : ApplicationContext
    {
        private enum State { Idle, Recording, Transcribing, Reviewing }

        private readonly Config _cfg;
        private readonly NotifyIcon _tray;
        private SettingsForm _settingsForm;
        private readonly OverlayForm _overlay;
        private readonly ToastForm _toast;
        private readonly Native.LowLevelKeyboardProc _hookProc;
        private readonly System.Windows.Forms.Timer _configReloadTimer;
        private readonly EventWaitHandle _openSettingsSignal;
        private DateTime _lastConfigWriteUtc;
        private IntPtr _hook = IntPtr.Zero;
        private State _state = State.Idle;
        private DateTime _recordStart;
        private IntPtr _target = IntPtr.Zero;
        private uint _targetPid; // 录音开始时的目标进程 ID，注入前复核句柄是否被复用（P1：窗口身份校验）
        private bool _hotkeyWasDown;
        private AsrSession _asrSession;
        // 边说边送会话：仅在开启 streamingSegments 时非空。waveIn 线程会读它，故 volatile。
        private volatile StreamingSegmentRunner _streamRunner;
        // 合成修饰键窗口的截止时间，见 InjectWithModifierRelease。
        private DateTime _syntheticModifierUntil = DateTime.MinValue;
        // 本会话是否已经把原文"边说边"注入过输入框。
        // 收尾逻辑跑在另一个方法（UI 线程的完成回调）里，拿不到 TranscribeWorker 的局部
        // 变量，所以用字段传递：它决定收尾还能不能写入（写入就会重复）。
        // 本会话是否走了边说边送（流式）。为真时输入框就是审阅面，不再弹审阅卡片。
        private volatile bool _streamUsed;
        // 当前会话的延迟分解。在按下热键时创建（时间轴 0 点），随会话传给 ASR provider。
        private LatencyTrace _trace;
        private System.Windows.Forms.Timer _cancelWatchdog;
        private DateTime _suppressHotkeyUntil; // 自动收尾（超时/keyup 丢失）后短暂抑制热键自动重复触发

        public TrayContext(bool openSettingsOnStart)
        {
            _cfg = Config.Load();
            _overlay = new OverlayForm();
            _overlay.MaxRecordMs = _cfg.MaxRecordMs;
            _overlay.MaxDurationReached = delegate(string reason)
            {
                // 自动收尾（静音自动停止/录音上限/keyup 丢失兜底）后热键若仍被按住，auto-repeat
                // 的 keydown 会在转写完成后误触发新一轮录音；抑制 1 秒，等用户实际松开再按才允许。
                // 顺带把"到底是谁结束了这次录音"写进日志——先前三个原因共用一个无参回调，
                // 日志里分不清是自动收尾还是松手，只能靠推断。
                Log.Write("录音收尾: " + reason);
                _suppressHotkeyUntil = DateTime.Now.AddSeconds(1);
                OnHoldRelease();
            };
            // 对抗审查 P1：向浮层注入热键实测探测，keyup 丢失（Alt+Tab/UAC/安全桌面/RDP）时自动收尾；toggle 模式不适用（不依赖按住）。
            _overlay.HoldKeyStillDown = delegate
            {
                if (IsToggleMode(_cfg)) return true; // toggle 模式不探测“抬起”，避免误终止
                // 合成修饰键窗口内必须仍然回答"按住"：注入文字前我们会伪造一次该修饰键的
                // 抬起，而 SendInput 会改变 GetAsyncKeyState 的结果。不挡住的话，第一次
                // 有字注入就会被误判成松手，录音当场结束。
                if (DateTime.Now < _syntheticModifierUntil) return true;
                return KeyDown(GetHotkeyVk(_cfg));
            };
            // 静音自动停止：分段器只回答"安静了多久"，阈值与开关都来自配置。默认关闭。
            // 关闭时这里只是一次委托调用就返回，不触碰分段器的锁。
            _overlay.ShouldAutoStopOnSilence = delegate
            {
                if (!_cfg.AutoStopOnSilence) return false;
                return Recorder.ShouldAutoStopOnSilence(_cfg.AutoStopSilenceMs);
            };
            // 边说边送：分段就绪回调由 waveIn 线程触发，这里只派发、不做 I/O。
            // 没有流式会话时直接丢弃——默认关闭时这条链路完全不存在。
            Recorder.SegmentReady = delegate(byte[] pcm)
            {
                StreamingSegmentRunner r = _streamRunner;
                if (r != null) r.Dispatch(pcm);
            };
            _toast = new ToastForm();

            var menu = new ContextMenuStrip();
            var settingsItem = new ToolStripMenuItem("设置…");
            settingsItem.Click += delegate { ShowSettings(); };
            var openSettings = new ToolStripMenuItem("高级：打开配置文件");
            openSettings.Click += delegate
            {
                if (SettingsBusy())
                {
                    _toast.ShowToast("当前会话未结束，暂时不能打开配置文件");
                    return;
                }
                if (!File.Exists(Config.SettingsPath))
                {
                    File.WriteAllText(Config.SettingsPath, SettingsTemplate.Default, Encoding.UTF8);
                }
                try { Process.Start("notepad.exe", Config.SettingsPath); }
                catch { }
                _toast.ShowToast("高级配置文件已打开；保存后空闲时会自动重载");
            };
            var openFolder = new ToolStripMenuItem("打开程序目录");
            openFolder.Click += delegate
            {
                try { Process.Start("explorer.exe", AppDomain.CurrentDomain.BaseDirectory); }
                catch { }
            };
            var exit = new ToolStripMenuItem("退出");
            exit.Click += delegate { ExitApp(); };
            menu.Items.Add(settingsItem);
            menu.Items.Add(openSettings);
            menu.Items.Add(openFolder);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exit);

            _tray = new NotifyIcon();
            _tray.Icon = MakeIcon();
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += delegate { ShowSettings(); };
            UpdateTrayText();
            _tray.Visible = true;

            // 命名事件：同用户的二次启动进程可以请求本托盘进程打开设置窗口。
            try
            {
                _openSettingsSignal = new EventWaitHandle(false, EventResetMode.AutoReset, Program.SettingsSignalName);
            }
            catch (Exception ex)
            {
                _openSettingsSignal = null;
                Log.Write("设置请求事件创建失败: " + ex.Message);
            }

            _hookProc = HookProc;
            _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _hookProc, Native.GetModuleHandle(null), 0);
            _lastConfigWriteUtc = GetConfigWriteTimeUtc();
            _configReloadTimer = new System.Windows.Forms.Timer();
            _configReloadTimer.Interval = 1000;
            _configReloadTimer.Tick += delegate
            {
                ConsumeOpenSettingsSignal();
                ReloadConfigIfChanged();
            };
            _configReloadTimer.Start();

            Log.Write("启动: model=" + _cfg.Model + " hasKey=" + _cfg.HasKey + " hook=" + (_hook != IntPtr.Zero));
            if (_hook == IntPtr.Zero)
            {
                // 对抗审查 P1：钩子安装失败仅留日志等于热键全盲且无人知晓（安全软件拦截常见）。
                _toast.ShowToast("全局热键不可用，可能被安全软件拦截；可从托盘右键打开设置");
                Log.Write("警告: 键盘钩子安装失败，热键功能不可用，需从托盘菜单操作");
            }
            if (!string.IsNullOrEmpty(_cfg.ParseIssue))
            {
                _toast.ShowToast("配置需要检查，请打开设置查看");
            }
            else if (!_cfg.HasKey)
            {
                _toast.ShowToast("请先打开设置填写 API Key（托盘右键 → 设置…）");
            }
            // 默认只驻留托盘，不弹窗；仅两种例外：
            //  1) 显式带 --settings 参数启动；
            //  2) 首次使用、尚未配置 API Key（否则程序不可用，引导一次）。
            if (openSettingsOnStart || !_cfg.HasKey) ShowSettings();
        }

        private void ConsumeOpenSettingsSignal()
        {
            try
            {
                if (_openSettingsSignal == null || !_openSettingsSignal.WaitOne(0)) return;
                Log.Write("收到二次启动的设置请求");
                ShowSettings();
            }
            catch { }
        }

        private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                try
                {
                    var data = (Native.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.KBDLLHOOKSTRUCT));
                    int msg = (int)wParam;
                    bool down = msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN;
                    bool up = msg == Native.WM_KEYUP || msg == Native.WM_SYSKEYUP;
                    int hotkeyVk = GetHotkeyVk(_cfg);
                    bool isHotkey = data.vkCode == (uint)hotkeyVk;
                    bool swallowHotkey = hotkeyVk == Native.VK_CAPITAL && isHotkey && (down || up);

                    if (_state == State.Transcribing && down && data.vkCode == Native.VK_ESCAPE)
                    {
                        if (_asrSession != null) _asrSession.Abort();
                        ArmCancelWatchdog(_asrSession);
                        return (IntPtr)1;
                    }

                    if (isHotkey)
                    {
                        if (down)
                        {
                            if (!_hotkeyWasDown)
                            {
                                // 自动收尾后的热键抑制期：忽略 auto-repeat 的 keydown，不触发新一轮（审查 P2）。
                                if (DateTime.Now < _suppressHotkeyUntil) return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
                                _hotkeyWasDown = true;
                                if (_state == State.Idle)
                                {
                                    if (!OtherModifierDown(hotkeyVk)) OnHoldStart();
                                }
                                else if (_state == State.Recording && IsToggleMode(_cfg))
                                {
                                    OnHoldRelease();
                                }
                            }
                        }
                        else if (up)
                        {
                            _hotkeyWasDown = false;
                            if (_state == State.Recording && !IsToggleMode(_cfg))
                            {
                                OnHoldRelease();
                            }
                        }
                        if (swallowHotkey) return (IntPtr)1;
                    }
                    else if (_state == State.Recording && down)
                    {
                        if (data.vkCode == Native.VK_ESCAPE)
                        {
                            CancelRecording();
                            return (IntPtr)1;
                        }
                        CancelRecording();
                    }
                }
                catch (Exception ex)
                {
                    Log.Write("钩子异常: " + ex.Message);
                }
            }
            return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        private static bool OtherModifierDown(int hotkeyVk)
        {
            return ShiftModifierDown(hotkeyVk) || MenuModifierDown(hotkeyVk) || ControlModifierDown(hotkeyVk)
                || KeyDown(Native.VK_LWIN) || KeyDown(Native.VK_RWIN);
        }

        private static bool ShiftModifierDown(int hotkeyVk)
        {
            // 聚合 VK_SHIFT 分不出左右：热键是右 Shift 时只查左 Shift，
            // 否则“按住热键 + 对侧同键”会被误判为没有其他修饰键。
            if (hotkeyVk == Native.VK_RSHIFT) return KeyDown(Native.VK_LSHIFT);
            return KeyDown(Native.VK_SHIFT);
        }

        private static bool MenuModifierDown(int hotkeyVk)
        {
            if (hotkeyVk == Native.VK_RMENU) return KeyDown(Native.VK_LMENU);
            return KeyDown(Native.VK_MENU);
        }

        private static bool ControlModifierDown(int hotkeyVk)
        {
            // 热键是右 Ctrl 时只查左 Ctrl（右 Ctrl 自身会同时置位聚合 VK_CONTROL，
            // 查聚合键会拦住自己）。右 Alt 热键必须查聚合键：AltGr 合成的是
            // VK_CONTROL（不是 VK_RCONTROL），查聚合键才能区分“纯右 Alt（可触发）”
            // 与“AltGr 组合（应拦截）”，否则每次 AltGr 打字都会误开一次录音。
            if (hotkeyVk == Native.VK_RCONTROL) return KeyDown(Native.VK_LCONTROL);
            return KeyDown(Native.VK_CONTROL) || KeyDown(Native.VK_LCONTROL);
        }

        private static bool KeyDown(int vk)
        {
            return (Native.GetAsyncKeyState(vk) & 0x8000) != 0;
        }

        private static bool IsToggleMode(Config cfg)
        {
            return cfg != null && string.Equals(cfg.HotkeyMode, "toggle", StringComparison.OrdinalIgnoreCase);
        }

        private static int GetHotkeyVk(Config cfg)
        {
            string hotkey = cfg == null ? "" : cfg.Hotkey;
            if (hotkey == "RMenu") return Native.VK_RMENU;
            if (hotkey == "RShift") return Native.VK_RSHIFT;
            if (hotkey == "Capital") return Native.VK_CAPITAL;
            return Native.VK_RCONTROL;
        }

        private static string GetHotkeyName(Config cfg)
        {
            string hotkey = cfg == null ? "" : cfg.Hotkey;
            if (hotkey == "RMenu") return "右 Alt";
            if (hotkey == "RShift") return "右 Shift";
            if (hotkey == "Capital") return "Caps Lock";
            return "右 Ctrl";
        }

        private static string BuildTrayText(Config cfg)
        {
            string hotkeyName = GetHotkeyName(cfg);
            string hint = IsToggleMode(cfg)
                ? hotkeyName + " 开始/结束"
                : "按住" + hotkeyName + "说话";
            return "VoxLeap 声跃 · " + hint;
        }

        private void UpdateTrayText()
        {
            string text = BuildTrayText(_cfg);
            if (text.Length > 63) text = text.Substring(0, 63);
            _tray.Text = text;
        }

        private void ApplyConfigRuntime()
        {
            _overlay.MaxRecordMs = _cfg.MaxRecordMs;
            UpdateTrayText();
        }

        // 取消必须保证回到 Idle：HTTP Abort 通常会让阻塞读抛异常，
        // 但个别驱动/代理下可能不返回；看门狗 2 秒后强制收尾。
        private void ArmCancelWatchdog(AsrSession session)
        {
            if (_cancelWatchdog != null)
            {
                _cancelWatchdog.Stop();
                _cancelWatchdog.Dispose();
            }
            var timer = new System.Windows.Forms.Timer();
            timer.Interval = 2000;
            timer.Tick += delegate
            {
                timer.Stop();
                timer.Dispose();
                if (_cancelWatchdog == timer) _cancelWatchdog = null;
                if (_state == State.Transcribing && object.ReferenceEquals(_asrSession, session))
                {
                    _asrSession = null;
                    _state = State.Idle;
                    _hotkeyWasDown = false;
                    _overlay.HideOverlay();
                    _toast.ShowToast("已取消，文字未保留");
                    Log.Write("取消看门狗兜底: 转写线程未在时限内返回");
                    // 悬挂会话的延迟分解同样有用：能看出请求卡在哪一段。
                    LogSessionTrace(session == null ? null : session.Trace);
                }
            };
            _cancelWatchdog = timer;
            timer.Start();
        }

        private void OnHoldStart()
        {
            // 时间轴 0 点 = 用户按下热键的瞬间。因此 trace.At(Release) 即录音时长，
            // 而「松手后 = 总时长 - 录音时长」就是用户真正在等的那段。
            _trace = new LatencyTrace();
            try
            {
                if (SettingsFormOpen())
                {
                    _toast.ShowToast("请先关闭设置窗口后再录音");
                    return;
                }
                if (!_cfg.HasKey)
                {
                    _toast.ShowToast("还没有配置 API Key（托盘右键 → 设置…）");
                    return;
                }
                IntPtr fg = Native.GetForegroundWindow();
                if (IsPasswordField(fg))
                {
                    _toast.ShowToast("检测到密码框，已跳过录音");
                    Log.Write("跳过录音: 目标控件带密码样式");
                    return;
                }
                if (!Recorder.Start(_cfg.VadThreshold))
                {
                    _toast.ShowToast("无法打开麦克风，检查录音设备");
                    return;
                }
                _target = fg;
                // 返回值为线程 ID；out 参数才是进程 ID
                Native.GetWindowThreadProcessId(fg, out _targetPid);
                _recordStart = DateTime.Now;
                _state = State.Recording;
                _overlay.ShowRecording(fg);
                _streamUsed = false;
                // 边说边送会话：默认关闭时为 null，整条流式链路完全不存在。
                if (_cfg.StreamingSegments)
                {
                    StreamingSegmentRunner runner = new StreamingSegmentRunner(_cfg);
                    // 这里**故意不设置 OnReadyText**：说话期间不再往输入框注入任何东西。
                    //
                    // 曾经在这里接过"伪造修饰键抬起 + 注入 + 补回按下"，真机上在装有中文输入法的
                    // 输入框里会把字符送进输入法的拼写缓存而不是上屏，候选框乱跳，最终把输入法
                    // 卡死，用户只能重启输入法。教训：按住修饰键的那段时间，不要碰输入框。
                    //
                    // 现在分段结果只用来在松手后拼出完整文本，由收尾路径一次性写入——那时热键
                    // 已经松开，纯键盘注入即可，不需要伪造修饰键，也没有 Ctrl+字符 的问题。
                    //
                    // 录音期字幕走 OnDisplayText（浮层显示，绝不注入）：它只读账本、按段序吐
                    // 累计文本给浮层。**不能**改用 OnReadyText 来显示——那个回调会推进账本的
                    // "已释放"游标、把 HasInjected 置真，于是 TranscribeWorker 里"流式不可信就
                    // 回退整段上传"的判定失效（分段失败时不再回退），用户这次录音会静默丢字。
                    runner.OnDisplayText = delegate(string text) { OnStreamDisplayText(runner, text); };
                    _streamRunner = runner;
                }
                else
                {
                    _streamRunner = null;
                }
                Log.Write("开始录音");
            }
            catch (Exception ex)
            {
                _state = State.Idle;
                // 对抗审查 P1：Recorder.Start 已成功后若浮层/GDI 抛异常，须停麦并隐藏浮层，
                // 否则麦克风持续采集但状态已回 Idle（静默常开）。
                try { Recorder.Stop(); } catch { }
                try { _overlay.HideOverlay(); } catch { }
                Log.Write("开始录音失败: " + ex.Message);
            }
        }

        // 录音期浮层字幕：把流式分段吐出来的文字画到浮层上。
        //
        // 这是全链路唯一"边说边显示"的入口，且**只显示**：不调用 InjectText / SendInput /
        // SendModifier，不碰任何输入框。曾经在这里做过"说话期间注入"，实测在装有中文输入法的
        // 输入框里会把字符送进输入法的拼写缓存而不是上屏，候选框乱跳并卡死输入法
        // （详见 OnHoldStart 里的注释）。那个教训只约束"注入"，不约束"在浮层上画字"。
        private void OnStreamDisplayText(StreamingSegmentRunner runner, string text)
        {
            try
            {
                // 在工作线程上被调用，必须封送到 UI 线程再动浮层。
                _overlay.BeginInvoke((MethodInvoker)delegate
                {
                    try
                    {
                        // 会话身份校验：已经收尾（_streamRunner 置 null）或已经开了新录音时，
                        // 上一会话迟到的那串字绝不能画到新会话的浮层上。
                        if (!object.ReferenceEquals(_streamRunner, runner)) return;
                        int speechMs = Recorder.CurrentSpeechMs();
                        _overlay.SetLiveCaption(text, speechMs);
                        // 每次字幕到达记一行**只有长度与时间**的账（不记正文，见产品约束 5）。
                        // 这一行是"铺开"唯一的可核对证据：间隔与说话时长能算出应有的铺开速率，
                        // 也能看出批次有多密（实机调手感全靠它，不然只能猜）。
                        Log.Write("实时字幕: 累计=" + (text == null ? 0 : text.Length) + "字 说话="
                            + speechMs + "ms 铺开速率=" + _overlay.LiveCaptionRate.ToString("0.0") + "字/秒");
                    }
                    catch { }
                });
            }
            catch { } // 浮层句柄未创建/已销毁：显示失败不得向上抛，更不得影响分段结算
        }

        private void OnHoldRelease()
        {
            LatencyTrace trace = _trace;
            // 摘掉流式会话：之后的分段派发一律不再受理（本会话已有结果仍由 stream 持有，
            // 尾段在 StopAndSave 里派发，所以摘除必须晚于它——见下方 StopAndSave 之后再取）。
            StreamingSegmentRunner stream = _streamRunner;
            // 松手 = 用户开始等待的起点。
            if (trace != null) trace.Mark(LatencyTrace.Release);
            _overlay.HideOverlay();
            double elapsedMs = (DateTime.Now - _recordStart).TotalMilliseconds;
            if (elapsedMs < 400)
            {
                _state = State.Idle;
                Recorder.Stop();
                return;
            }
            // 对抗审查实测发现：MCI 设备有线程亲和性，stop/save 必须在执行
            // open/record 的同一线程（UI 线程）完成，否则报 263 无效设备名。
            // 只有 HTTP 转写放到后台线程。
            string wav = Path.Combine(Path.GetTempPath(), "voxleap_" + DateTime.Now.Ticks + ".wav");
            if (!Recorder.StopAndSave(wav))
            {
                _state = State.Idle;
                _hotkeyWasDown = false;
                try { if (File.Exists(wav)) File.Delete(wav); } catch { }
                _toast.ShowToast("录音保存失败，请重试");
                if (trace != null)
                {
                    trace.NoteText("outcome", "保存失败");
                    LogSessionTrace(trace);
                }
                return;
            }
            if (trace != null) trace.Mark(LatencyTrace.Saved);
            // 尾段已在 StopAndSave 内派发完毕，现在才摘除流式会话。
            _streamRunner = null;
            var session = new AsrSession();
            session.Trace = trace;
            session.OnPartial = delegate(string text)
            {
                try
                {
                    _overlay.BeginInvoke((MethodInvoker)delegate
                    {
                        try
                        {
                            // 会话身份校验：上一个会话迟到的字幕不得污染新会话浮层。
                            if (object.ReferenceEquals(_asrSession, session)) _overlay.SetBusyCaption(text);
                        }
                        catch { }
                    });
                }
                catch { }
            };
            _asrSession = session;
            _state = State.Transcribing;
            _overlay.ShowBusy("识别中（Esc 取消）", _target);
            Thread t = new Thread(delegate() { TranscribeWorker(wav, session, stream); });
            t.IsBackground = true;
            t.Start();
        }

        private void CancelRecording()
        {
            _overlay.HideOverlay();
            Recorder.Stop();
            _state = State.Idle;
        }

        // 等所有分段结算并按序拼接。任一段失败或超时即返回 null，调用方回退整段上传——
        // 这是流式的正确性底线：宁可慢，也绝不把残缺文本交给用户。
        private AsrResult CollectStreamedTranscript(StreamingSegmentRunner stream)
        {
            bool settled = stream.WaitAll(_cfg.RequestTimeoutMs);
            // 先补注入剩下的、再读"注入过没有"：否则会读到过期标志，把"已经注入过"
            // 误判成"没注入过"，进而多弹一张审阅卡片（用户一点写入就重复）。
            stream.DrainReadyText();
            string text = settled ? stream.Ledger.Stitch() : null;
            Log.Write(stream.Ledger.ToLogLine()
                + " 分段音频=" + stream.SegmentBytes + "字节 已请求=" + stream.Requests
                + " 等待=" + (settled ? "已全部结算" : "超时"));
            if (string.IsNullOrEmpty(text))
            {
                Log.Write("流式分段不可信，回退整段上传");
                return null;
            }
            Log.Write("流式分段命中：跳过整段上传");
            var r = new AsrResult();
            r.Ok = true;
            r.HttpStatus = 200;
            r.Text = text;
            return r;
        }

        private void TranscribeWorker(string wav, AsrSession session, StreamingSegmentRunner stream)
        {
            AsrResult res;
            try
            {
                res = stream == null ? null : CollectStreamedTranscript(stream);
                // 这里**不能**用 HasInjected 把门（曾经就是那样，是个真缺陷）。
                // 说话期间早已不再注入任何东西，但账本"释放"文本时仍会把 HasInjected 置真
                // （TakeReadyText 会推进 _released、写入 _releasedText，PumpReadyText 是被
                // DrainReadyText 主动调用的）。于是流式一旦不可信就会跳过整段回退——输入框里
                // 明明是空的，用户却收到"输入框内已有部分内容"，整句录音直接丢掉。
                // 实机 2026-09-26 17:07:58 已发生过一次（识别 ok=False 字数=0）。
                // 现在说话期间不注入 ⇒ 回退不可能造成重复，所以无条件允许回退。
                if (res == null) res = AsrClient.Transcribe(_cfg, wav, session);
                if (res == null)
                {
                    res = new AsrResult();
                    res.Error = "流式识别中断，整段回退也失败，请重试";
                    Log.Write("流式分段中断，且整段回退也失败：本次无结果");
                }
                // 说明：这里**不再**在说话期间往输入框注入任何东西。
                // 曾经用过"伪造修饰键抬起 + 注入 + 补回按下"，实测在装有中文输入法的输入框里
                // 会把字符送进输入法的拼写缓存而不是上屏，输入法候选框乱跳，甚至把输入法卡死。
                // 现在说话期间只把文字显示在浮层上，完全不碰键盘与输入框；等松手、整理完成后再
                // 一次性写入。因此不需要伪造修饰键，也不需要 Shift+左选/Ctrl+C 那套回读校验。
                _streamUsed = stream != null;
                if (res.Ok && _cfg.AiOrganize && !session.Cancelled)
                {
                    ITextTransformProvider transformer = new OpenAiCompatibleTextTransformProvider();
                    OrganizerResult organized = transformer.Transform(_cfg, res.Text, session);
                    if (session.Cancelled)
                    {
                        res.Cancelled = true;
                        res.Error = "已取消";
                    }
                    else if (organized.Ok)
                    {
                        VoxleapCore.TextSafetyReport safety =
                            VoxleapCore.ValidateOrganizedText(res.Text, organized.Text);
                        if (safety.Safe) res.OrganizedText = organized.Text;
                        else res.OrganizeError = safety.Reason;
                    }
                    else
                    {
                        res.OrganizeError = organized.Error;
                        Log.Write("AI 整理失败，保留原文: " + organized.Error);
                    }
                }
                if (session.Cancelled) res.Cancelled = true;
            }
            catch (Exception ex)
            {
                res = new AsrResult();
                res.Error = ex.Message;
            }
            finally
            {
                try { if (File.Exists(wav)) File.Delete(wav); } catch { }
            }
            if (session != null && session.Trace != null)
            {
                LatencyTrace trace = session.Trace;
                trace.NoteText("outcome", res.Cancelled ? "已取消" : (res.Ok ? "ok" : "失败"));
                trace.NoteText("status", res.HttpStatus > 0 ? res.HttpStatus.ToString() : "无HTTP状态");
                trace.Note("chars", (res.Text ?? "").Length);
                if (!res.Ok && !string.IsNullOrEmpty(res.Error)) trace.NoteText("error", res.Error);
            }
            Log.Write("识别: ok=" + res.Ok + " cancelled=" + res.Cancelled + " status=" + res.HttpStatus + " 用时=" + res.LatencySeconds.ToString("0.00") + "s 字数=" + (res.Text ?? "").Length);
            FinishOnUi(res, session);
        }

        private void FinishOnUi(AsrResult res, AsrSession session)
        {
            IntPtr target = _target;
            LatencyTrace trace = session == null ? null : session.Trace;
            // 冻结"文本就绪"时刻的两个数字：审阅卡片打开后用户可能过很久才点写入，
            // 那时 trace.PostReleaseMs() 已不是"等待识别"的时长，必须提前取值。
            long readyMs = trace == null ? -1 : trace.PostReleaseMs();
            string readySuffix = trace == null ? "" : trace.ToMetaSuffix();
            try
            {
                _overlay.BeginInvoke((MethodInvoker)delegate
                {
                    // 会话身份校验：看门狗已收尾或用户已开新会话时，过期回调整体丢弃，
                    // 防止旧请求返回后打掉新会话状态、把旧正文注入新目标。
                    if (!object.ReferenceEquals(_asrSession, session))
                    {
                        // 过期会话仍然记录其延迟分解（例如请求最终悬挂了多久），便于排障。
                        LogSessionTrace(trace);
                        return;
                    }
                    _asrSession = null;
                    StopCancelWatchdog();
                    _hotkeyWasDown = false; // keyup 可能丢失，回 Idle 时复位去抖锁存。
                    _overlay.HideOverlay();
                    if (res.Cancelled)
                    {
                        _state = State.Idle;
                        _toast.ShowToast("已取消，文字未保留");
                        LogSessionTrace(trace);
                        return;
                    }
                    string originalText = NormalizeCjkLatinSpacing(res.Text ?? "");
                    string text = !string.IsNullOrEmpty(res.OrganizedText)
                        ? NormalizeCjkLatinSpacing(res.OrganizedText)
                        : originalText;
                    if (res.Ok && !string.IsNullOrEmpty(text))
                    {
                        if (!string.IsNullOrEmpty(res.OrganizeError))
                            _toast.ShowToast("AI 整理失败，已使用原文");
                        // 边走边送：说话期间文字已经在浮层上显示过了，**输入框本身就是审阅面**，
                        // 所以这里一次性写入最终文本（整理成功就是整理版），不再弹审阅卡片。
                        // 送到这里的是纯键盘注入，不伪造任何修饰键——此刻热键已经松开，
                        // 不存在 Ctrl+字符 的问题，因此也完全不去干扰输入法。
                        if (_streamUsed)
                        {
                            _state = State.Idle;
                            try { InjectText(text, readyMs); }
                            finally { if (trace != null) trace.Mark(LatencyTrace.Injected); }
                            LogSessionTrace(trace);
                        }
                        else if (_cfg.AutoInsert)
                        {
                            // 自动输入模式（用户主动开启）：跳过审阅，直接写入按下热键时捕获的目标。
                            _state = State.Idle;
                            try { InjectText(text, readyMs); }
                            finally { if (trace != null) trace.Mark(LatencyTrace.Injected); }
                            LogSessionTrace(trace);
                        }
                        else
                        {
                            string meta = "原文保留 · " + _cfg.Model;
                            if (!string.IsNullOrEmpty(res.OrganizedText))
                                meta = "AI 整理 · 原文可恢复 · " + _cfg.OrganizerModel;
                            if (readySuffix.Length > 0) meta = meta + " · " + readySuffix;
                            var review = new ReviewForm(text, originalText, meta,
                                delegate(string editedText)
                                {
                                    try { InjectText(editedText, -1); }
                                    finally { if (trace != null) trace.Mark(LatencyTrace.Injected); }
                                },
                                delegate(string msg) { _toast.ShowToast(msg); });
                            _state = State.Reviewing;
                            // 审阅会话在此收尾：此时注入耗时（若用户点了写入）已并入 trace。
                            review.FormClosed += delegate
                            {
                                _state = State.Idle;
                                LogSessionTrace(trace);
                            };
                            Point p = ScreenLayout.BottomCenterOf(target, review.Width, review.Height, 24);
                            review.Location = p;
                            review.Show();
                        }
                    }
                    else
                    {
                        _state = State.Idle;
                        string err = res.Ok ? "未识别到内容" : res.Error;
                        _toast.ShowToast("识别失败：" + err);
                        LogSessionTrace(trace);
                    }
                });
            }
            catch (Exception ex)
            {
                Log.Write("UI 回调失败: " + ex.Message);
                _asrSession = null;
                _state = State.Idle;
                LogSessionTrace(trace);
            }
        }

        // 每个会话只写一行延迟分解（隐私约束：无转写正文，只有阶段耗时与计数）。
        private static void LogSessionTrace(LatencyTrace trace)
        {
            if (trace == null) return;
            try { Log.Write("延迟: " + trace.ToLogLine()); } catch { }
        }

        private void StopCancelWatchdog()
        {
            if (_cancelWatchdog == null) return;
            _cancelWatchdog.Stop();
            _cancelWatchdog.Dispose();
            _cancelWatchdog = null;
        }

        private static string NormalizeCjkLatinSpacing(string text)
        {
            return VoxleapCore.NormalizeCjkLatinSpacing(text);
        }

        // 原文保底：写入剪贴板后延迟恢复用户原内容；期间被其他程序占用则不恢复。
        // 对抗审查 P1/P2：多恢复线程交叉覆盖——连续两次注入时，先发线程醒来后可能把剪贴板
        // 改回旧快照。用静态代次号保证只有最新一次写入的恢复线程能执行恢复。
        private static int _clipboardGen;
        private static bool SetClipboardAndRestore(string text)
        {
            string original = null;
            try { original = Clipboard.GetText(); } catch { }
            bool written = false;
            try { Clipboard.SetText(text); written = true; } catch { }
            int gen = System.Threading.Interlocked.Increment(ref _clipboardGen);
            if (!written) return false; // 写入失败时返回 false，调用方不得 toast 谎报“已复制”
            Thread t = new Thread(delegate()
            {
                Thread.Sleep(4000);
                try
                {
                    // 只放行最新代次；旧代次的恢复请求即使醒来也放弃，避免覆盖新内容。
                    if (gen != System.Threading.Volatile.Read(ref _clipboardGen)) return;
                    if (Clipboard.GetText() == text)
                    {
                        if (!string.IsNullOrEmpty(original)) Clipboard.SetText(original);
                        else Clipboard.Clear();
                    }
                }
                catch { }
            });
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            return true;
        }

        // readyMs：从松开热键到"文本就绪"的毫秒数，只为自动输入的成功提示附上真实等待时间。
        // 审阅路径传 -1：用户审阅了多久与识别延迟无关，混进来会得到一个无意义的巨大数字。
        // 按住 held 键，连按 count 次 tapKey，最后松开 held。整体一次 SendInput 发出。
        private static void SendHoldTap(int held, int tapKey, int count)
        {
            try
            {
                var inputs = new List<Native.INPUT>();
                if (held != 0) inputs.Add(VkInput((ushort)held, 0));
                for (int i = 0; i < count; i++)
                {
                    inputs.Add(VkInput((ushort)tapKey, 0));
                    inputs.Add(VkInput((ushort)tapKey, Native.KEYEVENTF_KEYUP));
                }
                if (held != 0) inputs.Add(VkInput((ushort)held, Native.KEYEVENTF_KEYUP));
                Native.SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf(typeof(Native.INPUT)));
            }
            catch { }
        }

        private static void SendCtrlC()
        {
            try
            {
                Native.INPUT[] inputs = new Native.INPUT[4];
                inputs[0] = VkInput(0x11, 0);                                  // Ctrl 按下
                inputs[1] = VkInput((ushort)'C', 0);
                inputs[2] = VkInput((ushort)'C', Native.KEYEVENTF_KEYUP);
                inputs[3] = VkInput(0x11, Native.KEYEVENTF_KEYUP);             // Ctrl 松开
                Native.SendInput(4, inputs, Marshal.SizeOf(typeof(Native.INPUT)));
            }
            catch { }
        }

        // 【已弃用，无调用方】Shift+左选 / Ctrl+C 回读同样是与输入法抢键盘状态：Shift 本身
        // 就是输入法的中英切换键，回读还永远是空（字符没上屏，复制不到）。**不要重新接上**，
        // 下次清理会删除。SendHoldTap / SendCtrlC 一并弃用。
        // 校验后替换：把输入框里"我们边说边注入的那段原文"换成整理版。
        //
        // 为什么必须校验：从注入到整理完成中间隔了好几秒，用户完全可能已经自己改过错、
        // 挪过光标、或者切走了窗口。盲选 N 个字符去替换，删掉的可能根本不是我们的字。
        // 所以流程是：往回选 N 个字符 → Ctrl+C 回读 → 与我们确实注入过的字符串**逐字比对**，
        // 一致才替换；不一致则**一个字都不动**，只提示。
        //
        // 前置硬条件：目标窗口必须仍是前台窗口。按键事件发给的是前台窗口而不是我们记下的
        // _target，不确认这一点就可能把 Ctrl+C 和文字送到别的程序里。
        private bool ReplaceInjected(string injected, string replacement)
        {
            if (string.IsNullOrEmpty(injected) || string.IsNullOrEmpty(replacement)) return false;
            if (injected == replacement) return false;
            if (injected.Length > 400) { Log.Write("替换放弃: 原文字数过多 " + injected.Length); return false; }

            IntPtr target = _target;
            if (!Native.IsWindow(target)) { _toast.ShowToast("目标窗口已关闭，未替换"); return false; }
            if (Native.GetForegroundWindow() != target)
            {
                _toast.ShowToast("已切走窗口，未自动替换（原文保留在输入框）");
                Log.Write("替换拒绝: 目标已非前台窗口，原文 " + injected.Length + " 字");
                return false;
            }

            string saved = null;
            try { if (Clipboard.ContainsText()) saved = Clipboard.GetText(); } catch { }
            try
            {
                try { Clipboard.Clear(); } catch { }
                SendHoldTap(Native.VK_SHIFT, 0x25, injected.Length); // Shift+左选 × N（0x25 = VK_LEFT）
                Thread.Sleep(50);
                SendCtrlC();
                string back = null;
                for (int i = 0; i < 15 && back == null; i++)
                {
                    Thread.Sleep(20);
                    try { if (Clipboard.ContainsText()) back = Clipboard.GetText(); } catch { }
                }
                if (back != injected)
                {
                    // 不一致 ⇒ 一个字都不动，并先按一次右方向键把选择收起来，光标回到原处。
                    SendHoldTap(0, 0x27, 1); // 0x27 = VK_RIGHT
                    Log.Write("替换放弃: 回读不一致（读到 " + (back == null ? "空" : back.Length.ToString()) + " 字，期望 " + injected.Length + " 字）");
                    _toast.ShowToast("输入框内容已变化，未自动替换（原文保留）");
                    return false;
                }
                InjectText(replacement, -1); // 有选择时注入即覆盖
                Log.Write("已替换为整理版: " + injected.Length + " 字 → " + replacement.Length + " 字");
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("替换异常: " + ex.Message);
                return false;
            }
            finally
            {
                try { if (!string.IsNullOrEmpty(saved)) Clipboard.SetText(saved); else Clipboard.Clear(); } catch { }
            }
        }

        // 热键是右 Ctrl / 右 Alt / 右 Shift 时返回该修饰键的 VK。
        // Caps Lock 不是修饰键（按住它不影响其它按键的含义），返回 -1。
        private static int HotkeyModifierVk(Config cfg)
        {
            int vk = GetHotkeyVk(cfg);
            if (vk == Native.VK_CAPITAL) return -1;
            return vk;
        }

        // 【已弃用，无调用方】InjectWithModifierRelease / SendModifier / HotkeyModifierVk
        // 在真机（装有中文输入法的输入框）上把字符送进了输入法的拼写缓存而不是上屏，
        // 候选框乱跳，最终把输入法卡死。**不要重新接上**，下次清理会删除。
        // 往输入框里写字。热键是修饰键且此刻物理按下时，先伪造一次该修饰键的"抬起"再注入。
        //
        // 为什么必须这样：按住右 Ctrl 说话时，物理键真的按着，目标程序就真的看到 Ctrl 按下，
        // 于是注入的文字会被当成 Ctrl+字符 快捷键（全选/粘贴/关闭标签/撤销），而不是打字。
        //
        // 三处必须同时成立，缺一个都会出事：
        //  1. 抬起与按下成对、且放在 finally 里——任何异常都不能让目标程序停留在
        //     "Ctrl 已抬起"的假状态，那会变成粘键；
        //  2. 窗口内必须让本程序的"松手即结束录音"判断闭嘴（见 HoldKeyStillDown），
        //     因为 SendInput 会改变 GetAsyncKeyState 的结果；
        //  3. 只在热键确实是修饰键、且此刻物理按下时才做（Caps Lock 与 toggle 模式都不需要）。
        private void InjectWithModifierRelease(string text)
        {
            int mod = HotkeyModifierVk(_cfg);
            bool fake = mod > 0 && !IsToggleMode(_cfg) && KeyDown(mod);
            if (!fake)
            {
                InjectText(text, 0);
                return;
            }
            _syntheticModifierUntil = DateTime.Now.AddSeconds(10); // 覆盖整段注入
            SendModifier(mod, false);
            try
            {
                InjectText(text, 0);
            }
            finally
            {
                SendModifier(mod, true);
                // 注入结束后再留一小段，吸收掉可能还在队列里的一次轮询；
                // 之后窗口自然过期，真实的松手会被立刻看到。
                _syntheticModifierUntil = DateTime.Now.AddMilliseconds(200);
            }
        }

        // 单独发一个修饰键的按下/抬起事件。这里不做任何多余的事：加锁、分配、日志都可能抛异常，
        // 而这条路径"必须走到"——它就是按下与抬起成对的那一半。
        private static void SendModifier(int vk, bool down)
        {
            try
            {
                Native.INPUT[] inputs = new Native.INPUT[1];
                inputs[0] = VkInput((ushort)vk, down ? 0u : Native.KEYEVENTF_KEYUP);
                Native.SendInput(1, inputs, Marshal.SizeOf(typeof(Native.INPUT)));
            }
            catch { }
        }

        private void InjectText(string text, long readyMs)
        {
            string waitSuffix = readyMs > 0 ? " · " + (readyMs / 1000.0).ToString("0.0") + "s" : "";
            try
            {
                IntPtr target = _target;
                if (string.IsNullOrEmpty(text))
                {
                    _toast.ShowToast("没有可写入的内容");
                    return;
                }
                if (!Native.IsWindow(target))
                {
                    if (!SetClipboardAndRestore(text))
                    {
                        _toast.ShowToast("目标窗口已关闭，且剪贴板不可用，未能保底复制");
                        return;
                    }
                    _toast.ShowToast("目标窗口已关闭，已复制到剪贴板");
                    return;
                }
                // 对抗审查 P1：HWND 是内核复用资源，录音→注入窗口期内原窗口关闭后
                // 句柄值可能被分配给任意新窗口。仅 IsWindow 无法区分，必须比进程 ID。
                if (_targetPid != 0)
                {
                    uint pidNow;
                    Native.GetWindowThreadProcessId(target, out pidNow);
                    if (pidNow != _targetPid)
                    {
                        Log.Write("写入拒绝: 目标进程变化 (" + pidNow + " != " + _targetPid + "), 文字入剪贴板 " + text.Length + "字");
                        if (!SetClipboardAndRestore(text))
                        {
                            _toast.ShowToast("目标窗口已消失或更换，且剪贴板不可用，未能保底复制");
                            return;
                        }
                        _toast.ShowToast("目标窗口已消失或更换，已复制到剪贴板");
                        return;
                    }
                }
                // UIPI：向更高完整性窗口发送输入时系统会静默丢弃（SendInput 仍返回成功数），
                // 因此注入前直接检测完整性级别，命中则改为剪贴板方案，不依赖返回值判定。
                if (TargetSafety.IsHigherIntegrity(target))
                {
                    Log.Write("写入拒绝: 目标完整性高于当前进程, 文字入剪贴板 " + text.Length + "字");
                    if (!SetClipboardAndRestore(text))
                    {
                        _toast.ShowToast("目标窗口需要更高权限，且剪贴板不可用，未能保底复制");
                        return;
                    }
                    _toast.ShowToast("目标窗口需要更高权限，已复制到剪贴板");
                    return;
                }
                ForceForeground(target);
                Thread.Sleep(80);
                if (Native.GetForegroundWindow() != target)
                {
                    if (!SetClipboardAndRestore(text))
                    {
                        _toast.ShowToast("无法切回目标窗口，且剪贴板不可用，未能保底复制");
                        return;
                    }
                    _toast.ShowToast("无法切回目标窗口，已复制到剪贴板");
                    return;
                }
                // 敏感控件二次校验：录音/转写期间焦点控件可能变化，注入前对聚焦控件重查密码样式。
                if (IsPasswordField(target))
                {
                    Log.Write("密码框检测命中, 拒绝写入, 文字入剪贴板 " + text.Length + "字");
                    if (!SetClipboardAndRestore(text))
                    {
                        _toast.ShowToast("检测到密码框，且剪贴板不可用，未能保底复制");
                        return;
                    }
                    _toast.ShowToast("检测到密码框，已拒绝写入，内容已复制到剪贴板");
                    return;
                }
                bool injected = false;
                string via;
                if (text.Length > _cfg.ClipboardThreshold && Clipboard.ContainsText())
                {
                    injected = InjectViaClipboard(text);
                    via = "剪贴板";
                }
                else
                {
                    uint sent = SendUnicode(text);
                    uint expected = (uint)text.Length * 2;
                    injected = sent == expected;
                    via = "SendInput";
                    if (!injected)
                    {
                        Log.Write("SendInput 被拦截或不完整: " + sent + "/" + expected);
                    }
                }
                if (!injected)
                {
                    // 目标可能是高权限窗口（UIPI 拦截）：兜底剪贴板 + Ctrl+V。
                    // 对抗审查 P1：SetText 失败（剪贴板被其他进程锁定）时不得继续 SendCtrlV，
                    // 否则粘贴的是用户剪贴板里的旧内容（可能含敏感信息）却 toast 报成功。
                    bool clipboardWritten = false;
                    try { Clipboard.SetText(text); clipboardWritten = true; } catch { }
                    if (!clipboardWritten)
                    {
                        _toast.ShowToast("写入失败：剪贴板当前不可用，请手动复制");
                        Log.Write("写入失败: 剪贴板 SetText 被拒");
                        return;
                    }
                    uint sent2 = SendCtrlV();
                    if (sent2 == 4)
                    {
                        _toast.ShowToastCentered("已输入 " + text.Length + " 字 · 剪贴板" + waitSuffix, target);
                        Log.Write("写入成功: " + text.Length + " 字, 路径=兜底剪贴板");
                        return;
                    }
                    _toast.ShowToast("写入失败（目标可能有更高权限），内容已在剪贴板，请手动粘贴");
                    Log.Write("写入失败: Ctrl+V 被拦截，内容已留在剪贴板");
                    return;
                }
                _toast.ShowToastCentered("已输入 " + text.Length + " 字" + waitSuffix, target);
                Log.Write("写入成功: " + text.Length + " 字, 路径=" + via);
            }
            catch (Exception ex)
            {
                if (!SetClipboardAndRestore(text))
                {
                    _toast.ShowToast("写入失败，且剪贴板不可用，请手动复制");
                    Log.Write("写入失败: " + ex.Message + "（剪贴板保底也失败）");
                    return;
                }
                _toast.ShowToast("写入失败，已复制到剪贴板");
                Log.Write("写入失败: " + ex.Message);
            }
        }

        private static void ForceForeground(IntPtr target)
        {
            uint pid;
            uint targetThread = Native.GetWindowThreadProcessId(target, out pid);
            uint ourThread = Native.GetCurrentThreadId();
            bool attached = false;
            if (ourThread != targetThread && targetThread != 0)
            {
                attached = Native.AttachThreadInput(ourThread, targetThread, true);
            }
            Native.SetForegroundWindow(target);
            Native.BringWindowToTop(target);
            if (attached) Native.AttachThreadInput(ourThread, targetThread, false);
        }

        // 返回实际注入的事件数；调用方据此判断是否被拦截（如管理员窗口的 UIPI）。
        private static uint SendUnicode(string text)
        {
            var inputs = new List<Native.INPUT>();
            foreach (char c in text)
            {
                inputs.Add(UnicodeInput(c, Native.KEYEVENTF_UNICODE));
                inputs.Add(UnicodeInput(c, Native.KEYEVENTF_UNICODE | Native.KEYEVENTF_KEYUP));
            }
            return Native.SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf(typeof(Native.INPUT)));
        }

        private static Native.INPUT UnicodeInput(char c, uint flags)
        {
            var input = new Native.INPUT();
            input.type = Native.INPUT_KEYBOARD;
            input.U.ki.wVk = 0;
            input.U.ki.wScan = (ushort)c;
            input.U.ki.dwFlags = flags;
            return input;
        }

        // 只在原剪贴板是文本时才走剪贴板路径（非文本内容无法可靠恢复，
        // 改由 SendInput 长文本直注，宁可慢一点也不覆盖用户剪贴板）。
        private bool InjectViaClipboard(string text)
        {
            string prev = null;
            try { prev = Clipboard.GetText(); } catch { }
            Clipboard.SetText(text);
            uint sent = SendCtrlV();
            if (sent != 4)
            {
                Log.Write("Ctrl+V 注入被拦截: " + sent + "/4");
                return false;
            }
            // 粘贴是目标应用异步处理的，恢复前多等一会儿；极端慢应用仍有竞态风险。
            Thread.Sleep(600);
            try
            {
                if (Clipboard.GetText() == text)
                {
                    if (!string.IsNullOrEmpty(prev)) Clipboard.SetText(prev);
                    else Clipboard.Clear();
                }
                else
                {
                    Log.Write("剪贴板在粘贴期间被其他程序写入，放弃恢复");
                }
            }
            catch
            {
                _toast.ShowToast("剪贴板被占用，跳过恢复");
            }
            return true;
        }

        private static uint SendCtrlV()
        {
            var inputs = new Native.INPUT[4];
            inputs[0] = VkInput(0x11, 0);
            inputs[1] = VkInput((ushort)'V', 0);
            inputs[2] = VkInput((ushort)'V', Native.KEYEVENTF_KEYUP);
            inputs[3] = VkInput(0x11, Native.KEYEVENTF_KEYUP);
            return Native.SendInput(4, inputs, Marshal.SizeOf(typeof(Native.INPUT)));
        }

        private static Native.INPUT VkInput(ushort vk, uint flags)
        {
            var input = new Native.INPUT();
            input.type = Native.INPUT_KEYBOARD;
            input.U.ki.wVk = vk;
            input.U.ki.dwFlags = flags;
            return input;
        }

        private static bool IsPasswordField(IntPtr window)
        {
            // 第一性原理：要检查的是“目标窗口所在线程”的输入焦点控件，而不是本线程（VoxLeap
            // 自身的无激活窗口永远没有焦点，GetGUIThreadInfo(0) 会恒为 false）。
            if (window == IntPtr.Zero) return false;
            uint pid;
            uint targetThread = Native.GetWindowThreadProcessId(window, out pid);
            if (targetThread == 0) return false;
            var gti = new Native.GUITHREADINFO();
            gti.cbSize = (uint)Marshal.SizeOf(typeof(Native.GUITHREADINFO));
            if (!Native.GetGUIThreadInfo(targetThread, ref gti)) return false;
            IntPtr focus = gti.hwndFocus != IntPtr.Zero ? gti.hwndFocus : gti.hwndActive;
            if (focus == IntPtr.Zero) return false;
            long style = Native.GetWindowLong(focus, Native.GWL_STYLE);
            return (style & Native.ES_PASSWORD) != 0;
        }

        private static Icon MakeIcon()
        {
            // 统一使用 exe 内嵌的正式 Logo（像素级与快捷方式一致）；
            // 无法取到内嵌图标时回退到旧的简易绘制兜底。
            try
            {
                Icon embedded = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (embedded != null) return embedded;
            }
            catch { }
            return LegacyDrawnIcon();
        }

        private static Icon LegacyDrawnIcon()
        {
            using (var bmp = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    int[] heights = { 10, 18, 26, 13, 20 };
                    Color[] colors =
                    {
                        Color.FromArgb(0, 70, 241),
                        Color.FromArgb(0, 70, 241),
                        Color.FromArgb(0, 226, 241),
                        Color.FromArgb(0, 70, 241),
                        Color.FromArgb(0, 226, 241)
                    };
                    for (int i = 0; i < 5; i++)
                    {
                        using (var brush = new SolidBrush(colors[i]))
                        {
                            g.FillRectangle(brush, 2 + i * 6, 30 - heights[i], 4, heights[i]);
                        }
                    }
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        private bool SettingsFormOpen()
        {
            return _settingsForm != null && !_settingsForm.IsDisposed;
        }

        private bool SettingsBusy()
        {
            return _state != State.Idle;
        }

        private void ShowSettings()
        {
            try
            {
                if (SettingsBusy())
                {
                    Log.Write("打开设置被拒: 会话未结束 state=" + _state);
                    _toast.ShowToast("当前会话未结束，暂时不能打开设置");
                    return;
                }
                if (SettingsFormOpen())
                {
                    if (_settingsForm.WindowState == FormWindowState.Minimized)
                    {
                        _settingsForm.WindowState = FormWindowState.Normal;
                    }
                    _settingsForm.BringToFront();
                    _settingsForm.Activate();
                    return;
                }
                _settingsForm = new SettingsForm(
                    _cfg.Clone(),
                    delegate(Config saved)
                    {
                        _cfg.CopyFrom(saved);
                        ApplyConfigRuntime();
                        _lastConfigWriteUtc = GetConfigWriteTimeUtc();
                        _toast.ShowToast("设置已保存，下次录音生效");
                    },
                    delegate { return !SettingsBusy(); });
                _settingsForm.FormClosed += delegate { _settingsForm = null; };
                _settingsForm.Show();
                _settingsForm.Activate();
                Log.Write("设置窗口已打开");
            }
            catch (Exception ex)
            {
                Log.Write("打开设置失败: " + ex.Message);
                _toast.ShowToast("打开设置失败");
            }
        }

        private static DateTime GetConfigWriteTimeUtc()
        {
            try
            {
                return File.Exists(Config.SettingsPath)
                    ? File.GetLastWriteTimeUtc(Config.SettingsPath)
                    : DateTime.MinValue;
            }
            catch { return DateTime.MinValue; }
        }

        private void ReloadConfigIfChanged()
        {
            if (_state != State.Idle || SettingsFormOpen()) return;
            DateTime changed = GetConfigWriteTimeUtc();
            if (changed == DateTime.MinValue || changed <= _lastConfigWriteUtc) return;
            _lastConfigWriteUtc = changed;
            Config reloaded = Config.Load();
            if (!string.IsNullOrEmpty(reloaded.ParseIssue))
            {
                _toast.ShowToast("外部配置未应用，请打开设置检查");
                return;
            }
            _cfg.CopyFrom(reloaded);
            ApplyConfigRuntime();
            _toast.ShowToast("设置已更新，下次录音生效");
        }

        private void ExitApp()
        {
            // 会话未结束时禁止退出：录音按 Esc、转写等看门狗或完成、审阅先走放弃确认，
            // 避免托盘退出绕过草稿保护静默丢字。
            if (_state != State.Idle)
            {
                _toast.ShowToast("当前会话未结束（按 Esc 可取消），稍后退出");
                return;
            }
            try
            {
                if (_asrSession != null) _asrSession.Abort();
                StopCancelWatchdog();
                if (_hook != IntPtr.Zero) Native.UnhookWindowsHookEx(_hook);
                Recorder.Stop();
                _configReloadTimer.Stop();
                _configReloadTimer.Dispose();
                if (_openSettingsSignal != null) _openSettingsSignal.Dispose();
                if (SettingsFormOpen()) _settingsForm.Close();
                _tray.Visible = false;
                _overlay.Dispose();
                _toast.Dispose();
                Log.Write("退出");
            }
            catch { }
            ExitThread();
        }
    }

    internal static class SettingsTemplate
    {
        public const string Default =
            "{\r\n" +
            "  \"baseUrl\": \"https://api.stepfun.com/step_plan/v1\",\r\n" +
            "  \"endpoint\": \"/audio/asr/sse\",\r\n" +
            "  \"api\": \"sse\",\r\n" +
            "  \"model\": \"stepaudio-2.5-asr\",\r\n" +
            "  \"apiKeyProtected\": \"\",\r\n" +
            "  \"autoInsert\": false,\r\n" +
            "  \"language\": \"zh\",\r\n" +
            "  \"hotkey\": \"RControl\",\r\n" +
            "  \"hotkeyMode\": \"hold\",\r\n" +
            "  \"hotwords\": \"\",\r\n" +
            "  \"clipboardThreshold\": 200,\r\n" +
            "  \"requestTimeoutMs\": 20000,\r\n" +
            "  \"maxRecordMs\": 300000\r\n" +
            "}\r\n";
    }
}
