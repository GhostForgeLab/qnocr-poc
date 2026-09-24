# qnocr-poc V0.2

独立的千牛视觉采集实验：发现千牛窗口 → 捕获完整窗口 → 固定坐标裁剪 → 保存 PNG。
使用 C# / .NET 8 / Windows；未接入或修改其他项目。

## 运行

在 Windows 交互桌面安装 .NET 8 SDK，展开 1638×1100 的千牛接待中心，在项目根目录运行：

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

V0.2 默认 `dotnet run` 在捕获成功后继续裁剪。也可以直接裁剪已有完整截图，无需千牛运行：

```powershell
dotnet run -- --extract Output/qianniu_20260924_221542.png
```

## 固定区域布局

`Models/QianniuLayout.cs` 定义四个 Rectangle；`Capture/QianniuRegionExtractor.cs`
从完整 PNG 逐像素裁剪，不缩放、不做 OCR、AI 或视觉检测。

原任务的 2226×1253 是工作台尺寸。重新捕获接待中心发现实际为 1638×1100，
经用户明确选择，V0.2 改按该实际尺寸人工校准。坐标相对 PNG 左上角 (0, 0)，单位为物理像素；
右/下边界不包含在区域内，即 [X, X+宽) × [Y, Y+高)。

| 属性 | X | Y | 宽 | 高 | 输出 |
| --- | ---: | ---: | ---: | ---: | --- |
| BuyerListRegion | 80 | 120 | 300 | 980 | buyer_list_region.png |
| ChatRegion | 380 | 181 | 654 | 537 | chat_region.png |
| ProductRegion | 1034 | 120 | 604 | 924 | product_region.png |
| InputRegion | 380 | 718 | 654 | 382 | input_region.png |

买家列表包含搜索、列表筛选和会话列表，不含蓝色导航栏。
聊天区域包含当前可见的消息历史，不含聊天对象标题和输入工具栏。
商品区域包含右侧标签页和商品/订单面板，不含底部客服工具栏；只截当前显示内容。
输入区域包含编辑区、输入工具栏以及关闭/发送按钮；不触发任何交互。

四张 PNG 写入当前目录的 `Output/Regions/`，再次运行会替换同名区域图，原图不变。
提取前检查 PNG 格式、准确尺寸、全部区域边界以及源文件/输出路径冲突。
尺寸不匹配或坐标越界会失败，且不写入任何新区域图；上一次的区域图可能仍在目录中，
应以本次成功日志判断输出是否更新。布局变动或 DPI 变化后需人工重新校准，程序不自动识别页面。

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
退出码：0 捕获和裁剪成功（或仅提取/列举成功），1 捕获或裁剪失败，2 参数错误。

保留运行日志：

```powershell
dotnet run 2>&1 | Tee-Object -FilePath Output/run.log
```

逐张人工确认：聊天区域边界、买家消息文字、商品卡片、CEF 页面内容、黑屏/空白。
当当前页面无聊天/商品卡片时，必须记为未验证。
截图和原始日志保留在本地 Output，不进入 Git。
V0.1 实测记录见 [VALIDATION.md](VALIDATION.md)，V0.2 见 [VALIDATION_V02.md](VALIDATION_V02.md)。

范围止于 V0.2 固定区域裁剪；没有 OCR、AI、视觉检测、知识库、自动回复、自动发送、DLL 注入、
进程内存读取、UI Automation 或 qnbot 集成。
