# Native SDK 暖石墨验证

日期：2026-10-22。独立技术验证，不是正式客户端，也不代表 M1 完成。

## 实现边界

- TypeScript + Native markup 原生界面，固定 Native SDK 0.11.0 / scriptc 0.3.0 / Zig 0.16.0；未启用 Web 层。暖石墨三栏、中文字体、搜索、复选框、多选计数、详情、目录与分页入口。
- `AileArc.NativeValidation` 是本地只读适配器：启动时指定一个压缩包，调用现有 Core/Worker，复用 `ArchivePresentation`。界面不能指定任意磁盘路径或执行命令。
- 随机 IPv4 回环端口、随机请求头令牌、拒绝 Origin、限制方法/端点/参数/响应；仅供本轮验证，不把 HTTP 预定为正式 IPC。无解析引擎进入 UI 进程。
- 真实归档每页最多 200 项，和当前 WebView2 一致。生成的 1,000/100,000 项模式用于虚拟列表渲染压力，不能混作扫描性能。列表只构建视口行；全选用全选标记和例外 ID 表示。
- 搜索框限制 512 UTF-8 字节，按 Enter/按钮提交；目录查询限制 512 字符。不同于 WebView2 的输入即查询，搜索时延不能直接排名。
- 不含密码重试、编码选择、归档写入、外部打开、拖放、完整语言切换或 Shell 迁移；原客户端和 WebView2 验证程序保留。

## 构建、启动与复现

```powershell
./scripts/Build-NativeValidation.ps1 -NodePath 'C:/path/to/node24/node.exe'
./scripts/Start-NativeValidation.ps1
./scripts/Start-NativeValidation.ps1 -Archive 'D:/Archives/example.zip'
python scripts/Create-UIValidationSamples.py .artifacts/ui-comparison/samples
./scripts/Measure-UIValidation.ps1 -HostKind Native -Archive .artifacts/ui-comparison/samples/entries-1000.zip
./scripts/Measure-UIValidation.ps1 -HostKind WebView2 -Archive .artifacts/ui-comparison/samples/entries-1000.zip
python scripts/Summarize-UIValidation.py .artifacts/ui-comparison
```

Node 24+、Python 3、PowerShell 7；首次恢复依赖需要网络。工具和编译器放在 `.tools`，结果放在 `.artifacts/native-validation`。字体由固定 SHA-256 的 Noto Sans SC 实例化为 400 字重，保留完整覆盖和 OFL 许可，不提交字体二进制。

## 已发现的问题及处理

1. npm 包遗漏构建脚本无条件引用的测试文件：改用同标签完整源码 `16f29572307c2cc133907537c4dfd98046d4e0f4`。
2. Windows 路径比较将有效主题类型误拒绝、CRLF 导致静态别名裁剪错误：保留两处最小补丁 `scripts/native-sdk-windows-path.patch`，构建验证补丁可应用/已应用，不修改生成 ABI 文件。
3. 固定高度虚拟列表生成未使用的 Zig 变量：使用 SDK 支持的高度估计器，固定返回 40。
4. TypeScript 子集检查与最终编译并非同一个验收：整数证明、枚举名称、字节文本方法有更严格约束。以最终原生构建为准。
5. 首次真实数据连接地址错误：普通模板插值会把 Uint8Array 转成逗号分隔的数字，改为明确的字节拼接，并增加 URL 回归测试。
6. 复选框在模型批量变更时保留旧值：原型对批量全选/清除/查询结果变更重建复选框子节点，行 ID 与滚动位置仍保持；不修改 SDK 控件引擎。
7. 上游 `scripts/gate.sh fast HEAD` 出现其他 canvas hook / slider 的 Windows 类型路径错误、部分 CRLF 黄金数据差异和服务测试失败。失败后中止剩余门禁，不能记为完整 SDK 测试通过。本轮只对实际使用的路径做验收。

## Windows 实机观察

- Windows 11 专业版 build 26200，i9-12900H，125% DPI。实际 Native SDK 快照报告 `gpu_backend=direct2d`、`present_fallback=none`，不是从 manifest 的历史 `metal` 枚举推测。
- 真实 1,000 项 ZIP 已通过现有 Worker 读取，显示 200 项首屏；第一轮日志 scan/index 192 ms、查询 19 ms，仅为单次诊断观察，不能视为稳定基准。
- 中文文件名正常显示。实际输入法通过按键 `x` 显示候选、按 `1` 提交“想”，文本进入搜索框，Enter 查询得到空结果。
- **输入法候选框位置异常**：出现在列表底部附近，没有跟随搜索框光标。普通提交可用，不代表 IME 体验通过完整验收；未覆盖所有输入法、组合编辑与跨屏 DPI。
- **Windows 辅助功能不完整**：Computer Use 的 UI Automation 只看到外层窗口/窗格，内部控件在 SDK 自有快照中可见。这两种树不能等同；不能宣称 Narrator 完整可用。
- 真实样本两项非连续复选可更新数量；初次批量全选暴露旧值保留问题，修复与复测记录另见下方结论。

## 对比方法

两个宿主使用相同生成 ZIP：1,000 项 154,022 字节，100,000 项 15,400,098 字节。均为固定内容和文件时间，未使用真实用户文件。

SHA-256：

- `entries-1000.zip`：`28a48114f6bcf474e2865530a9e7836639b6da4079e684c66fb48100ea9e9706`
- `entries-100000.zip`：`1c4121fee94669379326eed36dbcfe857a4e3c6670702bcd2bb74b0bd881fd35`

`Measure-UIValidation.ps1` 顺序启动宿主并采样整个观察到的进程树；Native 包括 .NET 适配器，WebView2 包括浏览器子进程。每秒采样是近似值，可能错过短命 Worker 或瞬间峰值；工作集会重复计算共享页，私有字节是提交量，不是物理驻留。

每次运行取最后 5 个采样点作为稳定观察。均为热文件缓存启动，未清系统缓存；没有首次可交互时间指标。Native 在开窗前扫描，WebView2 先开窗后扫描；空白窗口出现时间不能直接比较。

## 验证结论与测量结果

最终构建通过，4 项原型逻辑测试与现有 98 项 .NET 回归通过。补丁也在全新 LF 源码副本通过 `git apply --check`。

实机复测：真实 100,000 项归档可读，分页从 0 前进到 200；中文查询 `项目说明_099999` 返回最后一个文件。批量全选后可见行立即全部勾选、计数 200，清除后全部取消、计数 0，修复前的旧显示问题不再复现。测试归档 SHA-256 在验证后保持不变。

生成的十万项虚拟列表通过滚轮更新可见行，运行时快照仅约 175 个控件节点，而非十万个行控件。滚动条拖动检查未到达预期末尾，尚未判定是命中区域还是控件行为，未记为通过；长期滚动帧时间也尚未形成统计。

以下每格是两次运行的最后 5 个采样点均值范围，单位 MiB；不是冷启动或完整性能基准。

| 真实 ZIP | Native 私有提交 | WebView2 私有提交 | Native 工作集合计 | WebView2 工作集合计 |
|---|---:|---:|---:|---:|
| 1,000 项 | 335.9–337.5 | 346.7–349.4 | 168.5 | 552.2–559.3 |
| 100,000 项 | 375.8–376.5 | 402.8–408.4 | 210.1–210.4 | 620.2–623.6 |

- Native 稳态树为 3 个进程：UI、.NET 适配器、console host；Worker 完成扫描后退出。WebView2 为 7 个进程。1,000 项第一轮 Native UI 本身约 322.2 MiB 私有提交、115.3 MiB 工作集，因此不能把“没有浏览器”当作低提交量证明。
- 按两轮中点计算，本轮 Native 私有提交约低 3%（1,000 项）和 7%（100,000 项）。工作集更低，但共享页重复计算与提交/驻留差异意味着不能把工作集合计差额当作实际物理内存节省。
- Native 完整验证目录约 230.2 MiB，WebView2 约 271.5 MiB，均为未压缩自包含目录；Native 排除运行日志/自动化数据，包含 CJK 字体和许可；WebView2 不含系统 WebView2 Runtime。Native 可执行文件约 12.7 MiB，不能拿它与 WebView2 的整个目录比较。
- Native 两轮扫描/建索引诊断：1,000 项 202/176 ms；100,000 项 726/631 ms。它们没有同等的 WebView2 首次可交互测量，不能用于启动速度排名。
- Native 开启了验证用自动化服务；两者界面功能/列数并不完全相同。此结果比较的是当前原型组合，不是框架理论下限。
- 原始采样 JSON、后端日志、样本哈希与截图留在 `.artifacts/ui-comparison`，不提交二进制/测试归档或个人数据。截图 `evidence/native-100000-select-all.jpg`、`native-100000-search.jpg`、`webview2-1000.jpg`。

**当前判断：保留 Native SDK 为候选，不据此锁定正式技术栈。** 它已证明暖石墨原生绘制与现有 Core/Worker 可以衔接；中文候选框定位、Windows UI Automation、完整键盘/鼠标范围选择、冷启动与持续滚动性能仍是后续门槛。当前快捷键仅为原型子集，尚未实现 Explorer 完整 Ctrl/Shift 点击选择和跨页选择。
