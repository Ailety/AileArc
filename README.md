# AileArc

Modern archive management for Windows. **Archive should feel like a folder.**

当前为 Windows 11 x64 开发预览，正在迭代首个里程碑。不是正式发布版本。

源码仓库：[Ailety/AileArc](https://github.com/Ailety/AileArc)。

## 已实现

- WinUI 3 原生窗口、目录浏览、面包屑、名称搜索、名称/大小排序。
- 独立 Worker + 完整 7-Zip 26.03 引擎，ZIP/7Z/RAR 读取；按签名识别，不信任扩展名。
- 基础解压与 Smart Extract，重名询问/重命名/跳过/替换，逐文件校验后提交。
- 解压所选文件/目录（递归包含子项）、系统目录选择器和可滚动的错误详情。
- 双击包内文件交给 Windows 默认应用；无关联时提供系统打开方式，可执行内容先确认。
- 工作副本持久保留、SHA-256 修改检测、另存为和关闭保护；不自动回写压缩包。
- 密码输入及当前窗口会话内复用；取消后保留完成项、清理未完成的临时文件。
- zh-CN / en-US 内置资源，默认简体中文，重启生效。
- 路径、链接与重解析点防护，下载来源标记传播，IPC/输出配额，Worker Job Object 内存与生命周期限制。

## 构建与运行

需要 Windows 11 x64、PowerShell 7、联网下载官方构建依赖。开发脚本在 `.tools` 准备 .NET 10 SDK，并提取官方 7-Zip 依赖，不需要全局安装 Visual Studio。工具、依赖二进制和测试产物不纳入 Git。

在仓库目录执行：

```powershell
./scripts/Build.ps1
./scripts/Run.ps1
./scripts/Run.ps1 -Archive 'D:\Archives\example.zip'
```

构建脚本包含自动测试。Release 构建使用 `./scripts/Build.ps1 -Configuration Release`。初次下载可能较慢。常规复现入口是 Build.ps1；AileArc.slnx 供 IDE 打开工程。

## 当前边界

- 所选解压保留完整内部路径；文件按 Entry ID 选择，目录按边界扩展子项。重新解压/准备外部打开时核对文件身份、长度和修改时间，源文件变化需重新打开。
- 当前解压目标仅支持本地磁盘；符号链接、硬链接、重解析点和不安全名称直接拒绝，尚不支持修复名称。仅大小写不同的目录会停止提取，避免静默合并；普通文件的大小写重名按冲突策略处理。
- 不启用已有压缩包编辑/自动回写、新建压缩包、Shell、安装器、标签页或多窗口协调。
- 当前上限：250,000 原始条目、32 Mi 字符的条目路径总量、单 IPC 帧 1 MiB、单次提取 4 GiB、Worker 提交内存 768 MiB。上限是原型的保护预算，不是最终容量承诺。
- 读取或解码连续 2 分钟没有协议输出会停止；未实现性能基准与百万条目容量验收。
- CRC 失败不会替换目标文件；正常取消清理临时文件。应用被强制终止后的残留扫描与恢复界面仍待实现。
- 进程隔离与 Job Object 不等于权限沙箱，后续仍需要更完整的安全测试与发布审查。

## 外部编辑与工作副本

双击文件时，只提取该文件并交给 Windows 的默认应用。大文件准备过程中可取消；当前单文件外部打开上限为 256 MiB，更大的文件先使用普通解压。

工作副本和恢复记录位于用户本地应用数据目录下的 `AileArc/WorkCopies`。使用“工作副本”按钮可查看修改状态、打开副本位置或另存为。实际位置以界面入口为准；Windows 启动宿主可能启用应用数据目录重定向。

修改不会写回原压缩包；另存为也不改变原包。副本在退出/重启后保留，当前不做自动清理。准备被取消时记录为未完成，不会当成可打开的完整副本。恢复记录包含原包位置和条目路径，不含密码。

原始下载 ZoneId 随副本记录保留；编辑器原子保存导致 ADS 丢失时，再次打开或另存仍恢复该标记。修改检测采用内容哈希；被占用或不可读取时保留副本并显示不可用状态。

## 文档

- [M1 实施规范](docs/AileArc_M1_Implementation_Specification.md)
- [长期产品蓝图](docs/AileArc_Development_Specification.md)
- [架构决策](docs/AileArc_Architecture_Decisions.md)
- [开发历程](docs/Development_Log.md)
- [第三方声明](THIRD_PARTY_NOTICES.md)

项目自有代码采用 [MIT 许可证](LICENSE)，第三方组件保留各自许可，见 [第三方声明](THIRD_PARTY_NOTICES.md)。每次 BUG 修复或功能更新在验证后提交并推送，协作规则见 [AGENTS.md](AGENTS.md)。当前不创建官网、不自动发布正式 Release。
