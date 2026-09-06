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

        [DllImport("winmm.dll", CharSet = CharSet.Auto)]
        private static extern int mciSendString(string command, StringBuilder buffer, int bufferSize, IntPtr callback);

        private static int Send(string cmd)
        {
            var sb = new StringBuilder(512);
            int err = mciSendString(cmd, sb, sb.Capacity, IntPtr.Zero);
            if (err != 0) Log.Write("MCI 错误 " + err + " @ " + cmd);
            return err;
        }

        public static bool Start()
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
            Send("stop " + Alias);
            int err = Send("save " + Alias + " \"" + wavPath + "\"");
            Send("close " + Alias);
            _open = false;
            return err == 0 && File.Exists(wavPath);
        }

        public static void Stop()
        {
            if (!_open) return;
            StopLevelMonitor();
            Send("stop " + Alias);
            Send("close " + Alias);
            _open = false;
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
                    int samples = (int)header.dwBytesRecorded / 2;
                    double sum = 0;
                    for (int i = 0; i < samples; i++)
                    {
                        short sample = Marshal.ReadInt16(header.lpData, i * 2);
                        sum += (double)sample * sample;
                    }
                    double rms = samples == 0 ? 0 : Math.Sqrt(sum / samples) / 32768.0;
                    SetLevel((float)Math.Min(1, rms * 4));

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
        public int HttpStatus;
        public double LatencySeconds;
    }

    internal sealed class AsrSession
    {
        public volatile bool Cancelled;
        public volatile HttpWebRequest CurrentRequest;
        public Action<string> OnPartial;

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
        public static AsrResult Transcribe(Config cfg, string wavPath, AsrSession session = null)
        {
            var result = new AsrResult();
            var sw = Stopwatch.StartNew();
            HttpWebRequest req = null;
            try
            {
                byte[] wav = File.ReadAllBytes(wavPath);
                if (cfg.Api == "sse")
                {
                    return TranscribeSse(cfg, wav, sw, session);
                }
                string boundary = "----VoxLeapBoundary" + Guid.NewGuid().ToString("N");
                string endpoint = cfg.Endpoint.StartsWith("/") ? cfg.Endpoint : "/" + cfg.Endpoint;
                req = (HttpWebRequest)WebRequest.Create(cfg.BaseUrl + endpoint);
                SetSessionRequest(session, req);
                req.Method = "POST";
                req.Timeout = cfg.RequestTimeoutMs;
                req.ReadWriteTimeout = cfg.RequestTimeoutMs;
                req.ContentType = "multipart/form-data; boundary=" + boundary;
                req.Headers["Authorization"] = "Bearer " + cfg.ApiKey;

                byte[] body = BuildMultipart(boundary, cfg.Model, wav);
                req.ContentLength = body.Length;
                using (var rs = req.GetRequestStream()) { rs.Write(body, 0, body.Length); }

                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    string text = ReadAll(resp);
                    result.HttpStatus = (int)resp.StatusCode;
                    Match m = Regex.Match(text, "\"text\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
                    if (!m.Success)
                    {
                        result.Error = "响应里没有 text 字段";
                        // 隐私约束：日志不记录响应正文，只记长度。
                        Log.Write("响应缺少text字段, 响应长度=" + text.Length);
                    }
                    else
                    {
                        result.Text = JsonUtil.Unescape(m.Groups[1].Value);
                        result.Ok = true;
                    }
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

        // Step Plan 订阅专用：POST /step_plan/v1/audio/asr/sse，JSON + base64 PCM，SSE 流式返回。
        private static AsrResult TranscribeSse(Config cfg, byte[] wav, Stopwatch sw, AsrSession session)
        {
            var result = new AsrResult();
            HttpWebRequest req = null;
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
                using (var rs = req.GetRequestStream()) { rs.Write(bodyBytes, 0, bodyBytes.Length); }

                using (var resp = (HttpWebResponse)req.GetResponse())
                {
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
                            if (type == "transcript.text.done")
                            {
                                finalText = piece;
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
        private const int SurfaceInset = 10;
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
        private readonly float[] _waveLevels = new float[5];
        private readonly float[] _levelHistory = new float[8];
        private int _historyIndex;
        private float _visualLevel;
        private bool _recording;
        public int MaxRecordMs = 0; // 0=不限制；来自配置 maxRecordMs
        public Action MaxDurationReached; // 录音到上限时的回调（由宿主定义为转存并继续转写）
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
            _busyCaption = "";
            _busy = false;
            _busyTarget = targetWindow;
            ResetWave();
            Size = new Size(256, 76);
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
            UpdateBusyLayout();
            ShowLayeredAnimated(140);
        }

        public void SetBusyCaption(string text)
        {
            if (!_busy) return; // 迟到的字幕封送不得改用已退场的浮层尺寸。
            text = text ?? "";
            if (_busyCaption == text) return;
            _busyCaption = text;
            UpdateBusyLayout();
            if (Visible && IsHandleCreated) RefreshLayeredSurface();
        }

        public void HideOverlay()
        {
            _delayShow.Stop();
            _timer.Stop();
            _levelTimer.Stop();
            _recording = false;
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

        private void UpdateBusyLayout()
        {
            string text = GetBusyDisplayText();
            int textWidth = TextRenderer.MeasureText(text, Font).Width;
            Size = new Size(26 + 31 + 10 + textWidth + 26, 68);
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
            if (_recording && MaxRecordMs > 0 && totalSeconds * 1000L >= MaxRecordMs)
            {
                _recording = false; // 防重入
                if (MaxDurationReached != null) MaxDurationReached();
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
            if (IsHandleCreated && Visible) RefreshLayeredSurface();
        }

        protected override void DrawLayeredContent(Graphics g)
        {
            Rectangle surface = SurfaceBounds;
            if (_recording)
            {
                // 录音棚语言：● + 1/10 秒时间码 + 声波。红点是真实录音状态。
                float cy = surface.Top + surface.Height / 2f;
                using (var dot = new SolidBrush(Color.FromArgb(255, 69, 58)))
                {
                    g.FillEllipse(dot, surface.Left + 22, cy - 3.5f, 7, 7);
                }
                using (var timerFont = new Font("Consolas", 12f, FontStyle.Bold))
                {
                    DrawLayeredText(
                        g,
                        _elapsedText,
                        timerFont,
                        Color.FromArgb(225, 242, 245, 247),
                        new RectangleF(surface.Left + 37, surface.Top, 56, surface.Height),
                        false);
                }
                DrawBrandWave(g, surface.Right - 74.5f, cy);
            }
            else
            {
                // 等待态：声音停了，波形归位成 Logo 的静止形态（休止符）。
                float markH = 20f;
                DrawLogoMark(g, surface.Left + 26, surface.Top + (surface.Height - markH) / 2f, markH);
                using (var font = new Font("Microsoft YaHei UI", 12f, FontStyle.Bold))
                {
                    DrawLayeredText(
                        g,
                        GetBusyDisplayText(),
                        font,
                        Color.FromArgb(214, 242, 245, 247),
                        new RectangleF(surface.Left + 26 + 31 + 10, surface.Top, surface.Width - 26 - 31 - 10 - 20, surface.Height),
                        false);
                }
            }
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
        private bool _finished;

        public ReviewForm(string text, string meta, Action<string> onWrite, Action<string> onCopied)
        {
            _onWrite = onWrite;
            _onCopied = onCopied;
            _original = text ?? "";

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
        private bool _hotkeyWasDown;
        private AsrSession _asrSession;
        private System.Windows.Forms.Timer _cancelWatchdog;

        public TrayContext(bool openSettingsOnStart)
        {
            _cfg = Config.Load();
            _overlay = new OverlayForm();
            _overlay.MaxRecordMs = _cfg.MaxRecordMs;
            _overlay.MaxDurationReached = delegate { OnHoldRelease(); };
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
                }
            };
            _cancelWatchdog = timer;
            timer.Start();
        }

        private void OnHoldStart()
        {
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
                if (!Recorder.Start())
                {
                    _toast.ShowToast("无法打开麦克风，检查录音设备");
                    return;
                }
                _target = fg;
                _recordStart = DateTime.Now;
                _state = State.Recording;
                _overlay.ShowRecording(fg);
                Log.Write("开始录音");
            }
            catch (Exception ex)
            {
                _state = State.Idle;
                Log.Write("开始录音失败: " + ex.Message);
            }
        }

        private void OnHoldRelease()
        {
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
                return;
            }
            var session = new AsrSession();
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
            Thread t = new Thread(delegate() { TranscribeWorker(wav, session); });
            t.IsBackground = true;
            t.Start();
        }

        private void CancelRecording()
        {
            _overlay.HideOverlay();
            Recorder.Stop();
            _state = State.Idle;
        }

        private void TranscribeWorker(string wav, AsrSession session)
        {
            AsrResult res;
            try
            {
                res = AsrClient.Transcribe(_cfg, wav, session);
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
            Log.Write("识别: ok=" + res.Ok + " cancelled=" + res.Cancelled + " status=" + res.HttpStatus + " 用时=" + res.LatencySeconds.ToString("0.00") + "s 字数=" + (res.Text ?? "").Length);
            FinishOnUi(res, session);
        }

        private void FinishOnUi(AsrResult res, AsrSession session)
        {
            IntPtr target = _target;
            try
            {
                _overlay.BeginInvoke((MethodInvoker)delegate
                {
                    // 会话身份校验：看门狗已收尾或用户已开新会话时，过期回调整体丢弃，
                    // 防止旧请求返回后打掉新会话状态、把旧正文注入新目标。
                    if (!object.ReferenceEquals(_asrSession, session)) return;
                    _asrSession = null;
                    StopCancelWatchdog();
                    _hotkeyWasDown = false; // keyup 可能丢失，回 Idle 时复位去抖锁存。
                    _overlay.HideOverlay();
                    if (res.Cancelled)
                    {
                        _state = State.Idle;
                        _toast.ShowToast("已取消，文字未保留");
                        return;
                    }
                    string text = NormalizeCjkLatinSpacing(res.Text ?? "");
                    if (res.Ok && !string.IsNullOrEmpty(text))
                    {
                        if (_cfg.AutoInsert)
                        {
                            // 自动输入模式（用户主动开启）：跳过审阅，直接写入按下热键时捕获的目标。
                            _state = State.Idle;
                            InjectText(text);
                        }
                        else
                        {
                            string meta = "阶跃星辰 · " + _cfg.Model + " · 用时 " + res.LatencySeconds.ToString("0.0") + "s";
                            var review = new ReviewForm(text, meta, delegate(string editedText) { InjectText(editedText); },
                                delegate(string msg) { _toast.ShowToast(msg); });
                            _state = State.Reviewing;
                            review.FormClosed += delegate { _state = State.Idle; };
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
                    }
                });
            }
            catch (Exception ex)
            {
                Log.Write("UI 回调失败: " + ex.Message);
                _asrSession = null;
                _state = State.Idle;
            }
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
        private static void SetClipboardAndRestore(string text)
        {
            string original = null;
            try { original = Clipboard.GetText(); } catch { }
            try { Clipboard.SetText(text); } catch { }
            Thread t = new Thread(delegate()
            {
                Thread.Sleep(4000);
                try
                {
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
        }

        private void InjectText(string text)
        {
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
                    SetClipboardAndRestore(text);
                    _toast.ShowToast("目标窗口已关闭，已复制到剪贴板");
                    return;
                }
                // UIPI：向更高完整性窗口发送输入时系统会静默丢弃（SendInput 仍返回成功数），
                // 因此注入前直接检测完整性级别，命中则改为剪贴板方案，不依赖返回值判定。
                if (TargetSafety.IsHigherIntegrity(target))
                {
                    SetClipboardAndRestore(text);
                    _toast.ShowToast("目标窗口需要更高权限，已复制到剪贴板");
                    Log.Write("写入拒绝: 目标完整性高于当前进程, 文字入剪贴板 " + text.Length + "字");
                    return;
                }
                ForceForeground(target);
                Thread.Sleep(80);
                if (Native.GetForegroundWindow() != target)
                {
                    SetClipboardAndRestore(text);
                    _toast.ShowToast("无法切回目标窗口，已复制到剪贴板");
                    return;
                }
                // 敏感控件二次校验：录音/转写期间焦点控件可能变化，注入前对聚焦控件重查密码样式。
                if (IsPasswordField(target))
                {
                    SetClipboardAndRestore(text);
                    _toast.ShowToast("检测到密码框，已拒绝写入，内容已复制到剪贴板");
                    Log.Write("密码框检测命中, 拒绝写入, 文字入剪贴板 " + text.Length + "字");
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
                    try { Clipboard.SetText(text); } catch { }
                    uint sent2 = SendCtrlV();
                    if (sent2 == 4)
                    {
                        _toast.ShowToastCentered("已输入 " + text.Length + " 字 · 剪贴板", target);
                        Log.Write("写入成功: " + text.Length + " 字, 路径=兜底剪贴板");
                        return;
                    }
                    _toast.ShowToast("写入失败（目标可能有更高权限），已复制到剪贴板");
                    Log.Write("写入失败: SendInput 与剪贴板均被拦截");
                    return;
                }
                _toast.ShowToastCentered("已输入 " + text.Length + " 字", target);
                Log.Write("写入成功: " + text.Length + " 字, 路径=" + via);
            }
            catch (Exception ex)
            {
                SetClipboardAndRestore(text);
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
