# 纯 WPF 暖石墨验证与三方案对比

> 后续决策：用户已正式选择纯 WPF，见 ADR-016 和 WPF_Client_Migration.md。以下保留选型验证时的测量与结论。

日期：2026-10-23。状态：独立只读验证，不替换原客户端，不锁定正式 UI 技术，不代表 M1 完成。

## 实现

- 新增 `src/AileArc.WpfValidation`：.NET 10 / WPF 自定义资源字典、控件模板与三栏布局，没有 WebView2、浏览器、JavaScript 或第三方 UI 框架。
- 保留现有 Core / Worker / 7-Zip 引擎；真实扫描仍在 Worker。直接复用 `ArchivePresentation`，与 Native SDK、WebView2 都采用每页最多 200 项及原有字节预算。
- 输入框保留 WPF TextBox 的文本、选区和 IME 路径；只在组合结束后接受 Enter 查询。没有重新实现文本编辑器。
- DataGrid `Extended` / `FullRow` 选择，行状态与复选框双向同步；支持当前页全选、半选、清除，翻页/切换样本清除选择。复选框点击独立增减，行点击沿用原生选择行为。
- 数据表使用回收式行虚拟化，固定 40 像素行高，有限高度的布局容器；生成 1,000 / 100,000 项样本与真实归档模式分开标识。生成样本保留完整 C# 行模型，不能与 Native SDK 的按需生成行混作内存公平比较。
- 名称搜索在后台线程执行，扫描/查询带取消和世代检查，过时结果不覆盖新状态。Windows 文件选择器选包；只读目录/分页/详情，不接入提取、创建、外部打开或写入。

## 构建和启动

```powershell
./scripts/Build-WpfValidation.ps1 -Test
./scripts/Build-WpfValidation.ps1 -Run
./scripts/Build-WpfValidation.ps1 -Run -Archive 'D:/Archives/example.zip'
```

输出 `.artifacts/wpf-validation/Release/AileArc.Wpf.Validation.exe`。默认打开生成样本，也可以在窗口中选择真实归档；自包含 .NET，无须 WebView2。无文件关联/系统信任/安装状态修改。

## 验证证据

- 实际 WPF 控件自检 10 项通过：Unicode 文本行几何、十万项数量、首屏虚拟化、非连续选择、半选、全选、全选中排除一项、半选再次全选、清空、末项虚拟化及样本替换等断言组合。首屏 15、末尾 14 个 DataGridRow。自检是程序化控件验证，不等同于物理输入验证。
- 实际输入法：在搜索框输入 `x`，候选框出现在搜索框光标下方；选择第一候选提交“想”。第二次组合输入后 Escape 取消，保留已提交文本，没有误查询。首次窗口处于英文输入语言，切换到现有中文输入语言后重测；不把普通英文键入当作 IME 通过。
- 中文全包查询 `项目说明_099999` 命中十万项 ZIP 的最后一个文件；真实分页可前进到 offset 200，旧页选择清除。
- 鼠标复选第 1、3 行得到 2 项和半选表头；全选当前页得到 200 项，所有可见复选框立即同步。
- Shift+Down 实际扩展两行选择。Ctrl+Down 观察到 DataGrid 的边界跳转/重选行为，并非资源管理器式的焦点移动；Ctrl+Space 在本机输入环境未观察到预期选择切换。这些键位仍需产品化适配，不能宣称已经完整复刻 Explorer。
- 十万项生成列表可用鼠标拖动滚动条到约第 99,519 项附近，Ctrl+End 可到第 99,999 项；末项正常显示、可选。
- Windows UI Automation 可以识别编辑框、数据表、具名行、行复选框和选择计数。此处只证明控件语义可见，未完成 Narrator 全流程或所有辅助软件验证。
- 自定义 TextBox 初版重复应用 Padding 导致文本裁切，移除内容宿主的重复边距后复测中文完整显示，并增加文本行几何断言。大列表滚动条已设置滑块最小高度。

本机证据位于 `.artifacts/ui-comparison-wpf/evidence`：`wpf-ime-candidate.jpg`、`wpf-100000-search.jpg`、`wpf-100000-all.jpg`、`wpf-virtual-last.jpg`。原始样本、日志、截图均不提交仓库。

## 对比方法

使用上一轮同一批固定 ZIP，未读取真实用户文件：

| 样本 | 字节 | SHA-256 |
|---|---:|---|
| 1,000 项 | 154,022 | `28a48114f6bcf474e2865530a9e7836639b6da4079e684c66fb48100ea9e9706` |
| 100,000 项 | 15,400,098 | `1c4121fee94669379326eed36dbcfe857a4e3c6670702bcd2bb74b0bd881fd35` |

三种宿主已在同一轮重新采样，每种规模两次、每次 12 个采样点，最后 5 点均值作为稳定观察；第二轮反转宿主顺序。采样不附加 UI Automation 检查，不同时运行被测宿主，包含每次启动观察到的整个进程树。WPF/Native/WebView2 各自包含现有后端与被采样捕获的 Worker。

```powershell
./scripts/Measure-UIValidation.ps1 -HostKind WPF -Archive .artifacts/ui-comparison/samples/entries-1000.zip -Seconds 12 -OutputDirectory .artifacts/ui-comparison-wpf
python scripts/Summarize-UIValidation.py .artifacts/ui-comparison-wpf
```

私有字节是提交量，非物理驻留；工作集合计可能重复计算共享页。约每秒采样可能错过短命 Worker 或瞬间峰值，报告中的峰值只指采样最大值。未清系统缓存，不声称冷启动；没有跨三宿主的首次可交互计时。三版原型功能/列数和诊断开关存在差异，比较的是当前实现，不是框架理论下限。

## 资源结果

环境：Windows 11 专业版 build 26200，i9-12900H，125% DPI，.NET SDK 10.0.100；WebView2 Runtime 155.0.4283.62，Native SDK 0.11.0。下表单位 MiB，每格是两轮稳定均值的范围。

### 私有提交量

| 真实 ZIP | 纯 WPF | Native SDK + .NET 后端 | WPF + WebView2 |
|---|---:|---:|---:|
| 1,000 项 | **127.9–128.7** | 336.0–338.3 | 349.4–352.3 |
| 100,000 项 | **164.3–165.1** | 377.3–377.5 | 406.3–410.8 |

按两轮中点计算，纯 WPF 相比本轮另外两个组合的私有提交量约低 56%–63%。这是当前界面、数据规模与构建配置的结果，不是跨所有应用的框架排名。

### 工作集合计

| 真实 ZIP | 纯 WPF | Native SDK + .NET 后端 | WPF + WebView2 |
|---|---:|---:|---:|
| 1,000 项 | 186.1–186.2 | **165.8–168.5** | 552.6–557.7 |
| 100,000 项 | 221.5–221.7 | **207.4–207.5** | 613.4–615.8 |

Native SDK 的工作集合计略低于 WPF；不能根据 WPF 私有提交较低就声称它在全部内存指标上都最低。共享页、图形驱动和系统合成器的资源未作独立归因，未测专用 GPU 内存。

稳态观察进程数：WPF 1、Native 3（包括 .NET 适配器及 console host）、WebView2 7。独立 Worker 在扫描完成后退出。

自包含未压缩验证目录：WPF 268.7 MiB、Native 230.2 MiB、WebView2 271.5 MiB；排除测试报告/运行日志/自动化数据。WebView2 不包括系统运行时，Native 包含 CJK 字体，WPF 使用系统字体；三者都保留各自的 .NET/Worker 部署，未优化重复运行时文件。包体不是进程占用。

12 份原始结果位于 `.artifacts/ui-comparison-wpf/*-entries-*-run-*.json`，与上一轮 `.artifacts/ui-comparison` 分开。前后两次真实 ZIP 哈希一致。

## 体验与边界

| 项目 | 纯 WPF 本轮 | Native SDK 上轮 | WebView2 已有验证 |
|---|---|---|---|
| 暖石墨自定义布局 | 实际 XAML / 控件模板 | 原生绘制 | React / CSS |
| 真实 Core / Worker | 直接复用 | 本地只读适配器 | 宿主消息桥接 |
| 中文输入法 | 候选定位/提交/取消已实测 | 提交成功，候选位置异常 | 既有文本输入；本轮不重做完整 IME 验收 |
| 大列表 | 十万项虚拟行与末项导航 | 十万项窗口化渲染，滚动条拖动待查 | 真实归档 200 项分页 |
| 多选 | 原生扩展选择、半选状态；部分键位待适配 | 复选/全选，批量刷新需原型补偿 | 既有复选及选择流程 |
| 系统 UI Automation | 内部控件可识别 | 当时仅识别外层窗格 | 既有宿主检查可识别页面内容 |

共同尚未迁移：归档写入、工作副本、完整双语、现代 Shell、安装生命周期、完整异常与恢复流程。纯 WPF 此版暂未接入密码/编码对话框、排序和拖放；100 GB/百万 Entry 不是本轮已达成能力。

**建议：将纯 WPF 自定义界面列为下一阶段主候选。** 本轮它保留了现有 .NET 业务与成熟控件机制，私有提交明显较低，输入法候选定位和系统控件语义表现符合预期；实现没有新增 Zig/scriptc/npm 工具链。该建议仍需用户确认，不能把本轮结果解释为已迁移正式客户端。下一步应集中处理文件管理器键位、长文件名/不同 DPI、密码与异常流程，再逐步接回实际归档操作。

参考：[WPF 行虚拟化](https://learn.microsoft.com/en-us/dotnet/api/system.windows.controls.datagrid.enablerowvirtualization)、[WPF 文本组合事件](https://learn.microsoft.com/en-us/dotnet/api/system.windows.input.textcompositionmanager)。
