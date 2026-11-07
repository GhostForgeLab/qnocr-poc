# 便携测试包 test03 · 2026-10-24

交付文件：`Output/Distributions/QNAgent-Portable-20261024-test03-win-x64.zip`

大小：229,672,426 字节，约 219 MiB。

SHA256：`0CE01C43E1F1B79BF6FE9244D1E4CAD20DBE8B1CF77D69AFC302652EC31F7C57`

包含当前源码中的会话识别修复、最近 10 条买卖对话上下文、卖家消息保留草稿、JEV 意图展示、资料补充建议、诊断导出，以及固定店铺 ID、业务备份恢复、人工确认后生效的学习建议。

## 交付隔离

- 从全新的 `Output/PortableBuild/20261024-test03` artifacts/publish 目录构建；只复制编译发布产物、所需运行库、许可及通用使用说明。
- 原始 ZIP 没有 Data、商品数据库、原始聊天、学习样本、已确认经验、业务备份、截图、日志、配置或密钥；开发回放检查类没有编入发布版。
- 包内有 .NET 和 Visual C++ 运行依赖，解压后运行 `QNAgent.exe`。朋友自行设置模型/JEV 密钥、建立店铺和商品。
- 本轮没有替换现有开发版程序或读取真实千牛聊天；测试没有调用外部模型。

## 验证结果

1. 609 个文件逐项通过 SHA256 清单核验；431 个本机导入引用可解析至随包依赖或 Windows API。
2. 所有 ZIP 文件内容在内存中与本机明文/加密密钥、业务固定 ID、商品 ID、学习记录标识和可区分聊天片段比对，没有命中；未输出或另存这些私人值。
3. 在项目外的中文及空格临时目录解压验证。PATH 只保留 Windows，DOTNET_ROOT 指向不存在的目录，测试进程清除了模型密钥环境变量。
4. 16 项便携离线检查通过：本地运行时、空白配置、无聊天及业务/学习记录、DPAPI、固定 ID 改名、待审核经验隔离、人工确认、业务备份排除密钥、恢复身份与经验、SQLite、两次中文 OCR、工作进程本地运行时、VC 依赖、WPF 启停及诊断导出。
5. 另行普通启动，从其他工作目录启动也只在 EXE 同级生成空白 Data；命令行诊断导出通过。测试仅使用合成资料，没有连接真实千牛。
6. 测试在 ZIP 的独立解压副本进行。副本产生的 SmokeData、Data 和合成备份没有回拷到发布目录或 ZIP。

验证副本：`C:\Users\LPH\AppData\Local\Temp\QN Agent 便携验证 646e7f7c49ea4469a60421b353b57e81`

这验证了本机隔离环境中的便携性，其他电脑和不同千牛版本的兼容性仍交由朋友实测。反馈时导出“日志与诊断”的 ZIP，勿转发整个 Data 或业务备份。

复现：

```powershell
./Build-Portable.ps1 -Version '<新版本>'
python Validation/PortablePackageAudit.py '<ZIP 路径>'
./Validation/Verify-Portable.ps1 -Archive '<ZIP 路径>'
```

构建拒绝覆盖现有版本。静态密钥比对与 DPAPI 运行检查需在正常 Windows 用户环境中执行。
