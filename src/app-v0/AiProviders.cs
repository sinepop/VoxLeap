using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace VoxLeap
{
    internal interface IStreamingAsrProvider
    {
        AsrResult Transcribe(Config cfg, byte[] wav, AsrSession session);
    }

    internal sealed class StepFunStreamingAsrProvider : IStreamingAsrProvider
    {
        public AsrResult Transcribe(Config cfg, byte[] wav, AsrSession session)
        {
            return AsrClient.TranscribeStepFun(cfg, wav, session);
        }
    }

    internal sealed class OpenAiCompatibleAsrProvider : IStreamingAsrProvider
    {
            public AsrResult Transcribe(Config cfg, byte[] wav, AsrSession session)
            {
                var result = new AsrResult();
                var started = DateTime.UtcNow;
                HttpWebRequest request = null;
                LatencyTrace trace = session == null ? null : session.Trace;
                try
                {
                    string boundary = "----VoxLeapBoundary" + Guid.NewGuid().ToString("N");
                    string endpoint = (cfg.Endpoint ?? "/audio/transcriptions");
                    if (!endpoint.StartsWith("/")) endpoint = "/" + endpoint;
                    request = (HttpWebRequest)WebRequest.Create(cfg.BaseUrl + endpoint);
                    if (session != null)
                    {
                        session.CurrentRequest = request;
                        if (session.Cancelled) { result.Cancelled = true; return result; }
                    }
                    request.Method = "POST";
                    request.Timeout = cfg.RequestTimeoutMs;
                    request.ReadWriteTimeout = cfg.RequestTimeoutMs;
                    request.ContentType = "multipart/form-data; boundary=" + boundary;
                    request.Headers["Authorization"] = "Bearer " + cfg.ApiKey;
                    // 同 StepFun 路径：关闭写缓冲，让上传耗时可测且省一份请求体副本。
                    request.AllowWriteStreamBuffering = false;
                    byte[] body = BuildMultipart(boundary, cfg.Model, wav);
                    request.ContentLength = body.Length;
                    if (trace != null)
                    {
                        trace.Note("reqBytes", body.Length);
                        trace.Mark(LatencyTrace.Prepared);
                    }
                    using (Stream stream = request.GetRequestStream())
                    {
                        if (trace != null) trace.Mark(LatencyTrace.Connected);
                        stream.Write(body, 0, body.Length);
                    }
                    if (trace != null) trace.Mark(LatencyTrace.Uploaded);
                    using (var response = (HttpWebResponse)request.GetResponse())
                    {
                        if (trace != null)
                        {
                            trace.Mark(LatencyTrace.Ttfb);
                            trace.Mark(LatencyTrace.FirstDelta); // 非流式：响应到手即全部文本
                            trace.Mark(LatencyTrace.Done);
                        }
                        result.HttpStatus = (int)response.StatusCode;
                        string payload = ReadResponse(response);
                        Match match = Regex.Match(payload, "\"text\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
                        if (!match.Success) result.Error = "响应里没有 text 字段";
                        else
                        {
                            result.Text = JsonUtil.Unescape(match.Groups[1].Value);
                            result.Ok = true;
                        }
                    }
                }
                catch (WebException ex)
                {
                    if (session != null && session.Cancelled) result.Cancelled = true;
                    else result.Error = ex.Message;
                }
                catch (Exception ex)
                {
                    result.Error = ex.Message;
                }
                finally
                {
                    if (session != null && object.ReferenceEquals(session.CurrentRequest, request))
                        session.CurrentRequest = null;
                    result.LatencySeconds = (DateTime.UtcNow - started).TotalSeconds;
                }
                return result;
            }

            private static byte[] BuildMultipart(string boundary, string model, byte[] wav)
            {
                var body = new MemoryStream();
                Write(body, "--" + boundary + "\r\nContent-Disposition: form-data; name=\"model\"\r\n\r\n" + model + "\r\n");
                Write(body, "--" + boundary + "\r\nContent-Disposition: form-data; name=\"file\"; filename=\"audio.wav\"\r\nContent-Type: audio/wav\r\n\r\n");
                body.Write(wav, 0, wav.Length);
                Write(body, "\r\n--" + boundary + "--\r\n");
                return body.ToArray();
            }

            private static void Write(Stream stream, string text)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(text);
                stream.Write(bytes, 0, bytes.Length);
            }

            private static string ReadResponse(HttpWebResponse response)
            {
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                    return reader.ReadToEnd();
        }
    }

    internal sealed class OrganizerResult
    {
        public bool Ok;
        public string Text = "";
        public string Error = "";
    }

    internal interface ITextTransformProvider
    {
        OrganizerResult Transform(Config cfg, string original, AsrSession session);
    }

    internal sealed class OpenAiCompatibleTextTransformProvider : ITextTransformProvider
    {
        public OrganizerResult Transform(Config cfg, string original, AsrSession session)
        {
            return AiOrganizer.Organize(cfg, original, session);
        }
    }

    internal static class AiOrganizer
    {
        public static OrganizerResult Organize(Config cfg, string original, AsrSession session)
        {
            var result = new OrganizerResult();
            if (!cfg.AiOrganize) return result;
            if (string.IsNullOrEmpty(original)) { result.Error = "原文为空"; return result; }
            HttpWebRequest request = null;
            LatencyTrace trace = session == null ? null : session.Trace;
            try
            {
                if (session != null && session.Cancelled) { result.Error = "已取消"; return result; }
                string endpoint = (cfg.OrganizerEndpoint ?? "/chat/completions");
                if (!endpoint.StartsWith("/")) endpoint = "/" + endpoint;
                request = (HttpWebRequest)WebRequest.Create(cfg.OrganizerBaseUrl + endpoint);
                if (session != null) session.CurrentRequest = request;
                request.Method = "POST";
                request.Timeout = cfg.RequestTimeoutMs;
                request.ReadWriteTimeout = cfg.RequestTimeoutMs;
                request.ContentType = "application/json";
                request.Headers["Authorization"] = "Bearer " + cfg.OrganizerApiKey;
                string system = "你是语音输入整理器。只整理语序和标点，不添加事实。必须保留原文中的数字、专有名词、代码标识符、URL 和否定关系。只输出整理后的正文，不要解释。";
                // 整理是零推理任务（改标点、去语气词），但 step-3.7-flash 是思考模型：
                // 实测 34 字输入消耗 363 输出 token，绝大部分是思考，代价 3.7 秒。
                // 同时请求两种关闭/削弱思考的方式，因为各家部署支持情况不同：
                //   - chat_template_kwargs.thinking=false：NVIDIA NIM 对同一份权重的开关
                //     （模板 prefill 一个空思考块）；StepFun 自家 API 文档未列出，可能被忽略。
                //   - reasoning_effort=low：StepFun 自家 API 文档明确支持；但第三方实测它对
                //     思考长度只有约 2 倍的影响力，不是硬开关。
                // 不猜哪个生效：日志里的"输出token"会直接给出答案——
                //   降到约 50   → 强开关生效
                //   降到约 180  → 只有 reasoning_effort 生效
                //   维持 363    → 两者都被忽略，改走结构性方案（按段并行整理）
                string body = "{\"model\":\"" + VoxleapCore.EscapeJson(cfg.OrganizerModel) +
                    "\",\"temperature\":0.1,\"reasoning_effort\":\"low\"," +
                    "\"chat_template_kwargs\":{\"thinking\":false},\"messages\":[{\"role\":\"system\",\"content\":\"" +
                    VoxleapCore.EscapeJson(system) + "\"},{\"role\":\"user\",\"content\":\"" +
                    VoxleapCore.EscapeJson(original) + "\"}]}";
                byte[] bytes = Encoding.UTF8.GetBytes(body);
                request.ContentLength = bytes.Length;
                using (Stream stream = request.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);
                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    if (trace != null) trace.Mark(LatencyTrace.Organized);
                    string payload = ReadAll(response);
                    // 诊断：整理耗时与字数完全不成比例（20 字 5148ms，12 字 2061ms），怀疑是
                    // 模型在生成思考 token，而这里是整段阻塞等待（stream=false，无 max_tokens，
                    // 也没有任何推理开关），思考 token 全部要等完。
                    // 只记录用量与长度，不记录正文（日志不得含完整转写正文）。
                    Match inTok = Regex.Match(payload, "\"prompt_tokens\"\\s*:\\s*(\\d+)");
                    Match outTok = Regex.Match(payload, "\"completion_tokens\"\\s*:\\s*(\\d+)");
                    bool hasReasoning = payload.IndexOf("reasoning_content", StringComparison.Ordinal) >= 0;
                    Log.Write("整理用量: 原文=" + original.Length + "字 输入token="
                        + (inTok.Success ? inTok.Groups[1].Value : "?") + " 输出token="
                        + (outTok.Success ? outTok.Groups[1].Value : "?") + " 推理字段="
                        + (hasReasoning ? "有" : "无") + " 响应=" + payload.Length + "字节");
                    Match match = Regex.Match(payload, "\"content\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
                    if (!match.Success) { result.Error = "整理响应里没有 choices.message.content"; return result; }
                    result.Text = VoxleapCore.NormalizeCjkLatinSpacing(JsonUtil.Unescape(match.Groups[1].Value)).Trim();
                    result.Ok = result.Text.Length > 0;
                    if (!result.Ok) result.Error = "整理结果为空";
                }
            }
            catch (WebException ex)
            {
                result.Error = ex.Message;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
            }
            finally
            {
                if (session != null && object.ReferenceEquals(session.CurrentRequest, request))
                    session.CurrentRequest = null;
            }
            // 整理失败以前是**完全静默**的：调用方看到 OrganizedText 为空就回落成原文，
            // 日志里只留下"整理=3xxxms"，看不出整理其实没生效，用户以为只是慢。
            // 实机证据：连续 8 次会话里"识别字数"与"写入字数"完全相同——整理只要真的加过
            // 标点或删过语气词，字数几乎不可能每次都不变。必须留痕，不许静默回落。
            if (cfg.AiOrganize)
            {
                Log.Write("整理结果: ok=" + result.Ok
                    + " 整理后=" + (result.Text == null ? 0 : result.Text.Length) + "字"
                    + " 原文=" + original.Length + "字"
                    + (string.IsNullOrEmpty(result.Error) ? "" : " 错误=" + result.Error));
            }
            return result;
        }

        private static string ReadAll(HttpWebResponse response)
        {
            using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                return reader.ReadToEnd();
        }
    }
}
