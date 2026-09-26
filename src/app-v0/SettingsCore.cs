using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace VoxLeap
{
    internal interface ISecretProtector
    {
        string Protect(string plainText);
        string Unprotect(string protectedText);
    }

    internal sealed class DpapiSecretProtector : ISecretProtector
    {
        public string Protect(string plainText)
        {
            if (string.IsNullOrEmpty(plainText)) return "";
            byte[] plain = Encoding.UTF8.GetBytes(plainText);
            byte[] cipher = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(cipher);
        }

        public string Unprotect(string protectedText)
        {
            if (string.IsNullOrEmpty(protectedText)) return "";
            byte[] cipher = Convert.FromBase64String(protectedText);
            byte[] plain = ProtectedData.Unprotect(cipher, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain).Trim();
        }
    }

    internal sealed class ConfigValidationResult
    {
        public readonly List<string> Errors = new List<string>();

        public bool Ok
        {
            get { return Errors.Count == 0; }
        }

        public string ToDisplayText()
        {
            return string.Join(Environment.NewLine, Errors.ToArray());
        }
    }

    internal static class ConfigValidator
    {
        public static Config Normalize(Config cfg)
        {
            Config normalized = cfg == null ? new Config() : cfg.Clone();
            normalized.BaseUrl = (normalized.BaseUrl ?? "").Trim().TrimEnd('/');
            normalized.Endpoint = NormalizeEndpoint(normalized.Endpoint);
            normalized.Model = (normalized.Model ?? "").Trim();
            normalized.Api = (normalized.Api ?? "").Trim();
            normalized.ApiKey = (normalized.ApiKey ?? "").Trim();
            normalized.Language = (normalized.Language ?? "").Trim();
            normalized.Hotkey = (normalized.Hotkey ?? "").Trim();
            normalized.HotkeyMode = (normalized.HotkeyMode ?? "").Trim();
            normalized.Hotwords = NormalizeHotwords(normalized.Hotwords);
            normalized.OrganizerBaseUrl = (normalized.OrganizerBaseUrl ?? "").Trim().TrimEnd('/');
            normalized.OrganizerEndpoint = NormalizeEndpoint(normalized.OrganizerEndpoint);
            normalized.OrganizerModel = (normalized.OrganizerModel ?? "").Trim();
            normalized.OrganizerApiKey = (normalized.OrganizerApiKey ?? "").Trim();
            return normalized;
        }

        public static ConfigValidationResult Validate(Config cfg)
        {
            cfg = Normalize(cfg);
            var result = new ConfigValidationResult();
            Uri uri;
            if (cfg.BaseUrl.Length == 0)
            {
                result.Errors.Add("Base URL 不能为空。");
            }
            else if (!Uri.TryCreate(cfg.BaseUrl, UriKind.Absolute, out uri))
            {
                result.Errors.Add("Base URL 必须是完整地址；远程服务用 https://，本机回环可用 http://。");
            }
            else
            {
                bool loopbackHttp = uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback;
                if (uri.Scheme != Uri.UriSchemeHttps && !loopbackHttp)
                {
                    result.Errors.Add("为保护 API Key，远程服务必须使用 HTTPS；HTTP 仅允许本机回环地址。");
                }
            }

            if (cfg.Endpoint.Length == 0)
            {
                result.Errors.Add("endpoint 不能为空。");
            }
            else if (!cfg.Endpoint.StartsWith("/"))
            {
                result.Errors.Add("endpoint 必须以 / 开头，例如 /audio/asr/sse。");
            }

            if (cfg.Model.Length == 0)
            {
                result.Errors.Add("model 不能为空。");
            }
            if (!cfg.HasKey)
            {
                result.Errors.Add("API Key 不能为空。");
            }
            if (cfg.Api != "sse" && cfg.Api != "transcriptions")
            {
                result.Errors.Add("服务类型无效，只能是 StepFun SSE 或 OpenAI 兼容转写。");
            }
            if (cfg.Language.Length > 32)
            {
                result.Errors.Add("语言代码过长，请填写简短代码或留空自动识别。");
            }
            if (cfg.Hotkey != "RControl" && cfg.Hotkey != "RMenu" && cfg.Hotkey != "RShift" && cfg.Hotkey != "Capital")
            {
                result.Errors.Add("录音热键无效，只能是右 Ctrl、右 Alt、右 Shift 或 Caps Lock。");
            }
            if (cfg.HotkeyMode != "hold" && cfg.HotkeyMode != "toggle")
            {
                result.Errors.Add("触发方式无效，只能是按住说话或按一次开始/再按一次结束。");
            }
            if (cfg.Hotwords.Length > 500)
            {
                result.Errors.Add("热词过长，请控制在 500 个字符以内。");
            }
            if (cfg.RequestTimeoutMs < 1000 || cfg.RequestTimeoutMs > 300000)
            {
                result.Errors.Add("请求超时需在 1～300 秒之间。");
            }
            if (cfg.MaxRecordMs < 0 || cfg.MaxRecordMs > 3600000)
            {
                result.Errors.Add("录音上限需在 0～3600 秒之间，0 表示不限制。");
            }
            if (cfg.ClipboardThreshold < 1 || cfg.ClipboardThreshold > 100000)
            {
                result.Errors.Add("长文本剪贴板阈值超出允许范围。");
            }
            if (cfg.VadThreshold < 50 || cfg.VadThreshold > 10000)
            {
                result.Errors.Add("VAD 阈值需在 50～10000 之间。");
            }
            if (cfg.VadPaddingMs < 0 || cfg.VadPaddingMs > 2000)
            {
                result.Errors.Add("VAD 前后保留需在 0～2000 毫秒之间。");
            }
            if (cfg.AutoStopSilenceMs < 300 || cfg.AutoStopSilenceMs > 10000)
            {
                result.Errors.Add("静音自动停止的静音时长需在 300～10000 毫秒之间。");
            }
            if (cfg.AiOrganize)
            {
                if (cfg.OrganizerBaseUrl.Length == 0)
                    result.Errors.Add("已开启 AI 整理，但整理服务 Base URL 为空。");
                else if (Uri.TryCreate(cfg.OrganizerBaseUrl, UriKind.Absolute, out uri))
                {
                    bool loopbackOrganizer = uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback;
                    if (uri.Scheme != Uri.UriSchemeHttps && !loopbackOrganizer)
                        result.Errors.Add("整理服务远程地址必须使用 HTTPS；HTTP 仅允许本机回环地址。");
                }
                else result.Errors.Add("整理服务 Base URL 必须是完整地址。");
                if (!cfg.OrganizerEndpoint.StartsWith("/"))
                    result.Errors.Add("整理 endpoint 必须以 / 开头。");
                if (cfg.OrganizerModel.Length == 0)
                    result.Errors.Add("已开启 AI 整理，但整理模型为空。");
                if (cfg.OrganizerApiKey.Length == 0)
                    result.Errors.Add("已开启 AI 整理，但整理 API Key 为空。");
            }
            return result;
        }

        public static string NormalizeEndpoint(string endpoint)
        {
            endpoint = (endpoint ?? "").Trim();
            if (endpoint.Length > 0 && !endpoint.StartsWith("/")) endpoint = "/" + endpoint;
            return endpoint;
        }

        public static string NormalizeHotwords(string hotwords)
        {
            if (string.IsNullOrEmpty(hotwords)) return "";
            string normalized = hotwords.Replace("\r", ",").Replace("\n", ",");
            string[] pieces = normalized.Split(',');
            var kept = new List<string>();
            for (int i = 0; i < pieces.Length; i++)
            {
                string item = pieces[i].Trim();
                if (item.Length > 0) kept.Add(item);
            }
            return string.Join(",", kept.ToArray());
        }
    }

    internal static class ConfigStore
    {
        public static Config LoadFromPath(ISecretProtector protector)
        {
            string path = Config.SettingsPath;
            string backupPath = path + ".bak";
            Config cfg = TryLoadFile(path, protector);
            string pendingBackupPath = backupPath + ".next";
            string recoveryPath = File.Exists(pendingBackupPath) ? pendingBackupPath : backupPath;
            if (!string.IsNullOrEmpty(cfg.ParseIssue) && File.Exists(recoveryPath))
            {
                Config backup = TryLoadFile(recoveryPath, protector);
                if (string.IsNullOrEmpty(backup.ParseIssue))
                {
                    backup.ParseIssue = "主配置不可用，已恢复上一次可用配置。请检查后保存。";
                    cfg = backup;
                    Log.Write("settings.json 主文件不可用，已读取备份");
                }
            }

            if (cfg.LoadedLegacyPlaintextKey && cfg.HasKey && protector != null)
            {
                try
                {
                    SaveLegacyMigrationToPath(cfg, protector);
                    cfg.LoadedLegacyPlaintextKey = false;
                    Log.Write("API Key 已从明文配置迁移到 DPAPI");
                }
                catch (Exception ex)
                {
                    cfg.ParseIssue = "旧版明文 API Key 自动迁移失败，原配置未改动：" + ex.Message;
                    Log.Write("API Key 自动迁移失败: " + ex.Message);
                }
            }
            return cfg;
        }

        private static Config TryLoadFile(string path, ISecretProtector protector)
        {
            var cfg = new Config();
            if (!File.Exists(path)) return cfg;
            try
            {
                return LoadFromJson(File.ReadAllText(path, Encoding.UTF8), protector);
            }
            catch (Exception ex)
            {
                Log.Write(Path.GetFileName(path) + " 读取失败: " + ex.Message);
                cfg.ParseIssue = ex.Message;
                return cfg;
            }
        }

        public static Config LoadFromJson(string json, ISecretProtector protector)
        {
            var cfg = new Config();
            if (string.IsNullOrEmpty(json)) return cfg;

            try
            {
                string trimmed = json.Trim();
                if (!trimmed.StartsWith("{") || !trimmed.EndsWith("}")
                    || !Regex.IsMatch(trimmed, "\"(baseUrl|endpoint|api|model|apiKey|apiKeyProtected)\"\\s*:"))
                {
                    cfg.ParseIssue = "配置文件不是可识别的 VoxLeap JSON。";
                    return cfg;
                }

                string value;
                bool boolValue;
                int intValue;
                string legacyApiKey = "";
                string protectedApiKey = "";
                string protectedOrganizerKey = "";

                if (TryReadString(json, "baseUrl", out value)) cfg.BaseUrl = value.Trim().TrimEnd('/');
                if (TryReadString(json, "endpoint", out value)) cfg.Endpoint = value;
                if (TryReadString(json, "model", out value)) cfg.Model = value;
                if (TryReadString(json, "api", out value)) cfg.Api = value.Trim();
                if (TryReadString(json, "apiKey", out value)) legacyApiKey = value.Trim();
                if (TryReadString(json, "apiKeyProtected", out value)) protectedApiKey = value.Trim();
                if (TryReadString(json, "organizerApiKeyProtected", out value)) protectedOrganizerKey = value.Trim();
                if (TryReadBool(json, "autoInsert", out boolValue)) cfg.AutoInsert = boolValue;
                if (TryReadString(json, "language", out value)) cfg.Language = value;
                if (TryReadString(json, "hotkey", out value)) cfg.Hotkey = value;
                if (TryReadString(json, "hotkeyMode", out value)) cfg.HotkeyMode = value;
                if (TryReadString(json, "hotwords", out value)) cfg.Hotwords = value;
                if (TryReadInt(json, "clipboardThreshold", out intValue)) cfg.ClipboardThreshold = intValue;
                if (TryReadInt(json, "requestTimeoutMs", out intValue)) cfg.RequestTimeoutMs = intValue;
                if (TryReadInt(json, "maxRecordMs", out intValue)) cfg.MaxRecordMs = intValue;
                if (TryReadBool(json, "enableVad", out boolValue)) cfg.EnableVad = boolValue;
                if (TryReadInt(json, "vadThreshold", out intValue)) cfg.VadThreshold = intValue;
                if (TryReadInt(json, "vadPaddingMs", out intValue)) cfg.VadPaddingMs = intValue;
                if (TryReadBool(json, "autoStopOnSilence", out boolValue)) cfg.AutoStopOnSilence = boolValue;
                if (TryReadInt(json, "autoStopSilenceMs", out intValue)) cfg.AutoStopSilenceMs = intValue;
                if (TryReadBool(json, "aiOrganize", out boolValue)) cfg.AiOrganize = boolValue;
                if (TryReadString(json, "organizerBaseUrl", out value)) cfg.OrganizerBaseUrl = value;
                if (TryReadString(json, "organizerEndpoint", out value)) cfg.OrganizerEndpoint = value;
                if (TryReadString(json, "organizerModel", out value)) cfg.OrganizerModel = value;
                if (TryReadString(json, "organizerApiKey", out value)) cfg.OrganizerApiKey = value;

                if (!string.IsNullOrEmpty(protectedApiKey) && protector != null)
                {
                    try
                    {
                        cfg.ApiKey = protector.Unprotect(protectedApiKey);
                    }
                    catch (Exception ex)
                    {
                        cfg.ParseIssue = "apiKeyProtected 解密失败: " + ex.Message;
                        Log.Write(cfg.ParseIssue);
                    }
                }
                if (!cfg.HasKey && !string.IsNullOrEmpty(legacyApiKey))
                {
                    cfg.ApiKey = legacyApiKey;
                    cfg.LoadedLegacyPlaintextKey = cfg.HasKey;
                }
                if (!string.IsNullOrEmpty(protectedOrganizerKey) && protector != null)
                {
                    try { cfg.OrganizerApiKey = protector.Unprotect(protectedOrganizerKey); }
                    catch (Exception ex) { cfg.ParseIssue = "organizerApiKeyProtected 解密失败: " + ex.Message; }
                }
            }
            catch (Exception ex)
            {
                Log.Write("settings.json 解析失败: " + ex.Message);
                cfg.ParseIssue = ex.Message;
            }
            return ConfigValidator.Normalize(cfg);
        }

        public static string SerializeToJson(Config cfg, ISecretProtector protector)
        {
            Config normalized = ConfigValidator.Normalize(cfg);
            string protectedApiKey = "";
            string protectedOrganizerKey = "";
            if (normalized.HasKey && protector != null)
            {
                protectedApiKey = protector.Protect(normalized.ApiKey);
            }
            if (protector != null && !string.IsNullOrEmpty(normalized.OrganizerApiKey))
                protectedOrganizerKey = protector.Protect(normalized.OrganizerApiKey);

            var sb = new StringBuilder();
            sb.Append("{\r\n");
            AppendString(sb, "baseUrl", normalized.BaseUrl, true);
            AppendString(sb, "endpoint", normalized.Endpoint, true);
            AppendString(sb, "api", normalized.Api, true);
            AppendString(sb, "model", normalized.Model, true);
            AppendString(sb, "apiKeyProtected", protectedApiKey, true);
            AppendBool(sb, "autoInsert", normalized.AutoInsert, true);
            AppendString(sb, "language", normalized.Language, true);
            AppendString(sb, "hotkey", normalized.Hotkey, true);
            AppendString(sb, "hotkeyMode", normalized.HotkeyMode, true);
            AppendString(sb, "hotwords", normalized.Hotwords, true);
            AppendInt(sb, "clipboardThreshold", normalized.ClipboardThreshold, true);
            AppendInt(sb, "requestTimeoutMs", normalized.RequestTimeoutMs, true);
            AppendInt(sb, "maxRecordMs", normalized.MaxRecordMs, true);
            AppendBool(sb, "enableVad", normalized.EnableVad, true);
            AppendInt(sb, "vadThreshold", normalized.VadThreshold, true);
            AppendInt(sb, "vadPaddingMs", normalized.VadPaddingMs, true);
            AppendBool(sb, "autoStopOnSilence", normalized.AutoStopOnSilence, true);
            AppendInt(sb, "autoStopSilenceMs", normalized.AutoStopSilenceMs, true);
            AppendBool(sb, "aiOrganize", normalized.AiOrganize, true);
            AppendString(sb, "organizerBaseUrl", normalized.OrganizerBaseUrl, true);
            AppendString(sb, "organizerEndpoint", normalized.OrganizerEndpoint, true);
            AppendString(sb, "organizerModel", normalized.OrganizerModel, true);
            AppendString(sb, "organizerApiKeyProtected", protectedOrganizerKey, false);
            sb.Append("}\r\n");
            return sb.ToString();
        }

        public static void SaveToPath(Config cfg, ISecretProtector protector)
        {
            string json = SerializeToJson(cfg, protector);
            string path = Config.SettingsPath;
            string folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder)) Directory.CreateDirectory(folder);

            string tempPath = path + ".tmp";
            string backupPath = path + ".bak";
            string pendingBackupPath = backupPath + ".next";
            File.WriteAllText(tempPath, json, new UTF8Encoding(false));
            try
            {
                if (File.Exists(path))
                {
                    // 不先删除现有备份。主文件替换成功前，它始终保留最后可恢复版本。
                    if (File.Exists(pendingBackupPath)) File.Delete(pendingBackupPath);
                    File.Replace(tempPath, path, pendingBackupPath, true);
                    try
                    {
                        if (File.Exists(backupPath)) File.Replace(pendingBackupPath, backupPath, null, true);
                        else File.Move(pendingBackupPath, backupPath);
                    }
                    catch (Exception ex)
                    {
                        // 主文件已经安全替换；保留 .bak.next 作为较新的恢复候选。
                        Log.Write("配置备份轮换失败: " + ex.Message);
                    }
                }
                else
                {
                    File.Move(tempPath, path);
                }
            }
            finally
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
        }

        private static void SaveLegacyMigrationToPath(Config cfg, ISecretProtector protector)
        {
            string path = Config.SettingsPath;
            string tempPath = path + ".migrate.tmp";
            string backupPath = path + ".bak";
            string pendingBackupPath = backupPath + ".next";
            string json = SerializeToJson(cfg, protector);
            File.WriteAllText(tempPath, json, new UTF8Encoding(false));
            try
            {
                // 迁移前的主文件本身已含明文 Key，不能再把它复制成备份。
                if (File.Exists(backupPath)) File.Delete(backupPath);
                if (File.Exists(pendingBackupPath)) File.Delete(pendingBackupPath);
                if (File.Exists(path)) File.Replace(tempPath, path, null, true);
                else File.Move(tempPath, path);
            }
            finally
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
        }

        private static bool TryReadString(string json, string key, out string value)
        {
            Match m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (m.Success)
            {
                value = JsonUtil.Unescape(m.Groups[1].Value);
                return true;
            }
            value = "";
            return false;
        }

        private static bool TryReadBool(string json, string key, out bool value)
        {
            Match m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*(true|false)");
            if (m.Success)
            {
                value = m.Groups[1].Value == "true";
                return true;
            }
            value = false;
            return false;
        }

        private static bool TryReadInt(string json, string key, out int value)
        {
            Match m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*(-?\\d+)");
            if (m.Success)
            {
                return int.TryParse(m.Groups[1].Value, out value);
            }
            value = 0;
            return false;
        }

        private static void AppendString(StringBuilder sb, string key, string value, bool comma)
        {
            sb.Append("  \"").Append(key).Append("\": \"")
                .Append(VoxleapCore.EscapeJson(value ?? "")).Append("\"");
            if (comma) sb.Append(',');
            sb.Append("\r\n");
        }

        private static void AppendBool(StringBuilder sb, string key, bool value, bool comma)
        {
            sb.Append("  \"").Append(key).Append("\": ").Append(value ? "true" : "false");
            if (comma) sb.Append(',');
            sb.Append("\r\n");
        }

        private static void AppendInt(StringBuilder sb, string key, int value, bool comma)
        {
            sb.Append("  \"").Append(key).Append("\": ").Append(value);
            if (comma) sb.Append(',');
            sb.Append("\r\n");
        }
    }

    internal static class DiagnosticAudio
    {
        public static void WriteSilentWav(string path, int milliseconds)
        {
            const int sampleRate = 16000;
            const short channels = 1;
            const short bitsPerSample = 16;
            int safeMilliseconds = Math.Max(100, Math.Min(1000, milliseconds));
            int dataLength = sampleRate * safeMilliseconds / 1000 * channels * bitsPerSample / 8;
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream, Encoding.ASCII))
            {
                writer.Write(Encoding.ASCII.GetBytes("RIFF"));
                writer.Write(36 + dataLength);
                writer.Write(Encoding.ASCII.GetBytes("WAVE"));
                writer.Write(Encoding.ASCII.GetBytes("fmt "));
                writer.Write(16);
                writer.Write((short)1);
                writer.Write(channels);
                writer.Write(sampleRate);
                int byteRate = sampleRate * channels * bitsPerSample / 8;
                writer.Write(byteRate);
                writer.Write((short)(channels * bitsPerSample / 8));
                writer.Write(bitsPerSample);
                writer.Write(Encoding.ASCII.GetBytes("data"));
                writer.Write(dataLength);
                writer.Write(new byte[dataLength]);
            }
        }
    }

    internal static class ConnectionDiagnosis
    {
        public static string Describe(AsrResult result, Config cfg)
        {
            if (result == null) return "测试没有返回结果，请稍后重试。";
            if (result.Ok)
            {
                return "连接成功。服务地址、鉴权和协议均已响应。";
            }
            if (result.HttpStatus == 401 || result.HttpStatus == 403)
            {
                return "鉴权失败（" + result.HttpStatus + "）。请检查 API Key、订阅账户和该模型的使用权限。";
            }
            if (result.HttpStatus == 404)
            {
                if (cfg != null && cfg.Api == "sse")
                {
                    return "接口不存在（404）。Step Plan 必须使用 /step_plan/v1/audio/asr/sse；请恢复官方预设并核对订阅文档。";
                }
                return "接口不存在（404）。当前服务很可能不兼容所填 endpoint；请按服务商官方文档核对 Base URL、路径和协议。";
            }
            if (result.HttpStatus == 400 || result.HttpStatus == 422)
            {
                return "服务已收到请求，但模型名、请求协议或音频参数不被接受（" + result.HttpStatus + "）。请核对官方文档。";
            }
            if (result.HttpStatus == 429)
            {
                return "请求已到达服务，但额度不足或触发限流（429）。请检查套餐与余额。";
            }
            if (result.HttpStatus >= 200 && result.HttpStatus < 300)
            {
                return "服务已响应，但返回结构与当前协议不兼容。请确认它是否真正兼容所选转写协议。";
            }
            if (result.HttpStatus == 0)
            {
                return "无法建立连接。请检查网络、DNS、代理、证书和 Base URL。";
            }
            return "服务返回 HTTP " + result.HttpStatus + "。请打开官方文档核对接口与模型。";
        }
    }
}
