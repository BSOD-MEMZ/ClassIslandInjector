# ClassIsland 样式注入器

这是一个面向 ClassIsland 2.x 的深度外观插件。它不修改 ClassIsland 的二进制文件：在应用启动完成后，通过官方 `AppBase.Current.MainWindow` 生命周期入口定位 `MainWindow.axaml` 的命名节点，向该视觉子树注入变换、动画和后加载的 Avalonia 样式表。

## 安装与使用

1. 构建后安装 `cipx/ClassIslandInjector.cipx`，然后重启 ClassIsland。
2. 打开 **应用设置 → 样式注入器**。
3. 基础几何、形状、渐变、阴影和动画直接在设置页中调整；点击“保存并应用”即时预览。
4. 编辑设置页显示的 `Overrides.axaml`。默认开启文件监视，保存即可热重载。

设置页顶部提供四个预设：`GlassCapsule`、`NeonPulse`、`MaimaiHanabi`、`Minimal`，并可使用“恢复插件默认”一次性复位运行时设置。恢复操作会保留高级 `Overrides.axaml`，避免误删手工样式。

插件配置保存在 ClassIsland 为插件分配的配置目录中，而不是安装目录。首次启动会生成：

- `settings.json`：基础注入和动画设置。
- `Overrides.axaml`：不会在更新插件时被覆盖的高级样式表。

## 样式表入口

样式表在 ClassIsland 的主题样式之后添加到主窗口，因此可以覆盖同等选择器。以下是来自 `ClassIsland/MainWindow.axaml` 的稳定锚点：

| 选择器 | 所在层级 | 适用范围 |
| --- | --- | --- |
| `Window.classisland-injector` | 窗口 | 窗口透明、窗口级资源 |
| `Grid#WindowRoot` | 根网格 | 全局背景与剪裁 |
| `Panel#WorkingRoot` | 工作区 | 布局边界 |
| `StackPanel#StackPanelRootContainer.classisland-injector-root` | 岛屿根 | 整体视觉 |
| `Grid#GridRoot` | 主内容 | 课程线、组件、通知承载区 |

例如：

```xml
<Styles xmlns="https://github.com/avaloniaui">
  <Style Selector="Grid#GridRoot">
    <Setter Property="Opacity" Value="0.92" />
  </Style>
  <Style Selector="StackPanel#StackPanelRootContainer.classisland-injector-root TextBlock">
    <Setter Property="FontWeight" Value="SemiBold" />
  </Style>
</Styles>
```

Avalonia 选择器和 ClassIsland 控件资源均可使用；这使主题作者可以精确覆盖课程行、组件、字体和状态伪类。对于错误的 XAML，插件会忽略本次加载，不会中断 ClassIsland。

## 运行时特效

设置页还提供以下无需编写 XAML 的注入项：

- **形状**：保留宿主、直角矩形、圆角矩形、胶囊；复杂形状可继续由样式表控制。
- **背景与阴影**：纯色或双端线性渐变、阴影颜色、模糊、X/Y 偏移和不透明度。
- **主界面出现**：淡入、缩放、从上/下滑入。它监听主界面可见状态，不改动窗口管理行为。
- **提醒强调**：脉冲、弹跳、抖动、闪烁。它与提醒遮罩载入同步。
- **提醒遮罩出现/消失**：保留 ClassIsland 原生百叶遮罩，或改为淡入、上下左右滑动；该层直接覆盖 `MainWindowLine:mask-in` 与 `:mask-out` 状态的动画。
- **Ripple**：环、双环、发光、方框、`Hanabi` 或关闭。选择任一自定义类型时，插件会在提醒遮罩载入的同一时机接管该课程行的原生 ripple 播放器，避免原生单一效果覆盖选择结果；切回 `None` 或禁用插件时会原样还原播放器。

`Hanabi` 参考 MajdataView 的 Firework：先闪现双层中心光球，再快速向 16 个方向射出带末端火花的光束并淡出；它使用当前 Ripple 颜色、时长和线宽参数。

颜色可使用 `#RRGGBB` 或 `#AARRGGBB`。在动画、样式表或颜色无效时，插件只跳过该项并继续运行宿主。

## 边界

本插件的“注入”指运行时的视觉树和样式注入，不包含内存补丁、DLL 篡改或规避 ClassIsland 的安全/管理策略。ClassIsland 更新后若改动上述命名节点，基础动画会自动失效为不注入；请根据新版本 `MainWindow.axaml` 更新选择器。
