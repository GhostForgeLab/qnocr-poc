# 客服辅助工作台运行记录 · 2026-10-23

代码基线为 `579ed7e`，即 V1.35.0-C。此次直接补齐可运行入口，未再拆分为逐阶段评审。

## 已交付

- `Start-Agent.ps1`：恢复本地缓存依赖、编译、后台启动、探测 HTTP 就绪、输出地址与日志。默认使用独立合成演示；支持读取已有运行或通过显式 CaptureConfig 启动采集与工作台。
- `Stop-Agent.ps1`：核对进程开始时间后关闭本次工作台；向本次采集目录写 stop.request。
- 网页支持新 Agent 的 32 位 SessionId、散列 review 目录与实际 reply 回执；运行与状态目录严格配对，重放回执不重复展示，失败状态可见。保留原 Shadow 读取及人工操作接口。
- 采集元数据加入回复 StateDirectory；JSON 原子发布，避免网页读到写入一半的回执。
- 默认演示执行实际 Context → RuleBased Intent → Knowledge → 安全建议 Preview，两个合成买家，AI HTTP=0。页面明确标注合成演示。

## 实测结果

- AgentConsole 编译：0 警告、0 错误。
- 新旧 Provider/Preview 集成专项：65/65。
- 全量回归：1152/1152（原 1146 加 6 项新投影检查）。
- 网页 HTTP 200；浏览器完成会话查看、编辑、批准，实际状态依次为 HumanEdited、Approved，出现复制入口。
- 第二实例在 18731 端口启动后通过停止脚本关闭；主实例留在 18730。
- 启动暴露的旧依赖缓存/SQLite 加载问题通过恢复项目依赖修复；PowerShell JSON 时间自动解析导致的停止校验问题已修复。

日志：`Output/agent-console-integration-checks.log`、`Output/agent-workbench-regression.log`。
保留的运行目录：`Output/AgentRuns/20261023_124233_aab372be/`，含 launch.json、服务日志、演示数据库及网页截图。

## 真实运行仍需的输入与限制

此次验证的运行实例是本地演示，不是新的千牛实测或外部模型验收。CaptureConfig 的实时启动分支没有连接真实千牛验证；真实外部 AI、真实多买家和 Observed Seller 到实际知识目录的映射仍待补齐。商品展示为采集时快照，新 Agent 页面不提供旧 64 位 Session 的人工解绑动作。密钥仍走原配置机制，没有新增密钥或修改真实知识。

运行失败不会自动补跑已保存消息，沿用 V1.35.0-C 的执行语义。工作台不自动刷新表单，避免覆盖人工输入；查看新采集可刷新页面。自动发送保持关闭。

后续执行方式：按完整可运行功能批次实现，集中编译启动与回归，运行暴露问题后直接修复；只报告关键结果和需要用户输入的真实阻塞。
