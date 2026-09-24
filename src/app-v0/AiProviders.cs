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
                string body = "{\"model\":\"" + VoxleapCore.EscapeJson(cfg.OrganizerModel) +
                    "\",\"temperature\":0.1,\"messages\":[{\"role\":\"system\",\"content\":\"" +
                    VoxleapCore.EscapeJson(system) + "\"},{\"role\":\"user\",\"content\":\"" +
                    VoxleapCore.EscapeJson(original) + "\"}]}";
                byte[] bytes = Encoding.UTF8.GetBytes(body);
                request.ContentLength = bytes.Length;
                using (Stream stream = request.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);
                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    if (trace != null) trace.Mark(LatencyTrace.Organized);
                    string payload = ReadAll(response);
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
            return result;
        }

        private static string ReadAll(HttpWebResponse response)
        {
            using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                return reader.ReadToEnd();
        }
    }
}
