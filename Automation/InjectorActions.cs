using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using ClassIsland.Core.Abstractions.Automation;
using ClassIsland.Core.Attributes;

namespace ClassIslandInjector.Automation;

/// <summary>
/// 通过反射读写 <see cref="InjectorRuntime.Settings"/> 的设置项，并负责类型转换。
/// 自动化行动（后台线程）与设置控件共用此工具。
/// </summary>
internal static class InjectorSettingReflection
{
    /// <summary>
    /// 读取当前设置值。
    /// </summary>
    public static object? GetValue(string propertyName)
    {
        var property = typeof(InjectorSettings).GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        return property?.GetValue(InjectorRuntime.Settings);
    }

    /// <summary>
    /// 写入设置值（自动转换为属性类型）。设置变更会经 <see cref="InjectorSettings.Changed"/>
    /// 触发保存与应用。
    /// </summary>
    public static void SetValue(string propertyName, object? value)
    {
        var property = typeof(InjectorSettings).GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        if (property == null)
        {
            throw new KeyNotFoundException($"插件中找不到设置项“{propertyName}”。");
        }

        property.SetValue(InjectorRuntime.Settings, ConvertTo(property.PropertyType, value));
    }

    /// <summary>
    /// 把自动化行动设置中的值（可能是 <see cref="JsonElement"/> 或原始类型）转换为
    /// 目标属性类型。
    /// </summary>
    public static object? ConvertTo(Type targetType, object? value)
    {
        if (value == null)
        {
            return null;
        }

        if (targetType.IsInstanceOfType(value))
        {
            return value;
        }

        if (value is JsonElement json)
        {
            if (targetType == typeof(string))
            {
                return json.ValueKind == JsonValueKind.String ? json.GetString() : json.ToString();
            }

            if (targetType.IsEnum)
            {
                return json.ValueKind == JsonValueKind.String
                    ? Enum.Parse(targetType, json.GetString() ?? string.Empty)
                    : Enum.ToObject(targetType, json.GetInt32());
            }

            if (targetType == typeof(bool))
            {
                return json.GetBoolean();
            }

            if (targetType == typeof(int))
            {
                return json.GetInt32();
            }

            if (targetType == typeof(double))
            {
                return json.GetDouble();
            }

            return json.ToString();
        }

        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (underlying.IsEnum)
        {
            return value is string text ? Enum.Parse(underlying, text) : Enum.ToObject(underlying, Convert.ToInt32(value));
        }

        if (underlying == typeof(bool))
        {
            return Convert.ToBoolean(value);
        }

        if (underlying == typeof(int))
        {
            return Convert.ToInt32(value);
        }

        if (underlying == typeof(double))
        {
            return Convert.ToDouble(value);
        }

        if (underlying == typeof(string))
        {
            return value.ToString();
        }

        return value;
    }
}

/// <summary>
/// 自动化行动的状态存储：按「行动组 Guid + 行动 Id」保存行动触发前的设置快照，
/// 供「恢复」时还原。行动实例是瞬态的（每次触发新建），因此不能把旧值放在实例字段里。
/// </summary>
internal static class InjectorActionStateStore
{
    private static readonly ConcurrentDictionary<string, object?> Values = new();

    /// <summary>
    /// 保存某个行动触发前的完整设置快照。
    /// </summary>
    public static void SaveSnapshot(Guid actionSetGuid, string actionId)
    {
        Values[$"{actionSetGuid}|{actionId}"] = InjectorRuntime.Settings.Clone();
    }

    /// <summary>
    /// 取出并清除该行动的设置快照（恢复后即失效）。
    /// </summary>
    public static bool TryTakeSnapshot(Guid actionSetGuid, string actionId, out InjectorSettings? snapshot)
    {
        var key = $"{actionSetGuid}|{actionId}";
        if (Values.TryRemove(key, out var value) && value is InjectorSettings settings)
        {
            snapshot = settings;
            return true;
        }

        snapshot = null;
        return false;
    }
}

/// <summary>
/// 「修改设置」行动：把 <see cref="SetInjectorSettingActionSettings.Value"/> 写入
/// 插件设置项，并支持恢复为触发前的值。
/// </summary>
[ActionInfo(SetInjectorSettingAction.Id, "修改设置", "\uE161", addDefaultToMenu: false)]
public sealed class SetInjectorSettingAction : ActionBase<SetInjectorSettingActionSettings>
{
    public const string Id = "classisland.injector.setSetting";

    protected override async Task OnInvoke()
    {
        await base.OnInvoke();
        if (string.IsNullOrEmpty(Settings.PropertyName))
        {
            return;
        }

        var spec = InjectorSettingCatalog.Find(Settings.PropertyName);
        if (spec == null)
        {
            throw new KeyNotFoundException($"插件中找不到设置项“{Settings.PropertyName}”。");
        }

        // 值为空时保持当前值不变（视为无操作），避免新增行动未编辑就运行导致报错。
        if (Settings.Value == null)
        {
            return;
        }

        // 记录触发前快照，供恢复。
        InjectorActionStateStore.SaveSnapshot(ActionSet.Guid, Id);
        InjectorSettingReflection.SetValue(spec.PropertyName, Settings.Value);
    }

    protected override async Task OnRevert()
    {
        await base.OnRevert();
        if (string.IsNullOrEmpty(Settings.PropertyName))
        {
            return;
        }

        if (InjectorActionStateStore.TryTakeSnapshot(ActionSet.Guid, Id, out var snapshot) && snapshot != null)
        {
            var settings = InjectorRuntime.Settings;
            settings.BeginUpdate();
            settings.CopyFrom(snapshot);
            settings.EndUpdate();
            InjectorRuntime.SaveAndApply();
        }
    }
}

/// <summary>
/// 「切换用户预设」行动：套用用户保存的完整设置预设，并支持恢复为触发前状态。
/// </summary>
[ActionInfo(SwitchPresetAction.Id, "切换用户预设", "\uF42F", addDefaultToMenu: false)]
public sealed class SwitchPresetAction : ActionBase<SwitchPresetActionSettings>
{
    public const string Id = "classisland.injector.switchPreset";

    protected override async Task OnInvoke()
    {
        await base.OnInvoke();
        if (string.IsNullOrEmpty(Settings.PresetName))
        {
            return;
        }

        InjectorActionStateStore.SaveSnapshot(ActionSet.Guid, Id);
        if (!InjectorRuntime.ApplyPreset(Settings.PresetName))
        {
            throw new KeyNotFoundException($"找不到用户预设“{Settings.PresetName}”。");
        }
    }

    protected override async Task OnRevert()
    {
        await base.OnRevert();
        RestoreSnapshot(ActionSet.Guid, Id);
    }

    internal static void RestoreSnapshot(Guid actionSetGuid, string actionId)
    {
        if (InjectorActionStateStore.TryTakeSnapshot(actionSetGuid, actionId, out var snapshot) && snapshot != null)
        {
            var settings = InjectorRuntime.Settings;
            settings.BeginUpdate();
            settings.CopyFrom(snapshot);
            settings.EndUpdate();
            InjectorRuntime.SaveAndApply();
        }
    }
}


