# M1 开发预览验收记录

本表按[实施规范](AileArc_M1_Implementation_Specification.md)核对 Windows 11 x64 的日常使用流程。执行入口为 `scripts/Package.ps1`（Release、核心测试、双语 WPF 窗口和 Shell 接口）及 `scripts/Test-Install.ps1`（实际安装目录、升级和卸载）。GitHub 使用 Windows Server 托管运行器；本轮另在 Windows 11 开发机执行 D 盘跨版本安装验证。

| 规范项 | 已核对的结果 | 复查位置 |
|---|---|---|
| §1–2 平台与技术链路 | 默认构建、运行和自包含包使用暖石墨 WPF 客户端，通过私有管道调用独立 Worker。ZIP、7Z、RAR 可浏览，ZIP/7Z 可创建。已用带前缀和错扩展名的真实归档读写验证。 | [部署说明](Development_Installation.md)、[格式样本](../tests/AileArc.Tests/PrefixedArchiveTests.cs)、[创建回归](../tests/AileArc.Tests/CompressionTests.cs) |
| §3 找到条目 | 目录树、地址、前进后退、名称/类型/大小/路径排序、搜索范围、IME 组合事件和编码切换均走正式窗口。条目 ID 区分同名文件；切页后按当前页重新选择。 | [导航自检](../src/AileArc.Client/ClientChecks.Navigation.cs)、[目录树](../src/AileArc.Client/ClientChecks.Folders.cs)、[重读与编码](../src/AileArc.Client/ClientChecks.Reload.cs) |
| §3 默认应用与多窗口 | 检查默认应用收到的工作副本内容、可执行物的确认、同一进行中请求的合并及嵌套包重新激活新窗口。无关联时使用系统“打开方式”；取消返回时保留副本。自动化拦截启动调用和系统选择器结果，不执行测试脚本。 | [工作副本窗口自检](../src/AileArc.Client/ClientChecks.WorkCopies.cs)、[系统启动边界](../src/AileArc.Client/ExternalApplication.cs)、[多窗口身份与激活](../tests/AileArc.Tests/LifecycleTests.cs) |
| §3 外部编辑及校验失败 | 核对修改检测、跨退出保留、另存、关闭保护及显式清理。受限的未通过校验内容保存在独立标记的副本中，每次打开需单独选择；密码错误、零输出、取消及协议异常不会产生可打开的新副本。 | [工作副本回归](../tests/AileArc.Tests/WorkCopyTests.cs)、[损坏样本](../tests/AileArc.Tests/DamagedWorkCopyTests.cs) |
| §4 智能解压 | 显式和隐式单目录、单文件、多根项目、目录加说明文件、空包和目标重名均有规则测试；实际落盘核对原目标保留、唯一命名和完整内容。当前支持的格式范围见 README。 | [根结构规则](../tests/AileArc.Tests/CoreTests.cs)、[真实解压](../tests/AileArc.Tests/ExtractionTests.cs) |
| §5 任务语义 | 全部/所选/当前位置/指定位置、解压并打开偏好、冲突决定、进度、取消、部分失败与按失败 ID 重试均已验证。任务关闭、会话密码、异常退出后的登记文件和记录清理有独立回归。 | [解压结果](../src/AileArc.Client/ClientChecks.ExtractionResults.cs)、[只解压所选](../src/AileArc.Client/ClientChecks.PrimaryExtraction.cs)、[恢复测试](../tests/AileArc.Tests/RecoveryTests.cs) |
| §6 语言和区域 | zh-CN/en-US 的资源键、占位符和回退有测试；客户端按会话语言运行，保存首选项后可稍后或立即重启。测试覆盖拒绝关闭时保留原窗口、恢复编码参数、双语布局和文件大小按区域格式化。跨进程实际语言切换另有本机记录。 | [资源回归](../tests/AileArc.Tests/CoreTests.cs)、[生命周期自检](../src/AileArc.Client/ClientChecks.P3.cs)、[实机记录](Development_Log.md) |
| §7 安全边界 | 路径、链接、设备名、来源标记、元数据/输出预算、源版本和同名冲突均在实际读写测试中检查。Worker 限时等待、进程异常、半帧和错误元数据不会报告成功。已有目标的最终核对与改名之间仍有外部竞争窗口。 | [架构边界](AileArc_Architecture_Decisions.md)、[Worker 故障注入](../tests/AileArc.Tests/WorkerTimeoutTests.cs)、[只读完整性测试](../tests/AileArc.Tests/ArchiveVerificationTests.cs) |
| §8 性能与输入 | 已在正式 WPF 窗口测量一千项和十万项归档的首行绘制、进程树内存和取消延迟。键盘多选用真实 WPF 路由事件和可控修饰键，核对焦点、选中 ID 及最后写出的文件。 | [性能口径和数据](WPF_Client_Performance.md)、[键盘及尺寸回归](../src/AileArc.Client/ClientChecks.KeyboardLayout.cs) |
| §8 安装与 Shell | 普通开发包覆盖自定义目录、回滚、两种语言、原用户文件和默认程序保留。原生菜单命令、安装后 Worker、打开/新建/解压转交有自动验证。本机受信任的开发签名包另有 Explorer 注册/卸载/重装记录。 | [安装烟测](../scripts/Test-Install.ps1)、[签名和 Explorer 验证范围](Development_Installation.md) |

### 显示与输入的验证边界

中英文实窗对 1366 × 728 物理像素工作区分别换算 96、120、144、192 DPI 的逻辑尺寸，检查地址、搜索、解压、编码、翻页、目录与详情入口，并按对应像素密度渲染。还检查了本机实际 HWND 初始边界、缩窗后键盘焦点行位置、加宽列的横向滚动和短对话框底部的确认按钮。实窗可按以下方式将四种视口存为 PNG（指定未使用的新目录）：

```powershell
./scripts/Build-Client.ps1 -Configuration Release
& ./.artifacts/client/Release/AileArc.exe --self-test zh-CN --layout-output .artifacts/layout-zh | Out-Null
```

### 仍需独立环境或人工观察的项目

- 在新装 Windows 11 x64 上完整验证安装、证书信任、真实 Explorer 一级菜单及卸载后的系统界面。开发机和 Windows Server CI 的结果分开记录。
- 不同物理 DPI 显示器之间拖动窗口时的系统过渡、Narrator 的实际朗读、不同中文输入法对 Ctrl+Space 的占用，以及真实系统“打开方式”选择器的交互。文件列表也提供单独的空格键切换焦点行。
- 百万条目和 100 GB 是容量扩展目标。当前已测数据集、256 MiB 工作副本预算、4 GiB 单次解码等边界见 README 和性能报告。此表不构成正式 Release 或全量 M1 签收。

语言重启已有单次保存、恢复会话及拒绝关闭的证据。两个窗口快速连续保存不同语言时，最终磁盘选择和待重启提示是否一致，仍缺少专项自动回归。
