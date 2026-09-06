# Phase 0 / P0-01：WinUI 3 样机前置检查

## 目标

建立一个最小 Windows 客户端样机，验证三件事：

1. 托盘常驻与单实例生命周期；
2. 可配置全局快捷键；
3. 不激活目标窗口的透明悬浮层。

样机只验证窗口、热键和生命周期，不接入真实 ASR、文本注入或生成式整理。

## 验收条件

- [ ] Windows 10/11 上可启动并驻留托盘；
- [ ] 重复启动不会产生第二个实例；
- [ ] 热键按下时显示悬浮层，松开时隐藏；
- [ ] 悬浮层出现和隐藏不改变目标输入框焦点；
- [ ] 关闭样机后托盘、热键和窗口句柄都被释放；
- [ ] 在至少两种 DPI 和双显示器环境记录结果。

## 当前环境核查（2026-09-03）

- 当前终端为 WSL2；
- Windows 侧可见 .NET Host/Runtime 7.0.0；
- Windows 侧未发现已安装的 .NET SDK；
- 当前可见路径中未发现 Visual Studio Installer、Windows SDK 或 `makeappx.exe`。

因此目前只能完成前置检查，不能把 P0-01 标记为“已构建”或“已运行”。

## Windows 侧恢复条件

环境准备脚本（管理员 PowerShell 运行，winget 安装 .NET 8 SDK；Windows App SDK/Windows SDK 由 NuGet 在构建时拉取，无需先装 Visual Studio）：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\setup-windows-dev.ps1
```

脚本会自动完成以下官方验证并输出结果，作为环境就绪的证据：

```powershell
dotnet --info
dotnet --list-sdks
where.exe dotnet
```

脚本通过后，再创建并构建 `src/VoxLeap.App`。构建日志和实际 Windows 截图是 P0-01 的有效证据；仅能解析项目文件不算通过。

## 未执行项

- 尚未创建 WinUI 3 客户端工程；
- 尚未验证托盘、全局热键、无激活窗口或 DPI 行为；
- 浏览器 `prototype/` 仍只用于交互演示，不替代 Windows 样机；
- `scripts/setup-windows-dev.ps1` 尚未在 Windows 侧实际运行（ASCII-only 编写，规避 PowerShell 5.1 无 BOM UTF-8 乱码问题，但未验证）。
