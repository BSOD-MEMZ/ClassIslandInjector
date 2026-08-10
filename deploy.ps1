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

if (-not $NoBuild) {
    Write-Host "==> 构建（Release，不生成 cipx）..." -ForegroundColor Cyan
    Push-Location $projectDir
    dotnet build ClassIslandInjector.csproj -c Release -p:CreateCipx=false
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
Copy-Item (Join-Path $outputDir "*") $pluginDir -Recurse -Force

if (-not $NoStart) {
    Write-Host "==> 启动 ClassIsland ..." -ForegroundColor Cyan
    Start-Process "D:\Dev\ClassIsland\ClassIsland.exe"
}

Write-Host "部署完成。$(if (-not $NoStart) { 'ClassIsland 已启动。' })" -ForegroundColor Green
