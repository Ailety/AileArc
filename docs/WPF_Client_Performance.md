# 正式 WPF 客户端性能测量

使用 PowerShell 7、Python 3 和 Windows 11 x64。在仓库根目录运行以下命令即可生成公开样本并测量 Release 客户端。

```powershell
./scripts/Build-Client.ps1 -Configuration Release
python scripts/Create-UIValidationSamples.py .artifacts/measure-fixtures --tree
./scripts/Measure-Client.ps1 -Archive .artifacts/measure-fixtures/entries-1000.zip -Runs 5
./scripts/Measure-Client.ps1 -Archive .artifacts/measure-fixtures/entries-100000.zip -Runs 5
./scripts/Measure-Client.ps1 -Archive .artifacts/measure-fixtures/tree-100000.zip -Runs 5
./scripts/Measure-Client.ps1 -Archive .artifacts/measure-fixtures/entries-100000.zip -Mode cancel -Runs 5
```

默认报告保存在 `.artifacts/client-measurements`。可用 `-Executable` 指定开发包中的 AileArc.exe，用 `-Report` 指定新的 JSON 路径。报告包含样本与四个主要程序集的 SHA-256、CPU、内存、系统版本、窗口尺寸/DPI、各次原始读数和逐列中位数。两种平铺样本沿用桌面技术验证的数据；tree 样本按固定乱序写入，包含根目录文件及两级隐式目录。

## 时间口径

- 每次启动独立的正式客户端进程，使用简体中文和屏幕外的非最小化窗口，调用正式打开路径、实际 Worker 与 WPF 表格。测量入口隔离单实例转交，不改动用户设置。耗时从父进程发起启动前的单调时钟开始，包括测量进程加入 Job 的握手。磁盘文件缓存和系统缓存可以跨次复用。
- `scanStartMs`、`indexStartMs`、`boundMs` 分别对应开始读取、扫描完成开始建索引、首个非空页绑定。两项相减可查看对应阶段的耗时，其中包含切回界面线程的等待。
- `frameMs` 取 WPF 绘制回调：第一行属于当前数据源，连接到真实窗口，具有可见尺寸，且名称单元格的可见文本与条目名称匹配。这个时间点度量文件列表进入绘制；显示器扫描输出和磁盘冷启动需要单独测量。
- 取消样本在扫描进度第一次显示已读取条数且取消按钮可用时操作实际按钮。`cancelAtMs`、`entriesAtCancel` 记录触发时刻和当时显示的条目数。`cancelMs` 从按钮触发计至打开请求返回、窗口结束忙碌并显示已取消。小样本可能在首次进度刷新前读完，应使用十万项样本测取消。

## 内存口径

测量器将新进程及其子进程放入独立 Windows Job。报告包含实际观察到的进程名；重定向控制台时系统可能加入 conhost。结束时检查 Worker 曾出现并且 Job 内所有进程都已退出。

- `peakTreeCommitBytes` 取系统维护的 Job 提交内存峰值。覆盖加入 Job 后至退出，能计入短命 Worker。参见 [Microsoft Job 内存统计](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_extended_limit_information)。
- `sampledPeakTreeWorkingSetBytes` 取同次采样内各进程工作集之和的最大值。每轮采样后最多等待 20 ms 再检查进程；采样可能错过瞬时峰值，共享映射页也可能被重复计入。

CI 对首个数据绘制和扫描取消各做一次功能检查。机器负载、图形驱动和缓存会影响具体读数，性能对照使用同机同样本的多次运行。
