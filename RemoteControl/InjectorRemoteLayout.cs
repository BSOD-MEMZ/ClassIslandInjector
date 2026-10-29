using System.Globalization;
using System.Reflection;

namespace ClassIslandInjector.RemoteControl;

/// <summary>
/// RemoteCI WebUI「样式注入器」设置页的布局：分区 → 卡片 → 条目，<b>逐项照抄</b> <c>Views/InjectorSettingsPage.cs</c>
/// 的 <c>BuildPage</c>（分区标题、卡片标题/说明、条目名称/说明、取值范围与步长、卡片总开关、显隐与可用联动）。
/// <para>
/// 改设置页的结构或新增设置项时，同步改这里；<c>tools\RemoteProbe</c> 会校验目录里的每个设置项都出现在这里。
/// 页面上的纯操作按钮（预设管理、打开编辑器、预览、删除数据等）不是设置值，不在此列——它们由远程功能
/// （套用预设 / 下发样式包 / 恢复默认）覆盖或只能在教室端操作。分体块独立配色按教室端组件 GUID 存储，
/// 各班不同，也不开放。
/// </para>
/// </summary>
internal static class InjectorRemoteLayout
{
    /// <summary>页面上「卡片总开关」没有对应的设置属性（关闭即写 None），用 <c>toggle.属性名</c> 作为虚拟字段 Key。</summary>
    public const string TogglePrefix = "toggle.";

    public static IReadOnlyList<RemoteBinding> Build()
    {
        var b = new Builder();

        b.Section("远程管理");
        b.Lock();

        b.Section(null);
        b.Switch("Enabled", "运行时注入", "启用后由插件接管主界面根节点的视觉效果。");

        b.Section("用户预设");
        b.Switch("PresetFileAssociationEnabled", "双击安装预设包", "注册 .cizip 文件关联：双击预设包即可启动 ClassIsland 并进入安装确认。");

        // ===== 背景 =====
        b.Section("背景");
        b.Card("底色填充", "非分体模式画整个主界面背景；分体模式给勾选的分块上色。", toggle: "CustomBackgroundEnabled");
        b.Color("BackgroundColor", "背景色", "支持透明度的背景颜色。", enabled: [Is("DynamicBackgroundColorEnabled", "false")]);
        b.Switch("DynamicBackgroundColorEnabled", "动态专辑封面取色", "背景跟随当前 SMTC 专辑封面自动取色（关闭时使用固定颜色）。");
        b.Switch("GradientEnabled", "线性渐变", "开启后使用线性渐变背景。");
        b.Select("GradientDirection", "渐变方向", "线性渐变从起始色到终止色的方向。", SettingChoices.GradientDirections, enabled: [Is("GradientEnabled", "true")]);
        b.Color("GradientEndColor", "渐变终止色", "线性渐变的结束颜色。", enabled: [Is("GradientEnabled", "true")]);

        b.Card("底纹纹理", "在底色之上叠加可平铺的纹理图案。",
            enumToggle: ("BackgroundTextureType", BackgroundTexture.None, SettingChoices.BackgroundTextures[0].Value));
        b.Select("BackgroundTextureType", "纹理图案", "选择铺到勾选分块/主界面的纹理图案。", SettingChoices.BackgroundTextures);
        b.Color("BackgroundTextureColor", "纹理颜色", "支持透明度的纹理线条颜色。",
            visible: [Not("BackgroundTextureType", nameof(BackgroundTexture.Aero))]);
        b.Number("BackgroundTextureSize", "纹理大小", "单个纹理单元的大小（像素）。", 8, 80, 2,
            visible: [Not("BackgroundTextureType", nameof(BackgroundTexture.Spectrum), nameof(BackgroundTexture.Aero))]);
        var spectrum = Is("BackgroundTextureType", nameof(BackgroundTexture.Spectrum));
        b.Number("BackgroundTextureSpectrumSensitivity", "频谱灵敏度", "动态频谱柱条的放大倍率（越大跳动越剧烈）。", 0.1, 3, 0.05, slider: true, visible: [spectrum]);
        b.Number("BackgroundTextureSpectrumBars", "频谱柱条数", "主界面约 400 像素宽时的柱条数，柱条宽度保持恒定。", 4, 64, 1, visible: [spectrum]);
        b.Switch("BackgroundTextureSpectrumMirrored", "双面对称", "同时向上和向下绘制镜像频谱。", visible: [spectrum]);
        b.Switch("BackgroundTextureSpectrumAutoWidth", "自动匹配宽度", "开启后柱条数随主界面宽度自动增减（柱宽恒定）。", visible: [spectrum]);

        b.Card("背景图片", "打开图层编辑器，用 Photoshop 式图层自由设计主界面底图。（图层需在教室端的图层编辑器里设计，这里只控制底图开关。）",
            toggle: "WallpaperEnabled", itemsFollowToggle: false);
        b.Card("底图模糊", "对整个底图（含所有图层）应用高斯模糊（0 为关闭）。");
        b.Number("WallpaperBlurRadius", "模糊半径", "高斯模糊半径（像素，0 为关闭）。", 0, 60, 1);

        // 页面上视频填充组的开关只放在标题行，组内条目不随开关禁用（Group + Footer，不是 SwitchableGroup）。
        b.Card("动态视频填充", "用本地视频作为主界面动态背景（FFmpeg 解码，建议 H.264/MP4）。", toggle: "VideoFillEnabled", itemsFollowToggle: false);
        b.Switch("VideoProjectEnabled", "使用编辑工程", "优先播放视频编辑器导出的多片段工程；关闭则使用下方单文件。");
        b.LocalPath("VideoFillPath", "视频文件", "MP4 等视频文件的路径（教室端本机路径）。", @"如 D:\Videos\background.mp4");
        b.Number("VideoFillOpacity", "不透明度", "视频填充的整体透明度。", 0, 1, 0.05, slider: true);
        b.Select("VideoFillFit", "显示方式", "视频在主界面内的显示方式。", SettingChoices.VideoFillFits);
        b.Number("VideoFillBlurRadius", "模糊", "对视频填充应用高斯模糊（0 为关闭）。", 0, 60, 1);
        b.Number("VideoFillMaxDimension", "最大分辨率", "解码降采样上限（宽高中较大者，像素），降低资源占用。", 240, 1920, 80);
        b.Number("VideoFillTargetFps", "目标帧率", "解码播放帧率上限（fps），越低越省资源。", 1, 60, 1);
        b.Switch("VideoFillLoop", "循环播放", "播放到结尾后自动回到开头继续。");
        b.Switch("VideoFillAudioEnabled", "播放声音", "视频背景同时输出音频。");
        b.Number("VideoFillAudioVolume", "音量", "视频背景的音量。", 0, 1, 0.05, slider: true, enabled: [Is("VideoFillAudioEnabled", "true")]);
        // 「自定义 FFmpeg 下载源」刻意不开放：它排在下载源第一位，下载到的 DLL 会被加载进宿主进程，
        // 开放给远程等于让 WebUI 账号能往教室电脑下发任意原生代码（见 InjectorRemoteSettings.RetiredKeys）。

        b.Card("动态取色", "从音乐软件或浏览器获取 SMTC 信息，并进行莫奈取色");
        b.Switch("RevertColorsWhenPaused", "暂停/停止时恢复原色", "媒体暂停或停止播放时，从专辑取色平滑恢复为原始颜色，恢复播放后再跟随专辑。");
        b.Switch("DynamicThemeColorEnabled", "动态修改主题色", "从当前专辑封面取色并动态修改 ClassIsland 全局主题强调色。");
        b.Number("AlbumColorPollingIntervalSeconds", "兜底刷新间隔", "事件驱动失效时的兜底刷新间隔（秒）。", 0.5, 120, 0.5);
        b.Number("AlbumColorTransitionSeconds", "颜色过渡时长", "专辑颜色变化时，背景、边框、阴影平滑过渡到新颜色的时长（秒），0 为立即切换。", 0, 10, 0.1);

        // ===== 边框、阴影和文字 =====
        b.Section("边框、阴影和文字");
        b.Card("阴影", "为 ClassIsland 添加投影效果。", toggle: "ShadowEnabled");
        b.Switch("DynamicShadowColorEnabled", "动态取色", "阴影色调跟随专辑封面莫奈取色。");
        b.Color("ShadowColor", "阴影颜色", "支持透明度的阴影颜色。", enabled: [Is("DynamicShadowColorEnabled", "false")]);
        b.Number("ShadowBlur", "阴影模糊", "控制投影的柔和程度。", 0, 200, 1);
        b.Number("ShadowOffsetX", "阴影水平偏移", "控制投影向左或向右偏移。", -200, 200, 1);
        b.Number("ShadowOffsetY", "阴影垂直偏移", "控制投影向上或向下偏移。", -200, 200, 1);
        b.Number("ShadowOpacity", "阴影不透明度", "控制投影的深浅。", 0, 1, 0.05, slider: true);

        b.Card("边框", "为 ClassIsland 添加细边框。", toggle: "BorderEnabled");
        b.Switch("DynamicBorderColorEnabled", "动态取色", "边框色调跟随专辑封面莫奈取色。");
        b.Color("BorderColor", "边框颜色", "支持透明度的边框颜色。", enabled: [Is("DynamicBorderColorEnabled", "false")]);
        b.Number("BorderThickness", "边框线宽", "控制边框的粗细。", 0.25, 20, 0.25);

        b.Card("文字美化", "自定义主界面文字的字体、字号、字色与勾边。", toggle: "TextStylingEnabled");
        b.Text("TextFontFamily", "字体", "教室端已安装的字体名称（如 Microsoft YaHei）；留空保持宿主原字体。", "留空 = 保持宿主原字体");
        b.Number("TextFontSize", "字号", "主界面标准文字的字号（像素）。", 0, 96, 1);
        b.Number("TextFontScale", "字号缩放", "未指定字号时按此比例整体缩放宿主原字号。", 0.3, 3, 0.05);
        b.Select("TextColorMode", "字色来源", "保持原样 / 使用固定颜色 / 跟随背景自动反色。", SettingChoices.TextColorModes);
        var autoInvert = Is("TextColorMode", nameof(TextColorMode.AutoInvert));
        b.Color("TextLightColor", "深色背景字色", "背景较暗（亮度低于阈值）时使用的字色。", enabled: [autoInvert]);
        b.Color("TextDarkColor", "浅色背景字色", "背景较亮（亮度高于阈值）时使用的字色。", enabled: [autoInvert]);
        b.Number("TextInvertThreshold", "反色阈值", "背景相对亮度高于该值时使用浅色背景字色。", 0, 1, 0.05, enabled: [autoInvert]);
        b.Color("TextColor", "字色", "文字使用的固定颜色。", enabled: [Is("TextColorMode", nameof(TextColorMode.Fixed))]);
        b.Number("TextOutlineThickness", "勾边宽度", "为文字添加轮廓以提升可读性。", 0, 8, 0.5);
        b.Color("TextOutlineColor", "勾边颜色", "文字勾边（轮廓）的颜色，建议用与字色反差大的颜色。", enabled: [Not("TextOutlineThickness", "0")]);
        b.Switch("TextStylingMainWindowOnly", "仅主界面生效", "开启后只美化主界面文字，提醒等其它区域的文字保持宿主原样。");

        // ===== 动画 =====
        b.Section("动画");
        b.Card("持续动画", "打开后才会使用下方的循环动画设置。", toggle: "AnimationEnabled");
        b.Select("AnimationMode", "动画类型", "选择循环动画的运动方式。", SettingChoices.IslandAnimationModes);
        b.Number("AnimationAmount", "动画幅度", "控制循环动画的强弱。", 0, 1, 0.01, slider: true);
        b.Number("AnimationPeriodSeconds", "动画周期", "完成一次循环所需的时间（秒）。", 0.2, 60, 0.1);

        b.Card("主界面显示动画", "选择主界面出现或消失时使用的动画。",
            enumToggle: ("VisibilityAnimation", VisibilityAnimation.None, SettingChoices.VisibilityAnimations[0].Value));
        b.Select("VisibilityAnimation", "动画类型", "选择主界面出现或消失时使用的动画。", SettingChoices.VisibilityAnimations);
        b.Number("VisibilityDurationSeconds", "显示动画时长", "主界面显示动画的时长（秒）。", 0.1, 10, 0.05);

        b.Card("列表翻页动画", "自定义 ClassIsland 列表/轮播容器的上翻切换动画（轮播容器、上课提醒横幅等）。", toggle: "CarouselAnimationEnabled");
        b.Select("CarouselAnimationType", "动画类型", "列表切换时的动画方式。", SettingChoices.CarouselAnimationTypes);
        b.Number("CarouselAnimationDurationSeconds", "动画时长", "单次翻页动画的时长（秒）。", 0.05, 5, 0.05);
        b.Number("CarouselAnimationOffset", "翻页距离", "滑动/上翻类动画的位移距离（像素）。", 0, 500, 5);

        // ===== 提醒 =====
        b.Section("提醒");
        b.Card("提醒强调动画", "选择收到提醒时使用的强调效果。",
            enumToggle: ("EmphasisAnimation", EmphasisAnimation.None, SettingChoices.EmphasisAnimations[0].Value));
        b.Select("EmphasisAnimation", "动画类型", "选择收到提醒时使用的强调效果。", SettingChoices.EmphasisAnimations);
        b.Number("EmphasisAmount", "强调幅度", "控制强调动画的强弱。", 0, 1, 0.01, slider: true);
        b.Number("EmphasisDurationSeconds", "强调时长", "提醒强调动画的时长（秒）。", 0.1, 10, 0.05);

        b.Card("提醒遮罩动画", "选择提醒遮罩出现和消失时的过渡效果。",
            enumToggle: ("NotificationTransition", NotificationTransition.HostDefault, SettingChoices.NotificationTransitions[0].Value));
        b.Select("NotificationTransition", "过渡类型", "选择提醒遮罩出现和消失时的过渡效果。", SettingChoices.NotificationTransitions);
        b.Number("NotificationTransitionDurationSeconds", "遮罩动画时长", "提醒遮罩动画的时长（秒）。", 0.05, 5, 0.05);

        b.Card("提醒 Ripple", "选择提醒时的扩散效果，高级特效视觉效果更强。",
            enumToggle: ("RippleType", RippleType.None, SettingChoices.RippleTypes[0].Value));
        const string ripple = "RippleType";
        b.Select(ripple, "Ripple 类型", "选择提醒时的扩散效果。", SettingChoices.RippleTypes);
        var pjsk = Is(ripple, nameof(RippleType.Pjsk));
        b.Select("PjskRippleDirection", "特效方向", "PJSK 强调特效相对判定线（主界面）的喷射方向。", SettingChoices.PjskRippleDirections, visible: [pjsk]);
        b.Select("PjskNoteStyle", "note 效果样式", "note 击打效果的配色样式。", SettingChoices.PjskNoteStyles, visible: [pjsk]);
        b.Number("PjskMaxWidth", "特效最大宽度", "特效横向铺开的宽度上限（像素），0 = 跟随主界面宽度。", 0, 2000, 10, visible: [pjsk]);
        b.Switch("PjskShowJudge", "显示 PERFECT 字样", "特效期间在判定线上方显示 PERFECT 判定字样。", visible: [pjsk]);
        string[] highEnd = [nameof(RippleType.Hanabi), nameof(RippleType.Explode), nameof(RippleType.Cinematic), nameof(RippleType.Pjsk)];
        b.Color("RippleColor", "Ripple 颜色", "支持透明度的提醒扩散颜色。", visible: [Not(ripple, highEnd)]);
        b.Number("RippleDurationSeconds", "Ripple 时长", "扩散效果的播放时长（秒）。", 0.1, 10, 0.05, visible: [Not(ripple, highEnd)]);
        b.Number("RippleThickness", "Ripple 线宽", "线性 Ripple 的线条粗细。", 0.5, 40, 0.5,
            visible: [Not(ripple, [.. highEnd, nameof(RippleType.Particle)])]);
        b.Number("RippleOpacity", "全局不透明度", "全局降低 Ripple 效果的透明度，避免上课时分心。", 0.1, 1, 0.05, slider: true);
        var notScreen = Not(ripple, nameof(RippleType.Cinematic), nameof(RippleType.Pjsk));
        b.Switch("RippleConstraintEnabled", "限制扩散范围", "以主界面中心为圆心创建圆形裁剪，约束所有类型 Ripple 的扩散范围。", visible: [notScreen]);
        b.Number("RippleConstraintRadius", "约束半径", "Ripple 扩散的圆形约束半径（像素），0 为自动按主界面大小计算。", 0, 2000, 10,
            visible: [Is("RippleConstraintEnabled", "true"), notScreen]);
        var cinematic = Is(ripple, nameof(RippleType.Cinematic));
        // 这两项的上限按 setter 钳制范围（80 / 60）声明，比设置页滑块（60 / 40）宽：RemoteCI 严格按声明范围校验，
        // 自动化 / 预设能写入滑块范围外的值，声明窄了班级页整页提交会被整批拒绝。
        b.Number("CinematicShakeAmount", "晃动幅度", "提醒时画面晃动的最远位移（像素），0 为关闭晃动。", 0, 80, 1, slider: true, visible: [cinematic]);
        b.Number("CinematicBlurRadius", "模糊半径", "起始模糊半径。", 0, 60, 1, slider: true, visible: [cinematic]);
        b.Number("CinematicFlashAmount", "闪光强度", "中心白光的亮度扩散强度，0 为关闭闪光。", 0, 1, 0.05, slider: true, visible: [cinematic]);

        b.Card("全屏流光", "仿照手机智慧识屏或语音助手激活时的全屏内发光效果，可与上方任意 Ripple 效果叠加播放。", toggle: "MarqueeEnabled");
        b.Color("MarqueeColor", "流光颜色", "流光的整体色调；纯白为完整彩虹，带色调会整体偏向该颜色。");
        b.Number("MarqueeDurationSeconds", "流光时长", "流光效果的播放时长（秒）。", 0.1, 10, 0.05);
        b.Number("MarqueeOpacity", "流光不透明度", "流光效果的整体透明度。", 0.1, 1, 0.05, slider: true);
        b.Number("MarqueeSpeed", "旋转速度", "彩虹沿边框旋转的速度（每秒圈数）。", 0.1, 8, 0.1);
        b.Number("MarqueeFrameThickness", "边框厚度", "发光边框的粗细（相对屏幕短边的比例）。", 0.01, 0.15, 0.01);

        // ===== 即将上课样式 =====
        b.Section("即将上课样式");
        b.Card("即将上课样式", "选择即将上课倒计时期间显示的特效。",
            enumToggle: ("PrepareOnClassStyle", PrepareOnClassStyle.None, SettingChoices.PrepareOnClassStyles[0].Value));
        b.Select("PrepareOnClassStyle", "样式", "选择即将上课倒计时期间显示的特效。", SettingChoices.PrepareOnClassStyles);
        RemoteCondition[] StyleIs(PrepareOnClassStyle style) =>
            [Is(TogglePrefix + "PrepareOnClassStyle", "true"), Is("PrepareOnClassStyle", style.ToString())];

        b.Card("箭头", "斜向箭头从右向左滑动。");
        var arrows = StyleIs(PrepareOnClassStyle.Arrows);
        b.Color("CountdownArrowColor", "箭头颜色", "支持透明度的箭头颜色。", visible: arrows);
        b.Number("CountdownArrowCount", "箭头组数", "屏幕上同时滑动的箭头组数量。", 1, 24, 1, visible: arrows);
        b.Number("CountdownArrowPerGroup", "每组箭头数", "每组内包含的箭头数量。", 1, 12, 1, visible: arrows);
        b.Number("CountdownArrowSpacing", "组内箭头间距", "同一组内相邻箭头之间的距离（像素）。", 0, 100, 1, visible: arrows);
        b.Number("CountdownArrowGroupSpacing", "组间间距", "相邻箭头组之间的额外间距（像素）。", 0, 400, 1, visible: arrows);
        b.Number("CountdownArrowSpeed", "滑动速度", "箭头的移动速度。", 0.1, 12, 0.1, visible: arrows);
        b.Number("CountdownArrowThickness", "箭头线宽", "箭头的线条粗细。", 0.5, 20, 0.5, visible: arrows);

        b.Card("扩散光环", "从主界面中心向外扩散并淡出的圆环。");
        var pulse = StyleIs(PrepareOnClassStyle.PulseRing);
        b.Color("CountdownPulseColor", "光环颜色", "支持透明度的光环颜色。", visible: pulse);
        b.Number("CountdownPulseThickness", "光环线宽", "光环的线条粗细。", 0.5, 20, 0.5, visible: pulse);
        b.Number("CountdownPulseSpeed", "扩散速度", "每秒扩散的圈数。", 0.1, 8, 0.1, visible: pulse);
        b.Number("CountdownPulseMaxRadius", "最大半径", "光环最大半径占主界面宽高中较小值的比例。", 0.1, 1, 0.05, visible: pulse);

        b.Card("扫描线", "一道带渐变尾迹的光线扫过主界面。");
        var scan = StyleIs(PrepareOnClassStyle.Scanline);
        b.Select("CountdownScanDirection", "扫描方向", "横向为水平线上下扫，纵向为竖直线左右扫。", SettingChoices.ScanDirections, visible: scan);
        b.Switch("CountdownScanTailEnabled", "渐变尾迹", "关闭后只显示一条主线，不带渐变尾迹。", visible: scan);
        b.Color("CountdownScanColor", "扫描颜色", "支持透明度的扫描线颜色。", visible: scan);
        b.Number("CountdownScanThickness", "扫描线宽", "扫描线的粗细。", 0.5, 20, 0.5, visible: scan);
        b.Number("CountdownScanSpeed", "扫描速度", "每秒扫描次数。", 0.1, 8, 0.1, visible: scan);

        b.Card("光带", "一条柔和的、非线性运动的光带扫过主界面，如同光照反光。");
        var band = StyleIs(PrepareOnClassStyle.LightBand);
        b.Color("CountdownLightBandColor", "光带颜色", "支持透明度的光带颜色。", visible: band);
        b.Number("CountdownLightBandThickness", "光带粗细", "光带厚度（相对主界面宽高较大者的比例）。", 0.02, 0.5, 0.01, visible: band);
        b.Number("CountdownLightBandAngle", "光带角度", "光带的倾斜角度（度）。", -90, 90, 1, visible: band);
        b.Number("CountdownLightBandSpeed", "扫过速度", "每秒扫过主界面的次数。", 0.1, 8, 0.1, visible: band);

        b.Card("即将上课警告", "注意：本效果较为恐怖，且会阻断鼠标和触摸输入，请谨慎使用！", toggle: "PrepareWarningEnabled");
        b.Color("PrepareWarningColor", "警告颜色", "支持透明度的警告内发光颜色。");
        b.Number("PrepareWarningTriggerSeconds", "提前触发秒数", "距上课剩余秒数小于该值时显示警告。", 5, 600, 5);
        b.Number("PrepareWarningFlashSpeed", "闪动速度", "警告每秒闪动的次数。", 0.1, 10, 0.1);
        b.Number("PrepareWarningFlashAmount", "闪动幅度", "闪动时亮度起伏的深度，0 为常亮。", 0, 1, 0.05, slider: true);
        b.Number("PrepareWarningFrameThickness", "边框厚度", "发光边框的粗细（相对屏幕短边的比例）。", 0.005, 0.1, 0.005);
        b.Number("PrepareWarningOpacity", "透明度", "警告效果的整体透明度。", 0.1, 1, 0.05, slider: true);

        // ===== 交互 =====
        b.Section("交互");
        b.Switch("MouseHoverKeepVisible", "鼠标悬停保持可见", "注意：此功能可能会破坏 ClassIsland。");
        b.Card("主界面点击特效", "注意：此功能可能会破坏 ClassIsland，且大概率不工作。", toggle: "ClickEffectEnabled");
        b.Select("ClickEffectType", "特效类型", "选择点击特效的样式。", SettingChoices.ClickEffectTypes);

        // ===== 虚假天气 =====
        b.Section("虚假天气");
        b.Card("虚假天气", "向 ClassIsland 注入自定义天气数据", toggle: "FakeWeatherEnabled");
        b.Select("FakeWeatherCode", "天气类型", "选择要显示的天气。", SettingChoices.FakeWeatherCodes);
        b.Number("FakeWeatherTemperature", "温度", "虚假天气的温度（℃）。", -60, 60, 1);
        b.Number("FakeWeatherFeelsLike", "体感温度", "体感温度（℃）。", -60, 60, 1);
        b.Number("FakeWeatherHumidity", "湿度", "相对湿度（%）。", 0, 100, 1);
        b.Number("FakeWeatherPressure", "气压", "大气压（hPa）。", 800, 1200, 1);
        b.Number("FakeWeatherVisibility", "能见度", "能见度（km）。", 0, 100, 0.5);
        b.Text("FakeWeatherWindDirection", "风向", "如「东风」「西北风」。", "如：东风");
        b.Text("FakeWeatherWindScale", "风力", "如「2级」「3-4级」。", "如：2级");
        b.Number("FakeWeatherAqi", "空气质量 AQI", "AQI 数值（0-500），越高污染越重。", 0, 500, 1);
        b.Select("FakeWeatherAlertIcon", "预警图标", "预警图标等级；选择后会显示「图标+类型」胶囊，无则不显示图标。", SettingChoices.FakeWeatherAlertIcons);
        b.Text("FakeWeatherAlertType", "预警类型", "预警胶囊里显示的类型文字，如「暴雨」。", "如：暴雨");
        b.Text("FakeWeatherAlertLevel", "预警等级", "如「蓝色预警」，可留空。", "如：蓝色预警");
        b.Text("FakeWeatherAlertTitle", "预警标题", "用于天气规则匹配的完整标题，如「xx市气象台发布暴雨蓝色预警」。", "如：xx市气象台发布暴雨蓝色预警");
        b.Text("FakeWeatherAlertDetail", "预警详情", "预警详细内容，可留空。", "如：预计未来 6 小时……（可留空）");
        b.Number("FakeWeatherRainRemainingMinutes", "降水提醒", "距降雨开始分钟数（正值）；负值表示正在下雨、预计该分钟后停；0 为无降水。", -180, 180, 1);

        // ===== 高级样式表（含危险区域的基础变形）=====
        b.Section("高级样式表");
        b.LocalPath("StyleSheetPath", "覆盖样式表路径", "填写 .axaml 样式表的完整路径（教室端本机路径）。", @"如 D:\Styles\Overrides.axaml");
        b.Switch("WatchStyleSheet", "自动热重载", "保存样式表后自动重新加载。");
        b.Card("基础变形", "这些值会覆盖并叠加在 ClassIsland 的主界面外观设置之上。（危险区域：与 ClassIsland 原生外观设置重叠）");
        b.Number("Opacity", "不透明度", "控制主界面的整体透明度。", 0, 1, 0.05, slider: true);
        b.Number("OffsetX", "水平偏移", "向左或向右移动主界面。", -2000, 2000, 1);
        b.Number("OffsetY", "垂直偏移", "向上或向下移动主界面。", -2000, 2000, 1);
        b.Number("CornerRadius", "圆角半径", "注意：在此处修改圆角有时需要重启 ClassIsland 才会生效", 0, 20, 1);
        b.Number("Rotation", "旋转角度", "以中心点旋转主界面。", -360, 360, 1);
        // 设置页没有形状下拉（修改圆角时自动切为圆角矩形）；远程额外开放，便于统一改回「跟随 ClassIsland」。
        b.Select("Shape", "形状", "主界面的整体形状；修改圆角半径时会自动切为圆角矩形。", SettingChoices.IslandShapes);

        // ===== 调试 =====
        b.Section("调试");
        b.Select("StartupOpenTarget", "启动时自动打开", "调试用：ClassIsland 启动后自动打开指定位置。", SettingChoices.StartupOpenTargets);
        b.Switch("ReduceVisualBurden", "降低视觉负担", "隐藏设置项的说明文字，只保留名称，减少视觉干扰。");
        b.Switch("DisableVersionCheck", "关闭版本检查和提醒", "不再检查插件版本，也不提示去 GitHub 更新。");
        b.Switch("DisableDegradationCheck", "关闭宿主定位点失效检查", "不再检测宿主点位失效，也不显示降级提示。");
        b.Switch("DiagnosticLoggingEnabled", "输出诊断日志", "写入日志文件用于排查问题。");

        return b.Items;
    }

    private static RemoteCondition Is(string key, params string[] values) => new(key, values);

    private static RemoteCondition Not(string key, params string[] values) => new(key, values, Not: true);

    /// <summary>按设置页顺序累积字段；记住当前分区 / 卡片，卡片总开关自动成为卡片内条目的可用条件。</summary>
    private sealed class Builder
    {
        private string? _section;
        private string? _card;
        private string? _cardDescription;
        private RemoteCondition? _cardGate;

        public List<RemoteBinding> Items { get; } = [];

        public void Section(string? title)
        {
            _section = title;
            Row();
        }

        /// <summary>结束当前卡片：之后的条目单独成行（对应设置页的单行 Setting）。</summary>
        public void Row()
        {
            _card = null;
            _cardDescription = null;
            _cardGate = null;
        }

        /// <summary>
        /// 开始一张卡片。<paramref name="toggle"/> 为真实开关属性；<paramref name="enumToggle"/> 为页面专用的
        /// 「关闭即写 None」虚拟开关。<paramref name="itemsFollowToggle"/> 对应 SwitchableGroup（关时条目置灰）。
        /// </summary>
        public void Card(string title, string description, string? toggle = null,
            (string Property, Enum Off, Enum First)? enumToggle = null, bool itemsFollowToggle = true)
        {
            _card = title;
            _cardDescription = description;
            _cardGate = null;
            if (toggle != null)
            {
                Add(Property(toggle), new RemoteField(toggle, title, RemoteFieldType.Switch, description) { CardToggle = true });
                if (itemsFollowToggle) _cardGate = Is(toggle, "true");
            }
            else if (enumToggle is { } t)
            {
                var key = TogglePrefix + t.Property;
                Items.Add(new RemoteBinding(Decorate(new RemoteField(key, title, RemoteFieldType.Switch, description) { CardToggle = true }),
                    null, new RemoteEnumToggle(Property(t.Property), t.Off, t.First)));
                if (itemsFollowToggle) _cardGate = Is(key, "true");
            }
        }

        public void Lock() => Items.Add(new RemoteBinding(Decorate(new RemoteField(
            InjectorRemoteSettings.LockKey, "锁定本机样式设置", RemoteFieldType.Switch,
            "开启后本机「样式注入器」设置页变为只读并提示由学校统一管理，只能通过 WebUI 修改；关闭即恢复本地编辑。" +
            "教室端卸载或停用 RemoteCI 后锁定自动失效。")), null, null));

        public void Switch(string property, string label, string description,
            RemoteCondition[]? visible = null, RemoteCondition[]? enabled = null) =>
            Add(Property(property), new RemoteField(property, label, RemoteFieldType.Switch, description), visible, enabled);

        public void Number(string property, string label, string description, double min, double max, double step,
            bool slider = false, RemoteCondition[]? visible = null, RemoteCondition[]? enabled = null) =>
            Add(Property(property), new RemoteField(property, label, RemoteFieldType.Number, description, min, max)
            {
                Step = step,
                Slider = slider
            }, visible, enabled);

        public void Color(string property, string label, string description,
            RemoteCondition[]? visible = null, RemoteCondition[]? enabled = null) =>
            Add(Property(property), new RemoteField(property, label, RemoteFieldType.Color, description, Placeholder: "#AARRGGBB"),
                visible, enabled);

        public void Text(string property, string label, string description, string? placeholder = null,
            RemoteCondition[]? visible = null, RemoteCondition[]? enabled = null) =>
            Add(Property(property), new RemoteField(property, label, RemoteFieldType.Text, description, Placeholder: placeholder),
                visible, enabled);

        /// <summary>教室端本机文件路径：远程写入时拒绝网络共享路径 / URL（见 <see cref="RemoteField.LocalPathOnly"/>）。</summary>
        public void LocalPath(string property, string label, string description, string? placeholder = null) =>
            Add(Property(property), new RemoteField(property, label, RemoteFieldType.Text, description, Placeholder: placeholder)
            {
                LocalPathOnly = true
            });

        public void Select<T>(string property, string label, string description, IReadOnlyList<SettingChoice<T>> choices,
            RemoteCondition[]? visible = null, RemoteCondition[]? enabled = null) =>
            Add(Property(property), new RemoteField(property, label, RemoteFieldType.Select, description,
                Options: choices.Select(c => Convert.ToString(c.Value, CultureInfo.InvariantCulture)!).ToList(),
                OptionLabels: choices.Select(c => c.Text).ToList()), visible, enabled);

        private void Add(PropertyInfo property, RemoteField field,
            RemoteCondition[]? visible = null, RemoteCondition[]? enabled = null)
        {
            if (field.CardToggle)
            {
                Items.Add(new RemoteBinding(Decorate(field), property, null));
                return;
            }

            var gates = new List<RemoteCondition>();
            if (_cardGate != null) gates.Add(_cardGate);
            if (enabled != null) gates.AddRange(enabled);
            Items.Add(new RemoteBinding(Decorate(field) with
            {
                VisibleWhen = visible is { Length: > 0 } ? visible : null,
                EnabledWhen = gates.Count > 0 ? gates : null
            }, property, null));
        }

        private RemoteField Decorate(RemoteField field) => field with
        {
            Section = _section,
            Card = _card,
            CardDescription = _card == null ? null : _cardDescription
        };

        private static PropertyInfo Property(string name) =>
            typeof(InjectorSettings).GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"InjectorSettings 没有属性 {name}（远程布局与设置模型不一致）");
    }
}

/// <summary>远程字段与本机设置的绑定：普通属性、页面专用的「关闭即写 None」虚拟开关，或二者皆无（锁定开关）。</summary>
internal sealed record RemoteBinding(RemoteField Field, PropertyInfo? Property, RemoteEnumToggle? EnumToggle);

/// <summary>卡片总开关（虚拟）：开 = 属性不为 <see cref="Off"/>；从关打开且未同时指定类型时自动选第一项（同设置页 AutoSelectOnEnable）。</summary>
internal sealed record RemoteEnumToggle(PropertyInfo Property, Enum Off, Enum First);
