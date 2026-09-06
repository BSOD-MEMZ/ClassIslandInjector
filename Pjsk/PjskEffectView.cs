using System.Numerics;

namespace ClassIslandInjector.Pjsk;

/// <summary>note 击打效果样式（对应游戏内判定配色）。</summary>
internal enum PjskStyleKind
{
    /// <summary>普通 note（蓝紫）。</summary>
    Normal = 0,
    /// <summary>绝赞 note（金黄，游戏内 critical）。</summary>
    Critical = 1
}

/// <summary>单个待渲染粒子四边形（世界坐标角点 + 图集 UV 像素矩形），等价原 EffectOutputQuad 的
/// 粒子矩阵变换阶段；透视投影（视图 × 投影）由渲染方完成，因此必须保留 Z。</summary>
internal readonly record struct PjskEffectQuad(
    Vector3 Corner0,
    Vector3 Corner1,
    Vector3 Corner2,
    Vector3 Corner3,
    int UvX1,
    int UvY1,
    int UvX2,
    int UvY2,
    float Alpha,
    bool Additive,
    bool FlipUvs);

/// <summary>
/// 特效视图：管理发射器对象池并触发单次「note 命中」三件套——1 道 lane 光束、
/// 1 个地面 aura、1 个中心爆花 gen（与原 addNoteEffects 的单宽度 note 分派一致）。
/// 普通与绝赞两套样式各自独立成池。判定线锚定由调用方通过世界坐标原点完成。
/// </summary>
internal sealed class PjskEffectView
{
    /// <summary>lane / 世界 X 换算比例（原 EFFECT_WIDTH_RATIO，lane 间距）。</summary>
    public const float EffectWidthRatio = 0.84f;

    /// <summary>pjsk 谱面 14 条 lane 的世界跨度（14 × 0.84），渲染方以此对齐主界面宽度。</summary>
    public const float PlayfieldSpanWorldUnits = 14 * EffectWidthRatio;

    private const int PoolSize = 12;

    private readonly Dictionary<int, PjskEffectPool> _pools = new();

    /// <summary>触发时使用的样式（Init 后、Trigger 前设置）。</summary>
    public PjskStyleKind Style { get; set; } = PjskStyleKind.Critical;

    /// <summary>池键：0..2 = 绝赞的 lane/aura/gen，3..5 = 普通的 lane/aura/gen。</summary>
    private static int PoolKey(PjskStyleKind style, int kind) => (style == PjskStyleKind.Critical ? 0 : 3) + kind;

    /// <summary>加载两套样式的定义并建立对象池（池建立时即绑定定义，Play 仅 stop/start）。</summary>
    public void Init(PjskParticleDef laneCritical, PjskParticleDef criticalAura, PjskParticleDef criticalGen,
        PjskParticleDef laneNormal, PjskParticleDef normalAura, PjskParticleDef normalGen)
    {
        _pools.Clear();
        _pools[PoolKey(PjskStyleKind.Critical, 0)] = new PjskEffectPool(laneCritical, PoolSize);
        _pools[PoolKey(PjskStyleKind.Critical, 1)] = new PjskEffectPool(criticalAura, PoolSize);
        _pools[PoolKey(PjskStyleKind.Critical, 2)] = new PjskEffectPool(criticalGen, PoolSize);
        _pools[PoolKey(PjskStyleKind.Normal, 0)] = new PjskEffectPool(laneNormal, PoolSize);
        _pools[PoolKey(PjskStyleKind.Normal, 1)] = new PjskEffectPool(normalAura, PoolSize);
        _pools[PoolKey(PjskStyleKind.Normal, 2)] = new PjskEffectPool(normalGen, PoolSize);
    }

    public void Reset()
    {
        foreach (var pool in _pools.Values)
        {
            pool.Reset();
        }
    }

    /// <summary>
    /// 触发一次单宽度 note 命中：中心 1 道 lane 光束 + 1 个 aura + 1 个爆花。
    /// 对应原 addNoteEffects 对 1 lane note 的分派（普通 note 无 lane 光束的部分由数据自身控制）。
    /// </summary>
    public void TriggerHit(float spawnAtSec)
    {
        PlayAt(PoolKey(Style, 0), spawnAtSec);
        PlayAt(PoolKey(Style, 1), spawnAtSec);
        PlayAt(PoolKey(Style, 2), spawnAtSec);
    }

    private void PlayAt(int poolKey, float spawnAtSec)
    {
        var controller = _pools[poolKey].GetNext();
        controller.WorldX = 0;
        controller.Play(spawnAtSec, -1);
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
            const int atlasSize = 1024;
            var cellWidth = atlasSize / def.TextureSplitX;
            var cellHeight = atlasSize / def.TextureSplitY;
            var x1 = col * cellWidth;
            var x2 = x1 + cellWidth;
            var y1 = row * cellHeight;
            var y2 = y1 + cellHeight;

            // 与 drawQuadWithBlend 相同：四个公告板角点依次过粒子矩阵得到世界坐标。
            // 注意必须保留 Z（透视投影需要深度），由渲染方再乘 视图 × 投影。
            var corners = new Vector3[4];
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
                var w = MathF.Abs(value.W) > 0.000001f ? value.W : 1f;
                corners[i] = new Vector3(value.X / w, value.Y / w, value.Z / w);
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
