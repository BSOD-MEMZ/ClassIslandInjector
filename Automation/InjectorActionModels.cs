namespace ClassIslandInjector.Automation;

/// <summary>
/// 「修改设置」行动：通过 <see cref="PropertyName"/> 定位插件的一个设置项，
/// 并把 <see cref="Value"/> 写入该设置。所有设置项的「添加行动」菜单项都复用
/// 这一个行动类型，仅通过预设的 <see cref="PropertyName"/> 区分。
/// </summary>
public sealed class SetInjectorSettingActionSettings
{
    /// <summary>要修改的 <see cref="InjectorSettings"/> 属性名（C# 属性名）。</summary>
    public string PropertyName { get; set; } = string.Empty;

    /// <summary>要写入的值。JSON 加载后可能是 <see cref="System.Text.Json.JsonElement"/>。</summary>
    public object? Value { get; set; }
}

/// <summary>
/// 「切换用户预设」行动：套用一个用户保存的完整设置预设。
/// </summary>
public sealed class SwitchPresetActionSettings
{
    /// <summary>要套用的用户预设名称。</summary>
    public string PresetName { get; set; } = string.Empty;
}


