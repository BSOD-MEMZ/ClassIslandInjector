# tools/DumpResources

宿主 Avalonia 资源转储工具（宿主升级 / 结构验证用）。

## 背景

ClassIsland 的 XAML 以 PortableXaml 模式编译进 IL（`!AvaloniaResources` 仅存索引与教程/图标等
非 XAML 资源），无法直接读 axaml 文本。此工具用 Avalonia 自带的
`AvaloniaResourcesIndexReaderWriter` 解析嵌入资源索引，供验证宿主内部结构（控件名、样式类等）
时转储可访问的资源。

关键结论（ClassIsland 2.1.0.1 实测）：

- `ClassIsland.dll` 的 `!AvaloniaResources` 只有教程/图标/字体与 `!AvaloniaResourceXamlInfo`（类→资源路径映射）；
- 主界面 XAML（`MainWindowLine.axaml`、FluentTheme `Styles.axaml` 等）编译进 IL，其中的字符串
  以 **UTF-16** 存在于 DLL 中 —— 验证结构标记（如 `line-background`、`BackgroundBorderWrapper`）
  可用 UTF-16 字节搜索二进制；
- 分体主界面（`IsIslandSeperated`）下宿主隐藏 `BackgroundBorder`，背景改由每行根组件模板的
  `<Border Classes="line-background"/>` 提供（详见仓库根 `agents.md` 第 7 节）。

## 用法

```powershell
.\extract.ps1                                     # 列出默认 ClassIsland.dll 的全部资源路径
.\extract.ps1 -DllPath <path>                     # 指定程序集
.\extract.ps1 -Filter 'MainWindowLine'            # 只列出路径匹配的资源
.\extract.ps1 -ExtractPath '/Assets/AppLogo.ico'  # 提取指定资源内容到本目录
```
