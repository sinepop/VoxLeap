# 声跃 VoxLeap TODO（审查遗留）

> 来源：2026-09-06 三模型只读审查集群（deepseek-v4-flash-0731、step-3.7-flash、glm-5.3-flash 均已完成）。
> 结论：P0 = 0。归档见 `../../docs/reviews/REVIEW-*-voxleap-20260906.md`（即 ~/ai-workspace/docs/reviews/）。

## 已修复（2026-09-06，随最后一次部署上线）

- [x] P1 FlatTabs 键盘焦点无可见指示 → `OnPaint` 在 `Focused` 时绘制焦点框。
- [x] P1 FlatTabs 首次 OnPaint 前命中测试失效 → `HitTest` 按需预热矩形，`OnResize` 清空重算。
- [x] P2 连接测试期间取消按钮（含 Esc）未禁用，可能关窗丢回调或并发多个测试 → 测试期间禁用，完成回调恢复。

## 已修复（2026-09-07，对抗式审查后）

- [x] P1 注入目标只校验 IsWindow，未校验窗口身份：录音→注入窗口期内窗口销毁后句柄复用 → 文本写入未知窗口 → 记录 `_targetPid`，注入前 PID 复核不一致即拒绝（App.cs）。
- [x] P1 keyup 丢失（Alt+Tab/UAC/安全桌面/RDP）录音悬挂，maxRecordMs=0 时无上限 → 10 分钟安全闸 + `HoldKeyStillDown` 键态检测自动收尾（App.cs）。
- [x] P1 兜底剪贴板 SetText 失败仍 SendCtrlV 粘贴旧内容 → SetText 返回 bool，失败禁止粘贴并如实提示（App.cs）。
- [x] P1 OnHoldStart 若浮层/GDI 抛异常只回 Idle 不停麦 → catch 补 `Recorder.Stop()` + `HideOverlay()`（App.cs）。
- [x] P2 钩子安装失败（如杀软拦截）仅日志 → toast 提示一次（App.cs）。
- [x] P2 剪贴板多恢复线程无代次 → 静态代次号仅最新代恢复（App.cs）。
- [x] P2 自动收尾后按住热键 auto-repeat 白录第二轮 → `_suppressHotkeyUntil` 抑制 1s（App.cs）。
- [x] P2 ExtractPcm 拼接 size 可能 int 溢生死循环 → long 计算 + 钳制（VoxleapCore.cs）。
- [x] P2 FlatTabs 缺 Home/End → 补齐（SettingsForm.cs）。
- [x] P2 测试连接期间 X/Alt+F4 可关窗丢回调 → `OnFormClosing` 拦测试期关闭（SettingsForm.cs）。

## 未修（P2，不阻塞）

- [x] FlatTabs 键盘仅 Left/Right，补 Home/End 语义（deepseek → 2026-09-07 已补 Home/End；Up/Down 非原生 TabControl 语义，不补）。
- [ ] 状态文本缺可访问性 live region（`AccessibleLiveSetting` / `RaiseAutomationEvent`），读屏不播报校验与测试结果（deepseek）。
- [ ] footer `_statusLabel` 多行换行时在最小宽度 680 下可能挤压按钮列，需核对裁剪（deepseek）。
- [ ] `ShowSettings` 的 BringToFront 分支缺「设置窗口已打开」日志，与新建分支不一致（deepseek）。
- [ ] `RestoreStepFunPreset` 在 sse 态恢复地址时不重置 `_showApiKeyBox.Checked`（有意保留 Key，补测试覆盖确认语义）（deepseek）。
- [ ] `AddAlignedRow` 对 Margin.Top 的条件逻辑导致辅助说明行垂直间距不统一（7 vs 3+7）（step）。
- [ ] `HairlineTopTable` 改在 `OnPaintBackground` 绘制或加 `AllPaintingInWmPaint`，降低潜在闪烁（step）。
- [ ] FlatTabs 高度改为按字体度量动态计算，改善 150% DPI 下触控友好度（step）。

### glm-5.3-flash（启动/单实例/IPC）

- [ ] P1 `--settings` 投递竞态：二次启动落在「托盘已持 mutex 但命名事件尚未创建」窗口时 OpenExisting 失败被静默吞掉，日志仍记请求已发 → `RequestTraySettings` 加重试投递（约 3s）并记录投递结果。当前快捷方式已无 --settings，无实际触发路径，故未随本次部署上线。
- [ ] P2 信号消费即丢：busy 时 `WaitOne(0)` 已复位事件，请求方需自行重试 → 可改为拒绝时不消费或请求方重试。
- [ ] P2 `RequestTraySettings` 的 `catch { }` 无日志，无法区分「没带参数/事件不存在/Set 失败」。
- [ ] P2 `Log.Write` 跨进程并发：两进程同时 AppendAllText 同一日志文件，锁只保护本进程 → 可改命名 mutex 或容忍偶发交错。
- [ ] P2 同名事件已存在时 `new EventWaitHandle(...)` 直接打开现有句柄（构造参数被忽略）的语义需注释说明。
- [ ] P2 `Log.Write("退出")` 在 try 内，前置清理抛异常时该日志被跳过 → 移到 finally 或 try 外。
- [ ] P2 托盘进程无前台激活权限时 `Show()+Activate()` 可能静默失败 → 可补 AttachThreadInput 技巧或提示。
- [ ] P2 二次启动路径 mutex `initiallyOwned: true` 语义冗余（无害）→ 传 false。

## 既有项（非本次引入）

- [ ] 应用为 DPI-unaware，125%/150% 缩放下由系统位图拉伸（浮层同样受影响）；加 DPI 感知会改变浮层坐标计算，需单独立项评估。
- [ ] 9-05 结转实机矩阵：四种热键、toggle、Esc 取消、delta 字幕。
