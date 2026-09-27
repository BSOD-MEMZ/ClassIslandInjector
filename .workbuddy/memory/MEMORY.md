# ClassIslandInjector — 项目长期约定

## 仓库与工作目录

- **`.workbuddy/` 是故意提交进 git 的，不要动它。** 用户在两台电脑上开发，靠提交 `.workbuddy/`（含 `memory/`、`issue-*-reply.md` 等）同步 AI 记忆与工作记录。禁止建议加回 `.gitignore`、禁止从索引移除、禁止把它当"误提交"来提醒。（2026-09-27 明确）

## 构建与部署

- **提交时注意文件名大小写**：仓库索引里是 `agents.md`（小写），在 Windows 上执行 `git add AGENTS.md` 会**静默不匹配**（不报错也不暂存，改动会漏提交）。用 `git add agents.md` 或 `git add -A`。

- 构建：`dotnet build ClassIslandInjector.csproj -c Release -p:CreateCipx=false`
- 部署前必须关闭宿主：`Stop-Process -Name "ClassIsland*" -Force`，再复制 `bin\Release\net8.0-windows10.0.19041.0\*` 到 `D:\Dev\ClassIsland\data\Plugins\classisland.injector`
- 配置目录：`D:\Dev\ClassIsland\data\Config\Plugins\classisland.injector`（`settings.json`、`Overrides.axaml`、`preview-debug.log`、`album-color.log`）
- 含中文注释的脚本（deploy.ps1 等 UTF-8 无 BOM）必须用 `pwsh` 执行

## 设计约束

- 全新安装必须零改动：默认值中性（`Shape=HostDefault`、`RippleType=None`、`CountdownArrowsEnabled=false` 等）
- 目标框架锁定 `net8.0-windows10.0.19041.0`（必须与宿主的 WinRT SDK 对齐）
- Avalonia 派生控件必须覆写 `StyleKeyOverride`
- 判断 SMTC 焦点会话必须用 `SourceAppUserModelId` 字符串比较，不能用 `ReferenceEquals`
