# AileArc 开发设计文档

> **Project Codename:** Project Arcana\
> **Product Name:** AileArc\
> **Product Positioning:** Modern Archive Manager for Windows\
> **Core Philosophy:** Archive should feel like a folder.\
> **Document Status:** Product Blueprint / 已确认首个里程碑基线（2026-09-15）\
> **Target Platform:** 首个里程碑 Windows 11 x64；Windows 10 后续支持；ARM64 未来评估；不支持 32 位系统\
> **Primary UI Stack:** C# / .NET / WinUI 3\
> **Native Integration Stack:** C++ / Win32 / COM\
> **Archive Engine:** 完整 7-Zip 引擎为主；引擎仅在 Worker 中执行；LZMA SDK 不作为完整多格式引擎的替代\
> **License Strategy:** 项目自有代码采用 MIT；第三方组件保留各自许可

---

## 已确认的实施依据（2026-09-15）

本文件保留长期产品蓝图。以下新文档记录与项目作者讨论后确认的开发决策；本文件中旧阶段划分、预览优先级、首版范围与这些决策冲突时，以新文档为准：

- [首个里程碑实施规范](AileArc_M1_Implementation_Specification.md)：范围、操作语义、Smart Extract、安全与验收。
- [架构决策记录](AileArc_Architecture_Decisions.md)：引擎、Worker、语言资源与开发环境。
- [开发历程](Development_Log.md)：实际完成情况、验证结果与后续工作。

首个里程碑以日常可用为目标：浏览、搜索、排序、外部打开、基础解压与智能解压、基础 ZIP/7Z 创建；后半段加入少量资源管理器右键命令与开发安装/卸载支持。内置预览、已有压缩包编辑、自动回写、标签页、虚拟文件拖出、插件与挂载后置。百万 Entry 与 100 GB 为容量方向，首版不要求达到终极性能目标。首版内置简体中文和 en-US 资源，默认简体中文，切换后重启整个应用生效。允许数月迭代，代码与开发历程在 GitHub 开源，官网与正式 Release 不作为当前前置条件。

---

# 1. 项目背景

AileArc 是一个面向现代 Windows 桌面环境的压缩包浏览、管理、压缩与解压软件。

当前 Windows 压缩软件生态中，底层压缩能力已经非常成熟，但产品层仍存在明显断层：

- 7-Zip 拥有成熟、稳定、高效的压缩引擎，但 UI 与现代 Windows 使用习惯脱节；
- NanaZip 对 7-Zip 进行了较好的现代化改造，但总体仍延续传统压缩软件的信息架构；
- PeaZip 功能丰富，但整体 UI/UX 更偏传统高级工具；
- Bandizip 在普通用户体验上较成熟，但为闭源商业软件，且部分高级能力受到版本限制；
- 市面上缺乏一个真正从现代文件管理体验重新设计的、开源且高质量的 Archive Manager。

AileArc 的目标不是重新发明压缩算法，而是围绕成熟压缩引擎重新设计完整产品体验，使压缩包的打开、浏览、搜索、预览、拖放、编辑与普通文件夹尽可能接近。

---

# 2. 产品愿景

## 2.1 核心愿景

> **Archive should feel like a folder.**

用户不应将压缩包视为一个必须经过“解压”才能操作的特殊文件，而应将其视为一种可直接访问的容器。

AileArc 应让以下对象在用户感知层尽可能一致：

```text
普通文件夹
    ↕
ZIP
    ↕
7Z
    ↕
RAR
    ↕
TAR
    ↕
其他 Archive
```

因此产品设计应始终围绕以下问题判断功能是否合理：

- 普通文件夹能搜索，压缩包也应该能搜索；
- 普通文件夹能预览，压缩包也应该能预览；
- 普通文件夹能复制、拖放，压缩包也应该能复制、拖放；
- 普通文件夹有路径导航，压缩包也应该有 Breadcrumb；
- 普通文件夹能查看缩略图，压缩包也应该支持缩略图；
- 普通文件夹能多选、排序、筛选，压缩包也应该能；
- 普通文件夹可以被当作日常工作空间，压缩包也应尽量做到这一点。

---

# 3. 项目目标

## 3.1 核心目标

AileArc 需要同时满足以下目标：

1. **现代**
   - 原生 Windows 11 风格；
   - 深色模式；
   - Fluent / Mica；
   - 高 DPI；
   - 原生窗口与系统交互；
   - Windows 11 现代右键菜单。

2. **易用**
   - 减少传统压缩软件中的复杂设置；
   - 提供合理默认值；
   - 常用操作一到两次点击完成；
   - Smart Extraction；
   - 任务状态透明、错误信息友好。

3. **高性能**
   - 启动快速；
   - 百万级 Archive Entry 仍可浏览；
   - 大型压缩包不阻塞 UI；
   - 解压、搜索、预览均异步化；
   - 可取消任务。

4. **高可靠**
   - 损坏压缩包不能拖垮主 UI；
   - 压缩任务可恢复/清理；
   - Worker 崩溃不导致整个应用崩溃；
   - 安全处理异常路径与恶意 Archive。

5. **开放**
   - 核心功能开源；
   - 可扩展 Preview Provider；
   - 可扩展 Archive Engine；
   - 提供 CLI；
   - 后续可提供插件 SDK。

---

# 4. 非目标

AileArc 初期不追求以下目标：

- 自研新的压缩算法；
- 取代 7-Zip 的底层压缩性能；
- 第一版支持所有小众压缩格式；
- 第一版实现跨平台；
- 第一版实现云盘同步；
- 第一版实现复杂企业管理能力；
- 第一版做成完整文件管理器；
- 第一版支持所有 Windows Shell Edge Case；
- 第一版直接挑战 Bandizip 数十年积累的全部兼容性。

项目必须坚持“产品体验优先，功能边界清晰”的原则。

---

# 5. 目标用户

## 5.1 普通用户

典型需求：

- 双击打开 ZIP；
- 浏览内容；
- 解压到当前目录；
- 解压到同名目录；
- 智能解压；
- 压缩文件或文件夹；
- 加密码；
- 查看图片；
- 查看文本；
- 拖动文件。

## 5.2 开发者

典型需求：

- 浏览源码压缩包；
- 查看 JSON/YAML/XML；
- 查看日志；
- 快速搜索文件名；
- 查看依赖包；
- 解压构建产物；
- CLI 自动化。

## 5.3 运维与高级用户

典型需求：

- TAR / TAR.GZ / TAR.ZST；
- 服务器日志；
- ISO/WIM 等只读浏览；
- Hash；
- 大压缩包；
- 分卷；
- 校验；
- 批量任务；
- 网络路径。

---

# 6. 品牌与命名

## 6.1 正式名称

**AileArc**

建议展示：

> **AileArc — Modern Archive Manager for Windows**

## 6.2 项目代号

**Project Arcana**

可用于：

- 开发阶段；
- 内部架构文档；
- Early Preview；
- Git 分支/里程碑代号。

## 6.3 核心品牌关键词

- Modern
- Native
- Fast
- Open
- Archive
- Folder-like

---

# 7. 总体技术架构

推荐采用多进程、分层架构。

```text
┌─────────────────────────────────────┐
│              AileArc.UI             │
│      C# / .NET / WinUI 3            │
└─────────────────┬───────────────────┘
                  │
                  │ Application API
                  ▼
┌─────────────────────────────────────┐
│             AileArc.Core            │
│ Archive Model / Search / Tasks      │
│ Preview / Settings / History        │
└───────┬───────────────┬─────────────┘
        │               │
        │ IPC           │ Provider API
        ▼               ▼
┌───────────────┐   ┌─────────────────┐
│ AileArc.Worker│   │ AileArc.Preview │
│ Isolated Jobs │   │ Preview System  │
└───────┬───────┘   └─────────────────┘
        │
        ▼
┌─────────────────────────────────────┐
│            AileArc.Engine           │
│      Native Archive Abstraction     │
├─────────────────────────────────────┤
│ 7-Zip SDK / 7z.dll / libarchive... │
└─────────────────────────────────────┘

Windows Integration:
┌─────────────────────────────────────┐
│             AileArc.Shell           │
│ C++ / COM / IExplorerCommand        │
│ File Association / Context Menu     │
└─────────────────────────────────────┘

Installer:
┌─────────────────────────────────────┐
│             AileArc.Setup           │
│ Traditional Installer + Identity    │
└─────────────────────────────────────┘
```

---

# 8. 工程结构

建议初始 Solution：

```text
AileArc/
├─ src/
│  ├─ AileArc.UI/
│  ├─ AileArc.Core/
│  ├─ AileArc.Engine/
│  ├─ AileArc.Worker/
│  ├─ AileArc.Shell/
│  ├─ AileArc.Preview/
│  ├─ AileArc.CLI/
│  ├─ AileArc.SDK/
│  └─ AileArc.Shared/
│
├─ installer/
│  └─ AileArc.Setup/
│
├─ tests/
│  ├─ AileArc.Core.Tests/
│  ├─ AileArc.Engine.Tests/
│  ├─ AileArc.Security.Tests/
│  ├─ AileArc.Performance.Tests/
│  └─ AileArc.Integration.Tests/
│
├─ benchmarks/
├─ docs/
├─ samples/
├─ assets/
├─ scripts/
├─ third_party/
├─ .github/
└─ README.md
```

---

# 9. UI 技术栈

## 9.1 推荐

- C#
- .NET
- WinUI 3
- Windows App SDK
- MVVM

## 9.2 UI 设计原则

- Windows 11 原生视觉语言；
- 避免 Electron 风格；
- 避免大量自绘控件；
- 优先使用系统组件；
- 支持 Light / Dark / System；
- 支持 Mica；
- 支持高 DPI；
- 支持键盘导航；
- 支持 Accessibility；
- 动效节制；
- 不为了“现代”牺牲信息密度。

---

# 10. 主窗口设计

建议：

```text
┌────────────────────────────────────────────────────────┐
│ ← → ↑  MinecraftServer.7z                  🔍 Search   │
├────────────────────────────────────────────────────────┤
│ Home / MinecraftServer / plugins                       │
├────────────────────────────────────────────────────────┤
│ Name                Size       Type       Modified      │
│ 📁 EssentialsX                                        │
│ 📁 LuckPerms                                          │
│ 📄 server.jar       52.1 MB    JAR                     │
│ 📄 paper.yml        5.4 KB     YAML                    │
│                                                        │
├────────────────────────────────────────────────────────┤
│ 134 items      1.42 GB → 612 MB       7Z · LZMA2      │
└────────────────────────────────────────────────────────┘
```

---

# 11. 标签页系统

后续支持：

```text
[server.zip] [photos.7z] [logs.tar.zst] [+]
```

功能：

- 新标签页；
- 关闭标签；
- 中键关闭；
- Ctrl+T；
- Ctrl+W；
- Ctrl+Tab；
- 恢复最近关闭；
- 拖动排序；
- 可选多窗口模式。

---

# 12. Archive Browser

## 12.1 文件列表

字段：

- Name
- Extension
- Type
- Original Size
- Packed Size
- Compression Ratio
- CRC
- Method
- Modified Time
- Attributes
- Encrypted
- Path

## 12.2 查看模式

- Details
- List
- Large Icons
- Thumbnail Grid

## 12.3 排序

支持：

- 名称；
- 大小；
- 类型；
- 修改日期；
- 压缩后大小；
- 压缩率；
- 路径。

---

# 13. Breadcrumb 路径导航

示例：

```text
server.7z > plugins > Essentials > config
```

支持：

- 点击任意层级；
- 键盘导航；
- 复制内部路径；
- 面包屑输入模式；
- Back/Forward；
- Up。

---

# 14. 搜索系统

## 14.1 基础搜索

- 文件名；
- 路径；
- 扩展名；
- 模糊匹配；
- 即时搜索。

## 14.2 高级搜索

未来：

```text
ext:png
size:>10MB
encrypted:true
modified:>2026-01-01
path:/plugins/
```

## 14.3 搜索架构

Archive 打开后建立轻量索引：

```text
ArchiveIndex
├─ EntryID
├─ ParentID
├─ Name
├─ FullPath
├─ Size
├─ PackedSize
├─ Modified
├─ Attributes
└─ Flags
```

避免直接依赖 UI Tree。

---

# 15. 大压缩包虚拟化

必须支持百万级 Entry。

禁止：

```text
foreach entry:
    UI.Items.Add(entry)
```

采用：

- Virtualized List；
- Lazy Load；
- 分页/窗口化；
- 只渲染可见 Entry；
- 延迟获取扩展属性。

性能目标：

- 100 万 Entry 打开后 UI 不冻结；
- 滚动过程中保持流畅；
- 搜索不阻塞 UI；
- 索引过程提供进度。

---

# 16. 压缩包打开流程

```text
Open Archive
    ↓
Signature Detection
    ↓
Format Detection
    ↓
Metadata Scan
    ↓
Entry Index
    ↓
UI Ready
    ↓
Background Metadata Enrichment
```

扩展名只能作为参考，不作为唯一判断依据。

---

# 17. Archive Engine 抽象层

定义统一接口：

```text
IArchiveEngine
```

建议能力：

```text
Open()
ListEntries()
ReadEntry()
Extract()
Compress()
Test()
Update()
DeleteEntry()
RenameEntry()
GetProperties()
```

Engine 层屏蔽不同底层库差异。

---

# 18. 首期格式支持

## 18.1 解压

优先：

- ZIP
- 7Z
- RAR
- TAR
- GZ
- BZ2
- XZ
- ZST
- TAR.GZ
- TAR.XZ
- TAR.ZST

## 18.2 压缩

优先：

- ZIP
- 7Z
- TAR
- TAR.GZ
- TAR.XZ
- TAR.ZST

## 18.3 后续只读格式

- ISO
- WIM
- CAB
- VHD/VHDX
- DMG
- RPM
- DEB
- APK
- JAR
- XPI

## 18.4 小众格式

后续评估：

- ACE
- ARJ
- LZH
- ALZ
- EGG

---

# 19. Smart Extraction

核心功能。

## 19.1 行为

Archive 只有一个根目录：

```text
archive.zip
└─ Project/
```

则：

```text
Extract Here
```

Archive 为散文件：

```text
archive.zip
├ a.txt
├ b.txt
└ image.png
```

则自动创建：

```text
archive/
├ a.txt
├ b.txt
└ image.png
```

## 19.2 规则

- 单根目录避免重复套目录；
- 多根内容自动创建同名目录；
- 目录冲突时提供策略；
- 可设置默认行为；
- 右键菜单直接调用。

---

# 20. 解压功能

支持：

- Extract；
- Extract Here；
- Extract To...；
- Smart Extract；
- Extract Selected；
- Extract and Open Folder；
- 保持目录结构；
- 忽略目录结构；
- 覆盖策略；
- 重名策略；
- 密码输入；
- CRC 验证；
- 完成后打开目录。

---

# 21. 覆盖策略

用户可选：

- Ask；
- Overwrite；
- Skip；
- Rename Automatically；
- Overwrite if Newer；
- Overwrite if Size Differs。

支持：

```text
Apply to all
```

---

# 22. 压缩功能

## 22.1 创建 Archive

支持：

- 文件；
- 文件夹；
- 多选；
- Explorer 右键；
- 拖入 AileArc。

## 22.2 参数

- Format；
- Compression Level；
- Compression Method；
- Dictionary Size；
- Word Size；
- Solid Mode；
- Threads；
- Encryption；
- Encrypt File Names；
- Split Volumes；
- Delete Source after Success。

## 22.3 简化模式

默认 UI 不应暴露所有高级参数。

推荐：

```text
Fast
Balanced
Maximum
Custom
```

---

# 23. Archive 编辑

支持：

- 添加文件；
- 删除 Entry；
- 重命名；
- 创建目录；
- 替换文件；
- 拖入文件；
- Clipboard Paste。

某些格式无法原地修改时，可通过：

```text
Temporary Rebuild
    ↓
Validation
    ↓
Atomic Replace
```

实现。

---

# 24. Atomic Archive Update

更新 Archive 时：

禁止直接破坏原文件。

推荐：

```text
archive.7z
    ↓
archive.7z.ailearc.tmp
    ↓
Rebuild
    ↓
Test
    ↓
Atomic Replace
```

失败时保留原 Archive。

---

# 25. Worker 进程

压缩、解压、测试、预览解码等高风险或耗时任务放入：

```text
AileArc.Worker.exe
```

优势：

- UI 不崩；
- 更容易 Cancel；
- 可限制权限；
- 崩溃可重启；
- 第三方引擎故障隔离；
- 可实现并发任务。

---

# 26. IPC

UI ↔ Worker 推荐使用：

- Named Pipes；
- Local RPC；
- 其他高性能本地 IPC。

传输内容：

- Job Request；
- Progress；
- Entry Metadata；
- Errors；
- Stream Handle；
- Cancel。

---

# 27. Task Center

统一管理：

- Compress；
- Extract；
- Test；
- Hash；
- Preview Decode；
- Archive Scan。

界面：

```text
Tasks

MinecraftServer.7z
Extracting
███████████████░░░ 78%
1.1 GB / 1.4 GB
92 MB/s
00:04 remaining
```

支持：

- Pause（可行时）；
- Cancel；
- Retry；
- Open Destination；
- Show Details；
- Clear Completed。

---

# 28. 多任务调度

策略：

- SSD/HDD 区别；
- 同磁盘任务避免无意义并行；
- CPU 密集型压缩控制线程；
- 用户可设置最大并行任务数；
- 小任务可并发；
- 大型解压避免争抢 I/O。

---

# 29. Universal Preview

核心差异化功能。

定义：

```text
IPreviewProvider
```

## 29.1 图片

支持：

- JPG
- PNG
- WEBP
- GIF
- BMP
- TIFF
- SVG（安全策略下）

显示：

- 尺寸；
- 文件大小；
- 格式；
- Alpha；
- EXIF（后续）。

## 29.2 文本

支持：

- TXT
- LOG
- INI
- CFG
- JSON
- XML
- YAML
- TOML
- Markdown
- CSV

## 29.3 源码

支持：

- C/C++
- C#
- Java
- Python
- JavaScript
- TypeScript
- HTML
- CSS
- SQL
- Shell
- PowerShell

支持：

- Syntax Highlight；
- Encoding Detection；
- Line Numbers；
- Search。

## 29.4 PDF

后续支持只读预览。

## 29.5 音视频

后续：

- Metadata；
- Thumbnail；
- 可选播放。

## 29.6 字体

后续：

- 字体名称；
- 字符示例；
- 字形预览。

---

# 30. Preview 缓存

考虑 Solid Archive。

设计：

```text
Preview Request
    ↓
Block Decode
    ↓
Memory Cache
    ↓
Disk Cache
```

缓存策略：

- LRU；
- 大小上限；
- 用户可清理；
- 临时数据自动删除；
- 敏感文件不永久缓存。

---

# 31. Solid Archive 优化

问题：

用户预览一个文件可能需要解码前面的多个 Entry。

优化：

- Block-level Cache；
- Sequential Prefetch；
- Preview Prediction；
- Cancel；
- Background Decode；
- Memory Limit；
- Cache Reuse。

---

# 32. Thumbnail System

接口：

```text
IThumbnailProvider
```

支持：

- 图片缩略图；
- 视频封面；
- PDF 首页面；
- 文件类型图标；
- Archive 内嵌套 Archive 图标。

缓存：

- Memory Cache；
- Disk Cache；
- Size-aware Cache。

---

# 33. 拖放系统

这是高难度模块。

## 33.1 Drag Into AileArc

Explorer → Archive：

- 添加文件；
- 添加目录；
- 多选；
- Copy/Move 语义。

## 33.2 Drag Out of AileArc

Archive → Explorer：

目标体验：

> 不要求用户先手动解压。

实现方向：

- IDataObject；
- CFSTR_FILEDESCRIPTOR；
- CFSTR_FILECONTENTS；
- IStream；
- Async data capability。

尽可能采用流式解码，而不是先完整写入 Temp。

---

# 34. Clipboard

支持：

- Ctrl+C；
- Ctrl+X（允许格式下）；
- Ctrl+V；
- Copy Path；
- Copy Name；
- Copy Internal Archive URI。

未来可设计：

```text
ailearc://archive/path
```

---

# 35. Windows Shell Integration

项目：

```text
AileArc.Shell
```

语言建议：

- C++
- Win32
- COM

功能：

- Windows 11 Context Menu；
- File Association；
- Explorer Commands；
- Archive Icon；
- Open With；
- Shell verbs。

---

# 36. Windows 11 右键菜单

目标：

```text
AileArc
├─ Open
├─ Smart Extract
├─ Extract Here
├─ Extract to "xxx\"
└─ Test Archive
```

对普通文件：

```text
AileArc
├─ Add to archive...
├─ Add to xxx.7z
└─ Add to xxx.zip
```

---

# 37. 文件关联

支持：

- ZIP
- 7Z
- RAR
- TAR
- GZ
- BZ2
- XZ
- ZST
- ISO 等

需要：

- 用户主动选择；
- 可批量关联；
- 可恢复系统默认；
- 避免强制劫持关联。

---

# 38. 安装模式

目标：

> 传统安装体验 + 现代 Windows 集成。

用户可选择：

```text
C:\Program Files\AileArc
D:\Apps\AileArc
E:\Software\AileArc
```

推荐：

- Unpackaged WinUI；
- Traditional Installer；
- 必要时通过 identity/sparse package 获得现代系统能力。

---

# 39. Portable

长期支持：

```text
AileArc.Portable
```

限制：

- Shell Integration 可选；
- 不自动修改文件关联；
- 配置保存在程序目录；
- 支持 USB 运行。

---

# 40. CLI

可执行：

```text
ailearc.exe
```

或：

```text
ailearc-cli.exe
```

示例：

```bash
ailearc open test.7z
ailearc extract test.zip
ailearc extract test.zip -o D:\Output
ailearc pack folder --format 7z
ailearc test archive.7z
ailearc list archive.zip
ailearc hash file.iso
```

---

# 41. CLI 自动化

支持：

- JSON 输出；
- Quiet；
- Progress Off；
- Exit Code；
- CI/CD；
- PowerShell。

示例：

```bash
ailearc list test.zip --json
```

---

# 42. 密码功能

## 42.1 基础

- 密码 Archive；
- 自动重新尝试；
- Session Password Cache。

## 42.2 Password Vault

长期：

- Windows Credential Manager / DPAPI；
- 本地加密；
- 用户主动保存；
- Archive Fingerprint Mapping。

绝不明文保存。

---

# 43. Encryption

支持：

- ZIP AES；
- 7Z AES；
- Encrypt File Names；
- Password Strength Indicator。

禁止记录用户密码到日志。

---

# 44. Security Layer

这是核心模块。

所有 Entry 必须经过：

```text
Archive Entry
    ↓
Path Normalize
    ↓
Security Validate
    ↓
Destination Mapping
    ↓
Extract
```

---

# 45. Path Traversal 防护

禁止：

```text
..\..\evil.exe
```

禁止绕过方式：

- 多层 ../；
- 混合分隔符；
- Unicode 变体；
- Absolute Path；
- Drive Path；
- UNC Path。

---

# 46. Windows 特殊路径

防护：

- C:\...
- \\server\share
- \\?\
- \??\
- Device Path

---

# 47. 特殊文件名

处理：

- CON
- PRN
- AUX
- NUL
- COM1...
- LPT1...
- trailing dot
- trailing space

需要定义：

- Skip；
- Rename；
- Error。

---

# 48. Links

Archive 中可能存在：

- Symbolic Link；
- Hard Link；
- Junction。

默认安全策略：

- 禁止链接逃逸目标目录；
- 外部链接需要警告；
- 可配置高级模式。

---

# 49. NTFS ADS

必须识别：

```text
file.txt:stream
```

默认避免不透明地创建危险 ADS。

---

# 50. Zip Bomb 防护

检测：

- 极端压缩比；
- 解压后预测容量；
- Entry 数量；
- 嵌套 Archive；
- Recursive Bomb。

用户警告：

```text
Archive may expand to 2.4 TB.
Continue?
```

---

# 51. Resource Limits

Worker 可设置：

- 最大内存；
- 最大缓存；
- 最大并发；
- 最大预览大小；
- 最大递归深度。

---

# 52. Temp Security

临时文件：

- 独立 Temp 子目录；
- 随机名称；
- 权限限制；
- 正常退出清理；
- 崩溃后下次启动清理；
- 敏感预览支持立即清理。

---

# 53. Mark of the Web

需要评估：

- 从 Internet 下载的 Archive；
- 解压文件的 Zone 信息；
- 是否传播 MOTW；
- 不得为了便利绕过 Windows 安全模型。

---

# 54. Archive Integrity

支持：

- Test Archive；
- CRC；
- Hash；
- 数据错误报告；
- 部分可恢复 Entry；
- 错误详情。

---

# 55. 错误系统

禁止只显示：

```text
Error: 0x80004005
```

应转换：

```text
无法解压此文件。

原因：
压缩数据损坏或文件未完整下载。

受影响：
world/region/r.0.0.mca

[查看详细信息] [跳过并继续]
```

高级信息可展开。

---

# 56. History

可选记录：

- 最近打开；
- 最近压缩；
- 最近解压；
- 最近路径。

提供：

- Disable；
- Clear；
- Private Mode。

---

# 57. Start Page

建议：

```text
AileArc

Recent
────────────
server.7z
photos.zip
backup.tar.zst

Drop an archive here
or
[Open Archive]
```

---

# 58. Quick Actions

启动页：

- Open Archive；
- Create Archive；
- Extract Archive；
- Test Archive。

---

# 59. Command Palette

长期：

```text
Ctrl+Shift+P
```

示例：

- Extract Here
- Smart Extract
- Copy Path
- Toggle Preview
- Change View
- Test Archive
- Open Settings

---

# 60. Archive Compare

长期差异化功能。

比较：

```text
backup-v1.zip
backup-v2.zip
```

输出：

- Added；
- Removed；
- Modified；
- Same。

适合：

- 开发者；
- Mod Pack；
- Release；
- 备份。

---

# 61. Nested Archives

支持：

```text
outer.zip
└─ inner.7z
```

用户可直接双击进入。

技术上：

- Memory Stream；
- Temp Stream；
- Nested Worker；
- Recursion Limit。

---

# 62. Archive-as-Folder URI

内部抽象：

```text
ailearc://archive/{id}/plugins/config.yml
```

用于：

- Navigation；
- History；
- Tabs；
- Deep Link；
- Preview。

---

# 63. Hash

支持：

- CRC32
- CRC64
- MD5
- SHA-1
- SHA-256
- SHA-512
- BLAKE2/BLAKE3（后续）

可：

- 文件；
- 多文件；
- Archive Entry；
- Archive 本身。

---

# 64. Split Volumes

支持：

```text
archive.7z.001
archive.7z.002
```

创建与读取。

预设：

- 100 MB；
- 700 MB；
- 1 GB；
- 4 GB；
- Custom。

---

# 65. Self-Extracting Archive

长期可选。

考虑安全与维护成本后决定是否支持。

---

# 66. Repair

不作为早期功能。

后续可针对：

- ZIP Central Directory；
- 部分可恢复数据；
- Split Volume；

提供有限修复能力。

禁止宣传“万能修复”。

---

# 67. Encoding

必须处理：

- UTF-8；
- UTF-16；
- CP936；
- Shift-JIS；
- legacy ZIP filenames。

允许：

```text
Filename Encoding:
Auto
UTF-8
GBK
Shift-JIS
...
```

默认 Auto。

---

# 68. 国际化

初期：

- 简体中文；
- English。

后续：

- Traditional Chinese；
- Japanese；
- Korean；
- German；
- French；
- Spanish。

所有 UI 字符串资源化。

---

# 69. Accessibility

支持：

- Keyboard；
- Narrator；
- UI Automation；
- High Contrast；
- Large Text；
- Focus Visible；
- Reduced Motion。

---

# 70. 快捷键

建议：

```text
Ctrl+O        Open
Ctrl+N        Create Archive
Ctrl+F        Search
Ctrl+L        Focus Path
Ctrl+A        Select All
Ctrl+C        Copy
Ctrl+V        Paste
Ctrl+T        New Tab
Ctrl+W        Close Tab
Alt+Left      Back
Alt+Right     Forward
Alt+Up        Parent
F2            Rename
Delete        Delete
Enter         Open
Space         Quick Preview
Ctrl+Shift+P  Command Palette
```

---

# 71. Quick Preview

建议：

```text
Space
```

类似快速查看。

显示：

- 图片；
- 文本；
- 代码；
- PDF；
- Metadata。

---

# 72. File Type Intelligence

识别不应只依赖扩展名。

例如：

```text
foo.zip
```

实际为 RAR。

使用：

- Magic；
- Signature；
- Engine Detection。

---

# 73. Archive Properties

属性页：

```text
Format: 7Z
Method: LZMA2
Solid: Yes
Encrypted: Yes
Entries: 12,482
Folders: 1,209
Original: 4.82 GB
Packed: 1.92 GB
Ratio: 39.8%
Created:
Modified:
```

---

# 74. Entry Properties

显示：

- Full Path；
- Size；
- Packed Size；
- CRC；
- Method；
- Attributes；
- Modified；
- Encrypted；
- Comment。

---

# 75. Archive Comments

支持读取。

写入能力根据格式决定。

---

# 76. Nested Folder Size

后台计算。

避免打开 Archive 时全部同步计算。

---

# 77. Performance Targets

## 启动

目标：

- Warm start < 500 ms；
- Cold start 尽量 < 1 s。

## UI

- 所有耗时操作不得阻塞 UI；
- 目标 60 FPS 滚动；
- 大列表虚拟化。

## Archive

- 首屏优先；
- Metadata Progressive Loading。

---

# 78. 内存目标

普通使用：

- Idle 尽可能控制；
- Preview Cache 可配置；
- 巨型 Archive 不将所有 Entry 生成重量级 ViewModel。

---

# 79. 日志系统

等级：

- Trace
- Debug
- Info
- Warning
- Error
- Critical

默认：

- 不记录文件内容；
- 不记录密码；
- 避免完整敏感路径长期存储。

---

# 80. Crash Reporting

建议：

- 默认本地生成 Crash Dump / Diagnostic Bundle；
- 在线上报必须 Opt-in；
- 明确隐私说明。

---

# 81. Diagnostic Bundle

用户可导出：

```text
AileArc-Diagnostics.zip
```

包含：

- Version；
- OS；
- Engine；
- Logs；
- Crash Info；
- Settings Sanitized。

不含：

- 密码；
- 文件内容；
- 完整敏感数据。

---

# 82. Settings

分类：

```text
General
Appearance
Archives
Extraction
Compression
Preview
Integration
Performance
Privacy
Advanced
About
```

---

# 83. Appearance

- System；
- Light；
- Dark；
- Mica；
- Compact Mode；
- File Density；
- Animation。

---

# 84. Extraction Settings

- Default Path；
- Smart Extract；
- Conflict Strategy；
- Preserve Timestamps；
- Preserve Permissions；
- Open Folder After Extract；
- Delete Archive After Success；
- MOTW Policy。

---

# 85. Compression Settings

- Default Format；
- Preset；
- Encryption；
- Default Thread Limit；
- Solid Mode；
- Verify After Creation。

---

# 86. Privacy

- Recent Files；
- History；
- Cache；
- Crash Reporting；
- Telemetry。

核心原则：

> 默认不开启侵入式遥测。

---

# 87. Telemetry

若未来需要：

只采用：

- Opt-in；
- Anonymous；
- Aggregate；
- Clear Documentation。

禁止：

- 上传文件名；
- 上传路径；
- 上传 Archive 内容；
- 上传密码。

---

# 88. Plugin System

长期功能。

接口：

```text
IAileArcPlugin
```

子能力：

```text
IPreviewProvider
IThumbnailProvider
IArchiveProvider
IMetadataProvider
ICommandProvider
```

---

# 89. Plugin Sandbox

第三方插件应考虑：

- 独立进程；
- 权限；
- 超时；
- 崩溃隔离。

避免插件直接进入主 UI 进程。

---

# 90. SDK

未来提供：

```text
AileArc.SDK
```

包含：

- Interfaces；
- Models；
- Plugin Manifest；
- Samples；
- Documentation。

---

# 91. Update System

支持：

- Stable；
- Beta；
- Nightly（可选）。

更新策略：

- 自动检查；
- 用户确认；
- Delta Update（未来）；
- Release Notes。

---

# 92. Code Signing

正式发布：

- EXE；
- DLL；
- Installer；
- Shell Extension；

均应进行签名。

---

# 93. CI/CD

GitHub Actions：

```text
Build
Test
Static Analysis
Package
Sign
Release
```

PR：

- Build；
- Unit Tests；
- Security Tests。

---

# 94. Branch Strategy

建议简单化：

```text
main
develop（可选）
feature/*
fix/*
release/*
```

个人项目初期甚至只使用：

```text
main
feature/*
```

即可。

---

# 95. Versioning

推荐：

```text
0.1.0
0.2.0
0.5.0
1.0.0
```

SemVer：

```text
MAJOR.MINOR.PATCH
```

---

# 96. Testing

## Unit

- Path Normalize；
- Smart Extract；
- Archive Index；
- Search；
- Settings；
- Conflict Policy。

## Integration

- ZIP；
- 7Z；
- RAR；
- Encrypted；
- Split；
- Solid；
- Corrupted。

## Security

- Zip Slip；
- Symlink Escape；
- ADS；
- Device Paths；
- Zip Bomb；
- Unicode。

## Performance

- 1M Entries；
- 100 GB Archive；
- Million Small Files；
- SSD/HDD；
- Network Share。

---

# 97. Fuzz Testing

对 Archive Parser 输入进行 Fuzz。

尤其：

- 损坏 Header；
- 随机字节；
- 超大字段；
- Unicode；
- 特殊路径。

Engine 为第三方不代表应用层可以忽略安全测试。

---

# 98. Test Corpus

建立：

```text
tests/corpus/
```

包含：

- Standard；
- Legacy；
- Corrupted；
- Encrypted；
- Split；
- Unicode；
- Malicious；
- Huge Metadata；
- Empty；
- Nested。

---

# 99. Benchmark

工具：

```text
AileArc.Benchmarks
```

指标：

- Open Time；
- Entry Enumerate；
- Search；
- Extract；
- Compression；
- Thumbnail；
- Preview；
- Memory。

---

# 100. MVP 规划

## Phase 0 — Foundation

完成：

- Solution；
- CI；
- Logging；
- Settings；
- Engine Interface；
- 7-Zip Integration；
- Worker IPC。

目标：

> 能稳定打开一个 ZIP 并列出 Entry。

---

# 101. Phase 1 — Viewer

功能：

- ZIP；
- 7Z；
- RAR；
- 文件列表；
- Breadcrumb；
- Back/Forward；
- Search；
- Sort；
- Entry Properties；
- Archive Properties；
- Text Preview；
- Image Preview。

目标：

> 比传统压缩软件“看压缩包”更舒服。

版本：

```text
AileArc 0.1 Preview
```

---

# 102. Phase 2 — Extraction

功能：

- Extract；
- Extract Here；
- Extract To；
- Smart Extract；
- Password；
- Progress；
- Cancel；
- Conflict；
- CRC；
- Security Path Layer。

版本：

```text
AileArc 0.2 Alpha
```

目标：

> 能成为日常解压软件。

---

# 103. Phase 3 — Compression

功能：

- ZIP；
- 7Z；
- Drag In；
- Add；
- Delete；
- Rename；
- Encryption；
- Split；
- Presets。

版本：

```text
AileArc 0.3 Alpha
```

---

# 104. Phase 4 — Windows Integration

功能：

- File Associations；
- Modern Context Menu；
- Open With；
- Installer；
- Custom Install Path；
- Shell Commands。

版本：

```text
AileArc 0.5 Beta
```

目标：

> 可以真正接管 Windows 压缩文件工作流。

---

# 105. Phase 5 — Killer UX

功能：

- Tabs；
- Thumbnail Grid；
- Quick Preview；
- Command Palette；
- Task Center；
- Drag Out Virtual Files；
- Universal Preview；
- History。

版本：

```text
AileArc 0.7 Beta
```

目标：

> 不只是替代 7-Zip，而是开始形成独立产品优势。

---

# 106. Phase 6 — 1.0

必须达到：

- 高稳定；
- 完整安全测试；
- 安装升级可靠；
- Shell 不拖垮 Explorer；
- 主流格式覆盖；
- 大 Archive 可用；
- 文档完整；
- 自动测试成熟；
- Crash Recovery；
- 用户反馈闭环。

版本：

```text
AileArc 1.0
```

---

# 107. 1.x Roadmap

候选：

- Archive Compare；
- Plugin SDK；
- Portable；
- Password Vault；
- PDF Preview；
- Nested Archive；
- More Formats；
- Better Repair；
- Localization；
- Archive Mount。

---

# 108. Archive Mount

长期探索：

将 Archive 挂载为虚拟目录。

例如：

```text
AileArc Mount
X:\server.7z\
```

实现可能涉及：

- WinFsp；
- ProjFS；
- 其他 Virtual FS。

这是长期高级功能，不应进入 MVP。

---

# 109. Explorer Preview Handler

未来可选：

在 Explorer 预览窗格直接显示 Archive 信息。

注意：

- 安全；
- 进程隔离；
- Explorer 稳定性。

---

# 110. Explorer Thumbnail

未来：

让：

```text
photos.zip
```

显示内容拼图式缩略图。

同样必须确保：

- 快速；
- Cache；
- 不阻塞 Explorer。

---

# 111. 开源策略

建议：

核心：

- AileArc.UI
- AileArc.Core
- AileArc.Engine wrapper
- AileArc.CLI
- SDK

均开源。

需仔细审查：

- 7-Zip License；
- unRAR Restrictions；
- libarchive；
- 第三方 Codec。

---

# 112. 第三方依赖策略

建立：

```text
THIRD_PARTY_NOTICES.md
```

记录：

- Project；
- Version；
- License；
- URL；
- Modified；
- Binary Distribution Terms。

---

# 113. 安全更新策略

若依赖：

- 7-Zip；
- libarchive；
- Codec；

出现漏洞：

- 及时升级；
- 发布 Security Advisory；
- CVE Tracking。

---

# 114. Repository README

首页应明确：

```text
AileArc
Modern Archive Manager for Windows.

Archive should feel like a folder.
```

展示：

- Screenshot；
- Features；
- Download；
- Roadmap；
- Contributing；
- License。

---

# 115. Contribution

建立：

- CONTRIBUTING.md；
- CODE_OF_CONDUCT.md；
- SECURITY.md；
- Issue Templates；
- PR Templates。

---

# 116. Issue 标签

推荐：

```text
bug
feature
ux
performance
security
archive-format
shell
preview
good-first-issue
help-wanted
```

---

# 117. UX 原则

## 原则 1

常用功能优先。

## 原则 2

高级参数渐进暴露。

## 原则 3

默认值必须可靠。

## 原则 4

错误必须可理解。

## 原则 5

危险操作必须可逆或确认。

## 原则 6

不要复制 7-Zip UI。

## 原则 7

不要为了现代视觉牺牲效率。

---

# 118. 不建议设计

避免：

- Ribbon；
- 大量巨型按钮；
- 过多卡片；
- 过度动画；
- 每个功能都弹 Modal；
- 右键菜单塞几十项；
- 首页广告；
- 开机自启；
- 强制账号；
- 强制云服务。

---

# 119. 产品差异化

AileArc 不应宣传：

> “又一个开源 7-Zip GUI”

而应宣传：

> **Modern archive management, designed like a file manager.**

核心差异：

1. Archive-as-folder；
2. Universal Preview；
3. Thumbnail；
4. Modern Search；
5. Task Center；
6. Virtual Drag & Drop；
7. Native Windows UX；
8. Open Source；
9. Custom Install Location；
10. No Ads。

---

# 120. 成功标准

项目是否成功，不应只看：

- 支持多少格式；
- GitHub Star；
- 压缩率；

而应看：

> 用户是否愿意取消 Bandizip / 7-Zip 默认关联并交给 AileArc。

第一阶段最重要的内部成功标准：

> **作者本人愿意长期把 AileArc 作为默认压缩软件。**

---

# 121. 核心开发优先级

推荐优先级：

```text
P0
Engine
Open
List
Extract
Security
Worker
UI Stability

P1
Search
Preview
Smart Extract
Compression
Task Center

P2
Shell
File Association
Installer
Drag & Drop

P3
Thumbnail
Tabs
Plugin
Archive Compare
Mount
```

---

# 122. 最大技术风险

## 风险 1：Windows Shell

复杂度：

★★★★★

COM / Explorer 稳定性要求高。

## 风险 2：Virtual Drag & Drop

复杂度：

★★★★★

涉及 Virtual File / IDataObject / Stream。

## 风险 3：安全解压

复杂度：

★★★★★

不能因依赖 7-Zip 而忽略应用层路径安全。

## 风险 4：格式兼容

复杂度：

★★★★★

现实世界 Archive Edge Case 极多。

## 风险 5：Solid Archive Preview

复杂度：

★★★★☆

需要 Cache 与解码调度。

## 风险 6：大规模 Entry

复杂度：

★★★★☆

需虚拟化和轻量数据模型。

---

# 123. 技术学习路线

建议按顺序：

1. C# / .NET 基础；
2. WinUI 3；
3. MVVM；
4. P/Invoke；
5. Native DLL；
6. C++；
7. COM；
8. Windows Shell；
9. Named Pipe / IPC；
10. Windows File API；
11. Security；
12. Packaging。

不要一开始先啃 COM。

---

# 124. 第一周建议

目标：

> 打开 ZIP 并显示文件列表。

只做：

```text
AileArc.UI
AileArc.Core
AileArc.Engine
```

实现：

- Open File；
- 7-Zip wrapper；
- Enumerate；
- ListView；
- Breadcrumb mock；
- Dark Mode。

---

# 125. 第二阶段原型目标

做一个：

```text
ZIP/7Z/RAR Viewer
```

必须达到：

- 打开；
- 浏览；
- 搜索；
- 图片预览；
- 文本预览。

如果这一版自己都不愿意用，则不要急着做 Shell。

---

# 126. 代码质量

要求：

- Nullable Enable；
- Analyzer；
- Async 全链路；
- CancellationToken；
- Dependency Injection；
- Interface Boundary；
- Unit Test；
- XML Docs（公共 API）。

---

# 127. Async 原则

任何以下操作禁止在 UI Thread：

- Archive Open；
- Enumeration；
- Search；
- Preview Decode；
- Hash；
- Extract；
- Compress；
- File I/O。

---

# 128. Cancellation

所有长任务：

```text
CancellationToken
```

必须可取消。

取消后：

- 释放 Handle；
- 清理 Temp；
- 状态一致。

---

# 129. File Handle

注意：

- Archive 被其他程序占用；
- Archive 被删除；
- Archive 被替换；
- Network Disconnect；
- Removable Drive Removal。

必须处理。

---

# 130. 网络路径

支持：

```text
\\NAS\Share\backup.7z
```

考虑：

- Latency；
- Disconnection；
- Retry；
- Progress；
- File Lock。

---

# 131. 长路径

内部统一使用现代长路径策略。

不要在 Core 中假定：

```text
MAX_PATH == 260
```

---

# 132. Unicode

全链路 Unicode。

禁止：

- ANSI API；
- 随意 Encoding.Default；
- Locale-dependent Path Logic。

---

# 133. Date/Time

统一处理：

- UTC；
- Local；
- Archive Format Precision；
- Missing Timezone。

UI 显示本地时间。

---

# 134. Permissions

TAR/Unix Archive：

- Unix Permissions；
- Symlink；
- Owner；

Windows 提取时需要定义映射策略。

---

# 135. Nested Password

Nested Archive 可能再次请求密码。

Password Session：

```text
Archive Scope
Session Scope
Never Save
```

---

# 136. 进度计算

不同 Archive 格式可能无法精确预测。

状态分：

- Determinate；
- Indeterminate。

不得伪造精确 ETA。

---

# 137. ETA

采用移动平均。

避免进度：

```text
99% → 卡 5 分钟
```

尽可能区分：

- Reading；
- Decompressing；
- Writing；
- Verifying。

---

# 138. Disk Space

解压前：

- 尽可能估算；
- 检查目标盘剩余；
- 容量不足提前警告。

---

# 139. Duplicate Names

某些 Archive 允许：

```text
a.txt
a.txt
```

内部必须用 Entry ID，而非仅 Path 作为唯一标识。

---

# 140. Case Sensitivity

Archive：

```text
File.txt
file.txt
```

Windows 通常不区分大小写。

提取时需要冲突策略。

---

# 141. Invalid Windows Names

Unix Archive 可包含 Windows 无法创建的文件名。

提供：

- Auto Rename；
- Skip；
- Ask。

---

# 142. Restore Session

崩溃或重启后：

可恢复：

- Tabs；
- Open Archives；
- Navigation State。

用户可关闭。

---

# 143. Safe Mode

AileArc 自身可提供：

```text
--safe-mode
```

禁用：

- Plugins；
- Preview Extensions；
- Shell Hooks；
- Cache。

用于排错。

---

# 144. Read-only Mode

某些 Archive：

- RAR；
- ISO；
- WIM；

可明确：

```text
Read-only archive
```

而不是让用户操作后才报错。

---

# 145. Capability Matrix

建议维护：

```text
Format | Open | Extract | Create | Update | Encrypt | Split
```

UI 根据格式动态隐藏不可用功能。

---

# 146. 内部事件系统

可设计：

```text
ArchiveOpened
ArchiveClosed
TaskStarted
TaskProgress
TaskCompleted
PreviewRequested
SettingsChanged
```

减少模块耦合。

---

# 147. 软件启动参数

建议：

```text
AileArc.exe archive.zip
AileArc.exe --extract archive.zip
AileArc.exe --safe-mode
AileArc.exe --new-window
```

---

# 148. Single Instance

默认：

- 单实例；
- 新文件打开到新 Tab。

设置：

- Always New Window。

---

# 149. Shell 性能底线

Explorer Extension：

- 不读取完整 Archive；
- 不启动 UI；
- 不执行重任务；
- 不进行网络请求；
- 快速返回。

否则会拖慢 Explorer。

---

# 150. 1.0 Definition of Done

AileArc 1.0 发布前必须：

- 主流格式稳定；
- 无已知高危路径漏洞；
- Explorer Shell 不崩；
- Installer 可更新/卸载；
- 大 Archive 可操作；
- Worker 崩溃可恢复；
- 基础 Preview 稳定；
- Smart Extraction 可靠；
- 自动测试覆盖核心逻辑；
- Documentation 完整；
- License 合规；
- Code Signing 完成。

---

# 151. 最终产品定位

AileArc 不是：

- 7-Zip 换皮；
- Bandizip 克隆；
- 压缩算法实验；
- UI Demo。

AileArc 应成为：

> **一个以现代文件管理体验重新定义 Archive 使用方式的 Windows 原生开源软件。**

最终目标不是“能解压”。

而是：

> 用户不再特别意识到自己正在操作一个压缩包。

这就是：

# Archive should feel like a folder.

---

# 152. 推荐的首个开发里程碑

正式建立：

```text
Milestone: AileArc 0.1 Preview
```

范围严格限定：

- Windows 11；
- ZIP / 7Z / RAR；
- Open；
- Browse；
- Search；
- Sort；
- Image Preview；
- Text Preview；
- Archive Properties；
- Entry Properties；
- Worker；
- Basic Error Handling。

暂不做：

- Compress；
- Shell；
- Installer；
- Drag Out；
- Plugin；
- Mount；
- Password Vault。

验收标准：

> 能连续使用 AileArc 打开日常 Archive，而没有强烈想返回 7-Zip/Bandizip 的感觉。

若达到该标准，再进入真正的系统级产品开发阶段。

---

# 153. 项目一句话定义

> **AileArc is a modern, native and open-source archive manager for Windows, built around one principle: archives should feel like folders.**
