# 正式 WPF 客户端性能测量

使用 PowerShell 7、Python 3 和 Windows 11 x64。在仓库根目录运行以下命令即可生成公开样本并测量 Release 客户端。

```powershell
./scripts/Build-Client.ps1 -Configuration Release
python scripts/Create-UIValidationSamples.py .artifacts/measure-fixtures --tree
./scripts/Measure-Client.ps1 -Archive .artifacts/measure-fixtures/entries-1000.zip -Runs 5
./scripts/Measure-Client.ps1 -Archive .artifacts/measure-fixtures/entries-100000.zip -Runs 5
./scripts/Measure-Client.ps1 -Archive .artifacts/measure-fixtures/tree-100000.zip -Runs 5
./scripts/Measure-Client.ps1 -Archive .artifacts/measure-fixtures/entries-100000.zip -Mode cancel -Runs 5
./scripts/Measure-Client.ps1 -Archive .artifacts/measure-fixtures/entries-100000.zip -Mode scroll -ScrollSeconds 1800
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

CI 对首个数据绘制、扫描取消和连续滚动各做一次功能检查。机器负载、图形驱动和缓存会影响具体读数，性能对照使用同机同样本的多次运行。

滚动测量在正式表格中交替定位靠近页首和页尾的文件，每 24 次操作翻页，到末页后向前翻。逐次检查可见名称和条目 ID 是否对应、选中项是否在行容器回收后保留，以及翻页是否清除旧选择。同时记录实际生成的行容器峰值、全过程 Job 峰值及首尾强制 GC 后的托管内存。它检验数据和显示的一致性，物理滚轮手感和显示器帧率需另外观察。

## 2026-11-06 同机对照

Windows 11 专业版 26300，i9-12900H，系统报告内存 31.75 GiB，120 DPI，窗口约 1320 × 850 DIP。基线从测量入口提交 `c5c4b83` 构建；调优版取本次自包含开发包。两者使用同一测量脚本和生成的三个 ZIP，使用实际 WPF 打开路由，不计 Explorer 命令转交及现有实例激活。每种情况各启五个新进程，奇数轮先运行基线，偶数轮先运行调优版。以下均为逐列中位数，顺序为基线 → 调优。[40 次原始读数](measurements/WPF_Client_20261106.csv)

| 打开样本 | 首批绑定 ms | 首行进入绘制 ms | Job 峰值提交 MiB | 采样峰值工作集 MiB |
|---|---:|---:|---:|---:|
| 1,000 项，平铺 | 982 → 811 | 1,080 → 869 | 99.4 → 101.0 | 166.0 → 141.3 |
| 100,000 项，平铺 | 1,523 → 1,072 | 1,621 → 1,152 | 161.8 → 153.8 | 235.6 → 227.9 |
| 100,000 项，乱序含目录 | 1,590 → 1,162 | 1,740 → 1,269 | 166.8 → 161.2 | 239.6 → 233.8 |

在十万项平铺样本上，单独读取协议输出观察到元数据帧从 3,125 个降至 391 个，均合计 100,000 项。索引开始至首批绑定由 116 ms 降至 48 ms。乱序含目录样本仍需实际排序，这一阶段为 93 → 94 ms。采样包含 AileArc、Worker 和 conhost。

同一平铺大包的取消按钮触发时刻为启动后 986 → 820 ms，从触发至窗口空闲为 20.2 → 7.2 ms。扫描提前与窗口布局并行，触发时显示的已读数量也从 7,712 项变为 40,960 项；这组取消运行的峰值提交为 120.1 → 139.8 MiB，采样工作集为 190.6 → 203.9 MiB。应结合取消时已经完成的工作量解读内存数据。

### 增加修改时间列后的抽测

同一十万项平铺 ZIP，交替打开上一版和增加修改时间的开发包，各启三个新进程。旧版首行进入绘制为 1,647、1,349、1,422 ms，新版为 1,580、1,499、2,038 ms。测量期间本机另有持续滚动和安装验证，两组读数有重叠，仅作此次开发记录。上表的五轮同机对照单独保留。

### 连续滚动 30 分钟

Windows 11 26300，120 DPI，1320 × 850 DIP。读取 100,000 项平铺 ZIP，在同一个含修改时间列的窗口中滚动 1,800.07 秒。共定位 11,245 次、翻页 468 次，每次都核对可见条目与选中项。最多同时生成 17 个行容器。全程 Job 峰值提交 239.9 MiB，采样峰值工作集 306.5 MiB，首尾强制 GC 后的客户端托管内存分别为 30.3 和 31.5 MiB。测量进程树含客户端、Worker 和 conhost，结束时全部退出。

该长测使用动效调整前的客户端。最终自包含包另用同一归档执行 15 秒冒烟，定位 73 次、翻页 3 次，最多 17 个行容器。
