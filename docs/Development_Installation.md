# 开发安装包与 Windows Shell 集成

更新：2026-10-24。默认安装包已切换为纯 WPF 客户端；开发安装与文件关联候选可用，现代菜单尚未完成受信任签名环境的实际 Explorer 验收。不是正式 Release。

## 构建

在仓库目录使用 PowerShell 7：

```powershell
./scripts/Package.ps1 -OutputDirectory .artifacts/my-development-bundle
./scripts/Test-Install.ps1 -BundleDirectory .artifacts/my-development-bundle
```

输出目录必须不存在。Package 运行 Release 构建、完整 .NET 回归、中英文 WPF 自检与原生 Shell 接口测试，分别发布 WPF AileArc.exe 与 Worker，携带 .NET/WPF 运行时、7-Zip 及第三方许可，并生成 SHA-256 文件清单。包中不包含 WinUI、Windows App SDK、WebView2 或 PRI/XBF。MakeAppx 工具由 packaging/AileArc.Packaging.csproj 独立锁定。`-SkipTests` 仅用于已验证代码的重复打包，不代表测试通过。

`Test-Install.ps1` 在独立的中文空格测试路径完成安装/升级/回滚/卸载，已有开发安装时会拒绝运行，不覆盖用户安装。可加 `-PreviousBundleDirectory '<旧包路径>'` 验证旧 WinUI 包升级至 WPF；不加时验证 WPF 包自身升级。安装后的 WPF 会运行两种语言的自检。

Shell 编译使用固定版本与哈希的 LLVM-MinGW，下载到 `.tools`，无需安装全局 C++ 工具链。`Build-Shell.ps1 -Test` 可以单独执行原生接口测试。所有工具、包和运行产物保持 Git 忽略。

## 当前用户安装与卸载

在生成包所在目录运行，支持 Windows PowerShell 5.1 和 PowerShell 7：

```powershell
./Install.ps1 -InstallDirectory 'D:\Apps\AileArc-Development'
```

省略参数时使用当前用户本地应用数据目录中的 `Programs\AileArc-Development`。这是脚本形式的最小开发安装器，暂未提供图形安装向导。

- 安装目录须为空或为本安装器登记的目录。只允许一个当前用户开发安装实例。
- 注册 ZIP、7Z、RAR 的 Open With 候选、Windows 默认应用候选、开始菜单快捷方式和卸载入口。
- 不修改扩展名默认值或 `UserChoice`；用户自行在 Windows“打开方式”中选择默认应用。
- 可以从 Windows“已安装的应用”卸载，也可以执行当前安装版本目录里的 `Uninstall.ps1 -InstallDirectory 'D:\Apps\AileArc-Development'`。
- 不删除用户设置、工作副本或恢复记录。只清理收据列出且哈希未变化的文件；用户新增、修改或被占用文件保留并提示。不递归删除整个安装目录。
- 安装/升级/卸载前关闭该安装目录的 AileArc。安装器不会强制结束用户任务。

## 升级与失败行为

对同一安装目录再次执行新包的 Install。新文件先写入独立 `versions/<id>`，验证成功后切换注册和快捷方式，最后原子更新收据。正常失败恢复旧注册和快捷方式，旧版本文件保留到卸载。完整性错误在更新注册前停止。

当前现代菜单已注册的开发安装，升级前须先卸载再安装；其包身份外部位置切换尚未实现自动回滚。普通 Open With 安装支持原地升级验证。强制终止安装器、电源中断和其他进程同时改写安装目录的完整事务恢复仍待补充。

## Windows 11 现代菜单

原生 `IExplorerCommand` 提供 AileArc 子菜单：打开、智能解压、解压到、新建压缩包。仅 ZIP/7Z/RAR 显示解压命令，其他文件和目录可新建压缩包。一次最多 32 个路径，命令行长度有界。标题来自与主程序相同的 zh-CN/en-US 资源，主程序重启后更新菜单语言；Explorer 可能缓存菜单显示。

菜单查询不加载归档引擎，Invoke 启动 WPF AileArc.exe；智能解压使用压缩包所在目录，解压到显示目标对话框，新建带入所选源路径。命令通过主进程管道转交；忙碌窗口不会因新 Shell 命令取消当前任务。

现代菜单通过 sparse identity package 注册，保留自定义安装位置。默认产物为未签名开发包。2026-10-24 在当前机器用 WPF 包重新尝试 `Add-AppxPackage -AllowUnsigned`，仍返回 **0x80073D2B**：未签名包不能包含可执行激活。当前用户没有可用的代码签名私钥证书。因此不能把“DLL 和 manifest 已生成、命令转交成功”算作现代菜单验收通过。

如已有当前用户 Personal 证书存储中的代码签名证书，可构建签名包：

```powershell
./scripts/Package.ps1 -OutputDirectory .artifacts/signed-development-bundle -SigningCertificateThumbprint '<已有证书的40位指纹>'
# 在新生成的包目录执行：
./Install.ps1 -InstallDirectory 'D:\Apps\AileArc-Development' -EnableModernMenu
```

目标机器必须已经信任该证书。脚本不会自动创建/导入证书、启用开发者模式、提权或重启 Explorer。注册失败会回滚本次安装，错误不会被当成成功。签名包的实际菜单、卸载与重新安装仍待在具备签名条件的环境验证。

参考：[微软现代菜单集成](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/integrate-packaged-app-with-file-explorer)、[外部位置包身份](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/grant-identity-to-nonpackaged-apps)、[未签名包限制](https://learn.microsoft.com/en-us/windows/msix/package/unsigned-package)。

## 已验证与未验证

- Debug/Release WPF 默认构建；99 项 .NET 回归；中英文 WPF 和 Shell 动作自检；原生 COM 工厂/引用计数/枚举/菜单状态/中文空格路径与 Windows 参数引用往返测试。
- 自定义中文及空格安装路径、旧 WinUI 包到 WPF 包升级、损坏包拒绝、锁定收据失败回滚、卸载保留修改及额外文件、默认文件关联不变。
- 从实际安装目录启动自包含 Worker 的 8 项读包、解压、ZIP/7Z 与密码组合往返测试。
- 自包含窗口启动、新建对话框预选源、智能解压输出、已有窗口接收解压到请求及取消。
- 历史 WinUI 部署曾遗漏 PRI/XBF；该宿主现已退役。当前包检查 WPF 运行时和 AileArc.exe 存在，并拒绝混入旧宿主文件。
- 现代菜单的真实显示、签名安装路径、干净 Windows 11 机器上的完整交互和图形安装向导尚未验收。
