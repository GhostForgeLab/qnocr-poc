# V1.0 离线兼容性验证摘要

详细接口模型、差异和最小适配设计见 [接口分析报告](docs/QNBOT_MESSAGE_CONTRACT_ANALYSIS.md)。

- 分析qnbot基线：`4802807f26a6abf4cfb4457d5b158b2f51f4ff18`。
- 6份模型/协调器/状态机/安全辅助源码逐字节复制并校验SHA256，没有修改qnbot。
- 独立宿主在.NET Framework4.8、C#7.3下编译并运行，不引用或启动Bot.exe。
- 17项Adapter检查通过；真实协调器接收2条消息，生成generation 1、2，交给记录回调。
- 同一Envelope再次入队后，回调总数仍为1；禁止的AI/业务/发送调用为0。
- 合成样例和已有V0.9带测试身份Envelope均通过；空身份Envelope拒绝转换。
- 旧消息经原始历史安全判定ShouldCallAi=false，没有修改时间绕过过滤。
- ProductId只保留旁路上下文，原生派生itemId为空，未伪造商品链接。
- qnocr主项目严格构建0警告、0错误，148项既有检查通过。
- 兼容宿主0错误，保留原始协调器的1条CS4014警告；没有修改/屏蔽该源代码警告。

结果文件：

- `Output/Integration/run_20260925_101110_864/compatibility-result.json`
- 同目录 `qnbot-messages.json`、`converted-messages.json`、`build.log`、`run.log`
- `Output/Integration/v09-envelope/compatibility-result.json`
- `Output/v10-empty-identity.log`
- `Output/v10-qnocr-build.log`、`Output/v10-qnocr-checks.log`
- `Output/v10-qnbot-integrity.json`：结束时qnbot HEAD未变、工作区干净、源码哈希一致。

这是隔离环境中的真实协调器入口验证。外围运行时依赖使用受控测试替身，最终handler只记录数据。
没有验证真实普通入口、SalesAgent/Knowledge V2/Prompt/Reply Chain，没有部署Shadow或真实发送。
完整身份映射、新旧消息区分、跨源去重、商品上下文接入和Shadow状态隔离仍是后续前置条件。
完成V1.0后停止。
