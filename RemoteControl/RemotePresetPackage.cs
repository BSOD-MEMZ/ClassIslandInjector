using Avalonia.Threading;

namespace ClassIslandInjector.RemoteControl;

/// <summary>
/// 远程「下发样式包」：从 http(s) 下载 .cizip → <see cref="PresetExchange.Import"/> 解包 →
/// 按远程写入的规矩消毒（<see cref="InjectorRemoteSettings.SanitizeRemoteSettings"/>）→
/// 以覆盖同名的方式安装为用户预设 → 可选立即套用。不引用 RemoteCI 类型。
/// </summary>
internal static class RemotePresetPackage
{
    /// <summary>样式包体积上限：预设包里只有设置 + 图片/样式表资源，50MB 足够，防止误填视频地址拖垮教室电脑。</summary>
    private const long MaxPackageBytes = 50L * 1024 * 1024;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    /// <summary>
    /// 安装排队：回执窗口一过，RemoteCI 就放开同一功能的单飞，后一次下发可能与仍在下载的上一次并发。
    /// 不排队的话谁后下完谁覆盖——学校改正后重发的样式会被先发、却后下完的旧包盖掉；排队后按下发顺序依次安装。
    /// </summary>
    private static readonly SemaphoreSlim InstallGate = new(1, 1);

    public static async Task<RemoteApplyResult> InstallAsync(Uri url, string? nameOverride, bool apply, string? requestedBy)
    {
        await InstallGate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await InstallCoreAsync(url, nameOverride, apply, requestedBy).ConfigureAwait(false);
        }
        finally
        {
            InstallGate.Release();
        }
    }

    private static async Task<RemoteApplyResult> InstallCoreAsync(Uri url, string? nameOverride, bool apply, string? requestedBy)
    {
        var downloadDir = Path.Combine(InjectorRuntime.ConfigDirectory, "remote", "downloads");
        var file = Path.Combine(downloadDir, $"{Guid.NewGuid():N}.cizip");
        try
        {
            Directory.CreateDirectory(downloadDir);
            using (var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                {
                    return new RemoteApplyResult(false, $"下载失败：服务器返回 {(int)response.StatusCode}");
                }

                if (response.Content.Headers.ContentLength > MaxPackageBytes)
                {
                    return new RemoteApplyResult(false, "样式包超过 50MB，已拒绝下载");
                }

                await using var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                await using var output = File.Create(file);
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > MaxPackageBytes)
                    {
                        return new RemoteApplyResult(false, "样式包超过 50MB，已中止下载");
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                }
            }

            var imported = PresetExchange.Import(file, Path.Combine(InjectorRuntime.ConfigDirectory, "imported"));
            if (!imported.Success || imported.Preset == null)
            {
                return new RemoteApplyResult(false, imported.Message);
            }

            var preset = imported.Preset;
            if (!string.IsNullOrWhiteSpace(nameOverride))
            {
                preset.Name = nameOverride.Trim();
            }

            // 预设列表与设置都由 UI 线程上的设置页 / 注入器读写，安装与套用一并切回 UI 线程。
            return await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // 包里的预设套用时会经 CopyFrom 整体覆盖设置：先按逐字段远程写入的同一套规矩消毒
                // （FFmpeg 下载源等本机专属设置保持原值、本机路径不接受网络路径），装进列表的也是消毒后的版本。
                var neutralized = InjectorRemoteSettings.SanitizeRemoteSettings(preset.Settings, InjectorRuntime.Settings);
                var name = InjectorRuntime.ImportOrReplaceUserPreset(preset);
                if (apply)
                {
                    InjectorRuntime.ApplyPreset(name);
                    // 套用后旧版资源才真正失去引用，再清理一次。
                    InjectorRuntime.CleanupUnreferencedImportedResources();
                }

                RemoteManagementState.RecordRemoteChange(requestedBy);
                InjectorRuntime.NotifySettingsChangedRemotely();
                var message = apply ? $"已安装并套用预设「{name}」" : $"已安装预设「{name}」（未套用）";
                if (neutralized.Count > 0)
                {
                    message += $"（已忽略样式包中不接受远程修改的设置：{string.Join("、", neutralized)}）";
                }

                return new RemoteApplyResult(true, message);
            });
        }
        catch (Exception ex)
        {
            return new RemoteApplyResult(false, $"下发样式包失败：{ex.Message}");
        }
        finally
        {
            try
            {
                File.Delete(file);
            }
            catch
            {
                // 下载缓存删不掉不影响结果。
            }
        }
    }
}
