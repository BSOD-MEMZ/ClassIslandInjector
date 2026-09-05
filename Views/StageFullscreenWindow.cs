using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClassIsland.Core.Controls;

namespace ClassIslandInjector.Views;

/// <summary>
/// 舞台全屏预览窗口：无边框置顶黑底窗口，按工程画幅比例居中显示舞台视频的镜像，
/// 底部悬浮播放控制面板（播放/暂停 + 进度条 + 时间 + 退出全屏）。
/// 视频帧由编辑器的播放/seek 回调双写（编辑器舞台 + 本窗口镜像），播放状态与
/// 播放头仍由编辑器统一管理，本窗口只做显示与交互转发。
/// 面板：鼠标移动时显示，播放中静止 2.5 秒自动隐藏（暂停时常显）；
/// Esc / 双击画面 / 退出按钮退出全屏；单击画面 = 播放/暂停。
/// </summary>
internal sealed class StageFullscreenWindow : Window
{
    private const string GlyphPause = "\uEC91";
    private const string GlyphPlay = "\uEDB9";

    /// <summary>视频镜像层容器（尺寸按工程画幅比例居中摆放，变换基准与编辑器舞台一致）。</summary>
    private readonly Grid _host;
    private readonly List<Layer> _layers = [];
    private readonly Border _panel;
    private readonly IconText _playIcon = new() { Glyph = GlyphPlay, Text = "" };
    private readonly TextBlock _timeText = new()
    {
        Foreground = Brushes.White,
        FontSize = 13,
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        Opacity = 0.9
    };
    private readonly Slider _slider = new()
    {
        Width = 360,
        Minimum = 0,
        Maximum = 1,
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
    };
    private readonly DispatcherTimer _hideTimer;
    private double _aspect = 16.0 / 9;
    private bool _playing;
    private double _duration;
    private bool _scrubbing;
    private bool _updatingFromEditor;

    public Action? PlayPauseRequested { get; init; }
    public Action? ExitRequested { get; init; }
    public Action<double>? SeekRequested { get; init; }

    private sealed class Layer
    {
        public required int Track { get; init; }
        public required Image Image { get; init; }
        /// <summary>字段（非属性）：需以 ref 传给位图写入助手。</summary>
        public WriteableBitmap? Bitmap;
        public (double W, double H, double SX, double SY, double Rot, double OX, double OY) LastTransformA;
        public (double OP, double CL, double CT, double CR, double CB) LastTransformB;
        public bool HasTransform;
    }

    public StageFullscreenWindow(double outputWidth, double outputHeight)
    {
        if (outputWidth > 0 && outputHeight > 0)
        {
            _aspect = outputWidth / outputHeight;
        }

        Title = "全屏预览";
        SystemDecorations = SystemDecorations.None;
        WindowState = WindowState.FullScreen;
        ShowInTaskbar = false;
        Background = Brushes.Black;
        TransparencyBackgroundFallback = Brushes.Black;

        _host = new Grid { IsHitTestVisible = true };
        _panel = BuildControlPanel();
        var root = new Grid { Background = Brushes.Black, Children = { _host, _panel } };
        Content = root;

        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            HidePanel();
        };

        // 尺寸变化时按画幅比例重排镜像层容器。
        PropertyChanged += (_, e) =>
        {
            if (e.Property.Name == nameof(ClientSize))
            {
                LayoutHost();
            }
        };

        PointerMoved += (_, _) => PokePanel();
        // 单击画面 = 播放/暂停；双击 = 退出全屏；Esc = 退出。
        PointerPressed += (_, e) =>
        {
            if (e.Source is Visual v && IsAncestorOfPanel(v))
            {
                return; // 面板上的点击不触发播放切换。
            }

            if (e.ClickCount >= 2)
            {
                ExitRequested?.Invoke();
            }
            else
            {
                PlayPauseRequested?.Invoke();
            }

            e.Handled = true;
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                ExitRequested?.Invoke();
                e.Handled = true;
            }
        };
    }

    /// <summary>构建悬浮播放控制面板（半透明圆角，底部居中）。</summary>
    private Border BuildControlPanel()
    {
        var playButton = new Button
        {
            Content = _playIcon,
            Width = 36,
            Height = 32,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0)
        };
        playButton.Click += (_, _) => PlayPauseRequested?.Invoke();
        ToolTip.SetTip(playButton, "播放 / 暂停");

        _slider.PointerPressed += (_, e) =>
        {
            _scrubbing = true;
            SeekFromSlider(e);
        };
        _slider.PointerMoved += (_, e) =>
        {
            if (_scrubbing)
            {
                SeekFromSlider(e);
            }
        };
        _slider.PointerReleased += (_, _) => _scrubbing = false;
        _slider.ValueChanged += (_, e) =>
        {
            if (_updatingFromEditor || !_scrubbing)
            {
                return;
            }

            SeekRequested?.Invoke(e.NewValue);
        };
        ToolTip.SetTip(_slider, "播放进度");

        var exitButton = new Button
        {
            Content = new IconText { Glyph = "\uE8D2", Text = "" },
            Width = 36,
            Height = 32,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0)
        };
        exitButton.Click += (_, _) => ExitRequested?.Invoke();
        ToolTip.SetTip(exitButton, "退出全屏（Esc）");

        return new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromArgb(190, 16, 16, 20)),
            Padding = new Thickness(14, 8),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 36),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 12,
                Children = { playButton, _slider, _timeText, exitButton }
            }
        };
    }

    /// <summary>面板内的点击/拖动不冒泡到「单击画面 = 播放切换」。</summary>
    private bool IsAncestorOfPanel(Visual v)
    {
        foreach (var ancestor in v.GetVisualAncestors())
        {
            if (ReferenceEquals(ancestor, _panel))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>按滑块点击/拖动位置换算时间并请求 seek。</summary>
    private void SeekFromSlider(PointerEventArgs e)
    {
        if (_duration <= 0)
        {
            return;
        }

        var pos = e.GetPosition(_slider);
        var ratio = Math.Clamp(pos.X / Math.Max(1, _slider.Bounds.Width), 0, 1);
        SeekRequested?.Invoke(ratio * _duration);
    }

    /// <summary>鼠标活动：显示面板并重置自动隐藏计时（暂停时常显）。</summary>
    private void PokePanel()
    {
        ShowPanel();
        if (_playing)
        {
            _hideTimer.Stop();
            _hideTimer.Start();
        }
        else
        {
            _hideTimer.Stop();
        }
    }

    /// <summary>显示悬浮面板（编辑器打开窗口时立即调用，避免等鼠标活动）。</summary>
    public void ShowPanel()
    {
        _panel.Opacity = 1;
        _panel.IsHitTestVisible = true;
    }

    private void HidePanel()
    {
        // 拖动进度条途中不隐藏（隐藏会把 IsHitTestVisible 关掉、拖拽中断）。
        if (_playing && !_scrubbing)
        {
            _panel.Opacity = 0;
            _panel.IsHitTestVisible = false;
        }
    }

    /// <summary>按工程画幅比例居中摆放镜像层容器（尽量占满窗口，四周留 4% 边距）。</summary>
    private void LayoutHost()
    {
        var w = ClientSize.Width;
        var h = ClientSize.Height;
        if (w <= 0 || h <= 0 || _aspect <= 0)
        {
            return;
        }

        var availW = w * 0.96;
        var availH = h * 0.96;
        double hostW = availW, hostH = availW / _aspect;
        if (hostH > availH)
        {
            hostH = availH;
            hostW = availH * _aspect;
        }

        _host.Width = hostW;
        _host.Height = hostH;
    }

    /// <summary>更新指定轨道的镜像图层（位图 + 变换 + 显示）；帧像素在写入期间有效。</summary>
    public void UpdateLayer(int track, VideoFrame frame, VideoClip clip)
    {
        while (_layers.Count <= track)
        {
            var img = new Image
            {
                Stretch = Stretch.Uniform,
                IsHitTestVisible = false,
                IsVisible = false
            };
            _layers.Add(new Layer { Track = _layers.Count, Image = img });
            _host.Children.Insert(_layers.Count - 1, img);
        }

        var layer = _layers[track];
        WriteFrameToImage(layer.Image, ref layer.Bitmap, frame, clip.Grayscale);
        ApplyTransform(layer, clip);
        layer.Image.IsVisible = true;
    }

    /// <summary>隐藏指定轨道的镜像图层。</summary>
    public void ClearLayer(int track)
    {
        if (track < 0 || track >= _layers.Count)
        {
            return;
        }

        _layers[track].Image.IsVisible = false;
        _layers[track].Image.Source = null;
    }

    /// <summary>清空全部镜像图层（重新开始播放时）。</summary>
    public void ClearAll()
    {
        foreach (var layer in _layers)
        {
            layer.Image.IsVisible = false;
            layer.Image.Source = null;
        }
    }

    /// <summary>同步播放状态/播放头/总时长（由编辑器在各状态变化点调用）。</summary>
    public void UpdateTransport(bool playing, double time, double duration)
    {
        _playing = playing;
        _duration = Math.Max(0, duration);
        _playIcon.Glyph = playing ? GlyphPause : GlyphPlay;
        _slider.Maximum = _duration > 0 ? _duration : 1;
        if (!_scrubbing)
        {
            // 用户拖动进度条时冻结程序回填：播放中编辑器每拍推送播放头，
            // 若覆盖滑块值就会拖一下弹回去（一抽一抽）。
            _updatingFromEditor = true;
            _slider.Value = Math.Clamp(time, 0, Math.Max(0.001, _duration));
            _updatingFromEditor = false;
        }

        _timeText.Text = $"{FormatClock(time)} / {FormatClock(_duration)}";
        if (!playing)
        {
            ShowPanel();
        }
    }

    private static string FormatClock(double seconds)
    {
        seconds = Math.Max(0, seconds);
        var m = (int)(seconds / 60);
        var s = (int)(seconds % 60);
        return $"{m:00}:{s:00}";
    }

    /// <summary>把片段变换（缩放/翻转/旋转/偏移/不透明度/裁剪）应用到镜像图层（与编辑器 ApplyTransform 一致，基准为镜像容器尺寸）。</summary>
    private void ApplyTransform(Layer layer, VideoClip clip)
    {
        var image = layer.Image;
        var w = _host.Bounds.Width;
        var h = _host.Bounds.Height;
        if (w <= 0 || h <= 0)
        {
            return;
        }

        var sigA = (w, h,
            clip.Scale * clip.ScaleX * (clip.FlipH ? -1 : 1),
            clip.Scale * clip.ScaleY * (clip.FlipV ? -1 : 1),
            clip.Rotation, clip.OffsetX, clip.OffsetY);
        var sigB = (Math.Clamp(clip.Opacity, 0, 1), clip.CropLeft, clip.CropTop, clip.CropRight, clip.CropBottom);
        if (layer.HasTransform && layer.LastTransformA == sigA && layer.LastTransformB == sigB)
        {
            return;
        }

        layer.LastTransformA = sigA;
        layer.LastTransformB = sigB;
        layer.HasTransform = true;
        var group = new TransformGroup();
        group.Children.Add(new ScaleTransform(
            clip.Scale * clip.ScaleX * (clip.FlipH ? -1 : 1),
            clip.Scale * clip.ScaleY * (clip.FlipV ? -1 : 1)));
        group.Children.Add(new RotateTransform(clip.Rotation));
        group.Children.Add(new TranslateTransform(clip.OffsetX * w, clip.OffsetY * h));
        image.RenderTransform = group;
        image.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        image.Opacity = Math.Clamp(clip.Opacity, 0, 1);
        if (clip.CropLeft > 0 || clip.CropTop > 0 || clip.CropRight < 1 || clip.CropBottom < 1)
        {
            image.Clip = new RectangleGeometry(new Rect(
                clip.CropLeft * w,
                clip.CropTop * h,
                Math.Max(0, (clip.CropRight - clip.CropLeft) * w),
                Math.Max(0, (clip.CropBottom - clip.CropTop) * h)));
        }
        else
        {
            image.Clip = null;
        }
    }

    /// <summary>把解码帧写入可复用的 WriteableBitmap（与编辑器同款，含灰度处理）。</summary>
    private static void WriteFrameToImage(Image image, ref WriteableBitmap? bitmap, VideoFrame frame, double grayscale = 0)
    {
        var w = frame.Width;
        var h = frame.Height;
        if (bitmap == null ||
            bitmap.PixelSize.Width != w ||
            bitmap.PixelSize.Height != h)
        {
            bitmap?.Dispose();
            bitmap = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Premul);
        }

        using (var fb = bitmap.Lock())
        {
            var src = frame.Pixels;
            var dst = fb.Address;
            var srcStride = frame.Stride;
            var dstStride = fb.RowBytes;
            if (grayscale > 0.001)
            {
                for (var y = 0; y < h; y++)
                {
                    var si = y * srcStride;
                    var di = y * dstStride;
                    for (var x = 0; x < w; x++)
                    {
                        var b = src[si];
                        var g = src[si + 1];
                        var r = src[si + 2];
                        var gray = (byte)((r * 299 + g * 587 + b * 114) / 1000);
                        var a = Math.Clamp(grayscale, 0, 1);
                        Marshal.WriteByte(dst, di, (byte)(gray * a + b * (1 - a)));
                        Marshal.WriteByte(dst, di + 1, (byte)(gray * a + g * (1 - a)));
                        Marshal.WriteByte(dst, di + 2, (byte)(gray * a + r * (1 - a)));
                        Marshal.WriteByte(dst, di + 3, src[si + 3]);
                        si += 4;
                        di += 4;
                    }
                }
            }
            else if (srcStride == dstStride)
            {
                var len = Math.Min(src.Length, (int)(fb.RowBytes * h));
                Marshal.Copy(src, 0, dst, len);
            }
            else
            {
                for (var y = 0; y < h; y++)
                {
                    Marshal.Copy(src, y * srcStride, IntPtr.Add(dst, y * dstStride),
                        Math.Min(srcStride, dstStride));
                }
            }
        }

        image.Source = bitmap;
        image.InvalidateVisual();
    }
}
