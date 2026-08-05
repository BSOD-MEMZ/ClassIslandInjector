using System.Text.Json;

namespace ClassIslandInjector;

/// <summary>
/// 用户预设：将插件全部设置项（<see cref="InjectorSettings"/> 的完整快照）保存为一个可命名、
/// 可随时套用的方案。与内置的样式/动画预设不同，用户预设包含插件所有设置项。
/// </summary>
public sealed class UserPreset
{
    /// <summary>
    /// 预设名称。
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 预设保存时的完整设置快照。
    /// </summary>
    public InjectorSettings Settings { get; set; } = new();
}

/// <summary>
/// 用户预设的 JSON 持久化（presets.json）。
/// </summary>
internal static class InjectorPresetStore
{
    private const string PresetsFileName = "presets.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static List<UserPreset> Load(string configDirectory)
    {
        var path = Path.Combine(configDirectory, PresetsFileName);
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<List<UserPreset>>(File.ReadAllText(path), JsonOptions);
                if (loaded != null)
                {
                    return loaded;
                }
            }
        }
        catch (JsonException)
        {
            // 预设文件损坏时忽略，从空列表重新开始。
        }

        return [];
    }

    public static void Save(string configDirectory, IEnumerable<UserPreset> presets)
    {
        Directory.CreateDirectory(configDirectory);
        File.WriteAllText(
            Path.Combine(configDirectory, PresetsFileName),
            JsonSerializer.Serialize(presets, JsonOptions));
    }
}
