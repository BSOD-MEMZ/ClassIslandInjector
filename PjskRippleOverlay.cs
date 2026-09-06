using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ClassIslandInjector.Pjsk;

namespace ClassIslandInjector;

/// <summary>
/// pjsk 风格「强调」Ripple —— 一比一移植世界计划 critical 判定特效：
/// 真实的 Unity 粒子 JSON 定义 + 发射器模拟 + 原版相机/投影，用官方图集
/// （Assets/pjsk/effect.png，1024×1024）绘制菱形爆花、地面 aura 与伸向谱面
/// 纵深（屏幕上方）的 lane 光束。主界面被视作判定线：特效锚定在主界面底边
/// （方向=上）或顶边（方向=下，整体镜像）。
/// 特效配色/曲线完全来自原数据，时长固定 2.4s（由数据本身的寿命决定）。
/// 加法混合经 RenderOptions.BitmapBlendingMode 生效，后端不支持时退化为普通混合。
/// </summary>
internal sealed class PjskRippleOverlay : Control, IRippleEffect
{
    private const float CameraFovDegrees = 50.0f;
    private const float CameraYaw = -90.0f;
    private const float CameraPitch = 27.1f;
    private static readonly Vector3 CameraPosition = new(0f, 5.32f, -5.86f);
    private const float EffectsTargetAspect = 16f / 9f;
    private const float ProjectionNear = 0.3f;
    private const float ProjectionFar = 1000f;
    /// <summary>8 条 lane 的世界横向跨度（间距 = 0.84 × 0.84，与原 EffectView 的双重比例一致）。</summary>
    private const float LaneSpanWorldUnits = (PjskEffectView.LaneCount - 1) * 0.84f * 0.84f;
    /// <summary>特效数据自身的总寿命（实测 1s 内全部粒子自然消亡，留 0.2s 余量）。</summary>
    private static readonly TimeSpan EffectDuration = TimeSpan.FromSeconds(1.2);

    private static readonly Lazy<PjskEffectAssets?> Assets = new(LoadAssets);

    private readonly DateTime _startedAt = DateTime.UtcNow;
    private readonly Point _anchor;
    private readonly bool _up;
    private readonly double _islandWidth;
    private readonly PjskEffectView _view = new();
    private readonly Matrix4x4 _inverseView;
    private readonly List<PjskEffectQuad> _quads = [];

    public PjskRippleOverlay(Point anchor, double islandWidth, bool up, double opacityScale = 1)
    {
        _anchor = anchor;
        _up = up;
        _islandWidth = Math.Max(60, islandWidth);
        Opacity = Math.Clamp(opacityScale, 0, 1);
        IsHitTestVisible = false;
        ClipToBounds = false;

        _inverseView = PjskDx.InverseViewNoTranslation(_sharedView.Value);

        if (Assets.Value is { } assets)
        {
            try
            {
                _view.Init(assets.LaneCritical, assets.CriticalNormalAura, assets.CriticalNormalGen);
                _view.TriggerCriticalTap(0f);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write(InjectorRuntime.ConfigDirectory is { Length: > 0 }
                    ? Path.Combine(InjectorRuntime.ConfigDirectory, "pjsk-ripple.log")
                    : null, $"pjsk 特效初始化失败：{ex}");
            }
        }
    }

    public bool IsCompleted =>
        Assets.Value == null || DateTime.UtcNow - _startedAt >= EffectDuration;

    public void Advance()
    {
        var time = (float)(DateTime.UtcNow - _startedAt).TotalSeconds;
        try
        {
            _view.Update(time, _inverseView);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(InjectorRuntime.ConfigDirectory is { Length: > 0 }
                ? Path.Combine(InjectorRuntime.ConfigDirectory, "pjsk-ripple.log")
                : null, $"pjsk 粒子模拟异常：{ex}");
        }

        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Assets.Value is not { } assets || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        var aspect = (float)(Bounds.Width / Bounds.Height);
        var projectionScale = MathF.Min(aspect / EffectsTargetAspect, 1f);
        var projection = Matrix4x4.CreateScale(projectionScale, projectionScale, 1f)
                         * PjskDx.PerspectiveFovLh(CameraFovDegrees * MathF.PI / 180f, aspect, ProjectionNear, ProjectionFar);

        // 世界缩放：8 lane 跨度 == 主界面宽度（把主界面视作完整谱面宽度）。
        float pxPerWorldUnit = PixelsPerWorldUnit(projection);
        var worldZoom = pxPerWorldUnit > 0.0001f ? (float)(_islandWidth / LaneSpanWorldUnits) / pxPerWorldUnit : 1f;
        var worldTransform = Matrix4x4.CreateScale(worldZoom);

        // 判定线锚点：把世界原点（lane 中央、判定线处）平移到锚点像素。
        var originPixel = ProjectToPixel(new Vector4(0, 0, 0, 1), worldTransform, projection);
        var deltaX = (float)(_anchor.X - originPixel.X);
        var deltaY = (float)(_anchor.Y - originPixel.Y);

        _quads.Clear();
        _view.CollectQuads(_quads);
        if (_quads.Count == 0)
        {
            return;
        }

        Span<Point> corners = stackalloc Point[4];
        var quads = _quads;
        // 先普通混合后加法混合：跨组顺序不影响结果（无深度），组内保持触发顺序。
        // Avalonia 11.3 的 BitmapBlendingMode 没有 Addition，用 Screen 作为标准替代
        //（亮色粒子在暗底上观感与加法几乎一致）。
        for (var pass = 0; pass < 2; pass++)
        {
            var additivePass = pass == 1;
            using var blendOption = additivePass
                ? context.PushRenderOptions(new RenderOptions { BitmapBlendingMode = BitmapBlendingMode.Screen })
                : default;

            foreach (var quad in quads)
            {
                if (quad.Additive != additivePass)
                {
                    continue;
                }

                for (var i = 0; i < 4; i++)
                {
                    var corner = i switch
                    {
                        0 => quad.Corner0,
                        1 => quad.Corner1,
                        2 => quad.Corner2,
                        _ => quad.Corner3
                    };
                    var pixel = ProjectToPixel(new Vector4(corner.X, corner.Y, 0f, 1f), worldTransform, projection);
                    var x = pixel.X + deltaX;
                    var y = pixel.Y + deltaY;
                    if (!_up)
                    {
                        y = (float)(2 * _anchor.Y - y);
                    }

                    corners[i] = new Point(x, y);
                }

                // 仿射近似：公告板四角共享视空间朝向，透视差异可忽略。
                var a1 = corners[0] - corners[3];
                var a2 = corners[2] - corners[3];
                Matrix transform;
                if (quad.FlipUvs)
                {
                    // 拉伸公告板 UV 旋转 90°：src(s,t) = (t, 1-s)。
                    transform = new Matrix(a2.X, a2.Y, -a1.X, -a1.Y, corners[3].X + a1.X, corners[3].Y + a1.Y);
                }
                else
                {
                    transform = new Matrix(a1.X, a1.Y, a2.X, a2.Y, corners[3].X, corners[3].Y);
                }

                var sourceRect = new Rect(quad.UvX1, quad.UvY1, quad.UvX2 - quad.UvX1, quad.UvY2 - quad.UvY1);
                using var opacity = context.PushOpacity(quad.Alpha);
                using var transformPush = context.PushTransform(transform);
                context.DrawImage(assets.Atlas, sourceRect, new Rect(0, 0, 1, 1));
            }
        }
    }

    /// <summary>世界原点处 1 世界单位对应的像素数（经同样的世界缩放管线测得）。</summary>
    private float PixelsPerWorldUnit(Matrix4x4 projection)
    {
        var a = ProjectToNdc(new Vector4(0, 0, 0, 1), Matrix4x4.Identity, projection);
        var b = ProjectToNdc(new Vector4(1, 0, 0, 1), Matrix4x4.Identity, projection);
        var width = (float)Bounds.Width;
        var height = (float)Bounds.Height;
        var ax = (a.X + 1f) / 2f * width;
        var ay = (1f - a.Y) / 2f * height;
        var bx = (b.X + 1f) / 2f * width;
        var by = (1f - b.Y) / 2f * height;
        return MathF.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
    }

    private static Vector2 ProjectToNdc(Vector4 v, Matrix4x4 world, Matrix4x4 projection)
    {
        var value = Vector4.Transform(Vector4.Transform(v, world), _sharedView.Value);
        var w = MathF.Abs(value.W) > 0.000001f ? value.W : 1f;
        return new Vector2(value.X / w, value.Y / w);
    }

    /// <summary>_viewMatrix 跨实例不变，静态缓存避免每次投影重复传参。</summary>
    private static readonly Lazy<Matrix4x4> _sharedView = new(() => BuildViewMatrix());

    private static Matrix4x4 BuildViewMatrix()
    {
        var pitchRadians = CameraPitch * MathF.PI / 180f;
        var yawRadians = CameraYaw * MathF.PI / 180f;
        var front = Vector3.Normalize(new Vector3(
            MathF.Cos(yawRadians) * MathF.Cos(pitchRadians),
            -MathF.Sin(pitchRadians),
            -MathF.Sin(yawRadians) * MathF.Cos(pitchRadians)));
        return PjskDx.LookToLh(CameraPosition, front, Vector3.UnitY);
    }

    private Point ProjectToPixel(Vector4 v, Matrix4x4 world, Matrix4x4 projection)
    {
        var ndc = ProjectToNdc(v, world, projection);
        return new Point(
            (ndc.X + 1f) / 2f * Bounds.Width,
            (1f - ndc.Y) / 2f * Bounds.Height);
    }

    private static PjskEffectAssets? LoadAssets()
    {
        try
        {
            var assemblyPath = typeof(PjskRippleOverlay).Assembly.Location;
            var pluginDirectory = Path.GetDirectoryName(assemblyPath);
            if (pluginDirectory == null)
            {
                return null;
            }

            var assetsDir = Path.Combine(pluginDirectory, "Assets", "pjsk");
            var atlasPath = Path.Combine(assetsDir, "effect.png");
            if (!File.Exists(atlasPath))
            {
                return null;
            }

            using var stream = File.OpenRead(atlasPath);
            var atlas = new Bitmap(stream);
            var lane = PjskEffectLoader.Parse(File.ReadAllText(Path.Combine(assetsDir, "fx_lane_critical.json")));
            var aura = PjskEffectLoader.Parse(File.ReadAllText(Path.Combine(assetsDir, "fx_note_critical_normal_aura.json")));
            var gen = PjskEffectLoader.Parse(File.ReadAllText(Path.Combine(assetsDir, "fx_note_critical_normal_gen.json")));
            return new PjskEffectAssets(atlas, lane, aura, gen);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(InjectorRuntime.ConfigDirectory is { Length: > 0 }
                ? Path.Combine(InjectorRuntime.ConfigDirectory, "pjsk-ripple.log")
                : null, $"pjsk 特效资源加载失败：{ex.Message}");
            return null;
        }
    }

    private sealed record PjskEffectAssets(
        Bitmap Atlas,
        PjskParticleDef LaneCritical,
        PjskParticleDef CriticalNormalAura,
        PjskParticleDef CriticalNormalGen);
}
