# VoxLeap Personal v0.1（个人自用版）

零安装 WinForms 语音输入工具，使用 Windows 自带 .NET Framework 4.x `csc.exe` 构建。当前已验证的识别路径是 **阶跃星辰 StepFun SSE**；设置窗口里另外提供 **OpenAI 兼容转写（高级，未验证）**，仅适用于明确声明兼容 `/audio/transcriptions` 的接口，并非所有厂商都通用。

## 部署位置

- 可执行文件：`%LOCALAPPDATA%\VoxLeap\VoxLeap.exe`
- 配置：同目录 `settings.json`
- 日志：同目录 `voxleap.log`（只记状态、耗时和字数，不记转写正文或 API Key）
- 源码与构建脚本：项目 `src/app-v0/`

## 首次使用

1. 运行 `VoxLeap.exe`；默认只驻留托盘，不弹任何窗口；首次未配置 Key 时会打开一次设置窗口做引导；
2. 打开设置：双击托盘图标，或托盘右键菜单 **设置…**；程序已驻留托盘时再双击桌面快捷方式会静默退出，不弹窗；
3. 选择语音服务，填写模型与 API Key；先点 **测试连接**，确认后保存；
4. 下一次录音直接生效，无需重启。高级用户仍可显式带 `--settings` 参数启动打开设置（托盘进程已在运行时，请求会转发给托盘进程）。

> 高级用户仍可使用托盘菜单 **高级：打开配置文件** 编辑 JSON。托盘进程会在空闲时自动重载；若当前正在录音、转写或审阅，会等本次会话结束后再应用。

## 使用方式

1. 光标放到目标输入框；
2. 在设置里选择热键：**右 Ctrl / 右 Alt / 右 Shift / Caps Lock**；
3. 触发方式支持 **按住说话** 或 **按一次开始、再按一次结束**；Caps Lock 作为热键时会被吞掉，不会切换大小写；
4. 结束录音后开始转写；转写中可按 `Esc` 取消，本次文字不会保留；
5. 转写中底部 HUD 会显示来自 SSE `delta` 的实时增量字幕；它是累积提示，不是完整逐词结果；
6. `autoInsert: false`（默认）时打开审阅卡片，可编辑后写入；若已改动但未写入，关闭前会确认是否放弃；
7. `autoInsert: true` 时识别完成直接写入光标处。

## 设置窗口包含的字段

- 服务类型
  - `StepFun SSE（已验证）`
  - `OpenAI 兼容转写（高级，未验证）`
- 模型
- API Key（默认遮罩，可切换显示）
- **测试连接**（只发送 0.25 秒静音样本，不采集麦克风）
- **恢复 StepFun 官方预设**
- 官方接入文档入口
- 高级页：Base URL、endpoint，以及受警告保护的手动覆盖
- 语言
- 录音热键（右 Ctrl / 右 Alt / 右 Shift / Caps Lock）
- 触发方式（按住说话 / 按一次开始，再按一次结束）
- 热词
- VAD（默认开启）：按 16 kHz PCM RMS 去除首尾静音，并将超过 800ms 的中间长静音压缩为短停顿，保留可配置前后缓冲
- AI 整理（可选）：通过 OpenAI 兼容 `/chat/completions` 整理，原文始终保留
- 整理安全门：数字、URL、技术 Token 或否定关系发生风险变化时自动回退原文
- 设置窗口的“高级”页可配置 VAD 阈值/缓冲与整理服务地址、模型和密钥
- 自动输入
- 录音上限（秒，0=不限制）
- 请求超时（秒）

保存规则：

- 任一录音、转写或审阅会话未结束时不能打开或保存设置；
- 保存前校验 URL、endpoint、模型、Key、热键、触发方式、超时和录音上限；远程服务必须使用 HTTPS，本机回环地址可使用 HTTP；
- 使用临时文件 + 原子替换写入 `settings.json`；
- **新保存的 API Key 会先用 Windows DPAPI CurrentUser 加密，再写入 `apiKeyProtected`**；
- 兼容读取旧版明文 `apiKey`，程序启动时会自动迁移并删除明文字段；
- `settings.template.json` 不再包含明文占位密钥。

## 连接失败如何判断

设置窗口不会把所有错误都显示成“连接失败”：

| 状态 | 含义与处理 |
|---|---|
| `401 / 403` | 检查 API Key、订阅账户和模型权限 |
| `404` | 通常是 Base URL、订阅入口或 endpoint 错误，不是 Key 错；恢复官方预设并核对文档 |
| `400 / 422` | 服务已收到请求，但模型、协议或音频参数不兼容 |
| `429` | 额度不足或触发限流 |
| 无 HTTP 状态 | 检查网络、DNS、代理、证书和地址 |
| `2xx` 但无文本字段 | 服务响应结构与所选协议不兼容 |

Step Plan 当前官方且已验证的组合是：

```text
Base URL: https://api.stepfun.com/step_plan/v1
Endpoint: /audio/asr/sse
Model: stepaudio-2.5-asr
Protocol: JSON + base64 PCM + SSE
```

官方文档：<https://platform.stepfun.com/docs/zh/step-plan/integrations/audio-api>

## 延迟分解（排障用，2026-09-24 新增）

每次会话在 `voxleap.log` 追加**一行**延迟分解，用来回答"松手之后的等待到底花在哪"：

```text
延迟: 录音=9.4s 松手后=1523ms | 保存=12ms 准备=9ms 连接=210ms 上传=655ms 首字节=488ms 首delta=470ms 回传=32ms 注入=95ms | 请求=417KB 上行=636KB/s VAD=417KB->408KB 状态=200 结果=ok 字数=35 路径=SendInput | 判定=上传主导 655/1523ms(43%)
```

时间轴 0 点 = **按下热键的瞬间**，因此：

| 字段 | 含义 |
|---|---|
| `录音` | 按下到松开的时长（用户说话时间，不属于等待） |
| `松手后` | **用户真正在等的那段** = 最后阶段 - 松手 |
| `保存` | MCI 录音落盘 |
| `准备` | VAD 裁剪 + 重打包 WAV + base64 + JSON 组装 |
| `连接` | TCP + TLS 握手（`GetRequestStream`） |
| `上传` | 请求体真实写入网络。为使其可测，请求已设 `AllowWriteStreamBuffering=false`；默认缓冲会把上传和服务端处理混在一起，且为 300s 录音多留一份约 12.8MB 的内存副本 |
| `首字节` | 服务端收全音频到响应头到达 |
| `首delta` | 第一条带文本的 SSE 事件 |
| `回传` | 到最终文本（`transcript.text.done`） |
| `整理` | AI 整理返回（关闭 AI 时该字段不出现） |
| `注入` | 写入目标窗口耗时（仅自动输入模式；审阅模式在关闭审阅窗口时并入） |
| `上行` | 实测上行速率 KB/s。**低于链路标称速度即说明瓶颈不在本机带宽** |
| `判定` | 本地处理/连接握手/上传/服务端 四者中的最大项及其占「松手后」的比例 |

同一条信息也出现在两处界面：审阅卡片 meta 行（`松手后 1.5s · 上传 655ms · 服务端 520ms`）与自动输入成功提示的尾部小字（`已输入 35 字 · 1.5s`）。

隐私约束不变：该行只有阶段名、毫秒、字节数、字数与 HTTP 状态，**没有转写正文、音频或凭据**；`LatencyTrace` 也只有白名单键会被打印（有单测守护）。

### 分段影子（2026-09-24 新增，只观察不改行为）

每次录音结束还会追加**一行**实时分段摘要，用于在真正启用「静音自动停止 / 边说边送」之前，先用真实录音验证阈值是否合理：

```text
分段影子: 帧=94 字节=300800/300800 差=0 时长=9.4s 说话=7.1s 起=3 段=2 自动收尾=1 尾段=0ms 静音游程=[420,780,1350]ms 阈值rms=0.0137 分界=600ms 收尾=1200ms
```

| 字段 | 含义 |
|---|---|
| `帧` / `字节` / `差` | waveIn 实际交付的帧数与字节数。**`差` 不为 0 说明回调丢帧**——丢帧会直接毁掉流式识别，所以这是启用流式的前置检查项 |
| `说话` | 累计判定为语音的时长 |
| `起` / `段` / `自动收尾` | 分别对应语音起始、分段边界（静音超 600ms）、自动收尾（静音超 1200ms）的触发次数 |
| `尾段` | 结束时尚未被分界覆盖的尾部语音时长。边说边送需要补发这一段 |
| `静音游程` | 每次静音持续时长（最多记 24 个），是**选阈值的数据来源**，不靠猜 |

阈值与批处理 VAD 同口径：`vadThreshold / 32768.0`。当前为**影子模式**——只记录，不触发任何停止或分段行为；`分界 600ms / 收尾 1200ms` 是待验证的观察参数，不是用户设置。

`SpeechSegmenter` 是纯逻辑（可注入帧序列、可单测），投产后同一个原语会同时驱动「静音自动停止」与「边说边送的分段边界」——两者本质都是"静音持续超过某个时长"。

## 配置字段（settings.json）

| 键 | 说明 |
|---|---|
| `baseUrl` | 服务根地址 |
| `endpoint` | 转写端点 |
| `api` | `sse` 或 `transcriptions` |
| `model` | 模型名 |
| `apiKeyProtected` | 经过 DPAPI CurrentUser 加密后的 API Key |
| `autoInsert` | 是否跳过审阅直接写入 |
| `language` | 语言代码；留空表示自动 |
| `hotkey` | `RControl` / `RMenu` / `RShift` / `Capital` |
| `hotkeyMode` | `hold` 或 `toggle` |
| `hotwords` | 逗号分隔热词 |
| `clipboardThreshold` | 长文本转剪贴板注入阈值 |
| `requestTimeoutMs` | 请求超时（毫秒） |
| `maxRecordMs` | 单次录音上限（毫秒，0=不限制） |
| `enableVad` | 是否去除首尾静音 |
| `vadThreshold` | VAD RMS 阈值（默认 450） |
| `vadPaddingMs` | VAD 前后保留毫秒数（默认 180） |
| `aiOrganize` | 是否调用生成式整理，默认关闭 |
| `organizerBaseUrl` | 整理服务根地址 |
| `organizerEndpoint` | 整理 endpoint，默认 `/chat/completions` |
| `organizerModel` | 整理模型 |
| `organizerApiKeyProtected` | DPAPI 加密的整理 API Key |

## 构建

在 Windows 侧运行：

```cmd
build.cmd
```

构建脚本现在会额外引用 `System.Security.dll`，并编译：

- `App.cs`
- `VoxleapCore.cs`
- `SettingsCore.cs`
- `SettingsForm.cs`
- `AiProviders.cs`
- `LatencyTrace.cs`
- `SpeechSegmenter.cs`

## 验证

可在不联网的前提下先做编译验证；仓内还提供了 `tests/app-core-test/SettingsCoreTest.cs` 作为纯逻辑入口，用于验证：

- 明文 `apiKey` 读取兼容；
- `apiKeyProtected` 序列化不再回写明文；
- 热键与触发方式合法值校验；
- HTTPS 与本机回环地址校验；
- `401/404/422/429` 诊断文案；
- 静音测试 WAV 格式。

`tests/app-core-test/LatencyTraceTest.cs` 覆盖延迟分解的纯逻辑（注入假时钟，确定性复现）：分段计算、`Mark` 只记首次、缺段回退、数据不足保护、判定与上行速率、以及**隐私白名单**（未列入的键不得进日志）。

```cmd
csc /out:LatencyTraceTest.exe LatencyTraceTest.cs ..\..\src\app-v0\LatencyTrace.cs
LatencyTraceTest.exe
```

`tests/app-core-test/SpeechSegmenterTest.cs` 覆盖实时分段的时序语义（喂入构造好的帧序列，无音频依赖）：全静音不误触发、短促噪声（低于最短语音门槛）不触发收尾、同一次静音游程只报一次分界/收尾、分界后重新说话算新段起点、阈值取等号算语音、收尾阈值小于分界阈值时被钳制、`Finish` 补记末尾游程与尾段、以及 waveIn 丢帧的字节核对。

```cmd
csc /out:SpeechSegmenterTest.exe SpeechSegmenterTest.cs ..\..\src\app-v0\SpeechSegmenter.cs
SpeechSegmenterTest.exe
```

## 已知限制

- 实时字幕来自 SSE 增量 `delta` 的累积显示，不代表完整逐词最终结果；
- StepFun 仍是“整段上传后 SSE 回放”，并非真正的音频分片流式；ASR 已通过 `IStreamingAsrProvider` 隔离，后续可替换为 WebSocket/分片实现；
- 当前 VAD 是保守的能量门控，不是神经网络 VAD；它只压缩首尾和中间长静音，不会从重叠键盘声中分离人声；
- 浏览器/Electron 密码框并非都能被 Win32 `ES_PASSWORD` 检出；
- “OpenAI 兼容转写（高级，未验证）”仅作为高级手动配置入口，兼容性未实测。
