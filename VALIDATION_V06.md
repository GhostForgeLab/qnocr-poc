# V0.6 千牛布局定位增强验证

日期：2026-09-24。基于 V0.5 `5dbc47c15d430abf8364e526d869740d575c7eda`。

## 实现

- 新增 `Layout/QianniuLayoutResolver.cs` 和 `Models/ResolvedLayout.cs`。
- 四个区域分别输出 Bounds、Source、Reason，优先级 UIA → Visual → RatioFallback。
- `UiaGeometryProbe` 只获取布局节点几何；`VisualLayoutLocator` 使用浅色面板背景和长分隔线，
  不通过OCR或AI推断区域。`QianniuLayoutRatio` 保留为搜索先验及最终回退。
- 解析器接入默认单次流程、离线提取和监测；聊天区域位置/尺寸变化会重建监测基线。
- 新命令 `--layout` 只截图、定位和裁剪；`--uia-geometry` 只输出几何诊断。

## UIA读取边界

实际请求的UIA属性只有 ControlType、BoundingRectangle、IsOffscreen。
类型限定 Pane / Document / Window / Group，非布局控件不继续遍历其子树。
没有请求 Name、AutomationId、ValuePattern、TextPattern 或任何控件操作；
没有通过UIA读取昵称、消息正文或输入框内容。
UIA节点ID是本地遍历编号，ControlType字符串来自内置类型名称，不是界面文本。

探测限定启动时发现的千牛HWND及其布局子树，校验所属PID和截图尺寸，
将UIA屏幕物理坐标减去GetWindowRect原点；探测期间窗口移动/缩放或所有者变化时拒绝该次结果。
采用MTA后台线程；限制512个节点、10层、约1200ms遍历预算，调用方最多等待1800ms。
超时后保留最多一个未完成探测线程，后续探测跳过并回退，不无限叠加线程。
预算截断时已取得的有效节点仍可参与几何匹配；Complete=false及截断原因保留在诊断中。

当前接待中心仅暴露了有限的右侧CEF节点。实测在预算内取得5–6个布局节点，
不足以用UIA确定全部区域，但右侧Document边界经过去重和几何校验后可用。
没有把这次有限遍历描述为完整树扫描或UIA聊天面板定位成功。

## 构建与自动检查

Windows x64，.NET SDK8.0.425 / runtime8.0.31。
通过Windows Desktop/WPF框架引用使用UIAutomationClient，没有新增第三方UIA包。
主项目 `dotnet restore --locked-mode` 成功；主程序与验证项目严格编译 **0警告、0错误**。
日志：`Output/v06-restore.log`、`Output/v06-build.log`。

检查项目 **58项全部通过**（V0.4/V0.5的42项回归 + V0.6的16项）。
V0.6覆盖：UIA优先于视觉、部分UIA与视觉混用、有限遍历结果的透明记录、CEF重复节点、
相近候选歧义、过期尺寸、非布局类型/越界节点拒绝、视觉分隔线、空白/深色比例回退，
以及窗口和聊天裁剪尺寸相同但区域位置改变时的监测基线重建。

UIA优先级检查使用显式注入的几何记录，未模拟成真实UIA读取结果。
视觉检查使用具有已知背景边界/分隔线的程序生成图。
实际监测循环回放证明布局平移时 BaselineResets=1、OCR calls=0。
日志：`Output/v06-checks.log`。

## 实际窗口结果

当前接待中心：1638×949；最终完整截图 `Output/qianniu_20260924_232025.png`。

| 区域 | 来源 | X | Y | 宽 | 高 |
| --- | --- | ---: | ---: | ---: | ---: |
| BuyerList | Visual | 80 | 120 | 300 | 829 |
| Chat | Visual | 380 | 181 | 648 | 447 |
| Product | UIA Document | 1035 | 120 | 600 | 827 |
| Input | Visual | 380 | 628 | 648 | 321 |

原比例聊天框为 (380,156,654,463)。在同一截图上，新框上沿从156修正到181，
去掉约25px标题区；下沿定位到输入区分隔线附近，并排除右侧滚动条边缘。
四张图已目视检查，聊天图不再含被截断的对象标题栏，正文/时间戳仍可见，
输入图包含工具栏与按钮，商品图包含当前客服页的右侧自然面板。
Product表示面板，不表示对单张商品卡片的识别；面板内原有提示浮层不会被删除。

同一源图对照与区域图：

- `Output/ValidationV06/ratio_chat_same_frame.png`：V0.5比例结果。
- `Output/ValidationV06/live/chat_region.png`：V0.6结果。
- `Output/ValidationV06/live/`：四个区域图。
- 默认最新输出仍在 `Output/Regions/`。

最终布局JSON：
`Output/Layout/layout_20260924_232026_864_60488115e0ac44eb9951443937c6bcfd.json`。
它记录每区来源/坐标/原因、UIA节点类型与几何、预算状态、视觉长边界覆盖率及耗时，
不包含UIA消息文本。

## 离线回归与回退

历史1638×949订单页与1638×1100截图均通过视觉分隔线定位：

| 历史样本 | Chat X,Y,W,H | 来源 |
| --- | --- | --- |
| `qianniu_20260924_225736.png`（949高，订单页） | 380,181,655,447 | Visual |
| `qianniu_20260924_223020.png`（1100高） | 380,182,655,537 | Visual |

区域图保留于 `Output/ValidationV06/regions_949/`、`regions_1100/`。
这两张已有图片没有实时HWND，因此明确跳过UIA；日志 `v06-offline-949.log`、`v06-offline-1100.log`。
当前客服页的离线图片缺少右侧底栏特征，因此该图片的Product标为RatioFallback，
其余三块保持Visual；对应 `Output/v06-offline-customer.log`。
同一客服页在实时模式获得UIA Document后，Product使用UIA边界，体现逐区域优先级。

## 流程与性能

- `dotnet run --no-build -- --layout`：截图、布局诊断、四区裁剪成功，不执行OCR。
- `dotnet run --no-build`：布局解析 → 裁剪 → 原有PaddleOCR成功，退出码0。
- `dotnet run --no-build -- --watch --frames 3`：首帧建基线，后两帧无变化跳过OCR；
  Completed，Frames=3，BaselineResets=0，ChangedFrames=0，OCR calls=0。
- 日志：`Output/v06-live-layout.log`、`v06-pipeline.log`、`v06-watch.log`。
- 监测会话：`Output/Changes/session_20260924_232018_cba0a5bbea734c4cb596a0891c029b61/summary.json`。
- 默认流程OCR输出：`Output/OCR/chat_region_20260924_232028_807.txt` / `.json`。
  正文仍可读，昵称空格和图形误识别仍存在；布局增强不等同于OCR准确率保证。

三帧实测布局解析耗时约1449 / 1287 / 1221ms，主要是UIA遍历预算；
离线纯视觉样本约22–28ms。每帧处理完还会等待1000ms，因此本版不承诺1fps。
这是单次样本记录，不是吞吐压测。所有监测均已停止，没有遗留后台任务。

## 限制与范围

UIA区域身份依据几何相似度而非语义标签；视觉方案依赖浅色主题、长分隔线及比例搜索邻域。
大幅拖动面板、皮肤/DPI变化、内容伪装成分隔线或覆盖边缘，仍可能影响定位。
证据不足或冲突时显式回退，不宣称所有环境均精确。
UIA自然面板可能包括该面板自己的工具栏；视觉线边缘可能存在1–3px差异。
窗口尺寸或解析框变化时跳过该帧OCR，同帧到达的消息可能并入新基线。
UIA预算与有限候选不会读取消息补足定位；聊天文本仅可能由既有截图OCR模式识别。
未接入qnbot、知识库、AI接口、自动回复、点击或发送；范围止于V0.6。
