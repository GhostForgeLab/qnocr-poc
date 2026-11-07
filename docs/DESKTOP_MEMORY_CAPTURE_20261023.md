# 桌面客户端内存采集改造

日期：2026-10-23。

## 行为

- 桌面采集不再创建逐帧目录、PNG、裁剪图或 OCR JSON。图片通过当前 Windows 用户专用命名管道在内存中传递。
- 每次连接保留一个隔离工作进程，首次 OCR 初始化后复用模型。取消/超时终止自己创建的进程，避免遗留挂起的 PrintWindow/OCR。
- 默认画面检查间隔 500 毫秒，设置范围 250–2000 毫秒。旧 `PollSeconds` 配置忽略，其他设置保留。
- 聊天区、头部及商品区的指纹变化后，等待稳定 300 毫秒；连续变化最多等 1500 毫秒。等待受检查间隔和处理耗时影响，不承诺固定端到端延迟。
- 输入框和买家列表不参与变化判断；无买家的有效静止画面也不重复 OCR。最小化暂停，恢复重新验证。串行处理，不缓存待识别帧队列。
- 最新预览在内存中显示；查看原图不写盘，手动导出才保存。换连接时清空旧预览。
- 消息/会话文字继续持久化，采集标识为 `memory:<id>`，不再是截图路径。历史截图保留，旧命令行实验流程继续按原规则保存证据。
- 本次保留 PrintWindow 捕获，未迁移到 WGC；也未改变买家、消息、时间戳的识别规则。

## 验证

- Release 构建成功，零警告、零错误。
- `QNAgent.dll --memory-capture-checks`：33 项通过，涵盖稳定/最大等待、10,000 次不变画面不触发、输入框忽略、会话/商品变化、内存消息结构化、真实 OCR、进程取消退出及无新增磁盘文件。
- 受控文字图片本机 OCR：冷启动约 1.4–1.5 秒，复用模型约 0.54 秒。这不是实际千牛端到端延迟。
- `QNAgent.dll --self-test`：正常 Windows 用户环境 16 项通过；隔离环境 DPAPI 不可用，因此使用正常用户环境完成验证。
- `ChangeDetectionChecks.exe`：1,152 项通过，日志 `Output/memory-capture-regression.log`。须运行 apphost exe；用 `dotnet ...dll` 启动会使既有子进程测试误把 dotnet 当成测试程序。
- 实时验收：`--memory-capture-live-checks` 未找到可用且未最小化的接待窗口，正常用户环境亦如此；没有改动千牛窗口状态。真实窗口的捕获/识别延迟、多买家切换和长时间资源占用仍待实际使用验证。

## 主要实现

`Shadow/MemoryNativeWorker.cs`：有界管道协议、常驻隔离工作进程及取消/超时回收。

`Capture/MemoryChatFrame.cs`、`Detection/SettledFrameGate.cs`：内存布局/裁剪、变化指纹与稳定检测。

`DesktopClient/QianniuConnection.cs`：新采集循环；`MainWindow.cs`：内存预览和显式导出。

`PaddleOcrService`、`BuyerMessageStructurer`、`QianniuLayoutResolver` 增加内存入口，原文件入口保留。
