using ClassIslandInjector;

// 1. 模拟“把当前全部设置保存为预设”
var live = new InjectorSettings { StyleSheetPath = @"D:\cfg\Overrides.axaml" };
live.BeginUpdate();
live.Opacity = 0.85;
live.Rotation = -15;
live.CornerRadius = 18;
live.CustomBackgroundEnabled = true;
live.BackgroundColor = "#CC1A2334";
live.GradientEnabled = true;
live.GradientEndColor = "#8A394D70";
live.GradientDirection = GradientDirection.TopToBottom;
live.BackgroundTextureType = BackgroundTexture.Grid;
live.BackgroundTextureSpectrumSensitivity = 1.5;
live.BackgroundTextureSpectrumBars = 48;
live.BackgroundTextureSpectrumMirrored = false;
live.Shape = IslandShape.Capsule;
live.AnimationEnabled = true;
live.AnimationMode = IslandAnimationMode.Breathe;
live.VisibilityAnimation = VisibilityAnimation.Fade;
live.EmphasisAnimation = EmphasisAnimation.Pulse;
live.RippleType = RippleType.Glow;
live.RippleOpacity = 0.6;
live.DynamicBackgroundColorEnabled = true;
live.WallpaperEnabled = true;
live.WallpaperSource = WallpaperSource.SmtcAlbum;
live.PrepareOnClassStyle = PrepareOnClassStyle.Arrows;
live.CountdownScanTailEnabled = false;
live.CountdownArrowCount = 8;
live.CountdownArrowPerGroup = 4;
live.CountdownArrowSpacing = 20;
live.CountdownArrowGroupSpacing = 40;
live.CountdownArrowSpeed = 3.1;
live.EndUpdate();

var snapshot = live.Clone();
var preset = new UserPreset { Name = "晚自习", Settings = snapshot };

// 2. 模拟保存到 presets.json
var dir = Path.Combine(Path.GetTempPath(), "preset-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dir);
InjectorPresetStore.Save(dir, [preset]);
var written = File.ReadAllText(Path.Combine(dir, "presets.json"));
Console.WriteLine("=== presets.json ===");
Console.WriteLine(written);

// 3. 模拟加载
var loaded = InjectorPresetStore.Load(dir);
if (loaded.Count != 1 || loaded[0].Name != "晚自习")
    throw new Exception("预设列表加载失败");

// 4. 模拟套用：把当前设置替换为预设快照（CopyFrom）
var current = new InjectorSettings { StyleSheetPath = @"D:\cfg\Overrides.axaml" };
current.BeginUpdate();
current.CopyFrom(loaded[0].Settings);
current.EndUpdate();

// 5. 校验关键字段
int failures = 0;
void Check(string name, object? expected, object? actual)
{
    if (!Equals(expected, actual))
    {
        failures++;
        Console.WriteLine($"FAIL {name}: 期望 {expected}，实际 {actual}");
    }
    else
    {
        Console.WriteLine($"OK   {name}");
    }
}

Check("Opacity", 0.85, current.Opacity);
Check("Rotation", -15.0, current.Rotation);
Check("CornerRadius", 18.0, current.CornerRadius);
Check("Shape", IslandShape.Capsule, current.Shape);
Check("BackgroundColor", "#CC1A2334", current.BackgroundColor);
Check("GradientEndColor", "#8A394D70", current.GradientEndColor);
Check("GradientDirection", GradientDirection.TopToBottom, current.GradientDirection);
Check("BackgroundTextureType", BackgroundTexture.Grid, current.BackgroundTextureType);
Check("BackgroundTextureSpectrumSensitivity", 1.5, current.BackgroundTextureSpectrumSensitivity);
Check("BackgroundTextureSpectrumBars", 48, current.BackgroundTextureSpectrumBars);
Check("BackgroundTextureSpectrumMirrored", false, current.BackgroundTextureSpectrumMirrored);
Check("AnimationMode", IslandAnimationMode.Breathe, current.AnimationMode);
Check("VisibilityAnimation", VisibilityAnimation.Fade, current.VisibilityAnimation);
Check("EmphasisAnimation", EmphasisAnimation.Pulse, current.EmphasisAnimation);
Check("RippleType", RippleType.Glow, current.RippleType);
Check("RippleOpacity", 0.6, current.RippleOpacity);
Check("DynamicBackgroundColorEnabled", true, current.DynamicBackgroundColorEnabled);
Check("WallpaperSource", WallpaperSource.SmtcAlbum, current.WallpaperSource);
Check("CountdownArrowCount", 8, current.CountdownArrowCount);
Check("CountdownScanTailEnabled", false, current.CountdownScanTailEnabled);
Check("PrepareOnClassStyle", PrepareOnClassStyle.Arrows, current.PrepareOnClassStyle);
Check("CountdownArrowPerGroup", 4, current.CountdownArrowPerGroup);
Check("CountdownArrowSpacing", 20.0, current.CountdownArrowSpacing);
Check("CountdownArrowGroupSpacing", 40.0, current.CountdownArrowGroupSpacing);
Check("CountdownArrowSpeed", 3.1, current.CountdownArrowSpeed);
Check("StyleSheetPath", @"D:\cfg\Overrides.axaml", current.StyleSheetPath);

// 6. 删除预设后文件为空列表
InjectorPresetStore.Save(dir, []);
var afterDelete = InjectorPresetStore.Load(dir);
if (afterDelete.Count != 0)
    throw new Exception("删除后列表应为空");

Directory.Delete(dir, true);

// ===== 6. 内置「无预设」：重置为中性默认、不注入任何内容、保留样式表设置 =====
var noPreset = new InjectorSettings();
noPreset.StyleSheetPath = @"D:\cfg\Overrides.axaml";
noPreset.WatchStyleSheet = false;
noPreset.BeginUpdate();
noPreset.Opacity = 0.5;
noPreset.Shape = IslandShape.Capsule;
noPreset.CustomBackgroundEnabled = true;
noPreset.RippleType = RippleType.Hanabi;
noPreset.AnimationEnabled = true;
noPreset.AnimationMode = IslandAnimationMode.Breathe;
noPreset.PrepareOnClassStyle = PrepareOnClassStyle.Scanline;
noPreset.EndUpdate();
noPreset.ResetToDefaults();
Check("无预设 Shape=HostDefault", IslandShape.HostDefault, noPreset.Shape);
Check("无预设 Opacity=1", 1.0, noPreset.Opacity);
Check("无预设 CustomBackgroundEnabled=false", false, noPreset.CustomBackgroundEnabled);
Check("无预设 RippleType=None", RippleType.None, noPreset.RippleType);
Check("无预设 AnimationMode=None", IslandAnimationMode.None, noPreset.AnimationMode);
Check("无预设 即将上课样式=None", PrepareOnClassStyle.None, noPreset.PrepareOnClassStyle);
Check("无预设 RippleConstraintEnabled=true", true, noPreset.RippleConstraintEnabled);
Check("无预设 RippleConstraintRadius=0", 0.0, noPreset.RippleConstraintRadius);
Check("无预设保留 StyleSheetPath", @"D:\cfg\Overrides.axaml", noPreset.StyleSheetPath);
Check("无预设保留 WatchStyleSheet", false, noPreset.WatchStyleSheet);

// ===== 8. 全新安装默认值中性 + 兜底刷新默认 10s + 内置无预设名称 =====
var fresh = new InjectorSettings { StyleSheetPath = @"D:\cfg\Overrides.axaml" };
Check("全新默认 Enabled=true", true, fresh.Enabled);
Check("全新默认 Shape=HostDefault", IslandShape.HostDefault, fresh.Shape);
Check("全新默认 RippleType=None", RippleType.None, fresh.RippleType);
Check("全新默认 VisibilityAnimation=None", VisibilityAnimation.None, fresh.VisibilityAnimation);
Check("全新默认 EmphasisAnimation=None", EmphasisAnimation.None, fresh.EmphasisAnimation);
Check("全新默认 AnimationMode=None", IslandAnimationMode.None, fresh.AnimationMode);
Check("全新默认 即将上课样式=None", PrepareOnClassStyle.None, fresh.PrepareOnClassStyle);
Check("全新默认 RippleConstraintEnabled=true", true, fresh.RippleConstraintEnabled);
Check("全新默认 RippleConstraintRadius=0", 0.0, fresh.RippleConstraintRadius);
Check("全新默认 每组箭头数=2", 2, fresh.CountdownArrowPerGroup);
Check("全新默认 组内间距=12", 12.0, fresh.CountdownArrowSpacing);
Check("全新默认 组间间距=24", 24.0, fresh.CountdownArrowGroupSpacing);
Check("全新默认 箭头线宽=8", 8.0, fresh.CountdownArrowThickness);
Check("全新默认 扫描方向=横向", ScanlineDirection.Horizontal, fresh.CountdownScanDirection);
Check("全新默认 渐变方向=左上右下", GradientDirection.TopLeftToBottomRight, fresh.GradientDirection);
Check("全新默认 纹理类型=None", BackgroundTexture.None, fresh.BackgroundTextureType);
Check("全新默认 频谱灵敏度=1", 1.0, fresh.BackgroundTextureSpectrumSensitivity);
Check("全新默认 频谱柱条数=32", 32, fresh.BackgroundTextureSpectrumBars);
Check("全新默认 频谱双面对称=false", false, fresh.BackgroundTextureSpectrumMirrored);
Check("全新默认 频谱自动匹配宽度=true", true, fresh.BackgroundTextureSpectrumAutoWidth);
Check("全新默认 扫描尾迹=true", true, fresh.CountdownScanTailEnabled);
Check("全新默认 RippleOpacity=1", 1.0, fresh.RippleOpacity);
Check("全新默认 兜底刷新=10s", 10.0, fresh.AlbumColorPollingIntervalSeconds);
Check("内置无预设名称=无预设", "无预设", InjectorPresetStore.NoPresetName);

Console.WriteLine(failures == 0
    ? "=== 全部通过：设置模型 / 无预设 / 默认值 / 用户预设 验证 OK ==="
    : $"=== 有 {failures} 项失败 ===");
return failures == 0 ? 0 : 1;
