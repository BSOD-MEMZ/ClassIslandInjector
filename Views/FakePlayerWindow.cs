using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ClassIsland.Core;
using ClassIsland.Core.Controls;
using Windows.Media; // MediaPlaybackType
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace ClassIslandInjector.Views;

/// <summary>
/// 示例播放器：教学里演示「动态取色」的假播放器窗口（Fluent 风格）。
/// 自动扫描插件 Assets/Music 目录作为播放列表，支持上一首/下一首切歌与拖动 seek；
/// 用 WinRT MediaPlayer 播放，会话注册到系统 SMTC（需进程 AUMID），SmtcWatcher 读到
/// 专辑封面取色——切歌时封面变化，主界面颜色会跟着切换。播放/暂停/切歌绑定到系统
/// 媒体控件（音量浮出窗/任务栏预览）的按钮，播放时在 ClassIsland 任务栏图标上显示
/// 进度条。所有 WinRT / 文件读取均 try/catch 兜底，失败只写诊断日志，不影响教程流程。
/// </summary>
internal sealed class FakePlayerWindow : MyWindow
{
    private const string FallbackTitle = "未知歌曲";
    private const string FallbackArtist = "未知歌手";

    /// <summary>播放列表：插件 Assets/Music 下的全部 mp3（按文件名排序）。</summary>
    private static readonly IReadOnlyList<string> Playlist = LoadPlaylist();

    private MediaPlayer? _player;
    private int _index;
    private string _currentTitle = FallbackTitle;
    private string _currentArtist = FallbackArtist;
    private byte[]? _currentCover;

    private readonly TextBlock _titleText = new()
    {
        FontSize = 16,
        FontWeight = FontWeight.SemiBold,
        TextTrimming = TextTrimming.CharacterEllipsis,
        Text = FallbackTitle
    };
    private readonly TextBlock _artistText = new()
    {
        FontSize = 12,
        Opacity = 0.7,
        TextTrimming = TextTrimming.CharacterEllipsis,
        Text = FallbackArtist
    };
    private readonly TextBlock _positionText = new() { FontSize = 11, Opacity = 0.6, Text = "0:00", MinWidth = 34, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _durationText = new() { FontSize = 11, Opacity = 0.6, Text = "0:00", MinWidth = 34, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
    private readonly FluentIcon _playPauseIcon = new() { Glyph = "\uEDB8", FontSize = 18 };
    private readonly Image _coverImage = new() { Stretch = Stretch.UniformToFill, IsVisible = false };
    private readonly Border _coverPlaceholder = new();
    private readonly Slider _seekSlider = new() { Minimum = 0, Maximum = 100, Value = 0, VerticalAlignment = VerticalAlignment.Center };
    private readonly Slider _volumeSlider = new() { Minimum = 0, Maximum = 100, Value = 60, Width = 90, VerticalAlignment = VerticalAlignment.Center };
    private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    /// <summary>当前歌曲封面引用（随歌曲缓存，供重复推送 SMTC 元数据复用）。</summary>
    private RandomAccessStreamReference? _currentThumbnailRef;
    /// <summary>UI 刷新计数（周期性重推 SMTC 元数据用）。</summary>
    private int _uiTick;
    /// <summary>程序性更新滑动条时置 true（防止触发 seek）。</summary>
    private bool _updatingSeekFromPlayback;
    /// <summary>用户正在拖动滑动条（拖动期间不反向刷新滑动条）。</summary>
    private bool _isSeeking;
    /// <summary>任务栏进度条（ITaskbarList3），懒初始化。播放时在 ClassIsland 任务栏图标上显示进度。</summary>
    private static ITaskbarList3? _taskbar;
    /// <summary>宿主主窗口句柄（进度条挂载目标）。</summary>
    private static IntPtr _taskbarHwnd;
    /// <summary>任务栏进度条当前是否可见（避免重复设置）。</summary>
    private static bool _taskbarProgressVisible;

    public FakePlayerWindow()
    {
        Title = "ClassIsland 播放器";
        Width = 440;
        Height = 300;
        CanResize = false;   // 禁止拖拽边缘调整大小
        CanMaximize = false; // 禁止最大化（Avalonia Window 属性，隐藏标题栏最大化按钮）
        // 与 Min/Max 相等：彻底锁死尺寸，连 Aero Snap / Win+↑ 也无法改变窗口大小。
        MinWidth = 440;
        MaxWidth = 440;
        MinHeight = 240;
        MaxHeight = 240;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Content = BuildContent();
        Opened += (_, _) => StartPlayback();
        Closed += (_, _) => StopPlayback();
        _uiTimer.Tick += (_, _) => RefreshUi();

        // 音量滑动条 → MediaPlayer.Volume（0~1）。
        _volumeSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property != Slider.ValueProperty || _player == null)
            {
                return;
            }

            _player.Volume = _volumeSlider.Value / 100.0;
        };

        // 用户拖动滑块 → seek；拖动期间暂停程序性刷新以免打架。
        _seekSlider.AddHandler(Thumb.DragStartedEvent, (_, _) => _isSeeking = true);
        _seekSlider.AddHandler(Thumb.DragCompletedEvent, (_, _) =>
        {
            _isSeeking = false;
            SeekTo(_seekSlider.Value);
        });
        _seekSlider.PropertyChanged += (_, e) =>
        {
            // 点击滑轨（非拖动）也立即 seek。
            if (e.Property != Slider.ValueProperty || _updatingSeekFromPlayback || _isSeeking)
            {
                return;
            }

            SeekTo(_seekSlider.Value);
        };
    }

    private Control BuildContent()
    {
        // 封面：外层圆角裁剪，内层「占位图（渐变+音符）」与「真实封面 Image」二选一。
        _coverPlaceholder.Width = 72;
        _coverPlaceholder.Height = 72;
        _coverPlaceholder.Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromRgb(0x2E, 0x7D, 0x32), 0),
                new GradientStop(Color.FromRgb(0x39, 0xC5, 0xBB), 0.55),
                new GradientStop(Color.FromRgb(0x3D, 0x7E, 0xEA), 1)
            }
        };
        _coverPlaceholder.Child = new FluentIcon
        {
            Glyph = "\uEBC9", // ic_fluent_music_note_2
            FontSize = 28,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var coverHost = new Border
        {
            Width = 72,
            Height = 72,
            CornerRadius = new CornerRadius(10),
            ClipToBounds = true,
            Child = new Grid
            {
                Children = { _coverPlaceholder, _coverImage }
            }
        };

        // 播放控制行：上一首 / 播放暂停 / 下一首 / 静音标记 / 状态。
        var prevButton = IconButton("\uEE02", "上一首（切歌时颜色跟着变）", () => PreviousSong()); // ic_fluent_previous
        var playPauseButton = IconButton("\uEDB8", "播放/暂停（可演示暂停恢复原色）", () =>
        {
            var player = _player;
            if (player == null)
            {
                return;
            }

            if (player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
            {
                player.Pause();
            }
            else
            {
                player.Play();
            }
        });
        playPauseButton.Content = _playPauseIcon;
        var nextButton = IconButton("\uEBE5", "下一首（切歌时颜色跟着变）", () => NextSong()); // ic_fluent_next

        // 音量：扬声器图标 + 音量滑动条。
        var volumePanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new FluentIcon { Glyph = "\uF00C", FontSize = 14, Opacity = 0.7 }, // ic_fluent_speaker_2
                _volumeSlider
            }
        };

        // 底部行：上一首/播放/下一首在左，音量在右（中间留弹性间距）。
        var bottomRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            ColumnSpacing = 10
        };
        var buttonsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { prevButton, playPauseButton, nextButton }
        };
        bottomRow.Children.Add(buttonsPanel);
        Grid.SetColumn(volumePanel, 2);
        bottomRow.Children.Add(volumePanel);

        // 进度行：当前时间 - 滑动条（可拖动 seek）- 总时长。
        var progressGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            ColumnSpacing = 8
        };
        progressGrid.Children.Add(_positionText);
        Grid.SetColumn(_seekSlider, 1);
        progressGrid.Children.Add(_seekSlider);
        Grid.SetColumn(_durationText, 2);
        progressGrid.Children.Add(_durationText);

        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Children =
            {
                coverHost,
                new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = { _titleText, _artistText } }
            }
        };

        return new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 14,
            Children = { header, progressGrid, bottomRow }
        };
    }

    private static Button IconButton(string glyph, string tooltip, Action action)
    {
        var button = new Button
        {
            Content = new FluentIcon { Glyph = glyph, FontSize = 16 },
            Width = 40,
            Height = 32,
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(button, tooltip);
        button.Click += (_, _) => action();
        return button;
    }

    private static IReadOnlyList<string> LoadPlaylist()
    {
        try
        {
            var dir = Path.Combine(InjectorRuntime.PluginDirectory, "Assets", "Music");
            return Directory.Exists(dir)
                ? Directory.GetFiles(dir, "*.mp3").OrderBy(Path.GetFileName, StringComparer.Ordinal).ToArray()
                : [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>从文件名推导标题/艺术家（约定「歌手 - 歌名」，无分隔符则整段当歌名）。</summary>
    private static (string Title, string Artist) DeriveTitleArtist(string file)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        var sep = name.LastIndexOf(" - ", StringComparison.Ordinal);
        if (sep > 0)
        {
            var artist = name[..sep].Trim();
            var title = name[(sep + 3)..].Trim();
            if (title.Length > 0)
            {
                return (title, artist.Length > 0 ? artist : FallbackArtist);
            }
        }

        return (name, FallbackArtist);
    }

    private void EnsurePlayer()
    {
        if (_player != null)
        {
            return;
        }

        var player = new MediaPlayer();
        // 把播放/暂停/切歌绑定到系统 SMTC：系统媒体控件（音量浮出窗/任务栏预览）的
        // 按钮直接驱动播放器。用 MediaPlaybackCommandManager 显式把 Next/Previous 声明为
        // Always 启用并接管点击——比只设 IsNextEnabled/IsPreviousEnabled 更可靠，按钮在
        // 会话建立前也始终可用（否则系统可能显示为灰色、点了没反应）。
        var smtc = player.SystemMediaTransportControls;
        smtc.IsEnabled = true;
        smtc.IsPlayEnabled = true;
        smtc.IsPauseEnabled = true;
        smtc.IsNextEnabled = true;
        smtc.IsPreviousEnabled = true;
        var commands = player.CommandManager;
        commands.NextBehavior.EnablingRule = MediaCommandEnablingRule.Always;
        commands.PreviousBehavior.EnablingRule = MediaCommandEnablingRule.Always;
        commands.NextReceived += (_, _) =>
        {
            SmtcAlbumColorPicker.LogDiagnostic("示例播放器: SMTC 下一首");
            Dispatcher.UIThread.Post(NextSong);
        };
        commands.PreviousReceived += (_, _) =>
        {
            SmtcAlbumColorPicker.LogDiagnostic("示例播放器: SMTC 上一首");
            Dispatcher.UIThread.Post(PreviousSong);
        };
        // 显示属性（标题/艺术家/封面）必须在媒体项「已初始化」后再应用 SMTC 才会采纳；
        // MediaOpened（媒体已打开）即初始化完成信号（DisplayUpdater 直推则不依赖此）。
        player.MediaOpened += (_, _) =>
        {
            TryApplyMetadata(player, _currentTitle, _currentArtist, _currentThumbnailRef);
            // 系统会在媒体打开后把会话元数据重置为空，这里补推一次，并由定时器持续重推。
            TryPushSmtcMetadata(player, _currentTitle, _currentArtist, _currentThumbnailRef);
            SmtcAlbumColorPicker.LogDiagnostic($"示例播放器: 媒体已打开，显示属性已应用（{_currentTitle}）");
        };
        // 一首放完自动切下一首，让用户体验颜色切换。
        player.MediaEnded += (_, _) => Dispatcher.UIThread.Post(NextSong);
        _player = player;
    }

    private void StartPlayback()
    {
        try
        {
            // 桌面应用 MediaPlayer 需进程 AUMID 才能注册 SMTC 会话，先确保已设置。
            EnsureAppUserModelId();
            EnsurePlayer();
            if (Playlist.Count == 0)
            {
                SmtcAlbumColorPicker.LogDiagnostic("示例播放器: 未找到示例歌曲");
                return;
            }

            _ = LoadSongAsync(0, autoPlay: false); // 启动不自动播放，等用户点播放
            _uiTimer.Start();
            SmtcAlbumColorPicker.LogDiagnostic("示例播放器已启动（待播放），SMTC 会话已注册");
        }
        catch (Exception ex)
        {
            SmtcAlbumColorPicker.LogDiagnostic($"示例播放器启动失败: {ex}");
        }
    }

    private async Task LoadSongAsync(int index, bool autoPlay = true)
    {
        var player = _player;
        if (player == null || Playlist.Count == 0)
        {
            return;
        }

        _index = index;
        var file = Playlist[_index];
        (_currentTitle, _currentArtist) = DeriveTitleArtist(file);
        _currentCover = TryReadAlbumArt(file) ?? TryReadFallbackArt();
        _currentThumbnailRef = _currentCover is { Length: > 0 } ? await CreateThumbnailReferenceAsync(_currentCover) : null;
        _titleText.Text = _currentTitle;
        _artistText.Text = _currentArtist;
        UpdateCover(_currentCover);
        ResetProgressUi();

        var item = new MediaPlaybackItem(MediaSource.CreateFromUri(new Uri(file)));
        player.Source = item;
        // 立即经 DisplayUpdater 直推元数据（不依赖 item 初始化）；MediaOpened 时再补一次，定时器持续重推。
        TryPushSmtcMetadata(player, _currentTitle, _currentArtist, _currentThumbnailRef);
        if (autoPlay)
        {
            player.Play();
        }

        SmtcAlbumColorPicker.LogDiagnostic($"示例播放器{(autoPlay ? "切歌" : "载入")}: {_currentTitle} - {_currentArtist}");
    }

    private void PreviousSong()
    {
        if (Playlist.Count == 0)
        {
            return;
        }

        _ = LoadSongAsync((_index - 1 + Playlist.Count) % Playlist.Count);
    }

    private void NextSong()
    {
        if (Playlist.Count == 0)
        {
            return;
        }

        _ = LoadSongAsync((_index + 1) % Playlist.Count);
    }

    private void ResetProgressUi()
    {
        _updatingSeekFromPlayback = true;
        try
        {
            _seekSlider.Value = 0;
        }
        finally
        {
            _updatingSeekFromPlayback = false;
        }

        _positionText.Text = "0:00";
        _durationText.Text = "0:00";
    }

    private void SeekTo(double percent)
    {
        var session = _player?.PlaybackSession;
        if (session == null)
        {
            return;
        }

        try
        {
            var total = session.NaturalDuration.TotalSeconds;
            if (total > 0)
            {
                session.Position = TimeSpan.FromSeconds(total * Math.Clamp(percent, 0, 100) / 100.0);
            }
        }
        catch
        {
            // seek 失败忽略。
        }
    }

    private void UpdateCover(byte[]? cover)
    {
        if (cover is { Length: > 0 })
        {
            try
            {
                using var ms = new MemoryStream(cover);
                _coverImage.Source = new Bitmap(ms);
                _coverImage.IsVisible = true;
                _coverPlaceholder.IsVisible = false;
                return;
            }
            catch
            {
                // 图片解码失败回退占位图。
            }
        }

        _coverImage.IsVisible = false;
        _coverPlaceholder.IsVisible = true;
    }

    private void RefreshUi()
    {
        var session = _player?.PlaybackSession;
        if (session == null)
        {
            return;
        }

        var playing = session.PlaybackState == MediaPlaybackState.Playing;
        try
        {
            var total = session.NaturalDuration.TotalSeconds;
            var position = session.Position.TotalSeconds;
            if (total > 0)
            {
                if (!_isSeeking)
                {
                    _updatingSeekFromPlayback = true;
                    try
                    {
                        _seekSlider.Value = Math.Clamp(position / total * 100, 0, 100);
                    }
                    finally
                    {
                        _updatingSeekFromPlayback = false;
                    }
                }

                _durationText.Text = FormatTime(total);
            }

            _positionText.Text = FormatTime(position);
            _playPauseIcon.Glyph = playing ? "\uEC90" : "\uEDB8"; // pause/play
            // 播放时在 ClassIsland 任务栏图标上显示进度条。
            UpdateTaskbarProgress(playing, position, total);
        }
        catch
        {
            // 会话读取失败忽略。
        }

        // 每 ~2 秒重推一次 SMTC 元数据：系统会在媒体打开后把会话元数据重置为空，
        // 定时重推保证标题/封面在播放全程存活（快照去重，不会重复取色）。
        if (++_uiTick % 4 == 0 && _player != null)
        {
            TryPushSmtcMetadata(_player, _currentTitle, _currentArtist, _currentThumbnailRef);
        }
    }

    private static string FormatTime(double seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    private void StopPlayback()
    {
        _uiTimer.Stop();
        UpdateTaskbarProgress(false, 0, 0); // 关闭时清除任务栏进度
        if (_player is not { } player)
        {
            return;
        }

        try
        {
            player.Pause();
            player.Source = null;
            player.Dispose();
        }
        catch
        {
            // 停止失败忽略。
        }

        _player = null;
    }

    /// <summary>初始化任务栏 COM 对象并缓存宿主主窗口句柄。</summary>
    private static void EnsureTaskbar()
    {
        try
        {
            if (_taskbar == null)
            {
                _taskbar = (ITaskbarList3)new TaskbarList();
                _taskbar.HrInit();
            }

            if (_taskbarHwnd == IntPtr.Zero)
            {
                _taskbarHwnd = GetMainWindowHandle();
            }
        }
        catch
        {
            _taskbar = null;
            _taskbarHwnd = IntPtr.Zero;
        }
    }

    /// <summary>取宿主主窗口（ClassIsland.MainWindow）句柄，供任务栏进度条挂载。</summary>
    private static IntPtr GetMainWindowHandle()
    {
        try
        {
            if (AppBase.Current.MainWindow is { } main)
            {
                return main.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            }

            // 兜底：窗口列表中第一个非播放器窗口。
            var windows = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Windows;
            var alt = windows?.FirstOrDefault(w => w is not FakePlayerWindow && w.IsVisible);
            return alt?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>更新任务栏图标进度条：播放中显示正常进度，暂停保留进度，停止/关闭时清除。</summary>
    private static void UpdateTaskbarProgress(bool playing, double positionSeconds, double totalSeconds)
    {
        try
        {
            EnsureTaskbar();
            if (_taskbar == null || _taskbarHwnd == IntPtr.Zero)
            {
                return;
            }

            if (playing && totalSeconds > 0)
            {
                _taskbar.SetProgressState(_taskbarHwnd, TaskbarProgressState.Normal);
                _taskbar.SetProgressValue(_taskbarHwnd, (ulong)(positionSeconds * 1000), (ulong)(totalSeconds * 1000));
                _taskbarProgressVisible = true;
            }
            else if (!playing && positionSeconds > 0 && totalSeconds > 0 && _taskbarProgressVisible)
            {
                // 暂停：保留进度并显示「已暂停」状态。
                _taskbar.SetProgressState(_taskbarHwnd, TaskbarProgressState.Paused);
            }
            else if (_taskbarProgressVisible)
            {
                _taskbar.SetProgressState(_taskbarHwnd, TaskbarProgressState.NoProgress);
                _taskbarProgressVisible = false;
            }
        }
        catch
        {
            // 任务栏进度失败忽略。
        }
    }

    /// <summary>
    /// 桌面应用的 WinRT MediaPlayer 只有进程设置了 AppUserModelID（AUMID）才会向系统
    /// SMTC 注册媒体会话，且任务栏按钮的 AUMID 与会话一致时，缩略图预览下才会出现
    /// 媒体控制按钮。宿主 ClassIsland 进程未设置过 AUMID，这里尽早补设（插件初始化时
    /// 就调用，早于任何窗口创建，保证主窗口/播放器任务栏按钮都继承同一 AUMID）；
    /// 若进程已有 AUMID 则不动，避免覆盖宿主身份。
    /// </summary>
    public static void EnsureAppUserModelId()
    {
        try
        {
            if (GetCurrentProcessExplicitAppUserModelID(out var current) == 0 && !string.IsNullOrEmpty(current))
            {
                return;
            }

            SetCurrentProcessExplicitAppUserModelID("ClassIslandInjector");
        }
        catch
        {
            // 设置失败仅可能导致 SMTC 注册不了，不影响播放。
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentProcessExplicitAppUserModelID(out string appId);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    // ---- 任务栏进度条（ITaskbarList3）COM 互操作 ----

    [ComImport, Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        void SetProgressValue(IntPtr hwnd, ulong ullCompleted, ulong ullTotal);
        void SetProgressState(IntPtr hwnd, TaskbarProgressState tbpFlags);
        // 其余方法（RegisterTab、ThumbBar* 等）本插件未使用，无需声明。
    }

    [ComImport, Guid("56FDF344-FD6D-11D0-958A-006097C9A090")]
    private class TaskbarList { }

    private enum TaskbarProgressState
    {
        NoProgress = 0,
        Indeterminate = 0x1,
        Normal = 0x2,
        Error = 0x4,
        Paused = 0x8
    }

    /// <summary>把歌曲标题/艺术家/封面元数据应用到媒体项（Type=Music 才会被 SMTC 渲染）。</summary>
    private static void TryApplyMetadata(MediaPlayer player, string title, string artist, RandomAccessStreamReference? thumbnail)
    {
        try
        {
            if (player.Source is not MediaPlaybackItem item)
            {
                return;
            }

            var props = item.GetDisplayProperties();
            props.Type = MediaPlaybackType.Music;
            props.MusicProperties.Title = title;
            props.MusicProperties.Artist = artist;
            if (thumbnail != null)
            {
                props.Thumbnail = thumbnail;
            }

            item.ApplyDisplayProperties(props);
        }
        catch (Exception ex)
        {
            SmtcAlbumColorPicker.LogDiagnostic($"设置示例歌曲元数据失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 通过 MediaPlayer.SystemMediaTransportControls 直接向系统 SMTC 推送元数据
    /// （桌面应用向 SMTC 展示媒体信息的经典做法，与 MediaPlaybackItem 显示属性双保险；
    /// 系统会在媒体打开后重置会话元数据，因此定时器会周期性重推）。
    /// </summary>
    private static void TryPushSmtcMetadata(MediaPlayer player, string title, string artist, RandomAccessStreamReference? thumbnail)
    {
        try
        {
            var smtc = player.SystemMediaTransportControls;
            smtc.IsEnabled = true;
            var updater = smtc.DisplayUpdater;
            updater.Type = MediaPlaybackType.Music;
            updater.MusicProperties.Title = title;
            updater.MusicProperties.Artist = artist;
            if (thumbnail != null)
            {
                updater.Thumbnail = thumbnail;
            }

            updater.Update();
        }
        catch (Exception ex)
        {
            SmtcAlbumColorPicker.LogDiagnostic($"推送 SMTC 元数据失败: {ex.Message}");
        }
    }

    /// <summary>把封面字节流转成 RandomAccessStreamReference（流所有权随引用存活，不再随 writer 释放）。</summary>
    private static async Task<RandomAccessStreamReference?> CreateThumbnailReferenceAsync(byte[] bytes)
    {
        var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            writer.DetachStream();
        }

        return RandomAccessStreamReference.CreateFromStream(stream);
    }

    /// <summary>读取随插件部署的 album.jpg 作为兜底封面（mp3 内嵌封面解析失败时用）。</summary>
    private static byte[]? TryReadFallbackArt()
    {
        try
        {
            var path = Path.Combine(InjectorRuntime.PluginDirectory, "Assets", "album.jpg");
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>从 mp3 的 ID3v2 标签读取内嵌专辑封面（无封面时返回 null）。</summary>
    private static byte[]? TryReadAlbumArt(string file)
    {
        try
        {
            using var fs = File.OpenRead(file);
            var header = new byte[10];
            if (fs.Read(header, 0, 10) < 10 || header[0] != (byte)'I' || header[1] != (byte)'D' || header[2] != (byte)'3')
            {
                return null;
            }

            // ID3v2 标签大小是 4 字节 synchsafe 整数（每字节 7 位）。
            var tagSize = (header[6] << 21) | (header[7] << 14) | (header[8] << 7) | header[9];
            if (tagSize <= 0)
            {
                return null;
            }

            var body = new byte[tagSize];
            if (fs.Read(body, 0, tagSize) < tagSize)
            {
                return null;
            }

            var offset = 0;
            while (offset + 10 <= body.Length)
            {
                var frameId = System.Text.Encoding.ASCII.GetString(body, offset, 4);
                var frameSize = (body[offset + 4] << 24) | (body[offset + 5] << 16) | (body[offset + 6] << 8) | body[offset + 7];
                if (frameId == "\0\0\0\0" || frameSize <= 0)
                {
                    break; // 到达填充区或异常帧。
                }

                if (frameId == "APIC")
                {
                    return ParseApic(body, offset + 10, frameSize);
                }

                offset += 10 + frameSize;
            }
        }
        catch
        {
            // 解析失败忽略。
        }

        return null;
    }

    /// <summary>解析 APIC 帧数据（编码 + MIME + 类型 + 描述 + 图片字节）。</summary>
    private static byte[]? ParseApic(byte[] body, int start, int size)
    {
        if (size <= 0 || start + size > body.Length)
        {
            return null;
        }

        var end = start + size;
        var encoding = body[start];
        var p = start + 1;

        // MIME 类型（ASCII，0x00 结尾）。
        while (p < end && body[p] != 0)
        {
            p++;
        }

        if (p >= end)
        {
            return null;
        }

        p++; // 跳过 MIME 结束符。
        if (p >= end)
        {
            return null;
        }

        p++; // 图片类型字节。

        // 描述：UTF-16（1/2）以双 0x00 结尾，其余以单 0x00 结尾。
        if (encoding == 1 || encoding == 2)
        {
            while (p + 1 < end && !(body[p] == 0 && body[p + 1] == 0))
            {
                p += 2;
            }

            p += 2;
        }
        else
        {
            while (p < end && body[p] != 0)
            {
                p++;
            }

            p++;
        }

        if (p >= end)
        {
            return null;
        }

        var art = new byte[end - p];
        Array.Copy(body, p, art, 0, art.Length);
        return art;
    }
}
