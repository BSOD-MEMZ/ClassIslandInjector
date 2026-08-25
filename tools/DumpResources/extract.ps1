# 宿主 Avalonia 资源转储工具（宿主升级/结构验证用）
#
# 用法：
#   .\extract.ps1                              # 用默认 DLL（ClassIsland.dll）列出全部 .axaml/.json 资源路径
#   .\extract.ps1 -DllPath <path>              # 指定要解析的程序集
#   .\extract.ps1 -Filter 'MainWindowLine'     # 只列出路径匹配的资源
#   .\extract.ps1 -ExtractPath '/Controls/MainWindowLine.axaml' -OutDir <dir>  # 提取指定资源内容到文件
#
# 背景：ClassIsland 的 XAML 以 PortableXaml 模式编译进 IL（!AvaloniaResources 仅存索引与
# 非 XAML 资源），需要结合源码 / 运行时可视树验证。此工具用于转储可访问的嵌入资源。
param(
    [string]$DllPath = 'D:\Dev\ClassIsland\app-2.1.0.1-0\ClassIsland.dll',
    [string]$Filter = '',
    [string]$ExtractPath = '',
    [string]$OutDir = ''
)

$dir = Split-Path $DllPath
[System.Reflection.Assembly]::LoadFrom("$dir\Avalonia.Base.dll") | Out-Null
$asm = [System.Reflection.Assembly]::LoadFrom($DllPath)
$resName = $asm.GetManifestResourceNames() | Where-Object { $_ -like '*AvaloniaResources' } | Select-Object -First 1
if ($null -eq $resName) { Write-Output "程序集没有 !AvaloniaResources 资源: $DllPath"; exit 1 }

$stream = $asm.GetManifestResourceStream($resName)
$br = New-Object System.IO.BinaryReader($stream)
$indexSize = $br.ReadInt32()
$stream.Position = 4
$indexBytes = $br.ReadBytes($indexSize)
$ms = New-Object System.IO.MemoryStream(,$indexBytes)
$t = [Avalonia.Utilities.AvaloniaResourcesIndexReaderWriter]
$list = $t::ReadIndex($ms)
$dataBase = 4 + $indexSize
$stream.Close()

if ($ExtractPath -ne '') {
    $entry = $list | Where-Object { $_.Path -eq $ExtractPath } | Select-Object -First 1
    if ($null -eq $entry) { Write-Output "未找到资源: $ExtractPath"; exit 1 }
    $stream = $asm.GetManifestResourceStream($resName)
    $stream.Position = $dataBase + $entry.Offset
    $data = New-Object byte[] $entry.Size
    [void]$stream.Read($data, 0, $entry.Size)
    $stream.Close()
    if ($OutDir -eq '') { $OutDir = Split-Path $MyInvocation.MyCommand.Path }
    $outPath = Join-Path $OutDir (($ExtractPath.TrimStart('/')) -replace '[/\\]', '_')
    [System.IO.File]::WriteAllText($outPath, [System.Text.Encoding]::UTF8.GetString($data))
    Write-Output ("已提取: {0}  ({1} bytes)" -f $outPath, $entry.Size)
    exit 0
}

Write-Output ("{0}: {1} 个资源条目" -f $DllPath, $list.Count)
foreach ($e in $list) {
    if ($Filter -eq '' -or $e.Path -match $Filter) {
        Write-Output ("  {0}  ({1} bytes)" -f $e.Path, $e.Size)
    }
}
