# V0.1 实测记录

日期：2026-09-24（Asia/Shanghai）。范围仅为千牛窗口发现与 PNG 捕获。

## 环境与构建

- Windows 10，版本 10.0.19045，x64。
- 项目目标：net8.0-windows；实际 SDK 8.0.425，运行时 .NET 8.0.31。
- 本地 SDK 位于 .tools/dotnet；运行前设置终端 DOTNET_ROOT 和 PATH，步骤见 README。
- dotnet build --no-restore -warnaserror：通过，0 警告，0 错误。
- 构建日志：Output/build.log。
- 沙箱初次运行无法枚举桌面窗口；允许访问当前用户桌面后，dotnet run 成功。

## 成功样本

- 进程：AliWorkbench.exe，PID 39556（运行时发现，没有硬编码）。
- MainWindowHandle / 捕获 HWND：0xF0AE0。
- 目标：千牛工作台；完整窗口 2226 × 1253，左上角 (493, 56)。
- 方法：Win32 PrintWindow，PW_RENDERFULLCONTENT=0x2；未使用 WGC。
- 首次成功截图：Output/qianniu_20260924_220724.png。
- 最终代码复测截图：Output/qianniu_20260924_220928.png。
- 最终运行日志：Output/run.log；退出码 0。
- 最终截图采样：近黑像素 0.12%，未绘制洋红像素 0.00%。

运行日志摘录：

```text
[QN OCR POC]
Runtime: .NET 8.0.31
Searching Qianniu...
Process: AliWorkbench.exe
PID: 39556
MainWindowHandle: 0xF0AE0
HWND: 0xF0AE0
Bounds: 2226x1253 (X=493, Y=56)
Method: Win32 PrintWindow (PW_RENDERFULLCONTENT = 0x2)
Capture Success
Saved:
Output\qianniu_20260924_220928.png
```

## 人工图像检查与边界

首次和最终复测截图均已目视检查：工作台导航、网页横幅、数据卡片、右侧面板和中文文字可见，
未出现整图黑屏，未见明显大片缺失；当前工作台的网页/CEF 内容表观正常。
本次样本支持“此环境下工作台页面能被 PrintWindow 捕获”，不代表所有 CEF/GPU 页面都通过。
复测截图同时通过程序像素检查和人工图像检查。

| 验收项 | 结果 |
| --- | --- |
| 发现运行中的千牛进程/窗口 | 成功 |
| 完整窗口保存 PNG | 成功 |
| 工作台网页/CEF 内容 | 两张截图均目视正常 |
| 整图黑屏 | 两张均未发现，复测亦通过像素检查 |
| 聊天区域完整显示 | 未验证：接待中心最小化 |
| 买家消息文字可见 | 未验证 |
| 商品卡片可见 | 未验证 |
| 聊天窗口 CEF/GPU 内容 | 未验证 |

已发现另一个标题含“接待中心”的窗口 HWND 0x10098A，但其 Minimized=True，
边界为 (−32000, −32000)，大小 199 × 34。程序拒绝把最小化标题条当作有效聊天截图。
已请求用户手动还原接待中心并打开包含买家消息/商品卡片的会话；未自动操作窗口。
因此 V0.1 基础捕获链路通过，聊天场景可行性仍待该场景的截图验证，不能据此宣布 OCR 路线可行。

## 错误路径检查

- dotnet run --no-build -- --invalid：退出码 2，显示参数用法。
- dotnet run --no-build -- --hwnd 0x1：退出码 1，未捕获其他窗口。
- 日志：Output/invalid-argument.log、Output/invalid-window.log。

源码、说明和此验证记录进入 Git；截图、原始日志与本地 SDK 不进入 Git。
未实现 OCR、裁剪、AI、知识库、回复/发送、DLL 注入、内存读取、UI Automation 或 qnbot 集成。
