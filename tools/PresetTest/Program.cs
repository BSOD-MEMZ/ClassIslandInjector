using ClassIslandInjector;

// 1. 模拟“把当前全部设置保存为预设”
var live = new InjectorSettings { StyleSheetPath = @"D:\cfg\Overrides.axaml" };
live.BeginUpdate();
live.Opacity = 0.85;
live.Scale = 1.2;
live.Rotation = -15;
live.CornerRadius = 24;
live.CustomBackgroundEnabled = true;
live.BackgroundColor = "#CC1A2334";
live.GradientEnabled = true;
live.GradientEndColor = "#8A394D70";
live.Shape = IslandShape.Capsule;
live.AnimationEnabled = true;
live.AnimationMode = IslandAnimationMode.Breathe;
live.VisibilityAnimation = VisibilityAnimation.Fade;
live.EmphasisAnimation = EmphasisAnimation.Pulse;
live.RippleType = RippleType.Glow;
live.DynamicBackgroundColorEnabled = true;
live.WallpaperEnabled = true;
live.WallpaperSource = WallpaperSource.SmtcAlbum;
live.CountdownArrowsEnabled = true;
live.CountdownArrowCount = 8;
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
void Check(string name, object? expected, object? actual)
{
    if (!Equals(expected, actual))
        throw new Exception($"{name} 不一致：期望 {expected}，实际 {actual}");
}

Check("Opacity", 0.85, current.Opacity);
Check("Scale", 1.2, current.Scale);
Check("Rotation", -15.0, current.Rotation);
Check("CornerRadius", 24.0, current.CornerRadius);
Check("Shape", IslandShape.Capsule, current.Shape);
Check("BackgroundColor", "#CC1A2334", current.BackgroundColor);
Check("GradientEndColor", "#8A394D70", current.GradientEndColor);
Check("AnimationMode", IslandAnimationMode.Breathe, current.AnimationMode);
Check("VisibilityAnimation", VisibilityAnimation.Fade, current.VisibilityAnimation);
Check("EmphasisAnimation", EmphasisAnimation.Pulse, current.EmphasisAnimation);
Check("RippleType", RippleType.Glow, current.RippleType);
Check("DynamicBackgroundColorEnabled", true, current.DynamicBackgroundColorEnabled);
Check("WallpaperSource", WallpaperSource.SmtcAlbum, current.WallpaperSource);
Check("CountdownArrowCount", 8, current.CountdownArrowCount);
Check("CountdownArrowSpeed", 3.1, current.CountdownArrowSpeed);
Check("StyleSheetPath", @"D:\cfg\Overrides.axaml", current.StyleSheetPath);

// 6. 删除预设后文件为空列表
InjectorPresetStore.Save(dir, []);
var afterDelete = InjectorPresetStore.Load(dir);
if (afterDelete.Count != 0)
    throw new Exception("删除后列表应为空");

Directory.Delete(dir, true);
Console.WriteLine("=== 全部通过：用户预设 保存/加载/套用/删除 往返验证 OK ===");
