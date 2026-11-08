using ClassIslandInjector;
using ClassIslandInjector.Automation;
using ClassIslandInjector.RemoteControl;
using RemoteCI.Shared.Models;

// RemoteCI 远程管理映射回归：字段声明 / 布局 / 当前值 / 写入 四方口径一致。
var failures = 0;
void Check(bool ok, string name, string? detail = null)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail is null ? "" : "  → " + detail)}");
    if (!ok) failures++;
}

// 与 RemoteCiIntegration.ToParameter（新版 RemoteCI 分支）相同的转换（那份文件引用 RemoteCI.Plugin，探针不链接它）。
static ExtensionParameter ToParameter(RemoteField f) => new()
{
    Key = f.Key, Label = f.Label, Type = (ExtensionParameterType)(int)f.Type, Description = f.Description,
    Min = f.Min, Max = f.Max, Options = f.Options?.ToList(), OptionLabels = f.OptionLabels?.ToList(), Placeholder = f.Placeholder,
    Section = f.Section, Card = f.Card, CardDescription = f.CardDescription, CardToggle = f.CardToggle, Step = f.Step, Slider = f.Slider,
    VisibleWhen = f.VisibleWhen?.Select(c => new ExtensionFieldCondition { Key = c.Key, Values = c.Values.ToList(), Not = c.Not }).ToList(),
    EnabledWhen = f.EnabledWhen?.Select(c => new ExtensionFieldCondition { Key = c.Key, Values = c.Values.ToList(), Not = c.Not }).ToList(),
};

var fields = InjectorRemoteSettings.Fields;
var parameters = fields.Select(ToParameter).ToList();

// --dump <文件>：导出上报给 RemoteCI 的分组定义（字段 + 当前值），用于在 WebUI 里目视核对布局。
if (args is ["--dump", var dumpPath])
{
    var group = new ExtensionGroupDefinition
    {
        Id = "classisland.injector", DisplayName = "样式注入器", Settings = parameters, Values = InjectorRemoteSettings.ReadValues()
    };
    File.WriteAllText(dumpPath, System.Text.Json.JsonSerializer.Serialize(group));
    Console.WriteLine($"已导出 {parameters.Count} 个字段 → {dumpPath}");
    return 0;
}
var byKey = fields.ToDictionary(f => f.Key);

// 1. 字段声明（RemoteCiExtensionRegistry.RegisterGroup 的校验：Key 非空且唯一）
Check(fields.Count > 150, "字段数量", fields.Count.ToString());
Check(fields.All(f => !string.IsNullOrWhiteSpace(f.Key)), "字段 Key 非空");
var dup = fields.GroupBy(f => f.Key).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
Check(dup.Count == 0, "字段 Key 唯一", string.Join(",", dup));
Check(fields[0].Key == InjectorRemoteSettings.LockKey, "锁定开关排在最前");
var selects = parameters.Where(p => p.Type == ExtensionParameterType.Select).ToList();
Check(selects.All(p => p.Options is { Count: > 0 } && p.OptionLabels?.Count == p.Options.Count), "下拉字段的选项与显示名一一对应");
Check(byKey["TextColorMode"].OptionLabels?.SequenceEqual(SettingChoices.TextColorModes.Select(c => c.Text)) == true,
    "下拉显示名与设置页同源（SettingChoices）", string.Join("/", byKey["TextColorMode"].OptionLabels ?? []));
Check(byKey["RippleType"].Options?.Contains("None") == false, "卡片开关接管「无」：Ripple 类型下拉里没有 None（同设置页）");

// 2. 覆盖面：自动化目录里的每个设置项都出现在远程设置页（新增设置项忘了加布局时这里报错）
var missing = InjectorSettingCatalog.All.Select(s => s.PropertyName).Where(p => !byKey.ContainsKey(p)).ToList();
Check(missing.Count == 0, "目录中的设置项全部可远程修改", string.Join(",", missing));

// 3. 布局：分区顺序与设置页一致；卡片连续；联动条件引用的字段存在且取值合法
var sections = new List<string?>();
foreach (var f in fields) if (sections.Count == 0 || sections[^1] != f.Section) sections.Add(f.Section);
string?[] expectedSections = ["远程管理", null, "用户预设", "背景", "边框、阴影和文字", "动画", "提醒", "即将上课样式", "交互", "虚假天气", "高级样式表", "调试"];
Check(sections.SequenceEqual(expectedSections), "分区顺序 == 设置页", string.Join(" | ", sections.Select(s => s ?? "(无)")));
var cardRuns = new List<string>();
foreach (var f in fields.Where(f => f.Card != null))
    if (cardRuns.Count == 0 || cardRuns[^1] != $"{f.Section}/{f.Card}") cardRuns.Add($"{f.Section}/{f.Card}");
var splitCards = cardRuns.GroupBy(c => c).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
Check(splitCards.Count == 0, "同一卡片的字段连续（不会被拆成两张）", string.Join(",", splitCards));
Check(fields.Where(f => f.CardToggle).All(f => f.Type == RemoteFieldType.Switch && f.Card == f.Label), "卡片总开关都是开关，标签即卡片名");
Check(fields.Where(f => f.CardToggle).GroupBy(f => f.Card).All(g => g.Count() == 1), "每张卡片至多一个总开关");
var conditionErrors = new List<string>();
foreach (var f in fields)
foreach (var c in (f.VisibleWhen ?? []).Concat(f.EnabledWhen ?? []))
{
    if (!byKey.TryGetValue(c.Key, out var target)) { conditionErrors.Add($"{f.Key}→{c.Key}(不存在)"); continue; }
    var valid = target.Type switch
    {
        RemoteFieldType.Switch => c.Values.All(v => v is "true" or "false"),
        RemoteFieldType.Select => c.Values.All(v => target.Options!.Contains(v)),
        RemoteFieldType.Number => c.Values.All(v => double.TryParse(v, System.Globalization.CultureInfo.InvariantCulture, out _)),
        _ => true
    };
    if (!valid) conditionErrors.Add($"{f.Key}→{c.Key}={string.Join("/", c.Values)}");
}
Check(conditionErrors.Count == 0, "联动条件引用的字段与取值合法", string.Join(",", conditionErrors));
Check(byKey["ShadowColor"].EnabledWhen?.Select(c => c.Key).SequenceEqual(["ShadowEnabled", "DynamicShadowColorEnabled"]) == true,
    "SwitchableGroup：卡片开关 + 自身条件同时生效（阴影颜色）");
Check(byKey["VideoFillOpacity"].EnabledWhen is null, "视频填充组条目不随标题行开关禁用（Group + Footer）");
Check(byKey["CountdownArrowColor"].VisibleWhen?.Count == 2, "箭头卡片：即将上课开关开且样式为箭头时显示");
Check(fields.Count(f => f.Type == RemoteFieldType.Color) == 16 && byKey["BackgroundColor"].Type == RemoteFieldType.Color, "颜色字段用 Color 类型（取色器 + 透明度 + 色卡）");
Check(byKey["Opacity"].Slider && byKey["Opacity"].Step == 0.05 && !byKey["ShadowBlur"].Slider, "滑块 / 步长与设置页一致");

// 3b. 数值字段的声明范围必须覆盖 setter 能存下的全部值：RemoteCI 按声明范围严格校验，
//     教室端（自动化 / 预设）存着范围外的值时，班级页整页提交会整批被拒（字段被隐藏时管理员都看不到改哪项）。
var rangeGaps = new List<string>();
foreach (var f in fields.Where(f => f.Type == RemoteFieldType.Number))
{
    var property = typeof(InjectorSettings).GetProperty(f.Key)!;
    var probe = new InjectorSettings();
    foreach (var extreme in new[] { -1e9, 1e9 })
    {
        property.SetValue(probe, property.PropertyType == typeof(int) ? (object)(int)extreme : extreme);
        var stored = Convert.ToDouble(property.GetValue(probe));
        if (stored < f.Min || stored > f.Max) rangeGaps.Add($"{f.Key}={stored}（声明 {f.Min}..{f.Max}）");
    }
}
Check(rangeGaps.Count == 0, "数值字段声明范围覆盖 setter 可取的全部值", string.Join(",", rangeGaps));


// 4. 全新安装的当前值能通过 RemoteCI 服务端/插件端同一套校验（否则 WebUI 预填后原样下发也会被拒）
var values = InjectorRemoteSettings.ReadValues();
Check(values.Keys.ToHashSet().SetEquals(fields.Select(f => f.Key)), "上报值的键 == 声明的字段");
ExtensionFieldValidator.ValidateSettings(parameters, values, out var validateError);
Check(validateError is null, "默认值通过 RemoteCI ExtensionFieldValidator", validateError);
Check(values["toggle.RippleType"] == "false" && values["RippleType"] is null, "卡片关闭：开关上报 false、类型上报空（WebUI 显示未选择）");

// 5. 原样写回（= 班级页整页提交）：成功且不产生变化
var r = InjectorRemoteSettings.Apply(values, "probe");
Check(r.Success && r.Message.Contains("无需修改"), "原样写回无变化", r.Message);

// 6. 部分更新：只改出现的键（先经 RemoteCI 校验规范化，模拟真实链路）
var s = InjectorRuntime.Settings;
var before = s.ShadowBlur;
var normalized = ExtensionFieldValidator.ValidateSettings(parameters, new Dictionary<string, string?>
{
    ["Opacity"] = "0.5", ["Shape"] = "Capsule", ["BackgroundColor"] = "#ff112233",
    ["CornerRadius"] = "10", ["CustomBackgroundEnabled"] = "TRUE", ["CountdownArrowCount"] = "7"
}, out var e4);
Check(e4 is null, "RemoteCI 预检通过", e4);
r = InjectorRemoteSettings.Apply(normalized, "probe");
Check(r.Success, "部分更新成功", r.Message);
Check(s.Opacity == 0.5 && s.Shape == IslandShape.Capsule && s.BackgroundColor == "#FF112233" && s.CustomBackgroundEnabled
      && s.CountdownArrowCount == 7, "部分更新写入正确（颜色规范为 #AARRGGBB）", s.BackgroundColor);
Check(s.ShadowBlur == before, "未出现的键保持原值");

// 7. 只改圆角且形状为「跟随 ClassIsland」→ 自动切圆角矩形（与设置页一致）
s.Shape = IslandShape.HostDefault;
r = InjectorRemoteSettings.Apply(new Dictionary<string, string?> { ["CornerRadius"] = "12" }, "probe");
Check(r.Success && s.CornerRadius == 12 && s.Shape == IslandShape.RoundedRectangle, "改圆角自动切圆角矩形", s.Shape.ToString());

// 7b. 班级设置页是整页提交（每次都原样带上形状）：只改圆角同样要切圆角矩形；同时真的改了形状则尊重选择
s.Shape = IslandShape.HostDefault;
var page = InjectorRemoteSettings.ReadValues();
page["CornerRadius"] = "8";
r = InjectorRemoteSettings.Apply(ExtensionFieldValidator.ValidateSettings(parameters, page, out var ePage), "probe");
Check(ePage is null && r.Success && s.CornerRadius == 8 && s.Shape == IslandShape.RoundedRectangle,
    "整页提交只改圆角也切圆角矩形", $"{ePage ?? r.Message} → {s.Shape}");
page = InjectorRemoteSettings.ReadValues();
page["CornerRadius"] = "6";
page["Shape"] = nameof(IslandShape.HostDefault);
r = InjectorRemoteSettings.Apply(ExtensionFieldValidator.ValidateSettings(parameters, page, out ePage), "probe");
Check(ePage is null && r.Success && s.Shape == IslandShape.HostDefault,
    "整页提交里同时把形状改回「跟随 ClassIsland」时尊重选择", $"{ePage ?? r.Message} → {s.Shape}");

// 8. 卡片总开关（虚拟）：开 → 自动选第一项；指定类型 → 用指定；关 → None（忽略残留选项）；空下拉 → 保持
s.RippleType = RippleType.None;
r = InjectorRemoteSettings.Apply(new Dictionary<string, string?> { ["toggle.RippleType"] = "true" }, "probe");
Check(r.Success && s.RippleType == SettingChoices.RippleTypes[0].Value, "打开 Ripple 开关自动选第一项", s.RippleType.ToString());
r = InjectorRemoteSettings.Apply(new Dictionary<string, string?> { ["toggle.RippleType"] = "true", ["RippleType"] = "Star" }, "probe");
Check(r.Success && s.RippleType == RippleType.Star, "开关 + 类型一起下发", s.RippleType.ToString());
r = InjectorRemoteSettings.Apply(new Dictionary<string, string?> { ["toggle.RippleType"] = "false", ["RippleType"] = "Glow" }, "probe");
Check(r.Success && s.RippleType == RippleType.None, "关闭开关写 None", s.RippleType.ToString());
r = InjectorRemoteSettings.Apply(new Dictionary<string, string?> { ["RippleType"] = "", ["BackgroundColor"] = "" }, "probe");
Check(r.Success && s.RippleType == RippleType.None && s.BackgroundColor == "#FF112233", "未选择 / 空颜色保持原值", r.Message);
r = InjectorRemoteSettings.Apply(new Dictionary<string, string?> { ["RippleType"] = "Glow" }, "probe");
Check(r.Success && s.RippleType == RippleType.Glow && InjectorRemoteSettings.ReadValues()["toggle.RippleType"] == "true",
    "只下发类型即开启（批量页只勾了类型）");
s.AnimationMode = IslandAnimationMode.None;
r = InjectorRemoteSettings.Apply(new Dictionary<string, string?> { ["AnimationEnabled"] = "true" }, "probe");
Check(r.Success && s.AnimationMode == IslandAnimationMode.Breathe, "持续动画打开且类型为 None → 呼吸", s.AnimationMode.ToString());

// 9. 非法值整体拒绝、不落半截
var opacity = s.Opacity;
r = InjectorRemoteSettings.Apply(new Dictionary<string, string?> { ["Opacity"] = "0.9", ["Shape"] = "Banana" }, "probe");
Check(!r.Success && s.Opacity == opacity, "含非法枚举时整体不写", r.Message);
r = InjectorRemoteSettings.Apply(new Dictionary<string, string?> { ["Opacity"] = "0.9", ["ShadowColor"] = "red" }, "probe");
Check(!r.Success && s.Opacity == opacity, "含非法颜色时整体不写", r.Message);
ExtensionFieldValidator.ValidateSettings(parameters, new Dictionary<string, string?> { ["ShadowColor"] = "#12345" }, out var eColor);
Check(eColor?.Contains("阴影 · 阴影颜色") == true, "RemoteCI 预检拒绝非法颜色，错误带卡片名", eColor);
r = InjectorRemoteSettings.Apply(new Dictionary<string, string?> { ["NotASetting"] = "1" }, "probe");
Check(!r.Success, "未声明的键被拒", r.Message);
r = InjectorRemoteSettings.Apply(new Dictionary<string, string?> { ["Opacity"] = "" }, "probe");
Check(!r.Success, "数值字段清空被拒", r.Message);

// 10. 越界数值交给 setter 钳制；整数字段四舍五入；整数下拉
r = InjectorRemoteSettings.Apply(new Dictionary<string, string?> { ["Opacity"] = "5", ["CountdownArrowCount"] = "3.6", ["FakeWeatherCode"] = "7" }, "probe");
Check(r.Success && s.Opacity == 1 && s.CountdownArrowCount == 4 && s.FakeWeatherCode == 7, "越界钳制 / 整数取整 / 整数下拉",
    $"{s.Opacity} {s.CountdownArrowCount} {s.FakeWeatherCode}");

r = InjectorRemoteSettings.Apply(new Dictionary<string, string?> { ["CountdownArrowCount"] = "2.5" }, "probe");
Check(r.Success && s.CountdownArrowCount == 3, "整数 .5 远离零舍入（不是银行家舍入）", s.CountdownArrowCount.ToString());

// 10b. 安全：本机专属设置（FFmpeg 下载源，下载到的 DLL 会进宿主进程）不开放——含它的提交在 RemoteCI 预检与插件侧都整批被拒
Check(!byKey.ContainsKey(nameof(InjectorSettings.CustomFfmpegDownloadUrl)), "FFmpeg 下载源不开放给远程");
var ffmpegUrl = s.CustomFfmpegDownloadUrl;
var opacityBefore = s.Opacity;
var withFfmpeg = new Dictionary<string, string?>
{
    [nameof(InjectorSettings.CustomFfmpegDownloadUrl)] = "https://evil.example/ffmpeg.zip", ["Opacity"] = "0.4"
};
ExtensionFieldValidator.ValidateSettings(parameters, withFfmpeg, out var eFfmpeg);
r = InjectorRemoteSettings.Apply(withFfmpeg, "probe");
Check(eFfmpeg != null && !r.Success && s.CustomFfmpegDownloadUrl == ffmpegUrl && s.Opacity == opacityBefore,
    "含 FFmpeg 下载源的提交整批被拒（RemoteCI 预检与插件一致）", $"{eFfmpeg} / {r.Message}");

// 本机路径判定：只认盘符路径与相对路径；混写分隔符的 UNC、NT 前缀、FFmpeg 不带 // 的协议都算网络 / 外部来源
string[] networkPaths =
[
    @"\\evil\share\x", "//evil/share/x", @"\/evil/share/x", @"/\evil\share\x", @"\\?\UNC\evil\share\x",
    @"\??\UNC\evil\share\x", @"\\.\pipe\x", "http://evil.example/x", "http:evil.example/x", "tcp:evil.example:1234", "file:///C:/x"
];
string[] localPaths = [@"C:\Styles\x.axaml", "C:/Styles/x.axaml", "x.axaml", @"..\x.axaml", @"\Styles\x.axaml", "", @"  D:\x.mp4  "];
Check(networkPaths.All(InjectorRemoteSettings.IsNetworkPath) && !localPaths.Any(InjectorRemoteSettings.IsNetworkPath),
    "本机路径判定（盘符 / 相对路径才算本机）",
    string.Join(" ", networkPaths.Where(p => !InjectorRemoteSettings.IsNetworkPath(p)).Concat(localPaths.Where(InjectorRemoteSettings.IsNetworkPath))));
s.Opacity = 0.4;
foreach (var bad in new[] { @"\\evil\share\x.axaml", @"\/evil/share/x.axaml", @"\??\UNC\evil\share\x.axaml", "http://evil.example/x.axaml" })
{
    r = InjectorRemoteSettings.Apply(new Dictionary<string, string?> { ["StyleSheetPath"] = bad, ["Opacity"] = "0.6" }, "probe");
    Check(!r.Success && s.Opacity == 0.4, $"样式表路径拒绝网络路径 / 网址：{bad}", r.Message);
}
foreach (var bad in new[] { @"\\evil\share\x.mp4", @"/\evil\share\x.mp4", "tcp:evil.example:1234" })
{
    r = InjectorRemoteSettings.Apply(new Dictionary<string, string?> { ["VideoFillPath"] = bad }, "probe");
    Check(!r.Success, $"视频文件路径拒绝网络路径 / FFmpeg 协议：{bad}", r.Message);
}
r = InjectorRemoteSettings.Apply(new Dictionary<string, string?> { ["StyleSheetPath"] = @"D:\Styles\Overrides.axaml" }, "probe");
Check(r.Success && s.StyleSheetPath == @"D:\Styles\Overrides.axaml", "本机路径照常写入", r.Message);
s.StyleSheetPath = @"\\nas\styles\local.axaml";
r = InjectorRemoteSettings.Apply(new Dictionary<string, string?> { ["StyleSheetPath"] = @"\\nas\styles\local.axaml" }, "probe");
Check(r.Success, "本机自己设的网络路径原样回传不被拒（整页提交）", r.Message);

// 10c. 下发样式包：包里的整套设置按逐字段写入的同一套规矩消毒（预设套用走 CopyFrom，不消毒就是后门）
var current = new InjectorSettings { CustomFfmpegDownloadUrl = "https://mirror.school.example/ffmpeg.zip", VideoFillPath = @"D:\Videos\bg.mp4" };
var incoming = new InjectorSettings
{
    CustomFfmpegDownloadUrl = "https://evil.example/ffmpeg.zip",
    VideoProjectPath = @"\\evil\share\project.ciproj",
    VideoFillPath = @"\/evil/share/bg.mp4",
    StyleSheetPath = @"C:\imported\school\resources\style.axaml",
    WallpaperLayers = [new WallpaperLayerItem { Path = @"\\evil\share\a.png" }, new WallpaperLayerItem { Path = @"C:\imported\school\resources\layer_1.png" }],
};
var neutralized = InjectorRemoteSettings.SanitizeRemoteSettings(incoming, current);
Check(incoming.CustomFfmpegDownloadUrl == current.CustomFfmpegDownloadUrl && incoming.VideoProjectPath == current.VideoProjectPath
      && incoming.VideoFillPath == current.VideoFillPath && incoming.StyleSheetPath == @"C:\imported\school\resources\style.axaml"
      && incoming.WallpaperLayers[0].Path == "" && incoming.WallpaperLayers[1].Path.EndsWith("layer_1.png"),
    "样式包消毒：本机专属设置保持原值、网络路径恢复原值、本机路径保留", string.Join("、", neutralized));
Check(neutralized.SequenceEqual([nameof(InjectorSettings.CustomFfmpegDownloadUrl), nameof(InjectorSettings.VideoProjectPath),
        nameof(InjectorSettings.VideoFillPath), nameof(InjectorSettings.WallpaperLayers)]),
    "消毒结果如实列出（写进回执）", string.Join("、", neutralized));

// 10d. 样式包解包上限（防 zip 炸弹）：50MB 包体只限压缩后字节，解包按实际写出字节计、条目数也有上限
var bombDir = Path.Combine(Path.GetTempPath(), "remoteprobe-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(bombDir);
try
{
    string MakePackage(string name, Action<System.IO.Compression.ZipArchive> addResources)
    {
        var zip = Path.Combine(bombDir, name);
        using var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create);
        using (var w = new StreamWriter(archive.CreateEntry(PresetExchange.PresetFileName).Open()))
        {
            w.Write(System.Text.Json.JsonSerializer.Serialize(new UserPreset { Name = "炸弹", Settings = new InjectorSettings() }));
        }

        addResources(archive);
        return zip;
    }

    var bomb = MakePackage("bomb.cizip", a =>
    {
        using var s1 = a.CreateEntry("resources/zeros.bin", System.IO.Compression.CompressionLevel.Optimal).Open();
        s1.Write(new byte[4 * 1024 * 1024]); // 4MB 全零，压缩后只有几 KB
    });
    var importRoot = Path.Combine(bombDir, "imported");
    var bombResult = PresetExchange.Import(bomb, importRoot, maxExtractedBytes: 1024 * 1024);
    Check(!bombResult.Success && (!Directory.Exists(importRoot) || !Directory.EnumerateFileSystemEntries(importRoot).Any()),
        "解包超过字节上限即中止并清掉已解出的目录", bombResult.Message);
    var ok = PresetExchange.Import(bomb, importRoot, maxExtractedBytes: 8 * 1024 * 1024);
    Check(ok.Success, "上限之内照常导入", ok.Message);
    var many = MakePackage("many.cizip", a =>
    {
        for (var i = 0; i <= PresetExchange.MaxResourceEntries; i++) a.CreateEntry($"resources/f{i}.png");
    });
    var manyResult = PresetExchange.Import(many, Path.Combine(bombDir, "imported-many"));
    Check(!manyResult.Success && !Directory.Exists(Path.Combine(bombDir, "imported-many")), "资源条目超过上限直接拒绝", manyResult.Message);

    // preset.json 在解包资源之前就整体读入内存：解压后超过上限直接拒绝（高压缩比的超大 JSON）
    var hugeJson = Path.Combine(bombDir, "huge-json.cizip");
    using (var archive = System.IO.Compression.ZipFile.Open(hugeJson, System.IO.Compression.ZipArchiveMode.Create))
    using (var w = new StreamWriter(archive.CreateEntry(PresetExchange.PresetFileName, System.IO.Compression.CompressionLevel.Optimal).Open()))
    {
        w.Write(new string(' ', PresetExchange.MaxJsonBytes + 1));
        w.Write(System.Text.Json.JsonSerializer.Serialize(new UserPreset { Name = "大 JSON", Settings = new InjectorSettings() }));
    }
    var hugeResult = PresetExchange.Import(hugeJson, Path.Combine(bombDir, "imported-json"));
    Check(!hugeResult.Success && hugeResult.Message.Contains("preset.json"), "preset.json 解压后超过上限直接拒绝", hugeResult.Message);

    // 装进预设列表后按「这次导入」释放「导入中」登记——包里有没被引用的资源也一样（之后由清理按无引用删除）
    var unreferenced = MakePackage("unreferenced.cizip", a =>
    {
        using var s2 = a.CreateEntry("resources/unused.png").Open();
        s2.Write(new byte[16]);
    });
    var unrefRoot = Path.Combine(bombDir, "imported-unref");
    var unrefResult = PresetExchange.Import(unreferenced, unrefRoot);
    var unrefDir = Directory.EnumerateDirectories(unrefRoot).Single();
    var pendingBefore = PresetExchange.IsImportPending(unrefDir);
    PresetExchange.CompleteImport(unrefResult.Preset!);
    Check(unrefResult.Success && pendingBefore && !PresetExchange.IsImportPending(unrefDir),
        "装进列表后释放导入中登记（资源未被引用也释放）", $"{unrefResult.Message} pending: {pendingBefore} → {PresetExchange.IsImportPending(unrefDir)}");
}
finally
{
    Directory.Delete(bombDir, true);
}

// 11. 锁定开关：独立于 InjectorSettings，套用预设（CopyFrom）不会解锁
r = InjectorRemoteSettings.Apply(new Dictionary<string, string?> { [InjectorRemoteSettings.LockKey] = "true" }, "王老师");
Check(r.Success && RemoteManagementState.LockLocalEditing, "远程锁定");
s.CopyFrom(new InjectorSettings());
Check(RemoteManagementState.LockLocalEditing, "恢复默认后仍保持锁定");
Check(InjectorRemoteSettings.ReadValues()[InjectorRemoteSettings.LockKey] == "true", "锁定状态如实上报");
Check(RemoteManagementState.LastRemoteBy == "王老师", "记录远程修改人");

// 12. 所有字段写入任意合法值都不抛异常（反射 + setter）
var sample = new Dictionary<string, string?>();
foreach (var p in parameters)
{
    sample[p.Key] = p.Type switch
    {
        ExtensionParameterType.Switch => "true",
        ExtensionParameterType.Number => (p.Min ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture),
        ExtensionParameterType.Select => p.Options![^1],
        ExtensionParameterType.Color => "#80FFFFFF",
        _ => "probe"
    };
}
ExtensionFieldValidator.ValidateSettings(parameters, sample, out var e9);
r = InjectorRemoteSettings.Apply(sample, "probe");
Check(e9 is null && r.Success, $"全部 {sample.Count} 个字段一次写入", e9 ?? r.Message);
ExtensionFieldValidator.ValidateSettings(parameters, InjectorRemoteSettings.ReadValues(), out var e10);
Check(e10 is null, "写入后的当前值仍能通过校验", e10);
var ensured = InjectorRuntime.FileAssociationEnsured;
r = InjectorRemoteSettings.Apply(new Dictionary<string, string?> { ["PresetFileAssociationEnabled"] = "false" }, "probe");
Check(r.Success && InjectorRuntime.FileAssociationEnsured == ensured + 1, "远程改「双击安装预设包」后立即同步文件关联");
r = InjectorRemoteSettings.Apply(new Dictionary<string, string?> { ["PresetFileAssociationEnabled"] = "false" }, "probe");
Check(InjectorRuntime.FileAssociationEnsured == ensured + 1, "未变化时不重复注册文件关联");

Console.WriteLine(failures == 0 ? "\n全部通过" : $"\n{failures} 项失败");
return failures == 0 ? 0 : 1;

namespace ClassIslandInjector
{
    /// <summary>InjectorRuntime 桩：探针只需要设置实例与几个通知点。</summary>
    internal static class InjectorRuntime
    {
        public static InjectorSettings Settings { get; } = new();
        public static string ConfigDirectory => string.Empty;
        public static int FileAssociationEnsured { get; private set; }
        public static void NotifySettingsChangedRemotely() { }
        public static void EnsureFileAssociation() => FileAssociationEnsured++;
    }
}
