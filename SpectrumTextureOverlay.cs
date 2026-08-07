using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ClassIslandInjector;

/// <summary>
/// 动态频谱底纹覆盖层：由注入器在 16ms 动画循环中逐帧调用 <see cref="Update"/> 刷新，
/// 在 <see cref="Render"/> 中用 DrawingContext 直接绘制频谱柱条（不依赖 DrawingBrush，
/// 规避宿主 Avalonia 对画刷内容变更的重绘问题）。柱条底部对齐宿主（即 ClassIsland
/// 主界面底部），可选上下镜像。
/// </summary>
public sealed class SpectrumTextureOverlay : Control
{
    /// <summary>柱条基准宽度对应的参考主界面宽度（像素）：在此宽度下柱条数 = 设置值。</summary>
    private const double ReferenceWidth = 400;

    private readonly AudioSpectrumCapture _capture;
    private readonly float[] _levels = new float[64];
    private readonly SolidColorBrush _brush = new();
    private Color _color = Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF);
    private double _sensitivity = 1;
    private int _bars = 32;
    private bool _mirrored;
    private bool _autoWidth = true;

    public SpectrumTextureOverlay(AudioSpectrumCapture capture)
    {
        _capture = capture;
        IsHitTestVisible = false;
        ClipToBounds = true;
    }

    /// <summary>注入器每帧调用：同步最新参数并请求重绘。</summary>
    public void Update(Color color, int bars, double sensitivity, bool mirrored, bool autoWidth)
    {
        _color = color;
        _bars = bars;
        _sensitivity = sensitivity;
        _mirrored = mirrored;
        _autoWidth = autoWidth;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        if (!_capture.IsRunning)
        {
            return;
        }

        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0)
        {
            return;
        }

        // 自动匹配宽度：柱条数随主界面宽度动态增减（400px 宽时为设置值），柱宽恒定；
        // 关闭时使用固定柱条数，柱条随主界面拉伸。
        var bars = _autoWidth
            ? Math.Clamp((int)Math.Round(_bars * w / ReferenceWidth), 4, 64)
            : Math.Clamp(_bars, 4, 64);
        var levels = _capture.GetLevels(_levels);
        _brush.Color = _color;
        var count = _mirrored ? bars * 2 : bars;
        var slot = w / bars;
        var barWidth = slot * 0.72;
        for (var i = 0; i < count; i++)
        {
            // 捕获器只输出固定频段数（32），自动匹配宽度时显示柱数可能超过它；
            // 重采样保证每一根柱都有电平数据（都会运动），不会出现后半段静止。
            var level = Math.Clamp(
                SampleLevel(levels, _capture.BarCount, i % bars, bars) * (float)_sensitivity,
                0f, 1f);
            // 静音/无声音时跳过绘制，避免底部残留细小柱条。
            if (level < 0.01f)
            {
                continue;
            }

            var isBottom = _mirrored && i >= bars;
            var height = level * h * 0.9;
            var x = (i % bars) * slot + (slot - barWidth) / 2;
            // 常规：柱条从底部向上生长，贴齐宿主底边；镜像时上半排从顶部向下。
            var y = isBottom ? 0 : h - height;
            context.FillRectangle(_brush, new Rect(x, y, barWidth, height));
        }
    }

    /// <summary>
    /// 把捕获器的固定频段数重采样到显示柱条数（线性插值），
    /// 保证自动匹配宽度导致柱条数超过频段数时，所有柱条都有电平数据（都会运动）。
    /// </summary>
    private static float SampleLevel(float[] levels, int sourceCount, int index, int targetCount)
    {
        if (targetCount <= sourceCount)
        {
            var src = Math.Min(sourceCount - 1, index * sourceCount / targetCount);
            return levels[src];
        }

        var pos = (float)index * sourceCount / targetCount;
        var lo = (int)pos;
        var hi = Math.Min(sourceCount - 1, lo + 1);
        return levels[lo] + (levels[hi] - levels[lo]) * (pos - lo);
    }
}
