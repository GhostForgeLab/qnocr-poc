# Agent Console MVP（本地客服辅助）

在交互式 Windows 会话启动。需要一组匹配的 Shadow 运行目录与状态目录；可使用已完成的运行批次，也可在运行中刷新页面查看新预览：

```powershell
dotnet run --project AgentConsole/AgentConsole.csproj -- --state-dir D:\CodexWork\qnocr-poc\Output\ValidationV128\real-test --run-dir D:\CodexWork\qnocr-poc\Output\Shadow\shadow_20260927_221626_424_1425db7c36e747be966c21aea43fc4b3 --port 18730
```

终端会打印仅绑定 `127.0.0.1` 的完整访问地址和本轮随机令牌；停止服务后令牌失效。界面只读取指定运行批次的 Shadow 预览，状态目录与运行目录必须匹配且 SendEnabled=false。

客服流程：选择会话 → 查看 Shadow 采集的新消息、JEV/规则意图、当前商品、Knowledge 状态与 AI 建议 → 可编辑回复 → 明确批准当前文本 → 复制到本机剪贴板 → 客服自行切换千牛并人工粘贴发送 → 记录 Accepted、Edited 或 Rejected。编辑、复制、反馈由现有 AssistantReplyService 审计；页面没有千牛发送接口。已复制文本不能再次编辑，拒绝反馈不可覆盖。

当前商品区域显示 Session 中的绑定来源与时间。仅人工绑定可在页面清除；OCR 绑定由下一次当前窗口采集更新。该操作不发布知识、不自动选择新商品。

本地访问令牌不是多用户鉴权。真实客服使用前仍应规划操作员身份、访问控制和敏感数据保留策略；当前 UI 是单机辅助 MVP，不是自动回复系统。
