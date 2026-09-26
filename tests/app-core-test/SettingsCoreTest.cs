using System;
using System.IO;

namespace VoxLeap
{
    internal sealed class FakeProtector : ISecretProtector
    {
        public string Protect(string plainText)
        {
            return "ENC(" + plainText + ")";
        }

        public string Unprotect(string protectedText)
        {
            if (protectedText.StartsWith("ENC(") && protectedText.EndsWith(")"))
            {
                return protectedText.Substring(4, protectedText.Length - 5);
            }
            throw new InvalidOperationException("unexpected protected payload");
        }
    }

    internal static class SettingsCoreTest
    {
        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        public static int Main()
        {
            try
            {
                TestSerializeProtectedKey();
                TestLoadLegacyPlainKey();
                TestValidation();
                TestHotkeyValidation();
                TestHotkeyDefaultsWhenMissing();
                TestTransportValidation();
                TestAutoStopConfig();
                TestConnectionDiagnosis();
                TestDiagnosticAudio();
                TestLegacyMigrationAndBackupRecovery();
                Console.WriteLine("SettingsCoreTest OK");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        private static void TestSerializeProtectedKey()
        {
            var cfg = new Config();
            cfg.ApiKey = "secret-key";
            cfg.Hotkey = "Capital";
            cfg.HotkeyMode = "toggle";
            string json = ConfigStore.SerializeToJson(cfg, new FakeProtector());
            Assert(json.IndexOf("\"apiKeyProtected\": \"ENC(secret-key)\"") >= 0, "apiKeyProtected 未写入");
            Assert(json.IndexOf("\"apiKey\"") < 0, "明文 apiKey 不应再写回 JSON");
            Assert(json.IndexOf("\"hotkey\": \"Capital\"") >= 0, "hotkey 未写回 JSON");
            Assert(json.IndexOf("\"hotkeyMode\": \"toggle\"") >= 0, "hotkeyMode 未写回 JSON");

            Config roundTrip = ConfigStore.LoadFromJson(json, new FakeProtector());
            Assert(roundTrip.ApiKey == "secret-key", "保护后的 Key 未正确回读");
            Assert(roundTrip.Hotkey == "Capital", "hotkey 未正确回读");
            Assert(roundTrip.HotkeyMode == "toggle", "hotkeyMode 未正确回读");
        }

        private static void TestLoadLegacyPlainKey()
        {
            string legacyJson =
                "{\n" +
                "  \"baseUrl\": \"https://api.stepfun.com/step_plan/v1\",\n" +
                "  \"endpoint\": \"/audio/asr/sse\",\n" +
                "  \"api\": \"sse\",\n" +
                "  \"model\": \"stepaudio-2.5-asr\",\n" +
                "  \"apiKey\": \"legacy-key\",\n" +
                "  \"autoInsert\": false,\n" +
                "  \"language\": \"zh\",\n" +
                "  \"hotkey\": \"RShift\",\n" +
                "  \"hotkeyMode\": \"toggle\",\n" +
                "  \"hotwords\": \"Cursor, TypeScript\",\n" +
                "  \"clipboardThreshold\": 200,\n" +
                "  \"requestTimeoutMs\": 20000,\n" +
                "  \"maxRecordMs\": 300000\n" +
                "}\n";

            Config cfg = ConfigStore.LoadFromJson(legacyJson, new FakeProtector());
            Assert(cfg.ApiKey == "legacy-key", "旧版明文 apiKey 读取失败");
            Assert(cfg.LoadedLegacyPlaintextKey, "旧版明文 Key 应标记为待迁移");
            Assert(cfg.Hotkey == "RShift", "hotkey 读取失败");
            Assert(cfg.HotkeyMode == "toggle", "hotkeyMode 读取失败");
            Assert(cfg.Hotwords == "Cursor,TypeScript", "热词规范化失败");
        }

        private static void TestValidation()
        {
            var cfg = new Config();
            cfg.ApiKey = "test-key";
            cfg.BaseUrl = "not-a-url";
            ConfigValidationResult result = ConfigValidator.Validate(cfg);
            Assert(!result.Ok, "非法 Base URL 应当被拦截");
            Assert(result.ToDisplayText().IndexOf("Base URL") >= 0, "错误消息应提示 Base URL");
        }

        private static void TestHotkeyValidation()
        {
            var valid = new Config();
            valid.ApiKey = "test-key";
            valid.Hotkey = "Capital";
            valid.HotkeyMode = "toggle";
            Assert(ConfigValidator.Validate(valid).Ok, "合法热键与模式应通过校验");

            var badHotkey = new Config();
            badHotkey.ApiKey = "test-key";
            badHotkey.Hotkey = "LCtrl";
            Assert(!ConfigValidator.Validate(badHotkey).Ok, "非法热键应被拒绝");

            var badMode = new Config();
            badMode.ApiKey = "test-key";
            badMode.HotkeyMode = "tap";
            Assert(!ConfigValidator.Validate(badMode).Ok, "非法触发方式应被拒绝");
        }

        private static void TestHotkeyDefaultsWhenMissing()
        {
            string json = "{\"apiKeyProtected\":\"ENC(k)\"}";
            Config cfg = ConfigStore.LoadFromJson(json, new FakeProtector());
            Assert(cfg.Hotkey == "RControl", "旧配置缺 hotkey 应回退右 Ctrl");
            Assert(cfg.HotkeyMode == "hold", "旧配置缺 hotkeyMode 应回退按住说话");

            string roundTrip = ConfigStore.SerializeToJson(cfg, new FakeProtector());
            Assert(roundTrip.IndexOf("\"hotkey\": \"RControl\"") >= 0, "默认热键应写回配置");
            Assert(roundTrip.IndexOf("\"hotkeyMode\": \"hold\"") >= 0, "默认模式应写回配置");
        }

        private static void TestTransportValidation()
        {
            var remoteHttp = new Config();
            remoteHttp.ApiKey = "test-key";
            remoteHttp.BaseUrl = "http://example.com/v1";
            Assert(!ConfigValidator.Validate(remoteHttp).Ok, "远程 HTTP 必须被拒绝");

            var loopback = new Config();
            loopback.ApiKey = "test-key";
            loopback.BaseUrl = "http://127.0.0.1:9000/v1";
            Assert(ConfigValidator.Validate(loopback).Ok, "本机回环 HTTP 应允许用于本地服务");
        }

        private static void TestConnectionDiagnosis()
        {
            var missing = new AsrResult();
            missing.HttpStatus = 404;
            string message = ConnectionDiagnosis.Describe(missing, new Config());
            Assert(message.IndexOf("/step_plan/v1/audio/asr/sse") >= 0, "StepFun 404 应给出正确路径");

            var denied = new AsrResult();
            denied.HttpStatus = 401;
            Assert(ConnectionDiagnosis.Describe(denied, new Config()).IndexOf("API Key") >= 0,
                "401 应提示检查 API Key");
        }

        private static void TestAutoStopConfig()
        {
            var enabled = new Config();
            enabled.ApiKey = "test-key";
            enabled.AutoStopOnSilence = true;
            enabled.AutoStopSilenceMs = 1200;
            Assert(ConfigValidator.Validate(enabled).Ok, "合法静音自动停止配置应通过校验");

            var tooFast = new Config();
            tooFast.ApiKey = "test-key";
            tooFast.AutoStopSilenceMs = 100;
            Assert(!ConfigValidator.Validate(tooFast).Ok, "过短的静音时长应被拒绝");

            var tooSlow = new Config();
            tooSlow.ApiKey = "test-key";
            tooSlow.AutoStopSilenceMs = 20000;
            Assert(!ConfigValidator.Validate(tooSlow).Ok, "过长的静音时长应被拒绝");

            // 默认关闭属于"默认收紧"产品约束的一部分，不能被无意改成默认开启。
            Assert(!new Config().AutoStopOnSilence, "静音自动停止默认应关闭");

            var cfg = new Config();
            cfg.ApiKey = "test-key";
            cfg.AutoStopOnSilence = true;
            cfg.AutoStopSilenceMs = 900;
            string json = ConfigStore.SerializeToJson(cfg, new FakeProtector());
            Assert(json.IndexOf("\"autoStopOnSilence\": true") >= 0, "静音自动停止开关应写入配置");
            Assert(json.IndexOf("\"autoStopSilenceMs\": 900") >= 0, "静音时长应写入配置");

            Config back = ConfigStore.LoadFromJson(json, new FakeProtector());
            Assert(back.AutoStopOnSilence, "静音自动停止开关应能读回");
            Assert(back.AutoStopSilenceMs == 900, "静音时长应能读回");

            // 旧配置没有这两个字段。若缺字段时落到 0，会被校验器判为非法，
            // 等于把还在用旧配置的用户直接锁在设置窗口外，所以必须回退到默认值。
            Config legacy = ConfigStore.LoadFromJson("{\"apiKeyProtected\":\"ENC(k)\"}", new FakeProtector());
            Assert(!legacy.AutoStopOnSilence && legacy.AutoStopSilenceMs == 1200,
                "旧配置缺字段应回退到关闭与 1200ms，而不是 0");
        }

        private static void TestDiagnosticAudio()
        {
            string path = Path.Combine(Path.GetTempPath(), "voxleap_settings_test.wav");
            try
            {
                DiagnosticAudio.WriteSilentWav(path, 250);
                byte[] wav = File.ReadAllBytes(path);
                Assert(wav.Length == 8044, "0.25 秒 16kHz 单声道 WAV 长度不正确");
                Assert(VoxleapCore.ExtractPcm(wav).Length == 8000, "静音 WAV PCM 长度不正确");
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        private static void TestLegacyMigrationAndBackupRecovery()
        {
            string path = Config.SettingsPath;
            string backup = path + ".bak";
            string pending = backup + ".next";
            foreach (string file in new[] { path, backup, pending, path + ".tmp", path + ".migrate.tmp" })
            {
                if (File.Exists(file)) File.Delete(file);
            }

            try
            {
                File.WriteAllText(path,
                    "{\"baseUrl\":\"https://api.stepfun.com/step_plan/v1\","
                    + "\"endpoint\":\"/audio/asr/sse\",\"api\":\"sse\","
                    + "\"model\":\"stepaudio-2.5-asr\",\"apiKey\":\"legacy-key\"}");
                Config migrated = ConfigStore.LoadFromPath(new FakeProtector());
                string protectedJson = File.ReadAllText(path);
                Assert(migrated.ApiKey == "legacy-key", "迁移后内存 Key 不正确");
                Assert(protectedJson.IndexOf("\"apiKey\"") < 0, "迁移后不应保留明文 apiKey 字段");
                Assert(protectedJson.IndexOf("apiKeyProtected") >= 0, "迁移后应写入保护字段");
                Assert(!File.Exists(backup), "明文迁移不得生成旧配置备份");

                Config next = migrated.Clone();
                next.Model = "second-model";
                ConfigStore.SaveToPath(next, new FakeProtector());
                Assert(File.Exists(backup), "普通保存应保留上一次加密配置");

                File.WriteAllText(path, "broken");
                Config recovered = ConfigStore.LoadFromPath(new FakeProtector());
                Assert(recovered.Model == "stepaudio-2.5-asr", "主配置损坏时应恢复备份");
                Assert(recovered.ParseIssue.IndexOf("已恢复") >= 0, "恢复备份后应提示用户检查");
            }
            finally
            {
                foreach (string file in new[] { path, backup, pending, path + ".tmp", path + ".migrate.tmp" })
                {
                    if (File.Exists(file)) File.Delete(file);
                }
            }
        }
    }
}
