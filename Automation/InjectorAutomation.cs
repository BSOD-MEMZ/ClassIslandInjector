using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Extensions.Registry;
using ClassIsland.Core.Models.Automation;
using Microsoft.Extensions.DependencyInjection;

namespace ClassIslandInjector.Automation;

/// <summary>
/// 把插件的「切换预设」「修改设置」等能力注册为 ClassIsland 自动化行动，
/// 并在「添加行动」菜单中以「样式注入器 → 分组」的层叠结构呈现，
/// 避免所有设置项堆在一级菜单里。
/// </summary>
public static class InjectorAutomation
{
    /// <summary>
    /// 在插件初始化时注册全部自动化行动，并构建分组菜单。
    /// </summary>
    public static void Register(IServiceCollection services)
    {
        // 所有设置项共用同一个「修改设置」行动，通过菜单项预设 PropertyName 区分。
        services.AddAction<SetInjectorSettingAction, SetInjectorSettingActionSettingsControl>();
        services.AddAction<SwitchPresetAction, SwitchPresetActionSettingsControl>();

        BuildMenuTree();
    }

    private static void BuildMenuTree()
    {
        // 根分组：样式注入器
        var root = GetOrCreateGroup(IActionService.ActionMenuTree, InjectorSettingCatalog.RootGroupName, InjectorSettingCatalog.RootGroupIcon);

        // 预设子组：切换用户预设
        var presetGroup = new ActionMenuTreeGroup("预设", "\uF42F",
            new ActionMenuTreeItem<SwitchPresetActionSettings>(
                SwitchPresetAction.Id, "切换用户预设", "\uF42F", s => s.PresetName = string.Empty));
        root.Add(presetGroup);

        // 设置项分组：按目录中的分类生成层叠子菜单
        foreach (var group in InjectorSettingCatalog.Groups)
        {
            var menuGroup = new ActionMenuTreeGroup(group.Name, group.IconGlyph);
            foreach (var spec in group.Settings)
            {
                menuGroup.Children.Add(new ActionMenuTreeItem<SetInjectorSettingActionSettings>(
                    SetInjectorSettingAction.Id, spec.DisplayName, spec.IconGlyph,
                    s => s.PropertyName = spec.PropertyName));
            }

            root.Add(menuGroup);
        }
    }

    /// <summary>
    /// 获取（不存在则创建）指定名称的菜单组，返回其子节点集合。
    /// </summary>
    private static ActionMenuTreeNodeCollection GetOrCreateGroup(ActionMenuTreeNodeCollection parent, string name, string icon)
    {
        if (parent.Contains(name))
        {
            try
            {
                return parent[name];
            }
            catch (ArgumentException)
            {
                // 同名节点不是菜单组，忽略并新建。
            }
        }

        var created = new ActionMenuTreeGroup(name, icon);
        parent.Add(created);
        return created.Children;
    }
}
