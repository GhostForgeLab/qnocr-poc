# Agent Console MVP（本地客服辅助）

## 一条命令启动（2026-10-23）

```powershell
.\Start-Agent.ps1
```

默认启动隔离的本地演示，包含两个合成买家，真实执行本地 Context、规则意图、知识检查和 Preview 服务，无外部 AI 调用。网页醒目标明演示；消息缺少商品资料时展示固定人工核实建议，可编辑、批准、复制和记录反馈。运行目录在 `Output/AgentRuns/`，启动命令打印访问地址、日志和对应的 `Stop-Agent.ps1` 命令。端口已占用时会明确失败，可用 `-Port 18731`；已构建时可用 `-NoBuild`。

打开已有采集，自动读取配对的回复状态目录：

```powershell
.\Start-Agent.ps1 -RunDirectory 'D:\CodexWork\qnocr-poc\Output\你的采集目录'
```

新主链支持 32 位 SessionId、opaque MessageId 与散列 review 文件夹，读取当前采集的实际回复回执，不混入其它运行。AI 失败也能看到状态；商品显示采集时快照，人工回复继续复用原服务。旧 Shadow 目录仍兼容。10 月 23 日之前的新主链采集未记录 `StateDirectory`，不能直接作为配对工作台运行目录使用。

实时启动通过 `-CaptureConfig <JSON文件>` 同时启动现有采集主链和工作台。配置形状如下；窗口句柄必须是当前可见千牛接待窗口的十六进制值，买家标签和测试前缀必须与实际消息匹配：

```json
{
  "WindowHandle": "填写实际窗口句柄",
  "BuyerLabel": "填写实际买家标签",
  "Prefix": "填写至少6字符的测试前缀",
  "KnowledgeConfig": "Knowledge/product-knowledge.json",
  "KnowledgeWorkflow": "填写已审核的知识工作流文件路径",
  "Frames": 600,
  "IntervalMilliseconds": 2000
}
```

仍采用 `Config/intent-provider.json`、`Config/ai-provider.json` 和既有密钥读取机制。示例商品文件是合成数据，不代表真实店铺知识；Observed Seller 与知识目录的实际映射仍待补齐。采集限定显式买家与测试前缀；这条命令不会自动获得真实买家身份或把测试资料当成真实资料。

## 旧 Shadow 启动方式

在交互式 Windows 会话启动。需要一组匹配的 Shadow 运行目录与状态目录；可使用已完成的运行批次，也可在运行中刷新页面查看新预览：

```powershell
dotnet run --project AgentConsole/AgentConsole.csproj -- --state-dir D:\CodexWork\qnocr-poc\Output\ValidationV128\real-test --run-dir D:\CodexWork\qnocr-poc\Output\Shadow\shadow_20260927_221626_424_1425db7c36e747be966c21aea43fc4b3 --port 18730
```

终端会打印仅绑定 `127.0.0.1` 的完整访问地址和本轮随机令牌；停止服务后令牌失效。界面只读取指定运行批次的 Shadow 预览，状态目录与运行目录必须匹配且 SendEnabled=false。

客服流程：选择会话 → 查看 Shadow 采集的新消息、JEV/规则意图、当前商品、Knowledge 状态与 AI 建议 → 可编辑回复 → 明确批准当前文本 → 复制到本机剪贴板 → 客服自行切换千牛并人工粘贴发送 → 记录 Accepted、Edited 或 Rejected。编辑、复制、反馈由现有 AssistantReplyService 审计；页面没有千牛发送接口。已复制文本不能再次编辑，拒绝反馈不可覆盖。

当前商品区域显示 Session 中的绑定来源与时间。仅人工绑定可在页面清除；OCR 绑定由下一次当前窗口采集更新。该操作不发布知识、不自动选择新商品。

本地访问令牌不是多用户鉴权。真实客服使用前仍应规划操作员身份、访问控制和敏感数据保留策略；当前 UI 是单机辅助 MVP，不是自动回复系统。
