# AileArc 项目协作规则

## 开发依据

- 开发前阅读 `docs/AileArc_M1_Implementation_Specification.md` 和相关架构决策。
- M1 是分阶段里程碑；不能把部分实现描述为首个里程碑已经完成。
- 项目自有代码使用 MIT；第三方代码和二进制保留各自许可。

## 提交与同步（作者明确要求）

- 后续每次 BUG 修复或功能更新，完成相应验证后，必须进行 Git 提交并推送到 GitHub。
- 默认远端 `origin` 为 `https://github.com/Ailety/AileArc.git`，默认分支 `main`；如作者明确指定其他分支，以当前指示为准。
- 每个可独立说明的修复或功能形成一个逻辑提交，使用清晰的提交信息，例如 `fix: ...`、`feat: ...`、`docs: ...`、`chore: ...`。
- 提交前查看状态和差异，仅包含本次工作相关文件；保留作者或其他任务的未提交修改。
- 根据改动运行必要的构建/测试，更新开发历程；不能省略失败，也不能将未执行的检查记为通过。
- 推送后核对远端 SHA；涉及代码、依赖或 CI 的更新还要检查 GitHub Actions。CI 若发现问题，修复、验证并追加提交，不改写已公开历史。
- 不使用 force push、不重置或删除远端历史。若远端领先，先检查并以正常方式整合。
- 如果提交或推送被权限、网络或分支规则阻断，保留本地工作，明确报告阻塞与尚未同步的提交，不能声称上传成功。
- 不提交 `.tools`、`.artifacts`、`bin`、`obj`、下载的依赖二进制、个人设置、密码、令牌、工作副本或真实用户文件。
- 发布代码不等于发布正式版本；未经作者另行要求，不创建 Release、标签或官网。

## Native Windows execution

- On native Windows, verify `pwsh` first and prefer PowerShell 7 for encoding-sensitive or multi-line PowerShell work.
- Do not assume PowerShell 7 syntax is available until `$PSVersionTable.PSVersion` has been checked.
- Use explicit UTF-8 encoding for text reads and writes.
- Before path-specific work, verify the actual path with `Get-Location`, `Test-Path -LiteralPath`, and when applicable `git rev-parse --show-toplevel`. Never infer repository paths.
- Prefer absolute paths and use `-LiteralPath` for filesystem commands that support it.
- Use `apply_patch` for source and configuration edits. Avoid broad PowerShell `-replace` operations, especially in files containing Chinese text.
- After a command fails, do not retry it unchanged. Perform one targeted diagnostic, identify whether the cause is path, quoting, encoding, version, or permission, then switch techniques.
