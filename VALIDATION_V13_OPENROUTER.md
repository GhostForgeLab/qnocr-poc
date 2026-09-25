# V1.3 OpenRouter Provider修复

日期：2026-09-25。输入commit：`89b5bb68d3bc1f68430d98c41ddfa546bb795a4c`。

## 配置来源与修改范围

`Prepare-AiShadow.ps1` 将原配置复制到本仓库 `.tools/ai-shadow/data-root`。
未修改原 `C:/Users/LPH/AppData/Local/QianniuAiBot` 或 qnbot源码/运行库。

原装 `Bot.ChromeNs.AiEndpointStore` 通过 `PersistentParams` 读取 `ai / AiEndpointListJson`。
`ShopScopedParamBridge` 优先读取当前测试店铺的DPAPI设置；没有显式Endpoint时回退到
复制的 `data/params.db` 中的Endpoint列表或旧版BaseUrl、ApiKey、Model设置。
切换前，测试店铺设置只有 `AutoReplyRulesJson`、`IsAutoReply`，有效配置来自该回退路径：
`sub.cn2.io` / `gpt-5.5`。

切换后，测试店铺
`.tools/ai-shadow/data-root/shops/qnocr_ai_shadow_test/config/settings.json`
新增 `AiEndpointListJson`，唯一启用的Endpoint为：

- BaseUrl：`https://openrouter.ai/api/v1`
- Model / TextModel：`openai/gpt-5.5`
- Key：配置时优先读取进程 `OPENROUTER_API_KEY`，否则沿用本地已保存的OpenRouter Key。
- 保留原Endpoint的SystemPrompt和生成参数；使用 `SaveEndpointsJson`，不调用会同步旧版Prompt的 `SaveEndpoints`。

原Key和新Key均未提交。新Key由原配置存储接口使用 `DPAPI-CurrentUser` 加密。
`.tools/` 和 `Output/` 已在 `.gitignore` 中，无需新增例外。

仅改动隔离宿主的Provider配置命令、Provider冒烟工具、Endpoint校验和文档。
OCR、MessageEnvelope、Adapter、SalesAgent、Knowledge V2、业务Prompt、Send Chain均未修改。

## 配置及复验

从仓库根目录执行；使用原Windows用户，先按README准备运行库并执行一次 `--ai-preflight`。
不要把Key直接写入命令历史或源文件。

```powershell
dotnet build Validation/QnbotCompatibility/QnbotCompatibility.csproj
# 本机进程继承了旧Key；从已存在的Windows用户环境变量刷新当前会话：
$env:OPENROUTER_API_KEY = [Environment]::GetEnvironmentVariable('OPENROUTER_API_KEY', 'User')
./Validation/QnbotCompatibility/bin/Debug/net48/QnbotCompatibility.exe --ai-configure-openrouter openai/gpt-5.5
./Validation/QnbotCompatibility/bin/Debug/net48/QnbotCompatibility.exe --ai-preflight
./Validation/QnbotCompatibility/bin/Debug/net48/QnbotCompatibility.exe --ai-provider-smoke
```

配置命令在加载运行库、打开SQLite之前备份；控制台输出备份目录，不输出Key。
运行时使用保存后的本地配置，进程里的旧环境变量不会覆盖它。
继续AI Shadow时仍显式使用原 `--shadow --ai-shadow --identity ...` 启动方式；Send保持OFF。
本轮未启动持续OCR观察或真实买家测试。

## 实测结果

- 宿主构建通过，0错误，保留原上游协调器CS4014警告。
- 原有186项检查全部通过。
- 预检：EndpointCount=1，Host=openrouter.ai，Model=openai/gpt-5.5，AiEnabled=true，SendEnabled=false。
- 初次调用因当前进程继承旧Key返回401；OpenRouter `/key` 返回 `User not found.`。
  检查发现用户作用域Key与进程不同；用户作用域Key认证200，刷新并重新配置后完成以下成功验证。
- 简单请求：`Reply with exactly OPENROUTER_OK.`；POST `/api/v1/chat/completions`。

```json
{"HttpStatus":200,"Success":true,"Content":"OPENROUTER_OK","AiEnabled":true,"SendEnabled":false,"SendEntryInvoked":false,"SentMessages":0}
```

继续用仓库匿名合成样例运行原 `--ai-shadow` 入口，2条输入经原协调器合并，原装生成服务返回：

> 页面有刻字选项就支持，发货时间以页面显示为准，发下款式我帮您核对。

回执：Status=Converted，AiCalls=1，LastAiError为空，SourceFilesVerified=6，
ForbiddenBusinessCalls=0，SendEntryInvoked=false，SentMessages=0，耗时4637ms。
运行库SHA256检查通过，未修改或启动Bot.exe应用。
这证明配置和匿名生成冒烟成功，不代表真实10条买家问题或Knowledge V2商品绑定验收完成。

本地证据（Git忽略）：

- `Output/v13-openrouter-build.log`
- `Output/v13-openrouter-checks.log`
- `Output/v13-openrouter-configure.log`
- `Output/v13-openrouter-preflight.log`
- `Output/v13-openrouter-smoke.log`
- `Output/v13-openrouter-shadow-smoke.log`
- `Output/AiShadow/openrouter-smoke/shadow-adapter-result.json`

配置审计：原备份与当前DPAPI内容比较，只有 `AiEndpointListJson` 改变；
`IsAutoReply`、`AutoReplyRulesJson`原值不变；`params.db`与原备份逐字节一致。

## 备份与回滚

本轮切换前的完整配置备份目录（不是后续已含OpenRouter配置的备份）：

`D:/CodexWork/qnocr-poc/.tools/ai-shadow/provider-backups/before-openrouter-20260925-131624`

其中保留原 `data/params.db` 与测试店铺加密 `settings.json`，复制后校验哈希一致。
每次配置命令也生成独立备份；不要把含Key的本地备份提交或上传。

回滚时先停止本项目Shadow运行，恢复上述原始备份的两个文件到 `data-root` 中的同名位置：

```powershell
$backup = 'D:/CodexWork/qnocr-poc/.tools/ai-shadow/provider-backups/before-openrouter-20260925-131624'
$dataRoot = 'D:/CodexWork/qnocr-poc/.tools/ai-shadow/data-root'
Copy-Item -LiteralPath "$backup/data/params.db" -Destination "$dataRoot/data/params.db" -Force
Copy-Item -LiteralPath "$backup/shops/qnocr_ai_shadow_test/config/settings.json" -Destination "$dataRoot/shops/qnocr_ai_shadow_test/config/settings.json" -Force
```

再 `git revert <本次修复commit>` 并重新编译隔离宿主，以恢复旧域名校验；
仅恢复配置而不回滚宿主时，OpenRouter校验会拒绝旧域名。回滚后仍保持Send OFF，
旧端点此前的HTTP 530故障可能仍存在。

协议核对：[OpenRouter官方Quickstart](https://openrouter.ai/docs/quickstart)；
模型ID核对：[OpenRouter官方模型列表](https://openrouter.ai/api/v1/models)。
