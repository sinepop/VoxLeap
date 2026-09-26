# INDEX.md — 声跃 VoxLeap · 文件索引

> **AI 进入本项目先读此文件定位，再精准读目标文件，禁止盲扫。**
> 新增文件时在此补一行职责说明。项目一句话：Windows 系统级「按住说话」语音输入工具；当前可交付的是**个人自用运行版 v0.3.1**（`src/app-v0/`，边说边出字），商用 MVP 仍在 Phase 0 文档验证。

## 状态看板（每次会话先看这三个）

| 文件 | 作用 |
|---|---|
| `TODO.md` | 待办清单 |
| `PROGRESS.md` | 已完成批次记录（决策落盘处，已有结论不重议） |
| `AGENTS.md` | 项目内 agent 约定 |

对外首页是 `README.md`（含下载入口）；运行版使用与实现说明在 `src/app-v0/README.md`。

## 当前方向（已拍板，不重议）

视觉：Carbon Signal + Inkline HUD + Redline Studio 融合方向，重构空闲/监听/审阅三态（v0.4 视觉已否决）。


## 外部

- 决策沉淀：长期设计决策与避坑点录入本地知识库（Obsidian）「声跃 VoxLeap-设计决策」笔记，按需同步。

## 文件索引（自动区）

<!-- auto:index:start -->
<!-- 本区由 build_index.py 生成（结构勿手改，会被重建）；「待补」职责收尾时顺手改，脚本会继承。 -->

| 路径 | 职责 |
|---|---|
| `assets/brand/` (4 文件) | 品牌 Logo 波形主资产（母版 SVG + 深色版 + manifest + 溯源说明） |
| `docs/` (9 文件) | 文档目录（01-PRD 至 09-交接归档） |
| `prototype/` (4 文件) | 可运行浏览器原型（index.html + app.js） |
| `scripts/` (1 文件) | 工具脚本 |
| `src/app-v0/` (13 文件) | **个人自用运行版源码**：WinForms 单文件程序 + 构建脚本 + 使用说明 |
| `tests/app-core-test/` (6 文件) | 应用核心单元测试（C#，六个可独立运行的入口） |
| `tests/asr-eval/` (4 文件) | ASR 基准评测脚本与评分 |
| `tests/fixtures/` (1 文件) | 评测语料（ASR 输入输出） |
| `AGENTS.md` | 项目内 agent 约定 |
| `PROGRESS.md` | 已完成批次记录（决策落盘处，已有结论不重议） |
| `README.md` | 对外首页：现状、下载入口、产品原则、文档索引 |
| `TODO.md` | 待办清单 |
| `docs/01-PRD.md` | 产品需求文档（PRD） |
| `docs/02-UX与视觉规范.md` | UX 与视觉规范 |
| `docs/03-技术方案.md` | 技术方案 |
| `docs/04-MVP实施计划.md` | MVP 实施计划 |
| `docs/05-Phase0-P0-01-WinUI3样机.md` | WinUI3 样机 |
| `docs/06-Phase0-ASR基准测试.md` | ASR 基准测试 |
| `docs/07-Phase0-P0-09-访谈脚本.md` | 用户访谈脚本 |
| `docs/08-当前设计对抗式审查.md` | 当前设计对抗式审查 |
| `docs/09-运行版与OpenLess波形交接-2026-09-03.md` | 运行版与波形交接归档 |
| `prototype/app.js` | 浏览器原型交互脚本 |
| `prototype/index.html` | 浏览器原型入口 |
| `prototype/refinement-v4.css` | 原型 v0.4 样式（脉冲方块/极光） |
| `prototype/styles.css` | 原型基础样式 |
| `scripts/setup-windows-dev.ps1` | Windows 开发环境初始化脚本 |
| `src/app-v0/AiProviders.cs` | ASR 与整理 provider 工厂（StepFun SSE / OpenAI 兼容）与协议实现 |
| `src/app-v0/App.cs` | 单文件主程序：录音、分段调度、玻璃浮层（声纹+字幕）、托盘、审阅与注入 |
| `src/app-v0/LatencyTrace.cs` | 延迟埋点与日志格式（分段账本、松手后耗时、判定口径） |
| `src/app-v0/LiveCaptionAnim.cs` | 录音期字幕动画纯算术（宽度缓动、按说话速率逐字铺开、最新字淡入）※ 可离线单测 |
| `src/app-v0/README.md` | 运行版使用与实现说明（设置项、流式分段、字幕动画、验证方式） |
| `src/app-v0/SettingsCore.cs` | 设置读写与校验（DPAPI 加密、原子替换、旧版明文迁移） |
| `src/app-v0/SettingsForm.cs` | 设置窗口（服务、热键、VAD、AI 整理、超时） |
| `src/app-v0/SpeechSegmenter.cs` | 语音分段器：静音分界、软/硬上限兜底、累计说话时长 |
| `src/app-v0/StreamingAsr.cs` | 边说边送：分段账本、派发与"任一段失败即整段回退" |
| `src/app-v0/VoxleapCore.cs` | 核心纯逻辑（整理安全门、注入策略、诊断文案） |
| `src/app-v0/build.cmd` | 零安装构建：调用系统自带 `csc.exe` 输出到 `%LOCALAPPDATA%\VoxLeap` |
| `src/app-v0/settings.template.json` | 配置模板（不含密钥，随发布包分发） |
| `src/app-v0/voxleap.ico` | 托盘与快捷方式图标（品牌五方块） |
| `tests/app-core-test/LatencyTraceTest.cs` | 延迟账本纯逻辑测试 |
| `tests/app-core-test/LiveCaptionAnimTest.cs` | 字幕动画纯逻辑测试（宽度上限、逐字铺开、淡入、滞后有界） |
| `tests/app-core-test/SettingsCoreTest.cs` | 设置读写与校验测试 |
| `tests/app-core-test/SpeechSegmenterTest.cs` | 分段器测试（静音分界、软硬上限、说话时长） |
| `tests/app-core-test/StreamingAsrTest.cs` | 流式分段账本测试（落位、拼接、失败回退） |
| `tests/app-core-test/VoxleapCoreTest.cs` | 核心纯逻辑测试（安全门、注入策略） |
<!-- auto:index:end -->
