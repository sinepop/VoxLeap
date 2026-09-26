// ------------------------------------------------------------------
// SpeechSegmenter.cs — 实时语音分段（纯逻辑，不依赖 WinForms / 音频 API）
//
// 一个原语，服务两件事：
//   1) 静音自动停止：静音持续超过 endSilenceMs → AutoStop（不必一直按住热键）
//   2) 边说边送的分段边界：静音持续超过 splitSilenceMs → SegmentBoundary
//      （每段一结束就发一次识别请求，于是松手时只剩最后一段要传）
//
// 输入是 16 kHz / 16 bit / mono 的 100 ms 帧 RMS（归一化到 0..1）。
// 阈值沿用既有 vadThreshold 的语义：vadThreshold / 32768.0，
// 因此批处理 VAD 与实时分段共用同一个设置值，不会出现两套口径。
//
// 本次接入采用「影子模式」：只观察、只记录，不改变任何行为。
// 待真实录音验证阈值后，再让它真正驱动收尾与分段。
// ------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Text;

namespace VoxLeap
{
    internal sealed class SpeechSegmenter
    {
        public enum Signal
        {
            None,
            SpeechStarted,
            SegmentBoundary,
            AutoStop
        }

        // 16 kHz / 16 bit / mono = 32 字节 / 毫秒。用于核对 waveIn 是否丢帧。
        public const int BytesPerMs = 32;

        private const int MaxRecordedRuns = 24;

        private readonly int _frameMs;
        private readonly double _speechRms;
        private readonly int _minSpeechMs;
        private readonly int _splitSilenceMs;
        private readonly int _endSilenceMs;

        private bool _voiced;               // 上一帧是否是语音
        private bool _inSpeech;             // 已进入某一段的说话态且尚未收尾
        private int _silenceRunMs;          // 当前静音游程时长（有语音帧即清零）
        private int _speechInRunMs;         // 当前连续语音时长
        private int _speechBeforeSilenceMs; // 静音游程开始前那段语音的时长（游程开始时快照）
        private int _elapsedMs;
        private int _speechMsTotal;
        private int _frames;
        private int _speechStarts;
        private int _boundaries;
        private int _autoStops;
        private bool _splitFired;           // 同一次静音游程只报一次分界
        private bool _endFired;             // 同一次静音游程只报一次收尾
        private long _receivedBytes;        // 收到的全部音频字节（含停机回收的真实音频）
        private long _liveReceivedBytes;    // 只累计"直播"回调，用于与期望帧数比差
        private long _expectedBytes;
        private long _flushBytes;           // 停机回收的字节数
        private int _flushCallbacks;        // 停机回收的回调数
        private int _zeroLiveCallbacks;     // 运行期收到 0 字节的回调数 —— 确定性的丢帧信号
        private readonly List<int> _silenceRuns = new List<int>();

        public SpeechSegmenter(int frameMs, double speechRms, int minSpeechMs, int splitSilenceMs, int endSilenceMs)
        {
            if (frameMs <= 0) throw new ArgumentOutOfRangeException("frameMs");
            _frameMs = frameMs;
            _speechRms = speechRms;
            _minSpeechMs = Math.Max(0, minSpeechMs);
            _splitSilenceMs = Math.Max(1, splitSilenceMs);
            _endSilenceMs = Math.Max(_splitSilenceMs, endSilenceMs);
        }

        public bool InSpeech { get { return _inSpeech; } }
        public int SilenceRunMs { get { return _silenceRunMs; } }
        public int SpeechMsTotal { get { return _speechMsTotal; } }
        public int Frames { get { return _frames; } }
        public int SpeechStarts { get { return _speechStarts; } }
        public int Boundaries { get { return _boundaries; } }
        public int AutoStops { get { return _autoStops; } }
        public long ReceivedBytes { get { return _receivedBytes; } }
        public long LiveReceivedBytes { get { return _liveReceivedBytes; } }
        public long ExpectedBytes { get { return _expectedBytes; } }
        public int FlushCallbacks { get { return _flushCallbacks; } }
        public int ZeroLiveCallbacks { get { return _zeroLiveCallbacks; } }

        public IList<int> SilenceRuns { get { return _silenceRuns; } }

        // 每帧上报 waveIn 实际交付的字节数，用于核对采样连续性（丢帧会直接毁掉流式识别）。
        //
        // live=false 表示这是停机时 waveInReset 把在途缓冲半满回收回来的回调。它携带的字节是
        // 真实音频（必须计入 _receivedBytes，否则与 MCI 独立录到的字节数对不上），但它**不是
        // 一个完整帧**：
        //   - 按整帧计入期望字节，会凭空多出 3200 字节的差额，看起来像丢帧；
        //   - 喂给 Feed 又会凭空多出 100ms 静音，把时长与静音游程一并撑大。
        // 实机三次录音的差额恰好都等于"3 个半满缓冲"，正是这个原因，不是丢帧。所以停机回收
        // 既不参与期望、也不参与事件，只单独计数上报。
        public void NoteAudioBytes(int bytesRecorded, bool live)
        {
            if (bytesRecorded < 0) bytesRecorded = 0;
            _receivedBytes += bytesRecorded;
            if (!live)
            {
                _flushCallbacks++;
                _flushBytes += bytesRecorded;
                return;
            }
            _liveReceivedBytes += bytesRecorded;
            _expectedBytes += (long)_frameMs * BytesPerMs;
            // 运行期收到 0 字节：这才是不含糊的丢帧，必须能被日志直接看见。
            if (bytesRecorded == 0) _zeroLiveCallbacks++;
        }

        // 非破坏性查询：此刻是否满足自动收尾条件。宿主每 100ms 轮询一次，因此这里不能改变
        // 任何状态；也不复用影子模式的 _endFired —— 那个标记按构造时的阈值置位，而调用方
        // 传入的阈值来自配置，两者可能不同，复用会导致"阈值调大后永远收不了尾"。
        public bool ShouldAutoStop(int endSilenceMs)
        {
            if (endSilenceMs <= 0) return false;
            if (_voiced) return false;                       // 正在说话
            if (_speechMsTotal < _minSpeechMs) return false; // 一句话都还没说过，不能收尾
            return _silenceRunMs >= endSilenceMs;
        }

        public Signal Feed(double rms)
        {
            _frames++;
            _elapsedMs += _frameMs;

            if (rms >= _speechRms)
            {
                if (!_voiced)
                {
                    // 一次静音游程结束：记录长度、清零游程状态与该游程的边沿标记。
                    if (_silenceRunMs > 0) RecordRun(_silenceRunMs);
                    _silenceRunMs = 0;
                    _splitFired = false;
                    _endFired = false;
                    _speechInRunMs = 0;
                }
                _voiced = true;
                _speechInRunMs += _frameMs;
                _speechMsTotal += _frameMs;
                if (!_inSpeech)
                {
                    _inSpeech = true;
                    _speechStarts++;
                    return Signal.SpeechStarted;
                }
                return Signal.None;
            }

            // 语音→静音的瞬间，快照"这段静音之前说了多久"，供收尾/分界的门槛使用。
            if (_voiced)
            {
                _speechBeforeSilenceMs = _speechInRunMs;
                _voiced = false;
            }
            _silenceRunMs += _frameMs;

            // 两个门槛都必须"之前确实说过话"，否则一声咳嗽或一下敲击就能触发收尾/切段。
            // 边沿触发保证同一次静音游程只各报一次；收尾优先于分界（两者阈值有序）。
            bool spokeEnough = _speechBeforeSilenceMs >= _minSpeechMs && _speechMsTotal >= _minSpeechMs;
            if (spokeEnough && !_endFired && _silenceRunMs >= _endSilenceMs)
            {
                _endFired = true;
                _inSpeech = false; // 会话说完了
                _autoStops++;
                return Signal.AutoStop;
            }
            if (spokeEnough && !_splitFired && _silenceRunMs >= _splitSilenceMs)
            {
                _splitFired = true;
                _inSpeech = false; // 本段结束；之后再有语音即为新段的起点
                _boundaries++;
                return Signal.SegmentBoundary;
            }
            return Signal.None;
        }

        private void RecordRun(int ms)
        {
            if (_silenceRuns.Count < MaxRecordedRuns) _silenceRuns.Add(ms);
        }

        // 会话结束：补记末尾尚未闭合的静音游程（游程只在"语音恢复"时才记账，
        // 而多数录音正是在静音中结束的，漏掉它就丢了最有代表性的一次）。
        // 返回末尾"尾段"的说话时长：边说边送需要把这一段补发出去。
        public int Finish()
        {
            if (!_voiced && _silenceRunMs > 0) RecordRun(_silenceRunMs);
            return TailSpeechMs;
        }

        // 尚未被分界/收尾覆盖的末尾说话时长。
        //  - 结束时仍在说话：就是当前这段连续语音；
        //  - 结束时在静音中：若这次静音游程已经报过分界，则前面那段已发走，尾段为空。
        public int TailSpeechMs
        {
            get { return _voiced ? _speechInRunMs : (_splitFired ? 0 : _speechBeforeSilenceMs); }
        }

        // 无正文、无音频的影子里程碑摘要。字节差为正说明 waveIn 有丢帧。
        public string ToLogLine()
        {
            var sb = new StringBuilder();
            sb.Append("分段影子: 帧=").Append(_frames);
            // 字节=真实收到的音频总量（可与下一行 VAD 的输入字节数交叉核对：两条独立采集
            // 路径应当一致，不一致才说明真的丢了音频）。直播/期望只管连续性与丢帧判定。
            sb.Append(" 字节=").Append(_receivedBytes);
            sb.Append(" 直播=").Append(_liveReceivedBytes).Append('/').Append(_expectedBytes);
            sb.Append(" 差=").Append(_liveReceivedBytes - _expectedBytes);
            sb.Append(" 停机回收=").Append(_flushCallbacks).Append("帧/").Append(_flushBytes).Append("字节");
            sb.Append(" 零字节=").Append(_zeroLiveCallbacks);
            sb.Append(" 时长=").Append((_elapsedMs / 1000.0).ToString("0.0")).Append("s");
            sb.Append(" 说话=").Append((_speechMsTotal / 1000.0).ToString("0.0")).Append("s");
            sb.Append(" 起=").Append(_speechStarts);
            sb.Append(" 段=").Append(_boundaries);
            sb.Append(" 自动收尾=").Append(_autoStops);
            sb.Append(" 尾段=").Append(TailSpeechMs).Append("ms");
            sb.Append(" 静音游程=[");
            for (int i = 0; i < _silenceRuns.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(_silenceRuns[i]);
            }
            if (_silenceRuns.Count >= MaxRecordedRuns) sb.Append(",...");
            sb.Append("]ms");
            sb.Append(" 阈值rms=").Append(_speechRms.ToString("0.0000"));
            sb.Append(" 分界=").Append(_splitSilenceMs).Append("ms");
            sb.Append(" 收尾=").Append(_endSilenceMs).Append("ms");
            return sb.ToString();
        }
    }
}
