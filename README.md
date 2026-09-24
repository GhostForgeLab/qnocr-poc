# qnocr-poc V0.1

独立的千牛视觉采集实验：发现千牛窗口 → 捕获完整窗口 → 保存 PNG。
使用 C# / .NET 8 / Windows；未接入或修改其他项目。

## 运行

在 Windows 交互桌面安装 .NET 8 SDK，展开千牛窗口，在项目根目录运行：

```powershell
dotnet run
```

本次验证机器原先只有 .NET 10 SDK，因此另下载了微软官方 .NET 8.0.425 SDK 到
本项目 `.tools/dotnet`，并按微软发布元数据核对 SHA512。使用本地 SDK 的终端先执行：

```powershell
$env:DOTNET_ROOT = Join-Path $PWD '.tools\dotnet'
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
$env:DOTNET_CLI_HOME = Join-Path $PWD '.tools\cli-home'
dotnet run
```

`.tools` 不进入 Git；其他机器需要自行安装 .NET 8 SDK。
必须在千牛所在用户的交互桌面运行；受限沙箱或服务会话可能无法枚举窗口。
本次沙箱内 EnumWindows 失败，在允许访问用户桌面的运行环境中成功。

## 窗口发现与选择

- 按 `AliWorkbench` / `AliRender` 进程名查找，不硬编码 PID 或 HWND。
- 记录进程名、PID、Process.MainWindowHandle；使用 EnumWindows 枚举其顶层窗口。
- 区分进程 MainWindowHandle 与实际捕获 HWND，因为一个进程可能有多个窗口。
- 记录窗口标题、物理像素边界、可见/最小化/DWM cloaked 状态。
- 自动选择可见、非最小化、非 cloaked 的窗口，优先标题包含“接待中心”的窗口，再按面积排序。
- 默认显示可见窗口；`dotnet run -- --list` 显示全部匹配窗口，不截图。
- 多店铺/多窗口时，可用枚举结果指定：`dotnet run -- --hwnd 0x实际句柄`。
  句柄仍须属于本次发现的千牛进程。

## 捕获方案与局限

本版采用用户允许的 Windows API 方案：`PrintWindow(hwnd, hdc, 0x2)`
（PW_RENDERFULLCONTENT），保存整个窗口，无桌面区域截取降级。
使用 PerMonitorV2 DPI 感知，Bitmap/Graphics/HDC 均有对应释放操作。
拒绝最小化窗口，不自动恢复、不置前、不点击；PrintWindow 超过 15 秒则返回失败并退出程序。

WGC 已评估，本版未实现或实测：其接入还需 WinRT/COM、Direct3D 设备、帧池和 GPU 帧读回，
为控制 V0.1 范围先验证 PrintWindow。官方资料：
[WGC CreateForWindow](https://learn.microsoft.com/en-us/windows/win32/api/windows.graphics.capture.interop/nf-windows-graphics-capture-interop-igraphicscaptureiteminterop-createforwindow)、
[Direct3D11CaptureFramePool](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.direct3d11captureframepool)、
[PrintWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-printwindow)。

PrintWindow 是同步调用，依赖目标应用绘制；API 成功不等于 CEF/GPU 内容完整。
程序对采样像素进行近黑、未绘制洋红底色和单色检查；明显异常时保存诊断 PNG 但返回失败。
这些检查仅发现明显空白，不能识别局部黑屏、缺失聊天或缺失商品卡片；仍须目视检查。
`Capture Success` 仅表示 PNG 已保存且通过基础像素检查。
程序不会把像素检查冒充 CEF/聊天语义验证。

## 输出与验收

截图：当前工作目录的 `Output/qianniu_yyyyMMdd_HHmmss.png`。
同一秒文件名冲突时增加毫秒和唯一后缀，避免覆盖已有样本。
退出码：0 成功（或仅列举），1 捕获失败，2 参数错误。

保留运行日志：

```powershell
dotnet run 2>&1 | Tee-Object -FilePath Output/run.log
```

逐张人工确认：聊天区域边界、买家消息文字、商品卡片、CEF 页面内容、黑屏/空白。
当当前页面无聊天/商品卡片时，必须记为未验证。
截图和原始日志保留在本地 Output，不进入 Git。
实测记录见 [VALIDATION.md](VALIDATION.md)。

范围止于 V0.1；没有 OCR、AI、知识库、自动回复、自动发送、DLL 注入、进程内存读取、
UI Automation、qnbot 集成或截图区域裁剪。
