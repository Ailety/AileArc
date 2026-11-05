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

## 2026-11-06 同机对照

Windows 11 专业版 26300，i9-12900H，系统报告内存 31.75 GiB，120 DPI，窗口约 1320 × 850 DIP。基线从测量入口提交 `c5c4b83` 构建；调优版取本次自包含开发包。两者使用同一测量脚本和生成的三个 ZIP，使用实际 WPF 打开路由，不计 Explorer 命令转交及现有实例激活。每种情况各启五个新进程，奇数轮先运行基线，偶数轮先运行调优版。以下均为逐列中位数，顺序为基线 → 调优。[40 次原始读数](measurements/WPF_Client_20261106.csv)

| 打开样本 | 首批绑定 ms | 首行进入绘制 ms | Job 峰值提交 MiB | 采样峰值工作集 MiB |
|---|---:|---:|---:|---:|
| 1,000 项，平铺 | 982 → 811 | 1,080 → 869 | 99.4 → 101.0 | 166.0 → 141.3 |
| 100,000 项，平铺 | 1,523 → 1,072 | 1,621 → 1,152 | 161.8 → 153.8 | 235.6 → 227.9 |
| 100,000 项，乱序含目录 | 1,590 → 1,162 | 1,740 → 1,269 | 166.8 → 161.2 | 239.6 → 233.8 |

在十万项平铺样本上，单独读取协议输出观察到元数据帧从 3,125 个降至 391 个，均合计 100,000 项。索引开始至首批绑定由 116 ms 降至 48 ms。乱序含目录样本仍需实际排序，这一阶段为 93 → 94 ms。采样包含 AileArc、Worker 和 conhost。

同一平铺大包的取消按钮触发时刻为启动后 986 → 820 ms，从触发至窗口空闲为 20.2 → 7.2 ms。扫描提前与窗口布局并行，触发时显示的已读数量也从 7,712 项变为 40,960 项；这组取消运行的峰值提交为 120.1 → 139.8 MiB，采样工作集为 190.6 → 203.9 MiB。应结合取消时已经完成的工作量解读内存数据。
