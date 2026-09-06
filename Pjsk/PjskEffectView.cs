using System.Numerics;

namespace ClassIslandInjector.Pjsk;

/// <summary>单个待渲染粒子四边形（NDC 坐标 + 图集 UV 像素矩形），等价原 EffectOutputQuad。</summary>
internal readonly record struct PjskEffectQuad(
    Vector2 Corner0,
    Vector2 Corner1,
    Vector2 Corner2,
    Vector2 Corner3,
    int UvX1,
    int UvY1,
    int UvX2,
    int UvY2,
    float Alpha,
    bool Additive,
    bool FlipUvs);

/// <summary>
/// 特效视图：管理发射器对象池并触发「critical tap」三件套（lane 光束 / 地面 aura / 爆花 gen），
/// 逐行移植自 EffectView.cpp 的 critical 分支。判定线锚定由调用方通过世界坐标原点完成。
/// </summary>
internal sealed class PjskEffectView
{
    private const float EffectWidthRatio = 0.84f;

    /// <summary>pjsk 谱面标准 8 轨；整个岛宽度视作完整谱面宽度。</summary>
    public const int LaneCount = 8;

    private readonly Dictionary<int, PjskEffectPool> _pools = new();

    private PjskParticleDef? _laneCritical;
    private PjskParticleDef? _criticalNormalAura;
    private PjskParticleDef? _criticalNormalGen;

    public void Init(PjskParticleDef laneCritical, PjskParticleDef criticalNormalAura, PjskParticleDef criticalNormalGen)
    {
        _laneCritical = laneCritical;
        _criticalNormalAura = criticalNormalAura;
        _criticalNormalGen = criticalNormalGen;
        _pools.Clear();
        // 与原 EffectPool.setup 一致：池建立时就把每个发射器实例与定义绑定并 init（构建发射器树），
        // Play 仅负责 stop/start；否则 EmitterRoot.Ref 为 null，Start 直接 NRE。
        _pools[0] = new PjskEffectPool(laneCritical, 12);
        _pools[1] = new PjskEffectPool(criticalNormalAura, 12);
        _pools[2] = new PjskEffectPool(criticalNormalGen, 12);
    }

    public void Reset()
    {
        foreach (var pool in _pools.Values)
        {
            pool.Reset();
        }
    }

    /// <summary>
    /// 触发一次 critical tap 效果。与原 addNoteEffects 的 critical tap 分支一致：
    /// 每条 lane 一个 aura + 一个 lane 光束，中心一个 gen 爆花。spawnAtSec 为触发时间（秒）。
    /// </summary>
    public void TriggerCriticalTap(float spawnAtSec)
    {
        if (_laneCritical == null || _criticalNormalAura == null || _criticalNormalGen == null)
        {
            return;
        }

        for (var lane = 0; lane < LaneCount; lane++)
        {
            var laneX = (lane - (LaneCount - 1) / 2f) * EffectWidthRatio;
            AddAuraEffect(_criticalNormalAura, laneX, spawnAtSec);
            AddLaneEffect(_laneCritical, laneX, spawnAtSec);
        }

        AddGenEffect(_criticalNormalGen, 0f, spawnAtSec);
    }

    /// <summary>aura：每条 lane 独立一个发射器（对应原 addAuraEffect 的逐 lane 循环）。</summary>
    private void AddAuraEffect(PjskParticleDef def, float xPos, float time)
    {
        var controller = _pools[1].GetNext();
        controller.WorldX = xPos * EffectWidthRatio;
        controller.Play(time, -1);
    }

    /// <summary>lane 光束：每条 lane 独立一个发射器（对应原 addLaneEffect）。</summary>
    private void AddLaneEffect(PjskParticleDef def, float xPos, float time)
    {
        var controller = _pools[0].GetNext();
        controller.WorldX = xPos * EffectWidthRatio;
        controller.Play(time, -1);
    }

    /// <summary>gen 爆花：中心一个发射器（对应原 addEffect）。</summary>
    private void AddGenEffect(PjskParticleDef def, float xPos, float time)
    {
        var controller = _pools[2].GetNext();
        controller.WorldX = xPos * EffectWidthRatio;
        controller.Play(time, -1);
    }

    /// <summary>推进所有活跃发射器（对应 updateEffects）。</summary>
    public void Update(float time, Matrix4x4 inverseView)
    {
        foreach (var pool in _pools.Values)
        {
            foreach (var controller in pool.Controllers)
            {
                if (!controller.Active)
                {
                    continue;
                }

                controller.EmitterRoot.Update(time,
                    new Vector3(controller.WorldX, 0, 0), Vector3.Zero, Vector3.One, inverseView);
            }
        }
    }

    /// <summary>收集全部活跃粒子四边形（按触发时间稳定排序，对应 drawEffects）。</summary>
    public void CollectQuads(List<PjskEffectQuad> output)
    {
        var controllers = new List<PjskParticleController>();
        foreach (var pool in _pools.Values)
        {
            foreach (var controller in pool.Controllers)
            {
                if (controller.Active)
                {
                    controllers.Add(controller);
                }
            }
        }

        controllers.Sort((a, b) => a.StartTime.CompareTo(b.StartTime));
        foreach (var controller in controllers)
        {
            CollectEmitter(controller.EmitterRoot, output);
        }
    }

    private static void CollectEmitter(PjskEmitter emitter, List<PjskEffectQuad> output)
    {
        var def = emitter.Ref;
        foreach (var p in emitter.Particles)
        {
            if (!p.Alive)
            {
                continue;
            }

            var alpha = p.StartColor.A * def.ColorOverLifetime.Evaluate(p.Time / p.Duration, p.ColorLerpRatio).A;
            if (alpha <= 0.004f)
            {
                continue;
            }

            var frameCount = def.TextureSplitX * def.TextureSplitY;
            // 与原实现一致：整体求和后一次截断（而非分项截断）。
            var frameFloat = frameCount * def.StartFrame.Evaluate(p.Time, p.SpriteSheetLerpRatio)
                             + frameCount * def.FrameOverTime.Evaluate(p.Time / p.Duration, p.SpriteSheetLerpRatio);
            var frame = Math.Clamp((int)frameFloat, 0, Math.Max(0, frameCount - 1));
            var row = frame / def.TextureSplitX;
            var col = frame % def.TextureSplitX;
            var atlasSize = 1024;
            var cellWidth = atlasSize / def.TextureSplitX;
            var cellHeight = atlasSize / def.TextureSplitY;
            var x1 = col * cellWidth;
            var x2 = x1 + cellWidth;
            var y1 = row * cellHeight;
            var y2 = y1 + cellHeight;

            // 与 drawQuadWithBlend 相同：四个公告板角点依次过 粒子矩阵 * 视图 * 投影。
            var corners = new Vector2[4];
            for (var i = 0; i < 4; i++)
            {
                var local = i switch
                {
                    0 => new Vector4(0.5f, 0.5f, 0f, 1f),
                    1 => new Vector4(0.5f, -0.5f, 0f, 1f),
                    2 => new Vector4(-0.5f, -0.5f, 0f, 1f),
                    _ => new Vector4(-0.5f, 0.5f, 0f, 1f)
                };
                var value = Vector4.Transform(local, p.Matrix);
                var w = value.W;
                if (MathF.Abs(w) <= 0.000001f)
                {
                    w = 1f;
                }

                corners[i] = new Vector2(value.X / w, value.Y / w);
            }

            output.Add(new PjskEffectQuad(
                corners[0], corners[1], corners[2], corners[3],
                x1, y1, x2, y2,
                Math.Clamp(alpha, 0f, 1f),
                def.AdditiveBlend,
                def.RenderMode == PjskRenderMode.StretchedBillboard));
        }

        foreach (var child in emitter.Children)
        {
            CollectEmitter(child, output);
        }
    }

    private sealed class PjskEffectPool
    {
        public readonly List<PjskParticleController> Controllers = [];
        private int _next;

        public PjskEffectPool(PjskParticleDef def, int count)
        {
            for (var i = 0; i < count; i++)
            {
                var controller = new PjskParticleController();
                controller.EmitterRoot.Init(def, Vector3.Zero, Vector3.Zero, Vector3.One);
                Controllers.Add(controller);
            }
        }

        public PjskParticleController GetNext()
        {
            var controller = Controllers[_next];
            _next = (_next + 1) % Controllers.Count;
            return controller;
        }

        public void Reset()
        {
            foreach (var controller in Controllers)
            {
                controller.Stop();
            }
        }
    }

    private sealed class PjskParticleController
    {
        public float WorldX;
        public float StartTime;
        public bool Active;
        public PjskEmitter EmitterRoot { get; } = new();

        public void Play(float time, float end)
        {
            Active = true;
            StartTime = time;
            EmitterRoot.Stop(true);
            EmitterRoot.Start(time);
        }

        public void Stop()
        {
            Active = false;
            EmitterRoot.Stop(true);
        }
    }
}
