using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using ClassIsland.Core;
using ClassIsland.Core.Controls;

namespace ClassIslandInjector.Views;

/// <summary>
/// FFmpeg 解码库安装器：仿 Linux 软件包管理器的安装窗口。
/// 展示包信息、下载进度条、实时速度与剩余时间估算、安装日志；可随时取消。
/// 下载完成后窗口显示结果，关闭后由设置页刷新可用性（<see cref="FFmpegRuntime.Refresh"/>）。
/// </summary>
internal sealed class FfmpegInstallWindow : MyWindow
{
    /// <summary>下载统计用的等宽字体（增强「终端/包管理器」观感）。</summary>
    private static readonly FontFamily MonoFont = new("Consolas, Cascadia Mono, monospace");

    private readonly ProgressBar _progressBar = new() { Minimum = 0, Maximum = 100, Value = 0, Height = 8 };
    private readonly TextBlock _stageText = new() { FontSize = 13, Opacity = 0.85, Text = "正在准备…" };
    private readonly TextBlock _bytesText = new() { FontFamily = MonoFont, FontSize = 12, Opacity = 0.75, Text = "" };
    private readonly TextBlock _speedText = new() { FontFamily = MonoFont, FontSize = 12, Opacity = 0.75, Text = "" };
    private readonly TextBlock _etaText = new() { FontFamily = MonoFont, FontSize = 12, Opacity = 0.75, Text = "" };
    private readonly TextBlock _logText = new() { FontFamily = MonoFont, FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
    private readonly ScrollViewer _logScroller = new() { MaxHeight = 120, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly Button _actionButton = new() { Content = "取消" };
    private readonly CancellationTokenSource _cts = new();
    private readonly Progress<FfmpegInstallProgress> _progress;

    /// <summary>当前打开的安装器窗口（单实例；已存在时聚焦而非重复打开）。</summary>
    public static FfmpegInstallWindow? Current { get; private set; }

    public FfmpegInstallWindow()
    {
        Title = "FFmpeg 解码库安装器";
        Width = 480;
        Height = 420;
        CanResize = false;   // 禁止拖拽边缘调整大小
        CanMaximize = false; // 禁止最大化
        // 与 Min/Max 相等：彻底锁死尺寸。
        MinWidth = 480;
        MaxWidth = 480;
        MinHeight = 400;
        MaxHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        // Progress<T> 捕获创建时的同步上下文（UI 线程），回调自动 marshal 回 UI 线程。
        _progress = new Progress<FfmpegInstallProgress>(ApplyProgress);
        Content = BuildContent();
        Opened += (_, _) =>
        {
            Current = this;
            _ = RunInstallAsync();
        };
        _actionButton.Click += (_, _) => Close();
        Closed += (_, _) =>
        {
            if (Current == this)
            {
                Current = null;
            }

            _cts.Cancel();
        };
    }

    /// <summary>启动安装任务（窗口打开后触发）。完成后更新 UI 为结果状态。</summary>
    private async Task RunInstallAsync()
    {
        try
        {
            var (success, message) = await FFmpegRuntime.InstallAsync(_progress, _cts.Token);
            _progressBar.IsIndeterminate = false;
            _progressBar.Value = success ? 100 : 0;
            _stageText.Text = success ? "安装完成" : "安装失败";
            _actionButton.Content = "关闭";
            AppendLog((success ? "✓ " : "✗ ") + message);
        }
        catch (Exception ex)
        {
            _progressBar.IsIndeterminate = false;
            _stageText.Text = "安装中断";
            _actionButton.Content = "关闭";
            AppendLog($"✗ {ex.Message}");
        }
    }

    /// <summary>消费进度快照，更新进度条 / 速度 / 剩余时间 / 日志。</summary>
    private void ApplyProgress(FfmpegInstallProgress p)
    {
        _stageText.Text = p.Stage;
        if (p.Indeterminate)
        {
            _progressBar.IsIndeterminate = true;
        }
        else
        {
            _progressBar.IsIndeterminate = false;
            _progressBar.Value = p.TotalBytes > 0 ? Math.Clamp(p.DownloadedBytes * 100.0 / p.TotalBytes, 0, 100) : 0;
            // 仅下载阶段刷新统计；解压/完成等阶段保留最后一次下载统计。
            _bytesText.Text = p.TotalBytes > 0
                ? $"{FormatBytes(p.DownloadedBytes)} / {FormatBytes(p.TotalBytes)}"
                : FormatBytes(p.DownloadedBytes);
            _speedText.Text = p.SpeedBytesPerSecond > 0 ? $"{FormatBytes(p.SpeedBytesPerSecond)}/s" : string.Empty;
            _etaText.Text = p.Remaining is { } remaining ? $"剩余约 {FormatDuration(remaining)}" : string.Empty;
        }

        if (!string.IsNullOrEmpty(p.LogLine))
        {
            AppendLog(p.LogLine);
        }
    }

    private void AppendLog(string line)
    {
        _logText.Text += line + "\n";
        _logScroller.Offset = new Vector(0, _logScroller.Extent.Height);
    }

    private Control BuildContent()
    {
        // 包信息头部。
        var header = new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new TextBlock { Text = "正在安装：ffmpeg", FontSize = 18, FontWeight = FontWeight.SemiBold },
                new TextBlock
                {
                    Text = "版本 " + FFmpegRuntime.FfmpegVersion +
                           " · FFmpeg 共享解码库（avcodec / avformat / avutil / swscale / swresample）",
                    FontSize = 12,
                    Opacity = 0.6,
                    TextWrapping = TextWrapping.Wrap
                }
            }
        };

        // 统计行：已下载/总大小 | 速度 | 剩余时间。
        var statsRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 16,
            Children = { _bytesText, _speedText, _etaText }
        };

        _logScroller.Content = _logText;
        _actionButton.HorizontalAlignment = HorizontalAlignment.Right;
        _actionButton.MinWidth = 88;

        return new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 10,
            Children =
            {
                header,
                _progressBar,
                _stageText,
                statsRow,
                _logScroller,
                _actionButton
            }
        };
    }

    private static string FormatBytes(double bytes) =>
        bytes >= 1024 * 1024 * 1024 ? $"{bytes / (1024 * 1024 * 1024):0.00} GB"
        : bytes >= 1024 * 1024 ? $"{bytes / (1024 * 1024):0.0} MB"
        : bytes >= 1024 ? $"{bytes / 1024:0} KB"
        : $"{bytes:0} B";

    private static string FormatDuration(TimeSpan t)
    {
        var total = (int)t.TotalSeconds;
        return total >= 3600
            ? $"{total / 3600:0}:{(total % 3600) / 60:00}:{total % 60:00}"
            : $"{total / 60:00}:{total % 60:00}";
    }
}
