using System.Numerics;

namespace ClassIslandInjector.Pjsk;

// 枚举数值与 JSON 字段（Unity ParticleSystem 导出格式）一一对应。
internal enum PjskEmissionShape
{
    Sphere = 0,
    HemiSphere = 2,
    Cone = 4,
    Box = 5,
    Circle = 10
}

internal enum PjskRenderMode
{
    Billboard = 0,
    StretchedBillboard = 1,
    HorizontalBillboard = 2,
    VerticalBillboard = 3,
    Mesh = 4,
    None = 5
}

internal enum PjskAlignmentMode
{
    View = 0,
    World = 1,
    Local = 2,
    Facing = 3,
    Velocity = 4
}

internal enum PjskScalingMode
{
    Hierarchy = 0,
    Local = 1,
    Shape = 2
}

internal enum PjskTransformSpace
{
    Local = 0,
    World = 1
}

internal enum PjskArcMode
{
    Random = 0,
    Loop = 1,
    PingPong = 2,
    BurstSpread = 3
}

internal enum PjskEmitFrom
{
    Base = 0,
    Volume = 1
}

internal sealed class PjskBurst
{
    public float Time;
    public int Count;
    public int Cycles;
    public float Interval;
    public float Probability = 1f;
}

internal sealed class PjskEmissionDef
{
    public PjskEmissionShape Shape = PjskEmissionShape.Circle;
    public PjskMinMax RateOverTime = new();
    public PjskMinMax RateOverDistance = new();
    public readonly List<PjskBurst> Bursts = [];
    public float Angle;
    public float Radius = 0.0001f;
    public float RadiusThickness = 0.0001f;
    public float Arc = 360f;
    public PjskMinMax ArcSpeed = new();
    public float RandomizeDirection;
    public float RandomizePosition;
    public float SpherizeDirection;
    public PjskArcMode ArcMode;
    // 原版 readParticle 同样未解析该字段（恒为 Base），Cone 的 Volume 分支实际不可达，保持一致。
    public PjskEmitFrom EmitFrom = PjskEmitFrom.Base;
    public Vector3 ShapePosition;
    public Vector3 ShapeRotation;
    public Vector3 ShapeScale = Vector3.One;
}

/// <summary>单个粒子发射器定义（对应 JSON 根/子节点；直接持有子定义引用）。</summary>
internal sealed class PjskParticleDef
{
    public string Name = string.Empty;
    public Vector3 Position;
    public Vector3 Rotation;
    public Vector3 Scale = Vector3.One;

    public float Duration = 1f;
    public int MaxParticles = 1;
    public float FlipRotation;
    public bool Looping;
    public uint RandomSeed;
    public bool UseAutoRandomSeed = true;

    public PjskMinMax StartDelay = new();
    public PjskMinMax StartLifeTime = new();
    public PjskMinMax StartSpeed = new();
    public PjskMinMax GravityModifier = new();
    public PjskMinMax SpeedModifier = new();
    public PjskMinMax3 StartSize = new();
    public PjskMinMax3 StartRotation = new();
    public PjskMinMaxColor StartColor = new();

    public PjskEmissionDef Emission = new();

    public PjskMinMax3 VelocityOverLifetime = new();
    public PjskMinMax3 LimitVelocityOverLifetime = new();
    public float LimitVelocityDampen;
    public PjskMinMax3 ForceOverLifetime = new();
    public PjskMinMaxColor ColorOverLifetime = new();
    public PjskMinMax3 SizeOverLifetime = new();
    public PjskMinMax3 RotationOverLifetime = new();

    public int Order = 50;
    public bool AdditiveBlend;
    public PjskRenderMode RenderMode;
    public PjskAlignmentMode Alignment;
    public Vector3 Pivot;
    public float SpeedScale = 1;
    public float LengthScale = 1;

    public int TextureSplitX = 1;
    public int TextureSplitY = 1;
    public PjskMinMax StartFrame = new();
    public PjskMinMax FrameOverTime = new();

    public PjskScalingMode ScalingMode = PjskScalingMode.Local;
    public PjskTransformSpace VelocitySpace;
    public PjskTransformSpace SimulationSpace;
    public PjskTransformSpace ForceSpace;

    public readonly List<PjskParticleDef> Children = [];
}

internal sealed class PjskParticleInstance
{
    public Matrix4x4 Matrix;
    public Vector3 Position;
    public Vector3 Rotation;
    public Vector3 Scale = Vector3.One;
    public Vector3 Direction;
    public float StartTime;
    public float Time;
    public float Duration;
    public float SpriteSheetLerpRatio;
    public float GravityLerpRatio;
    public float VelocityLerpRatio;
    public float SizeLerpRatio;
    public float ColorLerpRatio;
    public float LimitVelocityLerpRatio;
    public float ForceLerpRatio;
    public float RotationLerpRatio;
    public float Speed;
    public PjskColorF StartColor = new(1, 1, 1, 1);
    public bool FlipRotationFlag;
    public bool Alive;
}

internal sealed class PjskBurstInstance
{
    public float LastBurstTime;
    public int CycleCount;
    public float NextCyclesResetTime;
}

/// <summary>
/// 发射器实例，逐行移植自 Particle.cpp 的 EmitterInstance（emit / updateEmission /
/// update / start / stop），含 Unity 语义的特殊处理（aura 按世界缩放、HorizontalBillboard 平铺地面等）。
/// 全局随机源（randomizeDirection / 自动种子）与原实现共享同一个 LCG 状态。
/// </summary>
internal sealed class PjskEmitter
{
    private const float Gravity = 9.81f;
    private const float BillboardScale = 0.71f;

    private static readonly PjskLcgRandom GlobalRandom = new();

    public PjskParticleDef Ref = null!;
    public readonly List<PjskEmitter> Children = [];
    public readonly List<PjskParticleInstance> Particles = [];
    public readonly List<PjskBurstInstance> Bursts = [];

    public Vector3 BasePosition;
    public Vector3 BaseRotation;
    public Vector3 BaseScale = Vector3.One;

    public float StartTime;
    public float LastEmissionTime;
    public float EmissionAccumulator;
    public float EmissionPosition;
    public float EmissionPositionInterval;
    public float RateOverTime = 1;

    private readonly PjskRandN _initialRandom = new();
    private readonly PjskRandN _shapeRandom = new();
    private readonly PjskRandN _velocityRandom = new();
    private readonly PjskRandN _sizeRandom = new();

    public void Init(PjskParticleDef def, Vector3 position, Vector3 rotation, Vector3 scale)
    {
        Ref = def;
        BasePosition = position;
        BaseRotation = rotation;
        BaseScale = scale;

        Bursts.Clear();
        for (var i = 0; i < def.Emission.Bursts.Count; i++)
        {
            Bursts.Add(new PjskBurstInstance());
        }

        var childPosition = position + def.Position;
        var childRotation = rotation + def.Rotation;
        var childScale = scale * def.Scale;

        Children.Clear();
        foreach (var childDef in def.Children)
        {
            var child = new PjskEmitter();
            child.Init(childDef, childPosition, childRotation, childScale);
            Children.Add(child);
        }
    }

    public void Start(float time)
    {
        var def = Ref;
        if (def.UseAutoRandomSeed)
        {
            var seed = (uint)(GlobalRandom.Get() * uint.MaxValue);
            _initialRandom.SetSeed(seed);
            _shapeRandom.SetSeed(seed);
            _velocityRandom.SetSeed(seed);
            _sizeRandom.SetSeed(seed);
        }
        else
        {
            _initialRandom.SetSeed(def.RandomSeed);
            _shapeRandom.SetSeed(def.RandomSeed);
            _velocityRandom.SetSeed(def.RandomSeed);
            _sizeRandom.SetSeed(def.RandomSeed);
        }

        var emissionRandom = GlobalRandom.Get();
        StartTime = time + def.StartDelay.Evaluate(0, emissionRandom);
        RateOverTime = def.Emission.RateOverTime.Evaluate(0, emissionRandom);

        var maxParticleCount = GetMaxParticleCount();
        if (maxParticleCount > Particles.Count)
        {
            Particles.EnsureCapacity(maxParticleCount);
            for (var i = Particles.Count; i < maxParticleCount; i++)
            {
                Particles.Add(new PjskParticleInstance());
            }
        }

        foreach (var burst in Bursts)
        {
            burst.NextCyclesResetTime = StartTime + def.Duration;
        }

        foreach (var child in Children)
        {
            child.Start(time);
        }
    }

    public void Stop(bool allChildren)
    {
        LastEmissionTime = 0;
        EmissionAccumulator = 0;
        EmissionPosition = 0;
        EmissionPositionInterval = 0;

        foreach (var burst in Bursts)
        {
            burst.LastBurstTime = 0;
            burst.CycleCount = 0;
            burst.NextCyclesResetTime = 0;
        }

        foreach (var p in Particles)
        {
            p.Alive = false;
        }

        if (allChildren)
        {
            foreach (var child in Children)
            {
                child.Stop(true);
            }
        }
    }

    private int GetMaxParticleCount()
    {
        var def = Ref;
        var count = (int)(RateOverTime * def.Duration) + 1;
        foreach (var burst in def.Emission.Bursts)
        {
            if (burst.Cycles == 0)
            {
                var cycleCount = MathF.Ceiling((def.Duration - burst.Time) / MathF.Max(burst.Interval, 1e-6f)) + 1;
                count += (int)cycleCount * burst.Count;
            }
            else
            {
                count += burst.Count * burst.Cycles;
            }
        }

        return Math.Min(count, def.MaxParticles) + 1;
    }

    private int FindFirstDeadParticle()
    {
        for (var i = 0; i < Particles.Count; i++)
        {
            if (!Particles[i].Alive)
            {
                return i;
            }
        }

        return -1;
    }

    private void UpdateEmission(Vector3 worldPosition, Vector3 worldRotation, Vector3 worldScale, float time)
    {
        var def = Ref;
        if (!def.Looping && time >= StartTime + def.Duration)
        {
            return;
        }

        if (RateOverTime > 0f)
        {
            var emissionInterval = EmissionAccumulator == 0 ? 0 : 1f / RateOverTime;
            var nextEmissionTime = StartTime + LastEmissionTime + emissionInterval;
            if (time >= nextEmissionTime)
            {
                EmissionPositionInterval = def.Emission.Arc / RateOverTime;
                EmissionAccumulator++;
                LastEmissionTime = time - StartTime;
                Emit(worldPosition, worldRotation, worldScale, nextEmissionTime);
            }
        }

        for (var i = 0; i < def.Emission.Bursts.Count; i++)
        {
            var refBurst = def.Emission.Bursts[i];
            var burst = Bursts[i];

            if (time >= burst.NextCyclesResetTime)
            {
                burst.CycleCount = 0;
                burst.NextCyclesResetTime += def.Duration;
            }

            if (burst.CycleCount < refBurst.Cycles || refBurst.Cycles == 0)
            {
                var interval = burst.CycleCount == 0 ? 0 : refBurst.Interval;
                var nextBurstTime = burst.LastBurstTime + interval + refBurst.Time + StartTime;
                if (time >= nextBurstTime)
                {
                    EmissionPosition = 0;
                    EmissionPositionInterval = def.Emission.Arc / refBurst.Count;
                    burst.LastBurstTime = time - StartTime;
                    burst.CycleCount++;

                    for (var j = 0; j < refBurst.Count; j++)
                    {
                        Emit(worldPosition, worldRotation, worldScale, time);
                    }
                }
            }
        }
    }

    private void Emit(Vector3 worldPosition, Vector3 worldRotation, Vector3 worldScale, float time)
    {
        var instanceIndex = FindFirstDeadParticle();
        if (instanceIndex == -1)
        {
            return;
        }

        var def = Ref;
        var qBase = PjskDx.QuatFromEulerZyx(BaseRotation);
        var baseAndRefRotation = BaseRotation + def.Rotation;
        var qBaseRef = PjskDx.QuatFromEulerZyx(baseAndRefRotation);
        var qAll = PjskDx.QuatFromEulerZyx(baseAndRefRotation + def.Emission.ShapeRotation);

        var transformPos = PjskDx.Rotate(def.Position * BaseScale, qBase);
        var basePos = PjskDx.Rotate(BasePosition * BaseScale, qBase);
        var shapePos = PjskDx.Rotate(def.Emission.ShapePosition, qBaseRef);
        var position = transformPos + basePos + shapePos;

        var a = _initialRandom.NextFloat();
        var b = _initialRandom.NextFloat();
        var c = _initialRandom.NextFloat();
        float cy = 0, cz = 0;
        if (def.StartSize.Is3D)
        {
            cy = _initialRandom.NextFloat();
            cz = _initialRandom.NextFloat();
        }

        _ = _initialRandom.NextFloat(); // d（原实现仅消耗随机数）
        var e = _initialRandom.NextFloat();
        float ey = 0, ez = 0;
        if (def.StartRotation.Is3D)
        {
            ey = _initialRandom.NextFloat();
            ez = _initialRandom.NextFloat();
        }

        _initialRandom.NextFloat(); // f
        var g = _initialRandom.NextFloat();
        var h = _initialRandom.NextFloat();

        var length = def.StartSpeed.Evaluate(b);
        var startRotation = def.StartRotation.Is3D
            ? def.StartRotation.Evaluate(0, new Vector3(e, ey, ez), 0)
            : new Vector3(def.StartRotation.X.Evaluate(0, e, 0), 0, 0);

        var emitPosition = Vector3.Zero;
        var direction = Vector3.Zero;
        var emission = def.Emission;

        switch (emission.Shape)
        {
            case PjskEmissionShape.Box:
            {
                var halfScale = emission.ShapeScale * 0.5f;
                var x = float.Lerp(-halfScale.X, halfScale.X, _shapeRandom.NextFloat());
                var y = float.Lerp(-halfScale.Y, halfScale.Y, _shapeRandom.NextFloat());
                var z = float.Lerp(-halfScale.Z, halfScale.Z, _shapeRandom.NextFloat());
                emitPosition = PjskDx.Rotate(new Vector3(x, y, z), qAll) + position;
                direction = PjskDx.Rotate(new Vector3(0, 0, 1), qBaseRef);
                break;
            }
            case PjskEmissionShape.Cone:
            {
                float arc;
                switch (emission.ArcMode)
                {
                    case PjskArcMode.Loop:
                        arc = emission.EmissionArcRadians(EmissionPosition);
                        EmissionPosition += emission.ArcSpeed.Evaluate(MathF.Max(time - StartTime, 0f), _shapeRandom.NextFloat()) * (time - StartTime) * 360f;
                        EmissionPosition %= emission.Arc;
                        break;
                    case PjskArcMode.BurstSpread:
                        arc = emission.EmissionArcRadians(EmissionPosition);
                        EmissionPosition += EmissionPositionInterval;
                        break;
                    default:
                        arc = float.Lerp(0, emission.ArcDegreesRadians(), _shapeRandom.NextFloat());
                        break;
                }

                var angle = emission.Angle * MathF.PI / 180f;
                var radius = float.Lerp(emission.Radius * (1 - emission.RadiusThickness), emission.Radius, _shapeRandom.NextFloat());

                var x = MathF.Cos(arc) * radius * emission.ShapeScale.X;
                var y = MathF.Sin(arc) * radius * emission.ShapeScale.Y;
                var z = emission.EmitFrom == PjskEmitFrom.Volume ? float.Lerp(0, length, _shapeRandom.NextFloat()) : 0;

                var xRandom = float.Lerp(-emission.RandomizeDirection, emission.RandomizeDirection, GlobalRandom.Get());
                var yRandom = float.Lerp(-emission.RandomizeDirection, emission.RandomizeDirection, GlobalRandom.Get());
                var zRandom = float.Lerp(-emission.RandomizeDirection, emission.RandomizeDirection, GlobalRandom.Get());

                emitPosition = new Vector3(x, y, z) + new Vector3(xRandom, yRandom, zRandom);
                var positionNormalized = new Vector3(
                    Vector3.Normalize(emitPosition).X,
                    Vector3.Normalize(emitPosition).Y,
                    MathF.Cos(angle));
                var angles = new Vector3(MathF.Sin(angle), MathF.Sin(angle), 1f);
                direction = PjskDx.Rotate(positionNormalized * angles, qAll);
                emitPosition = PjskDx.Rotate(emitPosition, qAll) + position;
                break;
            }
            case PjskEmissionShape.Circle:
            {
                float angle;
                switch (emission.ArcMode)
                {
                    case PjskArcMode.Loop:
                        angle = emission.EmissionArcRadians(EmissionPosition);
                        EmissionPosition += emission.ArcSpeed.Evaluate(MathF.Max(time - StartTime, 0f), 0) * (time - StartTime) * 360f % emission.Arc;
                        break;
                    case PjskArcMode.BurstSpread:
                        angle = emission.EmissionArcRadians(EmissionPosition);
                        EmissionPosition += EmissionPositionInterval;
                        break;
                    default:
                        angle = float.Lerp(0, emission.ArcDegreesRadians(), _shapeRandom.NextFloat());
                        break;
                }

                var radius = float.Lerp(emission.Radius * (1 - emission.RadiusThickness), emission.Radius, _shapeRandom.NextFloat());
                var x = MathF.Cos(angle) * radius * emission.ShapeScale.X;
                var y = MathF.Sin(angle) * radius * emission.ShapeScale.Y;
                emitPosition = PjskDx.Rotate(new Vector3(x, y, 0), qAll);
                direction = emitPosition;
                emitPosition += position;
                break;
            }
            case PjskEmissionShape.Sphere:
            {
                var angle = float.Lerp(0, emission.ArcDegreesRadians(), _shapeRandom.NextFloat());
                var angle2 = float.Lerp(0, MathF.PI, _shapeRandom.NextFloat());
                var radius = float.Lerp(emission.Radius * (1 - emission.RadiusThickness), emission.Radius, _shapeRandom.NextFloat());
                var x = MathF.Cos(angle) * MathF.Sin(angle2) * radius * emission.ShapeScale.X;
                var y = MathF.Sin(angle) * radius * emission.ShapeScale.Y;
                var z = MathF.Cos(angle) * MathF.Cos(angle2) * radius * emission.ShapeScale.Z;
                emitPosition = PjskDx.Rotate(new Vector3(x, y, z), qAll);
                direction = emitPosition;
                emitPosition += position;
                break;
            }
            case PjskEmissionShape.HemiSphere:
            {
                var angle = float.Lerp(0, emission.ArcDegreesRadians(), _shapeRandom.NextFloat());
                var angle2 = float.Lerp(0, MathF.PI / 2f, _shapeRandom.NextFloat());
                var radius = float.Lerp(emission.Radius * (1 - emission.RadiusThickness), emission.Radius, _shapeRandom.NextFloat());
                var x = MathF.Cos(angle) * MathF.Sin(angle2) * radius * emission.ShapeScale.X;
                var y = MathF.Sin(angle) * MathF.Sin(angle2) * radius * emission.ShapeScale.Y;
                var z = MathF.Cos(angle2) * radius * emission.ShapeScale.Z;
                emitPosition = PjskDx.Rotate(new Vector3(x, y, z), qAll);
                direction = emitPosition;
                emitPosition += position;
                break;
            }
        }

        direction = Vector3.Normalize(direction);

        var instance = Particles[instanceIndex];
        instance.Alive = true;
        instance.Position = emitPosition;
        instance.Rotation = -startRotation;
        instance.Duration = def.StartLifeTime.Evaluate(a);
        instance.SpriteSheetLerpRatio = a;
        instance.GravityLerpRatio = h;
        instance.StartColor = def.StartColor.Evaluate(g);
        instance.ColorLerpRatio = g;
        instance.Direction = direction;
        instance.Speed = length;
        instance.StartTime = time;
        instance.Time = 0;

        var velocityR = _velocityRandom.NextFloat();
        instance.VelocityLerpRatio = velocityR;
        instance.LimitVelocityLerpRatio = velocityR;
        instance.ForceLerpRatio = velocityR;

        instance.RotationLerpRatio = e;
        instance.SizeLerpRatio = c;

        var startSize = def.StartSize.Is3D
            ? def.StartSize.Evaluate(0, new Vector3(c, cy, cz), 1)
            : new Vector3(
                def.StartSize.X.Evaluate(0, c, 1),
                def.StartSize.Y.Evaluate(0, c, 1),
                def.StartSize.Z.Evaluate(0, c, 1));
        instance.Scale = def.Scale * startSize;

        if (def.SimulationSpace == PjskTransformSpace.World)
        {
            var qShift = PjskDx.QuatFromEulerZyx(worldRotation);
            instance.Position = PjskDx.Rotate(instance.Position, qShift) + worldPosition;
            instance.Rotation += worldRotation;
            instance.Direction = PjskDx.Rotate(instance.Direction, qShift);
        }

        instance.FlipRotationFlag = def.FlipRotation > 0.5f;
    }

    /// <summary>逐帧推进（dt 由粒子自身时间差得出，与原实现一致）。</summary>
    public void Update(float t, Vector3 worldPosition, Vector3 worldRotation, Vector3 worldScale, Matrix4x4 inverseView)
    {
        var def = Ref;
        var qLocal = PjskDx.QuatFromEulerZyx(BaseRotation);
        var qShift = PjskDx.QuatFromEulerZyx(worldRotation);
        var baseRotation = BaseRotation + def.Rotation;
        var pivot = def.Pivot;

        var isViewAligned = def.RenderMode == PjskRenderMode.Billboard && def.Alignment == PjskAlignmentMode.View;
        var isWorldAligned = def.RenderMode == PjskRenderMode.Billboard && def.Alignment == PjskAlignmentMode.World;

        UpdateEmission(worldPosition, worldRotation, worldScale, t);

        foreach (var p in Particles)
        {
            if (!p.Alive)
            {
                continue;
            }

            var dt = t - p.StartTime - p.Time;
            p.Time = t - p.StartTime;
            var normalizedTime = p.Time / p.Duration;

            if (normalizedTime < 0)
            {
                continue;
            }

            if (p.Time >= p.Duration)
            {
                p.Alive = false;
                continue;
            }

            var currentRotation = isViewAligned || isWorldAligned
                ? p.Rotation
                : baseRotation + p.Rotation;
            if (def.RotationOverLifetime.Enabled)
            {
                currentRotation -= def.RotationOverLifetime.Integrate(0, normalizedTime, p.Duration, p.RotationLerpRatio);
            }

            var currentScale = p.Scale;
            var velocityScale = def.Scale;
            if (def.ScalingMode == PjskScalingMode.Hierarchy)
            {
                currentScale *= BaseScale;
                velocityScale *= BaseScale;
            }

            if (def.SizeOverLifetime.Enabled)
            {
                currentScale *= def.SizeOverLifetime.Evaluate(normalizedTime, p.SizeLerpRatio, 1f);
            }

            var currentVelocity = Vector3.Zero;
            if (def.VelocityOverLifetime.Enabled)
            {
                var vol = def.VelocityOverLifetime.Evaluate(normalizedTime, p.VelocityLerpRatio);
                if (def.VelocitySpace == PjskTransformSpace.Local)
                {
                    vol = PjskDx.Rotate(vol, qLocal);
                }

                currentVelocity += vol;
            }

            if (def.ForceOverLifetime.Enabled)
            {
                var fol = def.ForceOverLifetime.Integrate(0, normalizedTime, p.Duration, p.ForceLerpRatio);
                if (def.ForceSpace == PjskTransformSpace.Local)
                {
                    fol = PjskDx.Rotate(fol, qLocal);
                }

                currentVelocity += fol;
            }

            currentVelocity += p.Direction * p.Speed;

            var speedModifier = def.SpeedModifier.Evaluate(normalizedTime, p.VelocityLerpRatio, 1f);
            var gravity = Gravity * def.GravityModifier.Evaluate(normalizedTime, p.GravityLerpRatio) * p.Time;
            var gravityVector = new Vector3(0, -gravity, 0);

            currentVelocity *= speedModifier;
            currentVelocity = currentVelocity * velocityScale + gravityVector;

            var velocityLimit = def.LimitVelocityOverLifetime.Evaluate(normalizedTime, p.LimitVelocityLerpRatio);
            currentVelocity = LimitVelocity(currentVelocity, velocityLimit, def.LimitVelocityDampen, p.Time);

            p.Position += currentVelocity * dt;

            var rotationFactor = p.FlipRotationFlag ? -1f : 1f;

            var matrix = Matrix4x4.Identity;
            var directionMatrix = Matrix4x4.Identity;
            var currentRotationAdjusted = currentRotation;
            switch (def.RenderMode)
            {
                case PjskRenderMode.Billboard:
                    if (def.Alignment == PjskAlignmentMode.View)
                    {
                        directionMatrix = inverseView;
                    }

                    break;
                case PjskRenderMode.StretchedBillboard:
                    directionMatrix = RotateToDirection(def, currentVelocity, currentScale);
                    currentScale = new Vector3(currentScale.X, 1f, currentScale.Z);
                    break;
                case PjskRenderMode.HorizontalBillboard:
                    currentRotationAdjusted = new Vector3(90f * rotationFactor, 0, currentRotation.Z);
                    currentScale *= BillboardScale;
                    break;
                case PjskRenderMode.VerticalBillboard:
                    directionMatrix = inverseView;
                    currentScale *= BillboardScale;
                    break;
            }

            if (p.FlipRotationFlag)
            {
                currentRotationAdjusted = -currentRotationAdjusted;
            }

            var pivotScale = currentScale;
            matrix *= Matrix4x4.CreateScale(currentScale);
            matrix *= Matrix4x4.CreateTranslation(pivot * pivotScale);
            var rotationMatrix = Matrix4x4.CreateFromQuaternion(PjskDx.QuatFromEulerZyx(currentRotationAdjusted));

            if (def.RenderMode == PjskRenderMode.StretchedBillboard)
            {
                Matrix4x4.Invert(rotationMatrix, out var inverted);
                directionMatrix *= inverted;
            }

            matrix *= directionMatrix;
            matrix *= rotationMatrix;
            matrix *= Matrix4x4.CreateTranslation(p.Position);

            if (def.SimulationSpace == PjskTransformSpace.Local)
            {
                var worldOffset = Matrix4x4.Identity;
                if (def.Name == "aura")
                {
                    worldOffset *= Matrix4x4.CreateScale(worldScale);
                }

                worldOffset *= Matrix4x4.CreateFromQuaternion(qShift);
                worldOffset *= Matrix4x4.CreateTranslation(worldPosition);
                matrix *= worldOffset;
            }

            p.Matrix = matrix;
        }

        foreach (var emitter in Children)
        {
            emitter.Update(t, worldPosition, worldRotation, worldScale, inverseView);
        }
    }

    private static Vector3 LimitVelocity(Vector3 velocity, Vector3 limit, float damp, float time)
    {
        // 移植自 Particle.cpp（原始出处 UnityPlayer.dll）
        var k = MathF.Pow(1 - damp, time * 30);
        var result = velocity;
        result.X = ApplyAxisLimit(velocity.X, limit.X, k);
        result.Y = ApplyAxisLimit(velocity.Y, limit.Y, k);
        result.Z = ApplyAxisLimit(velocity.Z, limit.Z, k);
        return result;
    }

    private static float ApplyAxisLimit(float value, float limit, float k)
    {
        var sign = value >= 0 ? 1f : -1f;
        var abs = MathF.Abs(value);
        if (abs > 0.0001f && abs > limit)
        {
            abs = limit + (abs - limit) * k;
        }

        return abs * sign;
    }

    /// <summary>拉伸公告板的速度对齐矩阵（逐行移植 rotateToDirection）。</summary>
    private static Matrix4x4 RotateToDirection(PjskParticleDef def, Vector3 velocity, Vector3 scale)
    {
        var up = Vector3.UnitY;
        var normalizedVelocity = Vector3.Normalize(-velocity);
        var axis = Vector3.Cross(normalizedVelocity, up);
        var angle = axis.Length();

        if (angle >= 0.000001f)
        {
            angle = MathF.Asin(MathF.Min(angle, 1f));
        }
        else
        {
            angle = 0f;
            axis = Vector3.UnitX;
            if (axis.Length() < 0.000001f)
            {
                axis = -Vector3.UnitX;
            }
        }

        if (Vector3.Dot(-normalizedVelocity, up) < 0f)
        {
            angle = MathF.PI - angle;
        }

        var lengthSquared = velocity.LengthSquared();
        var length = lengthSquared > 0.000001f ? MathF.Sqrt(lengthSquared) : 0f;
        var yScale = def.SpeedScale * length + def.LengthScale * scale.X;
        var stretchDirection = normalizedVelocity * (yScale * 0.5f);

        var result = Matrix4x4.Identity;
        result *= Matrix4x4.CreateScale(1f, yScale, 1f);
        result *= Matrix4x4.CreateFromAxisAngle(axis, angle);
        result *= Matrix4x4.CreateTranslation(stretchDirection);
        return result;
    }
}

/// <summary>JSON 解析辅助扩展（保持与 C++ 常量相同的换算位置）。</summary>
internal static class PjskEmissionExtensions
{
    public static float ArcDegreesRadians(this PjskEmissionDef emission) => emission.Arc * MathF.PI / 180f;

    public static float EmissionArcRadians(this PjskEmissionDef emission, float degrees) => degrees * MathF.PI / 180f;
}
