using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;

namespace ClassIslandInjector.Views;

/// <summary>
/// 预设安装确认对话框：展示预设名与元数据（作者 / 学校 / 打包时间 / 宿主版本 / 插件版本），
/// 让用户确认是否安装（导入到预设列表）。由「导入」按钮与「双击 .cizip 安装」共用。
/// </summary>
internal static class PresetInstallDialog
{
    public static async Task<ContentDialogResult> ShowAsync(Window? host, string presetName, PresetMetadata? metadata)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = $"将把预设「{presetName}」导入到你的预设列表，是否继续？",
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeight.SemiBold
        });
        panel.Children.Add(new TextBlock
        {
            Text = "导入后可随时在「用户预设」中套用或删除；若已有同名预设将被覆盖。",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7
        });

        if (metadata != null)
        {
            var metaPanel = new StackPanel { Spacing = 4, Margin = new Thickness(0, 4, 0, 0) };
            AddRow(metaPanel, "作者", string.IsNullOrWhiteSpace(metadata.Author) ? "未知" : metadata.Author);
            if (!string.IsNullOrWhiteSpace(metadata.School))
            {
                AddRow(metaPanel, "学校 / 组织", metadata.School);
            }

            if (DateTime.TryParse(metadata.CreatedAt, out var created))
            {
                AddRow(metaPanel, "打包时间", created.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
            }

            if (!string.IsNullOrWhiteSpace(metadata.HostVersion))
            {
                AddRow(metaPanel, "宿主版本", metadata.HostVersion);
            }

            if (!string.IsNullOrWhiteSpace(metadata.PluginVersion))
            {
                AddRow(metaPanel, "插件版本", metadata.PluginVersion);
            }

            panel.Children.Add(metaPanel);
        }

        var dialog = new ContentDialog
        {
            Title = "安装预设",
            Content = panel,
            PrimaryButtonText = "安装",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };

        return host != null ? await dialog.ShowAsync(host) : await dialog.ShowAsync();
    }

    private static void AddRow(StackPanel panel, string label, string value)
    {
        var labelBlock = new TextBlock { Text = label, Opacity = 0.7, Margin = new Thickness(0, 0, 12, 0) };
        var valueBlock = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap };
        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star)
            },
            Children = { labelBlock, valueBlock }
        };
        Grid.SetColumn(labelBlock, 0);
        Grid.SetColumn(valueBlock, 1);
        panel.Children.Add(grid);
    }
}
