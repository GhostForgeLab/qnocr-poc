# V0.2 固定区域裁剪验证

日期：2026-09-24（Asia/Shanghai）。基于 V0.1 commit
`7d9c62f84c3d722e18a4bca77c96190f41e62029`，仅新增区域模型与 PNG 裁剪。

## 布局依据

用户手动展开接待中心后，重新捕获的实际窗口为 **1638×1100**，
而 V0.1 的 2226×1253 是另一个工作台窗口。经用户明确确认，采用实际 1638×1100 校准固定坐标。
没有缩放原图、自动调整窗口、OCR、AI 或视觉检测。

人工校准参考：`Output/qianniu_20260924_221542.png`。
最终完整流程源图：`Output/qianniu_20260924_221811.png`。
进程 AliWorkbench.exe，PID 39556，实际 HWND 0x10098A，窗口左上角 (708, 115)。
桌面位置不参与裁剪计算，坐标只相对完整 PNG 左上角。

| 区域 | X | Y | 宽 | 高 | 人工检查 |
| --- | ---: | ---: | ---: | ---: | --- |
| chat_region.png | 380 | 181 | 654 | 537 | 当前可见买家/客服消息、时间与表情正常，不含输入区 |
| buyer_list_region.png | 80 | 120 | 300 | 980 | 搜索、筛选与两个会话条目可见 |
| product_region.png | 1034 | 120 | 604 | 924 | 右侧订单、商品缩略图和价格等内容可见 |
| input_region.png | 380 | 718 | 654 | 382 | 工具栏、空白编辑区、关闭与发送按钮可见 |

四张区域图已逐张目视检查，没有发现黑屏或裁剪导致的缩放。
输入区本来为空，不表示捕获失败；本版没有输入或发送任何消息。
`product_region` 定义为完整右侧商品/订单面板，并非单张商品卡片检测。

## 运行与检查

使用项目本地 .NET SDK 8.0.425、运行时 .NET 8.0.31：

- `dotnet build --no-restore -warnaserror`：0 警告、0 错误。
- `dotnet run --no-build -- --extract Output/qianniu_20260924_221542.png`：成功，退出码 0。
- `dotnet run`：接待中心捕获 → 四区域裁剪均成功，退出码 0。
- 四张区域图分别核对 1320、1121、2072、960 个像素采样点（含边缘）；
  均与最终源图对应坐标的 ARGB 完全一致，输出尺寸符合模型。
- 输入旧的 2226×1253 工作台截图：返回退出码 1；先前四张区域图的 SHA256 均未改变。
- 将最后一个区域设为会导致整数溢出的超大矩形：捕获 ArgumentOutOfRangeException，
  没有创建输出目录或文件，验证所有区域在写入前一起检查。
- 再次成功提取会替换四张同名输出；源图 SHA256 保持不变。

日志：`Output/v02-build.log`、`Output/v02-extract.log`、`Output/v02-run.log`、
`Output/v02-wrong-size.log`、`Output/v02-verification.log`。

完整流程关键输出：

```text
Capture Success
Saved:
Output\qianniu_20260924_221811.png
Layout: fixed 1638x1100 reception center; manually calibrated pixel coordinates, no scaling.
Region Extraction Success
Saved: D:\CodexWork\qnocr-poc\Output\Regions\chat_region.png
Saved: D:\CodexWork\qnocr-poc\Output\Regions\buyer_list_region.png
Saved: D:\CodexWork\qnocr-poc\Output\Regions\product_region.png
Saved: D:\CodexWork\qnocr-poc\Output\Regions\input_region.png
```

## 适用边界

固定坐标仅验证当前尺寸及布局。改变面板宽度、DPI、页面或窗口尺寸后需人工重新校准；
即使尺寸相同，程序也不会判断截图是否确为接待中心。尺寸不匹配会直接失败，不做自动适配。
捕获只包含当前可见内容，不滚动、不展开订单、不读取被遮蔽或未显示的历史内容。
程序日志中的 CEF/Chat 未验证字段保留 V0.1 的自动检查含义，人工图像检查结论记录于本文。
截图和日志继续仅保留在本地，不进入 Git。工作范围止于 V0.2，不接入 OCR 或 AI。
