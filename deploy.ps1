# ============================================================
# 构建 + 部署 + 启动脚本（ClassIslandInjector）
# 用法：
#   .\deploy.ps1             # 构建并部署，然后启动 ClassIsland
#   .\deploy.ps1 -NoBuild    # 跳过构建（只部署当前产物）
#   .\deploy.ps1 -NoStart    # 构建并部署，但不启动 ClassIsland
# ============================================================
param(
    [switch]$NoBuild,
    [switch]$NoStart
)
$ErrorActionPreference = "Stop"

$projectDir = $PSScriptRoot
$pluginDir  = "D:\Dev\ClassIsland\data\Plugins\classisland.injector"
$outputDir  = Join-Path $projectDir "bin\Release\net8.0-windows10.0.19041.0"

# 定位可用的 dotnet：本机已知可用安装（带 SDK 8.0.423）在 %LOCALAPPDATA%\Microsoft\dotnet，
# 而 PATH 中靠前的 C:\Program Files\dotnet 是残缺安装（--list-sdks 能列出但 build 报找不到 SDK）。
# 因此优先取 LOCALAPPDATA，其次 PATH，再回退 Program Files。
$candidates = @(
    (Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe"),
    (Get-Command dotnet -ErrorAction SilentlyContinue).Source,
    (Join-Path $env:ProgramFiles "dotnet\dotnet.exe"),
    (Join-Path ${env:ProgramFiles(x86)} "dotnet\dotnet.exe")
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -Unique
$dotnet = $candidates | Select-Object -First 1
if (-not $dotnet) {
    Write-Host "未找到 dotnet，请先安装 .NET SDK（https://dotnet.microsoft.com/download）。" -ForegroundColor Red
    exit 1
}

if (-not $NoBuild) {
    Write-Host "==> 构建（Release，不生成 cipx）..." -ForegroundColor Cyan
    Push-Location $projectDir
    & $dotnet build ClassIslandInjector.csproj -c Release -p:CreateCipx=false
    $code = $LASTEXITCODE
    Pop-Location
    if ($code -ne 0) {
        Write-Host "构建失败，已中止。" -ForegroundColor Red
        exit 1
    }
}

Write-Host "==> 关闭 ClassIsland（避免 DLL 被占用）..." -ForegroundColor Cyan
Get-Process -Name "ClassIsland*" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800

Write-Host "==> 部署到 $pluginDir ..." -ForegroundColor Cyan
if (-not (Test-Path $outputDir)) {
    Write-Host "未找到产物目录：$outputDir" -ForegroundColor Red
    exit 1
}
# 先清空插件目录再整体复制：避免旧布局残留（如资源平铺在顶层）与目录/文件同名冲突，
# 导致 Copy-Item 报「Container cannot be copied onto existing leaf item」。
# 插件目录只含构建产物（用户数据在 data\Config\Plugins\classisland.injector），可安全清空。
Remove-Item (Join-Path $pluginDir "*") -Recurse -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path $outputDir "*") $pluginDir -Recurse -Force

if (-not $NoStart) {
    Write-Host "==> 启动 ClassIsland ..." -ForegroundColor Cyan
    Start-Process "D:\Dev\ClassIsland\ClassIsland.exe"
}

Write-Host "部署完成。$(if (-not $NoStart) { 'ClassIsland 已启动。' })" -ForegroundColor Green
