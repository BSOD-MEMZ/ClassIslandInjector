using System.Runtime.CompilerServices;
using Avalonia.Threading;
using ClassIsland.Shared;
using RemoteCI.Plugin.Extensions;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace ClassIslandInjector.RemoteControl;

/// <summary>
/// 与 RemoteCI 插件的实际对接：注册「样式注入器」扩展分组（WebUI 插件设置页）与三个远程功能。
/// <para>
/// ⚠️ 本文件是插件里<b>唯一</b>引用 RemoteCI 程序集的地方。未安装 RemoteCI 时，JIT 编译这里的任何方法
/// 都会抛 <see cref="FileNotFoundException"/>，所以只能经 <see cref="RemoteCiBridge"/> 的
/// NoInlining + try/catch 入口调用，其它代码不要直接引用这里的类型。
/// </para>
/// <para>
/// RemoteCI.Plugin.dll / RemoteCI.Shared.dll 只做编译期引用（不随包分发），运行时由 <see cref="RemoteCiBridge"/>
/// 在本插件加载上下文的 Resolving 里重定向到 RemoteCI 自己加载的那一份，类型才能对上（不声明 manifest 依赖，见该类说明）。
/// </para>
/// </summary>
internal static class RemoteCiIntegration
{
    public const string GroupId = "classisland.injector";
    private const string ApplyPresetId = "classisland.injector.apply_preset";
    private const string InstallPackageId = "classisland.injector.install_package";
    private const string ResetId = "classisland.injector.reset";

    private static IRemoteCiExtensionRegistry? _registry;
    private static Timer? _notifyTimer;

    /// <summary>
    /// 保护 <see cref="_notifyTimer"/> 的创建 / 改期 / 释放：<see cref="RemoteManagementState.Changed"/> 可能在任意线程触发，
    /// 与 UI 线程上的注销并发时，不加锁会读到被置 null 的字段或改期一个已释放的计时器。
    /// </summary>
    private static readonly object NotifyLock = new();

    /// <summary>注册分组与扩展；RemoteCI 注册表不存在（旧版 / 未加载）时返回 false。</summary>
    public static bool Register()
    {
        var registry = IAppHost.TryGetService<IRemoteCiExtensionRegistry>();
        if (registry == null)
        {
            return false;
        }

        // 先清掉上一次可能没注销干净的残留（断开 / 重连之间注销中途失败），否则重复 Id 会让本次注册失败。
        // 正常情况下这些 Id 本来就不存在，失败不记日志。
        UnregisterAll(registry, logFailures: false);
        try
        {
            // 先注册分组再注册扩展：扩展的 GroupId 要能对上已存在的分组。
            registry.RegisterGroup(new InjectorExtensionGroup());
            registry.Register(new ApplyPresetExtension());
            registry.Register(new InstallPackageExtension());
            registry.Register(new ResetStyleExtension());
        }
        catch
        {
            // 半途失败要回滚：否则分组仍挂在 WebUI 上、能远程写设置和锁定，而本机认为未接入（锁定不生效、退出时也不注销）。
            // 还没来得及注册的项注销失败是预期内的，不记日志（真正的失败原因由调用方记录）。
            UnregisterAll(registry, logFailures: false);
            throw;
        }

        _registry = registry;
        RemoteCiBridge.Log(LayoutSupported
            ? "RemoteCI 支持设置页布局：WebUI 按本机设置页的分区 / 卡片 / 联动显示"
            : "RemoteCI 版本较旧（不支持分区 / 卡片 / 颜色），WebUI 退回平铺表单；请把教室端 RemoteCI 插件升级到 3.3.1.4+");

        InjectorRuntime.SettingsSaved += OnLocalChanged;
        RemoteManagementState.Changed += OnLocalChanged;
        return true;
    }

    public static void Unregister()
    {
        InjectorRuntime.SettingsSaved -= OnLocalChanged;
        RemoteManagementState.Changed -= OnLocalChanged;
        lock (NotifyLock)
        {
            _notifyTimer?.Dispose();
            _notifyTimer = null;
        }

        var registry = _registry;
        _registry = null;
        if (registry != null)
        {
            UnregisterAll(registry);
        }
    }

    /// <summary>逐项注销（每项单独兜底）：某一项失败不能让后面的项留在 RemoteCI 里。</summary>
    private static void UnregisterAll(IRemoteCiExtensionRegistry registry, bool logFailures = true)
    {
        foreach (var unregister in new Action[]
                 {
                     () => registry.Unregister(ApplyPresetId),
                     () => registry.Unregister(InstallPackageId),
                     () => registry.Unregister(ResetId),
                     () => registry.UnregisterGroup(GroupId)
                 })
        {
            try
            {
                unregister();
            }
            catch (Exception ex)
            {
                if (logFailures)
                {
                    RemoteCiBridge.Log($"注销 RemoteCI 扩展项失败：{ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// 本机设置变化后通知 RemoteCI 重新上报当前值（WebUI 预填 / 各班级当前值对比）。
    /// 拖动滑块时设置每帧都在变，防抖 1 秒，只同步最终值。
    /// </summary>
    private static void OnLocalChanged(object? sender, EventArgs e)
    {
        lock (NotifyLock)
        {
            if (_registry == null)
            {
                return;
            }

            _notifyTimer ??= new Timer(_ =>
            {
                try
                {
                    _registry?.NotifySettingsChanged(GroupId);
                }
                catch (Exception ex)
                {
                    RemoteCiBridge.Log($"同步设置值失败：{ex.Message}");
                }
            });
            _notifyTimer.Change(TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
        }
    }

    private static CommandResult Ok(string message) => new()
    {
        Success = true,
        Code = CommandResultCodes.Ok,
        Message = message
    };

    private static string Who(ExtensionExecutionContext context) =>
        string.IsNullOrWhiteSpace(context.RequestedBy.DisplayName) ? context.RequestedBy.Username : context.RequestedBy.DisplayName;

    /// <summary>
    /// 运行中的 RemoteCI 是否支持设置页布局（分区 / 卡片 / 联动 / 颜色类型，RemoteCI 3.3.1.4+）。
    /// 编译期引用的是新版 RemoteCI.Shared，但教室端可能还装着旧版：旧版没有这些成员，直接赋值会在 JIT 时抛
    /// <see cref="MissingMethodException"/>，所以先反射探测，不支持就退回平铺字段（标签带卡片名、颜色按文本）。
    /// </summary>
    private static readonly bool LayoutSupported =
        typeof(ExtensionParameter).GetProperty("Section") != null &&
        typeof(ExtensionParameter).GetProperty("EnabledWhen") != null &&
        Enum.IsDefined(typeof(ExtensionParameterType), (int)RemoteFieldType.Color);

    private static ExtensionParameter ToParameter(RemoteField field)
    {
        var parameter = new ExtensionParameter
        {
            Key = field.Key,
            Label = LayoutSupported ? field.Label : field.QualifiedLabel,
            Type = (ExtensionParameterType)(int)(field.Type == RemoteFieldType.Color && !LayoutSupported ? RemoteFieldType.Text : field.Type),
            Description = field.Description,
            Min = field.Min,
            Max = field.Max,
            Options = field.Options?.ToList(),
            OptionLabels = field.OptionLabels?.ToList(),
            Placeholder = field.Placeholder
        };
        if (LayoutSupported)
        {
            ApplyLayout(parameter, field);
        }

        return parameter;
    }

    /// <summary>只有确认 RemoteCI 支持布局时才调用（单独成方法，旧版下不会被 JIT）。</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ApplyLayout(ExtensionParameter parameter, RemoteField field)
    {
        parameter.Section = field.Section;
        parameter.Card = field.Card;
        parameter.CardDescription = field.CardDescription;
        parameter.CardToggle = field.CardToggle;
        parameter.Step = field.Step;
        parameter.Slider = field.Slider;
        parameter.VisibleWhen = ToConditions(field.VisibleWhen);
        parameter.EnabledWhen = ToConditions(field.EnabledWhen);
    }

    private static List<ExtensionFieldCondition>? ToConditions(IReadOnlyList<RemoteCondition>? conditions) =>
        conditions is { Count: > 0 }
            ? conditions.Select(c => new ExtensionFieldCondition { Key = c.Key, Values = c.Values.ToList(), Not = c.Not }).ToList()
            : null;

    /// <summary>「样式注入器」分组：WebUI 侧边栏「扩展插件」里的设置页（全部可远程修改的样式设置）。</summary>
    private sealed class InjectorExtensionGroup : RemoteCiExtensionGroupBase
    {
        private readonly IReadOnlyList<ExtensionParameter> _settings =
            InjectorRemoteSettings.Fields.Select(ToParameter).ToList();

        public override string Id => GroupId;

        public override string DisplayName => "样式注入器";

        public override string? Description =>
            "统一管理各班 ClassIsland 主界面的样式：基础变形、背景/阴影/边框、文字美化、动画与提醒效果、即将上课特效等。" +
            "开启「锁定本机样式设置」可禁止教室端本地修改。";

        public override string? Icon => "tune";

        public override IReadOnlyList<ExtensionParameter> Settings => _settings;

        public override IReadOnlyDictionary<string, string?> GetSettings() => InjectorRemoteSettings.ReadValues();

        public override async Task<CommandResult> ApplySettingsAsync(
            ExtensionExecutionContext context,
            IReadOnlyDictionary<string, string?> values,
            CancellationToken cancellationToken)
        {
            var who = Who(context);
            var result = await Dispatcher.UIThread.InvokeAsync(() => InjectorRemoteSettings.Apply(values, who));
            RemoteCiBridge.Log($"{who} 远程修改设置（{values.Count} 项：{string.Join(", ", values.Keys)}）→ {result.Message}");
            return result.Success ? Ok(result.Message) : CommandResult.Failure(CommandResultCodes.InvalidRequest, result.Message);
        }
    }

    /// <summary>套用教室端已有的命名预设（含内置「无预设」）。</summary>
    private sealed class ApplyPresetExtension : RemoteCiExtensionBase
    {
        public override string Id => ApplyPresetId;

        public override string DisplayName => "套用样式预设";

        public override UserPermissions RequiredPermission => UserPermissions.RunExtensions;

        public override string? Icon => "tune";

        public override string? GroupId => RemoteCiIntegration.GroupId;

        public override string? Description => "把教室端已保存的用户预设套用为当前样式（预设可先用「下发样式包」统一安装）。";

        public override IReadOnlyList<ExtensionParameter> Parameters =>
        [
            new ExtensionParameter
            {
                Key = "name",
                Label = "预设名称",
                Type = ExtensionParameterType.Text,
                Required = true,
                Placeholder = "与教室端「用户预设」中的名称一致",
                Description = "不区分大小写；填「无预设」等同于恢复默认样式。"
            }
        ];

        public override async Task<CommandResult> ExecuteAsync(
            ExtensionExecutionContext context,
            IReadOnlyDictionary<string, string?> args,
            CancellationToken cancellationToken)
        {
            var name = args.GetValueOrDefault("name")?.Trim() ?? string.Empty;
            var applied = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!InjectorRuntime.ApplyPreset(name))
                {
                    return false;
                }

                RemoteManagementState.RecordRemoteChange(Who(context));
                InjectorRuntime.NotifySettingsChangedRemotely();
                return true;
            });
            RemoteCiBridge.Log($"{Who(context)} 远程套用预设「{name}」→ {(applied ? "成功" : "预设不存在")}");
            if (applied)
            {
                return Ok($"已套用预设「{name}」");
            }

            var names = InjectorRuntime.GetPresetNames();
            return CommandResult.Failure(CommandResultCodes.InvalidRequest,
                $"本机没有名为「{name}」的预设。现有预设：{string.Join("、", names)}");
        }
    }

    /// <summary>
    /// 下发样式包：教室端从 URL 下载 .cizip（与预设商店 / 双击安装同一格式）→ 导入为用户预设 → 可选立即套用。
    /// 学校只需把导出的样式包放到任意可访问的 http(s) 地址，即可对全部班级批量执行。
    /// </summary>
    private sealed class InstallPackageExtension : RemoteCiExtensionBase
    {
        /// <summary>RemoteCI 等扩展回执的上限是 15 秒，留出余量。</summary>
        private static readonly TimeSpan ReplyWindow = TimeSpan.FromSeconds(12);

        public override string Id => InstallPackageId;

        public override string DisplayName => "下发样式包";

        public override UserPermissions RequiredPermission => UserPermissions.RunExtensions;

        public override string? Icon => "download";

        public override string? GroupId => RemoteCiIntegration.GroupId;

        public override string? Description =>
            "从 http(s) 地址下载样式注入器预设包（.cizip，在设置页「用户预设 → 导出」生成）并安装到教室端；同名预设会被更新。";

        public override IReadOnlyList<ExtensionParameter> Parameters =>
        [
            new ExtensionParameter
            {
                Key = "url",
                Label = "样式包地址",
                Type = ExtensionParameterType.Text,
                Required = true,
                Placeholder = "https://example.edu.cn/styles/school.cizip"
            },
            new ExtensionParameter
            {
                Key = "name",
                Label = "安装为预设名",
                Type = ExtensionParameterType.Text,
                Placeholder = "留空使用样式包内的预设名",
                Description = "教室端已有同名预设时直接覆盖，便于反复下发同一套学校样式。"
            },
            new ExtensionParameter
            {
                Key = "apply",
                Label = "安装后立即套用",
                Type = ExtensionParameterType.Switch,
                DefaultValue = "true"
            }
        ];

        public override async Task<CommandResult> ExecuteAsync(
            ExtensionExecutionContext context,
            IReadOnlyDictionary<string, string?> args,
            CancellationToken cancellationToken)
        {
            var url = args.GetValueOrDefault("url")?.Trim() ?? string.Empty;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            {
                return CommandResult.Failure(CommandResultCodes.InvalidRequest, "样式包地址必须是 http:// 或 https:// 开头的完整网址");
            }

            var nameOverride = args.GetValueOrDefault("name")?.Trim();
            var apply = !string.Equals(args.GetValueOrDefault("apply"), "false", StringComparison.OrdinalIgnoreCase);
            var who = Who(context);

            // 下载用自己的超时，不跟随 RemoteCI 的 15 秒令牌：回执超时只代表「等不及了」，安装应继续完成。
            var work = RemotePresetPackage.InstallAsync(uri, nameOverride, apply, who);
            var finished = await Task.WhenAny(work, Task.Delay(ReplyWindow, CancellationToken.None));
            if (finished != work)
            {
                _ = work.ContinueWith(t => RemoteCiBridge.Log(
                    $"{who} 下发样式包（后台完成）→ {(t.IsCompletedSuccessfully ? t.Result.Message : t.Exception?.GetBaseException().Message)}"),
                    TaskScheduler.Default);
                return Ok("样式包仍在后台下载（或在排队等上一次下发完成）；完成后会自动安装" + (apply ? "并套用" : string.Empty) + "。可稍后在插件设置页确认。");
            }

            var result = await work;
            RemoteCiBridge.Log($"{who} 下发样式包 {uri} → {result.Message}");
            return result.Success ? Ok(result.Message) : CommandResult.Failure(CommandResultCodes.InvalidRequest, result.Message);
        }
    }

    /// <summary>恢复默认样式（等同套用内置「无预设」：主界面回到 ClassIsland 原生外观）。</summary>
    private sealed class ResetStyleExtension : RemoteCiExtensionBase
    {
        public override string Id => ResetId;

        public override string DisplayName => "恢复默认样式";

        public override UserPermissions RequiredPermission => UserPermissions.RunExtensions;

        public override string? Icon => "restart_alt";

        public override string? GroupId => RemoteCiIntegration.GroupId;

        public override string? Description => "把样式注入器全部设置恢复为中性默认，主界面回到 ClassIsland 原生外观（不删除预设，不改变锁定状态）。";

        public override async Task<CommandResult> ExecuteAsync(
            ExtensionExecutionContext context,
            IReadOnlyDictionary<string, string?> args,
            CancellationToken cancellationToken)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                InjectorRuntime.ApplyPreset(InjectorPresetStore.NoPresetName);
                RemoteManagementState.RecordRemoteChange(Who(context));
                InjectorRuntime.NotifySettingsChangedRemotely();
            });
            RemoteCiBridge.Log($"{Who(context)} 远程恢复默认样式");
            return Ok("已恢复默认样式");
        }
    }
}
