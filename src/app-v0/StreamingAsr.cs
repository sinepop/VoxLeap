using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace VoxLeap
{
    // 边说边送的分段结果账本。
    //
    // 分段请求在线程池线程上并发完成，**完成顺序与段序无关**（各段大小不同、服务端耗时
    // 也不同），所以这里只负责两件事：按段号落位，以及判定这次流式到底可不可信。
    // 纯逻辑、无 I/O，便于单测。
    internal sealed class SegmentLedger
    {
        private readonly object _gate = new object();
        private readonly List<string> _texts = new List<string>();
        private readonly List<string> _failures = new List<string>();
        private int _dispatched;
        private int _settled;
        private int _empty;
        private int _released;
        // 已经真正注入到用户输入框里的那串字。收尾替换时要拿它逐字比对，
        // 确认框里那段确实是我们的，才敢动。
        private readonly StringBuilder _releasedText = new StringBuilder();

        // 派发时先占位，保证段号与按键顺序一致。
        public int Dispatch()
        {
            lock (_gate)
            {
                _texts.Add(null);
                return _dispatched++;
            }
        }

        // 段内没有有效语音（本地 VAD 已判定）：不算失败，文本为空，但**必须结算**，
        // 否则等待方会一直等下去。
        public void NoteEmpty(int index)
        {
            lock (_gate)
            {
                _texts[index] = "";
                _settled++;
                _empty++;
            }
        }

        public void NoteText(int index, string text)
        {
            lock (_gate)
            {
                _texts[index] = text == null ? "" : text;
                _settled++;
            }
        }

        public void NoteFailure(int index, string error)
        {
            lock (_gate)
            {
                _texts[index] = null;
                _failures.Add(string.IsNullOrEmpty(error) ? "未知错误" : error);
                _settled++;
            }
        }

        public int Dispatched { get { lock (_gate) return _dispatched; } }
        public int Settled { get { lock (_gate) return _settled; } }
        public int EmptySegments { get { lock (_gate) return _empty; } }
        public int Released { get { lock (_gate) return _released; } }
        public string ReleasedText { get { lock (_gate) return _releasedText.ToString(); } }
        public bool AllSettled { get { lock (_gate) return _settled >= _dispatched; } }
        public bool AnyFailed { get { lock (_gate) return _failures.Count > 0; } }

        // 按段序交出**本次可以安全注入**的新文本。
        //
        // 分段完成顺序与段序无关（各段长短不同、服务端耗时也不同），所以绝不能"谁先回来就
        // 先注入谁"——那会往用户输入框里写出一句乱序的话。这里只交出从游标开始、连续已结算
        // 的那一串，遇到第一个未结算的段就立刻停住。
        //
        // 返回 null 表示"这次流式已经注定要回退，不要再注入任何东西"；
        // 返回空串表示"暂时没有新内容"；两者含义不同，调用方必须区分。
        public string TakeReadyText()
        {
            lock (_gate)
            {
                if (_failures.Count > 0) return null;
                var sb = new StringBuilder();
                while (_released < _texts.Count && _texts[_released] != null)
                {
                    sb.Append(_texts[_released]);
                    _released++;
                }
                _releasedText.Append(sb);
                return sb.ToString();
            }
        }

        // 按段序拼接。**只要有一段失败、或还有段没结算，就返回 null**——调用方必须回退
        // 整段上传，绝不能把残缺文本交给用户。这是整个流式方案的正确性底线。
        // 注意"没结算"不等于"失败"：如果只检查失败，中间缺一段时会拼出一句读不通的话。
        public string Stitch()
        {
            lock (_gate)
            {
                if (_failures.Count > 0) return null;
                if (_settled < _dispatched) return null;
                var sb = new StringBuilder();
                for (int i = 0; i < _texts.Count; i++)
                {
                    if (_texts[i] != null) sb.Append(_texts[i]);
                }
                return sb.ToString();
            }
        }

        public string ToLogLine()
        {
            lock (_gate)
            {
                var sb = new StringBuilder();
                sb.Append("流式分段: 派发=").Append(_dispatched);
                sb.Append(" 结算=").Append(_settled);
                sb.Append(" 无语音=").Append(_empty);
                sb.Append(" 失败=").Append(_failures.Count);
                if (_failures.Count > 0) sb.Append(" 首个失败=").Append(_failures[0]);
                return sb.ToString();
            }
        }
    }

    // 把一个录音会话的分段音频逐段送出识别。
    //
    // Dispatch 由 waveIn 回调线程调用，因此它只做派发、绝不做 I/O；真正的请求跑在线程池
    // 上。这样"边说边送"不会给音频回调引入任何阻塞。
    internal sealed class StreamingSegmentRunner
    {
        private readonly Config _cfg;
        private readonly SegmentLedger _ledger = new SegmentLedger();
        private readonly AsrSession _session = new AsrSession();
        private int _segmentBytes;
        private int _requests;

        public StreamingSegmentRunner(Config cfg) { _cfg = cfg; }

        public AsrSession Session { get { return _session; } }
        public SegmentLedger Ledger { get { return _ledger; } }
        public int SegmentBytes { get { return _segmentBytes; } }
        public int Requests { get { return _requests; } }

        // 按段序就绪的文本会交给它（在工作线程上调用）。
        // **当前无人设置它，这是故意的**：说话期间不再往输入框注入任何东西——实测在装有
        // 中文输入法的输入框里会把字符送进输入法的拼写缓存而不是上屏，并卡死输入法。
        // 现在分段结果只用于松手后拼出完整文本，由收尾路径一次性写入。
        public Action<string> OnReadyText = null;
        private readonly object _injectGate = new object();

        // 已经往用户输入框里注入过文字。
        // 一旦为真就**绝不能**再走整段回退——否则框里会先有半截流式内容，
        // 回退路径又在后面追加整段，变成重复内容。
        //
        // 判据必须是"确实注入过**非空**文字"，不能是"释放过段数"：无语音段也会被释放
        // （文本为空），按 Released>0 判断会把"什么都没注入"误判成"已注入过"，
        // 于是该回退时不回退、去掉重复保护、还会多弹一张卡片。实机日志里已经发生过。
        public bool HasInjected { get { return _ledger.ReleasedText.Length > 0; } }

        // 每有一段结算后调用：把按段序就绪的新文本取出来交给宿主注入。
        // TakeReadyText 返回 null 表示整次流式已作废，此后不再注入任何东西。
        // 整体串行化，避免两个段的完成回调交错着往输入框里写。
        private void PumpReadyText()
        {
            Action<string> sink = OnReadyText;
            if (sink == null) return;
            lock (_injectGate)
            {
                string ready = _ledger.TakeReadyText();
                if (string.IsNullOrEmpty(ready)) return;
                try { sink(ready); }
                catch (Exception ex) { _ledger.NoteFailure(_ledger.Released, "注入失败: " + ex.Message); }
            }
        }

        public void Dispatch(byte[] pcm)
        {
            if (pcm == null || pcm.Length == 0) return;
            int index = _ledger.Dispatch();
            Interlocked.Add(ref _segmentBytes, pcm.Length);
            try
            {
                ThreadPool.QueueUserWorkItem(delegate(object state) { Send(index, (byte[])state); }, pcm);
            }
            catch (Exception ex)
            {
                _ledger.NoteFailure(index, ex.Message);
            }
        }

        // 等所有段结算之后、读 HasInjected/ReleasedText 之前，**必须**先调它一次。
        // 原因：AllSettled 是在 NoteText 内部置真的，等待方可能先于 PumpReadyText 返回，
        // 那样读到的就是过期标志——"已经注入过"会被误判成"没注入过"，于是多弹一张审阅
        // 卡片，用户一点写入就重复。这一调用是幂等的（没有新内容时 TakeReadyText 返回空串）。
        public void DrainReadyText() { PumpReadyText(); }

        private void Send(int index, byte[] pcm)
        {
            try
            {
                // 先在本地判定这一段有没有语音：没有就根本不必发请求。
                // （整段上传路径目前仍会照发，是已知的浪费。）
                if (_cfg.EnableVad)
                {
                    byte[] trimmed = VoxleapCore.TrimSilencePcm(pcm, 16000, _cfg.VadThreshold, _cfg.VadPaddingMs);
                    if (trimmed == null || trimmed.Length == 0)
                    {
                        _ledger.NoteEmpty(index);
                        PumpReadyText();
                        return;
                    }
                }
                AsrResult r = AsrClient.TranscribePcm(_cfg, pcm, _session);
                if (r == null) { _ledger.NoteFailure(index, "无结果"); return; }
                if (r.Cancelled) { _ledger.NoteFailure(index, "已取消"); return; }
                if (!r.Ok)
                {
                    _ledger.NoteFailure(index, string.IsNullOrEmpty(r.Error) ? ("HTTP " + r.HttpStatus) : r.Error);
                    return;
                }
                if (string.IsNullOrEmpty(r.Text)) { _ledger.NoteEmpty(index); PumpReadyText(); return; }
                Interlocked.Increment(ref _requests);
                _ledger.NoteText(index, r.Text);
                PumpReadyText();
            }
            catch (Exception ex)
            {
                _ledger.NoteFailure(index, ex.Message);
            }
        }

        // 轮询等待所有已派发的段结算。分段请求自身有 RequestTimeoutMs 上限，
        // 所以这里的时间上限只是兜底，正常不会等满。
        public bool WaitAll(int timeoutMs)
        {
            int waited = 0;
            while (!_ledger.AllSettled && waited < timeoutMs)
            {
                Thread.Sleep(20);
                waited += 20;
            }
            return _ledger.AllSettled;
        }
    }
}
