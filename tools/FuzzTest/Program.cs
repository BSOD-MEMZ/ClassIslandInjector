using ClassIslandInjector;

// 暴力模糊测试：向 InjectorSettingsStore / InjectorPresetStore 喂各种畸形、
// 越界、极端值 JSON，验证：
//   1. 任何输入都不会抛出未捕获异常（Load 只捕获 JsonException，其余异常会崩插件）
//   2. 损坏输入安全回退默认值，不污染后续
//   3. 合法极端值被正确 clamp / 保留
int failures = 0;

void Report(string name, bool ok, string? detail = null)
{
    Console.WriteLine($"{(ok ? "OK  " : "FAIL")} {name}{(detail != null ? "：" + detail : "")}");
    if (!ok) failures++;
}

static string CreateTempDir()
{
    var dir = Path.Combine(Path.GetTempPath(), "injector-fuzz-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    return dir;
}

static string Truncate(string s, int n = 70) => s.Length <= n ? s : s[..n] + "...";

// 构造一个包含全部典型极端值的设置对象，验证序列化→反序列化往返不丢字段、不崩
void RoundTripExtremeValues()
{
    var s = new InjectorSettings
    {
        Opacity = 1e300,          // 远超 clamp 上限
        Rotation = -1e300,        // 远超 clamp 下限
        CornerRadius = -5,        // 低于下限
        WallpaperScale = 1e9,
        BackgroundTextureSpectrumSensitivity = -100,
        BackgroundTextureSpectrumBars = 99999,
        CountdownArrowCount = -1,
        RippleColor = "#GGGGGG",  // 非法颜色（字符串型，无 clamp）
        StyleSheetPath = "",
        FakeWeatherAlertType = new string('X', 5000),
    };
    s.BeginUpdate();
    s.EndUpdate();

    var dir = CreateTempDir();
    try
    {
        InjectorSettingsStore.Save(dir, s);
        var raw = File.ReadAllText(Path.Combine(dir, "settings.json"));
        var loaded = InjectorSettingsStore.Load(dir, Path.Combine(Path.GetTempPath(), "no-plugin"));
        Report("极端值往返", loaded != null && loaded.Opacity <= 1 && loaded.Rotation >= -360 && loaded.CornerRadius >= 0,
            $"opacity={loaded?.Opacity}, rotation={loaded?.Rotation}, corner={loaded?.CornerRadius}, bars={loaded?.BackgroundTextureSpectrumBars}");
    }
    catch (Exception ex)
    {
        Report("极端值往返", false, $"抛异常 {ex.GetType().Name}: {ex.Message}");
    }
}

// 畸形 / 越界 / 类型错误 / 边界输入
string[] malformed =
[
    "{",
    "{\"Opacity\":",
    "[1,2,3]",
    "\"hello\"",
    "123",
    "true",
    "null",
    "",
    "   ",
    "{",
    "{\"Opacity\": \"abc\"}",
    "{\"Enabled\": \"yes\"}",
    "{\"RippleType\": \"hello\"}",
    "{\"StyleSheetPath\": null}",
    "{\"Opacity\": null}",
    "{\"Opacity\": 1e999}",
    "{\"Opacity\": -1e999}",
    "{\"Opacity\": 999}",
    "{\"Opacity\": -5}",
    "{\"Opacity\": 0.5}",
    "{\"Rotation\": 1e308}",
    "{\"Rotation\": 999999}",
    "{\"CornerRadius\": -3}",
    "{\"RippleType\": 999}",
    "{\"Shape\": -1}",
    "{\"AnimationMode\": 42}",
    "{\"opacity\": 0.5}",
    "{\"Opacity\": 0.5, \"Opacity\": 0.9}",
    "{\"Enabled\": true, \"Opacity\": \"x\"}",
    "{\"StyleSheetPath\": \"" + new string('A', 1_000_000) + "\"}",
    "{\"CountdownArrowColor\": \"#GGGGGG\"}",
    "{\"CountdownArrowColor\": \"notacolor\"}",
    "{\"CountdownArrowColor\": \"\"}",
    "{\"RippleColor\": null}",
    "\uFEFF{\"Opacity\": 0.5}",
    "{\"Opacity\": 0.5} trailing",
    "{\"Opacity\": NaN}",
    "{\"Opacity\": Infinity}",
    "{\"Opacity\": -Infinity}",
    "{\"Opacity\": 0x10}",
    "{\"Opacity\": 5e-324}",
    "{\"BackgroundTextureSpectrumBars\": 0}",
    "{\"BackgroundTextureSpectrumBars\": -3}",
    "{\"Opacity\": 0.9999999999999999}",
    "{\"RippleThickness\": 0}",
    "{\"RippleDurationSeconds\": 0}",
    "{\"EmphasisDurationSeconds\": -1}",
    "{\"FakeWeatherTemperature\": 1e308}",
    "{\"FakeWeatherAqi\": -1e308}",
    "{\"WallpaperIntervalSeconds\": 0}",
    "{\"AlbumColorPollingIntervalSeconds\": 0}",
];

foreach (var json in malformed)
{
    try
    {
        var dir = CreateTempDir();
        File.WriteAllText(Path.Combine(dir, "settings.json"), json);
        var s = InjectorSettingsStore.Load(dir, Path.Combine(Path.GetTempPath(), "no-plugin"));
        Report($"Load {Truncate(json)}", s != null, s != null ? $"opacity={s.Opacity}" : "返回null");
    }
    catch (Exception ex)
    {
        Report($"Load {Truncate(json)}", false, $"抛异常 {ex.GetType().Name}: {ex.Message}");
    }
}

// 损坏 JSON 后应能自动恢复（备份 + 全新默认），且再次 Load 正常
try
{
    var dir = CreateTempDir();
    File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"Opacity\": \"broken\"");
    var first = InjectorSettingsStore.Load(dir, Path.Combine(Path.GetTempPath(), "no-plugin"));
    var second = InjectorSettingsStore.Load(dir, Path.Combine(Path.GetTempPath(), "no-plugin"));
    var backups = Directory.GetFiles(dir, "settings.json.invalid-*").Length;
    Report("损坏后自动恢复+备份", first != null && second != null && backups >= 1, $"备份数={backups}");
}
catch (Exception ex)
{
    Report("损坏后自动恢复+备份", false, $"抛异常 {ex.GetType().Name}: {ex.Message}");
}

// 预设存储模糊测试
try
{
    var dir = CreateTempDir();
    File.WriteAllText(Path.Combine(dir, "presets.json"), "[{bad json");
    var list = InjectorPresetStore.Load(dir);
    Report("畸形 presets.json", list != null, list != null ? $"数量={list.Count}" : "null");
}
catch (Exception ex)
{
    Report("畸形 presets.json", false, $"抛异常 {ex.GetType().Name}: {ex.Message}");
}

try
{
    var dir = CreateTempDir();
    File.WriteAllText(Path.Combine(dir, "presets.json"), "null");
    var list = InjectorPresetStore.Load(dir);
    Report("null presets.json", list != null && list.Count == 0, $"数量={list?.Count}");
}
catch (Exception ex)
{
    Report("null presets.json", false, $"抛异常 {ex.GetType().Name}: {ex.Message}");
}

try
{
    var dir = CreateTempDir();
    File.WriteAllText(Path.Combine(dir, "presets.json"), "[{\"Name\":null,\"Settings\":null},{\"Name\":\"\",\"Settings\":{}}]");
    var list = InjectorPresetStore.Load(dir);
    Report("预设含 null 字段", list != null, $"数量={list?.Count}");
}
catch (Exception ex)
{
    Report("预设含 null 字段", false, $"抛异常 {ex.GetType().Name}: {ex.Message}");
}

// 合法设置：Enabled=false 等各种组合应原样保留
try
{
    var dir = CreateTempDir();
    var s = new InjectorSettings { Enabled = false, RippleType = RippleType.Hanabi, Shape = IslandShape.Capsule, Opacity = 0.33 };
    InjectorSettingsStore.Save(dir, s);
    var loaded = InjectorSettingsStore.Load(dir, Path.Combine(Path.GetTempPath(), "no-plugin"));
    Report("合法设置保留", loaded != null && loaded.Enabled == false && loaded.RippleType == RippleType.Hanabi && loaded.Shape == IslandShape.Capsule && Math.Abs(loaded.Opacity - 0.33) < 1e-9);
}
catch (Exception ex)
{
    Report("合法设置保留", false, $"抛异常 {ex.GetType().Name}: {ex.Message}");
}

RoundTripExtremeValues();

Console.WriteLine(failures == 0
    ? "=== 全部通过：设置/预设 模糊测试 OK ==="
    : $"=== 有 {failures} 项失败 ===");
return failures == 0 ? 0 : 1;
