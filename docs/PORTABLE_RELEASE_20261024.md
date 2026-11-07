# 免安装测试版交付记录 · 2026-10-24

最终文件：`Output/Distributions/QNAgent-Portable-20261024-test02-win-x64.zip`，229,638,833 字节（约 219 MiB）。

SHA256：`7F020E8D258D30378C7727A6B53C2B582ACCCE4DCCA2314BE779B4F9BEC58EDF`。

解压后运行 `QNAgent.exe`。内含 `使用说明.txt`、版本信息、文件校验清单与依赖许可。目标为 Windows 10/11 x64，回复模型和 JEV 密钥由测试者自行配置，商品资料需自行录入或另行导入并核对。

## 发布隔离

- `Build-Portable.ps1` 使用全新 artifacts/publish 目录，分别发布桌面和内存 OCR 工作进程；内含 .NET 8.0.31 和签名有效的 Microsoft Visual C++ 本地依赖，不依赖开发目录的 dotnet。
- `PortableRelease=true` 不编译桌面的开发检查/真实数据回放类，工作进程只接受 `--memory-worker` 内部协议。不发布示例商品、PDB、源码、已有设置、聊天、截图或数据库。
- 发布版只在 EXE 同级 `Data/` 保存运行数据；开发版维持原来的项目 `Output/DesktopClient/` 行为。
- 最终 ZIP 由干净的发布目录压缩；测试在解压后的另一副本中完成。验证副本产生的 `SmokeData/` 和 `Data/` 从未进入交付 ZIP。

## 验证

首次在受限执行环境中验证 DPAPI 失败（该环境不具备正常用户的密钥保护上下文）。随后在正常 Windows 用户环境运行离线检查，全部 10 项通过：主程序本地 .NET、空白配置、空白商品/消息、DPAPI 加解密、SQLite、两次真实中文 OCR、工作进程本地 .NET、已加载 VC 库位于程序目录、WPF 界面启动/关闭、诊断导出。

验证副本位于项目外的临时中文及空格路径；进程 PATH 只保留 Windows 目录，DOTNET_ROOT 指向不存在的位置，清除了测试进程的模型密钥环境变量。未调用 AI 服务、未连接或截取真实千牛聊天。

普通启动额外验证：从其他工作目录启动，正确创建 EXE 同级 Data；主窗口正常关闭；`--export-diagnostics` 成功导出该 Data 的诊断包。数据仅含新建的运行状态与诊断日志/随机盐。

最终包静态审计：609 个文件通过清单 SHA256；未发现私人数据目录、聊天文件、截图、调试符号或已保存的加密密钥值；应用程序集不含开发回放中的既有买家/商品标识；两宿主 runtimeconfig 均声明内含框架；431 个本机 PE 导入引用解析到随包文件或 Windows API。

test02 仅补充依赖许可；498 个 EXE/DLL/deps/runtimeconfig 文件与已完成运行检查的 test01 逐文件 SHA256 一致，因此没有重复运行相同二进制的检查。原开发配置也另行构建成功，0 警告、0 错误，没有替换当前开发程序的运行文件。

这证明本机隔离目录下的可运行性，不代表已经完成另一台干净 Windows 电脑、所有千牛版本或不同 CPU 的兼容验证。第一轮外部测试应优先确认启动、连接、识别与模型配置。

复现构建：`./Build-Portable.ps1 -Version '<新版本号>'`。脚本拒绝覆盖已有版本；需要已有的包缓存（缺失时先恢复依赖）。审计：`python Validation/PortablePackageAudit.py <ZIP路径>`。

Microsoft 本地运行库部署参考：https://learn.microsoft.com/cpp/windows/redistributing-visual-cpp-files 。
