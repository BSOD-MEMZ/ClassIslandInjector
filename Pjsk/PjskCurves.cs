using System.Numerics;

namespace ClassIslandInjector.Pjsk;

/// <summary>
/// pjsk 特效引擎的曲线与随机数部分，逐行移植自 sekai-mmw-preview-web 的
/// MinMax.h/.cpp 与 Utilities（RandN xorshift / globalRandom LCG）。
/// 求值公式（含 Unity 加权切线的 Hermite 简化）与原实现保持一致。
/// </summary>
internal struct PjskKeyFrame
{
    public float Time;
    public float Value;
    public float InTangent;
    public float OutTangent;
    public float InWeight;
    public float OutWeight;

    public PjskKeyFrame(float time, float value, float inTangent, float outTangent, float inWeight, float outWeight)
    {
        Time = time;
        Value = value;
        InTangent = inTangent;
        OutTangent = outTangent;
        InWeight = inWeight;
        OutWeight = outWeight;
    }
}

internal enum PjskMinMaxMode
{
    Constant = 0,
    Curve = 1,
    TwoCurves = 2,
    TwoConstants = 3
}

internal enum PjskMinMaxColorMode
{
    Constant = 0,
    Gradient = 1,
    TwoColors = 2,
    TwoGradients = 3,
    Random = 4
}

/// <summary>浮点颜色（线性 0..1，与 JSON 一致）。</summary>
internal struct PjskColorF
{
    public float R, G, B, A;

    public PjskColorF(float r, float g, float b, float a)
    {
        R = r;
        G = g;
        B = b;
        A = a;
    }

    public static PjskColorF operator *(PjskColorF a, PjskColorF b) =>
        new(a.R * b.R, a.G * b.G, a.B * b.B, a.A * b.A);
}

internal static class PjskCurves
{
    // 逐行移植 MinMax.cpp::hermite —— 原实现对加权切线用简化 Hermite（以 ratio 直接代入，x0..x3 未使用）。
    public static float Hermite(in PjskKeyFrame k1, in PjskKeyFrame k2, float time)
    {
        var dt = k2.Time - k1.Time;
        var y0 = k1.Value;
        var y1 = k1.Value + k1.OutTangent * k1.OutWeight * dt;
        var y2 = k2.Value - k1.InTangent * k1.InWeight * dt;
        var y3 = k2.Value;

        var oneMinusT = 1 - time;
        var oneMinusT2 = oneMinusT * oneMinusT;
        var oneMinusT3 = oneMinusT2 * oneMinusT;
        var t2 = time * time;
        var t3 = t2 * time;

        return oneMinusT3 * y0 + 3 * oneMinusT2 * time * y1 + 3 * oneMinusT * t2 * y2 + t3 * y3;
    }

    public static float HermiteArea(in PjskKeyFrame k1, in PjskKeyFrame k2, float time)
    {
        var dt = k2.Time - k1.Time;
        var y0 = k1.Value;
        var y1 = k1.Value + k1.OutTangent * k1.OutWeight * dt;
        var y2 = k2.Value - k1.InTangent * k1.InWeight * dt;
        var y3 = k2.Value;

        var a = -y0 + 3 * y1 - 3 * y2 + y3;
        var b = 3 * y0 - 6 * y1 + 3 * y2;
        var c = -3 * y0 + 3 * y1;
        var d = y0;

        var t2 = time * time;
        var t3 = t2 * time;
        var t4 = t3 * time;

        return a / 4f * t4 + b / 3f * t3 + c / 2f * t2 + d * time;
    }

    public static int FindKeyFrame(List<PjskKeyFrame> keyframes, float time)
    {
        int min = 0, max = keyframes.Count - 1;
        while (min <= max)
        {
            var index = (min + max) / 2;
            if (time < keyframes[index].Time)
            {
                max = index - 1;
            }
            else
            {
                min = index + 1;
            }
        }

        return min;
    }

    public static float EvaluateCurve(List<PjskKeyFrame> keyframes, float time, float fallback)
    {
        if (keyframes.Count == 0)
        {
            return fallback;
        }

        if (time <= keyframes[0].Time)
        {
            return keyframes[0].Value;
        }

        if (time >= keyframes[^1].Time)
        {
            return keyframes[^1].Value;
        }

        var index = FindKeyFrame(keyframes, time);
        if (index == 0)
        {
            return keyframes[0].Value;
        }

        var k1 = keyframes[index - 1];
        var k2 = keyframes[index];
        var ratio = k2.Time - k1.Time > 0 ? (time - k1.Time) / (k2.Time - k1.Time) : 0;
        return Hermite(k1, k2, ratio);
    }

    public static float IntegrateCurve(List<PjskKeyFrame> keyframes, float from, float to, float scale)
    {
        if (keyframes.Count == 0)
        {
            return 0;
        }

        if (keyframes.Count == 1)
        {
            return keyframes[0].Value;
        }

        var index = FindKeyFrame(keyframes, to);
        if (index == 0)
        {
            return keyframes[0].Value;
        }

        float total = 0;
        for (var i = 0; i < index - 1; i++)
        {
            var timeFactor = keyframes[i + 1].Time - keyframes[i].Time;
            total += HermiteArea(keyframes[i], keyframes[i + 1], 1) * (timeFactor * scale);
        }

        if (index <= keyframes.Count - 1)
        {
            var k1 = keyframes[index - 1];
            var k2 = keyframes[index];
            var ratio = k2.Time - k1.Time > 0 ? (to - k1.Time) / (k2.Time - k1.Time) : 0;
            total += HermiteArea(k1, k2, ratio) * ((k2.Time - k1.Time) * scale);
        }

        return total;
    }
}

/// <summary>移植 MinMax（Constant / Curve / TwoCurves / TwoConstants）。</summary>
internal sealed class PjskMinMax
{
    public PjskMinMaxMode Mode;
    public float Constant;
    public float Min;
    public float Max;
    public readonly List<PjskKeyFrame> CurveMin = [];
    public readonly List<PjskKeyFrame> CurveMax = [];

    public float Evaluate() => Evaluate(0, 0, 0);

    public float Evaluate(float lerpRatio) => Evaluate(0, lerpRatio, 0);

    public float Evaluate(float time, float lerpRatio, float fallback = 0)
    {
        return Mode switch
        {
            PjskMinMaxMode.TwoConstants => Lerp(Min, Max, lerpRatio),
            PjskMinMaxMode.Curve => PjskCurves.EvaluateCurve(CurveMin, time, fallback),
            PjskMinMaxMode.TwoCurves => Lerp(
                PjskCurves.EvaluateCurve(CurveMin, time, fallback),
                PjskCurves.EvaluateCurve(CurveMax, time, fallback), lerpRatio),
            _ => Constant
        };
    }

    public float Integrate(float from, float to, float scale, float lerpRatio, float fallback = 0)
    {
        return Mode switch
        {
            PjskMinMaxMode.TwoConstants => Lerp(Min, Max, lerpRatio) * ((to - from) * scale),
            PjskMinMaxMode.Curve => PjskCurves.IntegrateCurve(CurveMin, from, to, scale),
            PjskMinMaxMode.TwoCurves => Lerp(
                PjskCurves.IntegrateCurve(CurveMin, from, to, scale),
                PjskCurves.IntegrateCurve(CurveMax, from, to, scale), lerpRatio),
            _ => Constant * ((to - from) * scale)
        };
    }

    public void AddKeyFrame(PjskKeyFrame k, bool max)
    {
        (max ? CurveMax : CurveMin).Add(k);
    }

    public void SortKeyFrames()
    {
        CurveMin.Sort((a, b) => a.Time.CompareTo(b.Time));
        CurveMax.Sort((a, b) => a.Time.CompareTo(b.Time));
    }

    internal static float Lerp(float start, float end, float ratio) => start + (end - start) * ratio;
}

/// <summary>移植 MinMax3（三维分量，各为一条 MinMax）。</summary>
internal sealed class PjskMinMax3
{
    public bool Enabled;
    public bool Is3D;
    public PjskMinMax X = new();
    public PjskMinMax Y = new();
    public PjskMinMax Z = new();

    public Vector3 Evaluate(float time, float lerpRatio, float fallback = 0) => new(
        X.Evaluate(time, lerpRatio, fallback),
        Y.Evaluate(time, lerpRatio, fallback),
        Z.Evaluate(time, lerpRatio, fallback));

    public Vector3 Evaluate(float time, Vector3 lerpRatio, float fallback = 0) => new(
        X.Evaluate(time, lerpRatio.X, fallback),
        Y.Evaluate(time, lerpRatio.Y, fallback),
        Z.Evaluate(time, lerpRatio.Z, fallback));

    public Vector3 Integrate(float from, float to, float scale, float lerpRatio) => new(
        X.Integrate(from, to, scale, lerpRatio),
        Y.Integrate(from, to, scale, lerpRatio),
        Z.Integrate(from, to, scale, lerpRatio));
}

/// <summary>移植 MinMaxColor。</summary>
internal sealed class PjskMinMaxColor
{
    public PjskMinMaxColorMode Mode;
    public PjskColorF Constant = new(1, 1, 1, 1);
    public PjskColorF Min = new(1, 1, 1, 1);
    public PjskColorF Max = new(1, 1, 1, 1);
    public readonly List<(float Time, PjskColorF Color)> GradientMin = [];
    public readonly List<(float Time, PjskColorF Color)> GradientMax = [];

    public PjskColorF Evaluate() => Evaluate(0, 0);

    public PjskColorF Evaluate(float lerpRatio) => Evaluate(0, lerpRatio);

    public PjskColorF Evaluate(float time, float lerpRatio)
    {
        switch (Mode)
        {
            case PjskMinMaxColorMode.TwoColors:
                return new PjskColorF(
                    Lerp(Min.R, Max.R, lerpRatio),
                    Lerp(Min.G, Max.G, lerpRatio),
                    Lerp(Min.B, Max.B, lerpRatio),
                    Lerp(Min.A, Max.A, lerpRatio));
            case PjskMinMaxColorMode.Random:
                if (GradientMin.Count == 0)
                {
                    return Constant;
                }

                return GradientMin[Math.Clamp((int)(GradientMin.Count * lerpRatio), 0, GradientMin.Count - 1)].Color;
            case PjskMinMaxColorMode.Gradient:
                return At(GradientMin, time);
            case PjskMinMaxColorMode.TwoGradients:
            {
                var c1 = At(GradientMin, time);
                var c2 = At(GradientMax, time);
                return new PjskColorF(
                    Lerp(c1.R, c2.R, lerpRatio),
                    Lerp(c1.G, c2.G, lerpRatio),
                    Lerp(c1.B, c2.B, lerpRatio),
                    Lerp(c1.A, c2.A, lerpRatio));
            }
            default:
                return Constant;
        }
    }

    public void AddKeyFrame(float time, PjskColorF color, bool max)
    {
        (max ? GradientMax : GradientMin).Add((time, color));
    }

    public void SortKeyFrames()
    {
        GradientMin.Sort((a, b) => a.Time.CompareTo(b.Time));
        GradientMax.Sort((a, b) => a.Time.CompareTo(b.Time));
    }

    private static PjskColorF At(List<(float Time, PjskColorF Color)> keyframes, float time)
    {
        if (keyframes.Count == 0)
        {
            return new PjskColorF(1, 1, 1, 1);
        }

        if (time >= keyframes[^1].Time)
        {
            return keyframes[^1].Color;
        }

        var min = 0;
        var max = keyframes.Count - 1;
        while (min <= max)
        {
            var index = (min + max) / 2;
            if (time < keyframes[index].Time)
            {
                max = index - 1;
            }
            else
            {
                min = index + 1;
            }
        }

        if (min == 0)
        {
            return keyframes[0].Color;
        }

        var k1 = keyframes[min - 1];
        var k2 = keyframes[min];
        var ratio = k2.Time - k1.Time > 0 ? (time - k1.Time) / (k2.Time - k1.Time) : 0;
        return new PjskColorF(
            Lerp(k1.Color.R, k2.Color.R, ratio),
            Lerp(k1.Color.G, k2.Color.G, ratio),
            Lerp(k1.Color.B, k2.Color.B, ratio),
            Lerp(k1.Color.A, k2.Color.A, ratio));
    }

    internal static float Lerp(float start, float end, float ratio) => start + (end - start) * ratio;
}

/// <summary>移植 Utilities 的 RandN（xorshift，种子展开与原实现一致）。</summary>
internal sealed class PjskRandN
{
    private const uint Mt19937 = 1812433253;
    private uint _x, _y, _z, _w;

    public void SetSeed(uint seed)
    {
        _x = seed;
        _y = Mt19937 * _x + 1;
        _z = Mt19937 * _y + 1;
        _w = Mt19937 * _z + 1;
    }

    private uint XorShift()
    {
        var t = _x ^ (_x << 11);
        _x = _y;
        _y = _z;
        _z = _w;
        return _w = _w ^ (_w >> 19) ^ t ^ (t >> 8);
    }

    public float NextFloat() => 1f - NextFloatRange(0f, 1f);

    public float NextFloatRange(float min, float max) =>
        (min - max) * ((int)(XorShift() << 9) / 4294967295f) + max;
}

/// <summary>移植 globalRandom（LCG：a=1812433253, c=367, m=2^31-1，缺省种子 1）。</summary>
internal sealed class PjskLcgRandom
{
    private uint _state = 1;

    public float Get()
    {
        _state = (uint)((1812433253UL * _state + 367) % 2147483647UL);
        return _state / 2147483647f;
    }
}

/// <summary>DirectXMath 语义的数学辅助（仅移植引擎实际用到的部分，公式抄自 vendor 头文件）。</summary>
internal static class PjskDx
{
    /// <summary>DirectXMath quaternionFromZYX：XMQuaternionMultiply(Multiply(qZ,qY),qX) = Hamilton(qX, qY, qZ)。</summary>
    public static Quaternion QuatFromEulerZyx(Vector3 eulerDegrees)
    {
        var xRad = eulerDegrees.X * MathF.PI / 180f;
        var yRad = eulerDegrees.Y * MathF.PI / 180f;
        var zRad = eulerDegrees.Z * MathF.PI / 180f;
        var qx = Quaternion.CreateFromAxisAngle(Vector3.UnitX, xRad);
        var qy = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yRad);
        var qz = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, zRad);
        // XMQuaternionMultiply(Q1,Q2) = Hamilton(Q2,Q1)；三层合并 = qX ⊗ qY ⊗ qZ
        return qx * qy * qz;
    }

    /// <summary>DirectXMath XMVector3Rotate = q⁻¹ ⊗ v ⊗ q（注意与 .NET Vector3.Transform 相反）。</summary>
    public static Vector3 Rotate(Vector3 v, Quaternion q)
    {
        var a = new Quaternion(v.X, v.Y, v.Z, 0f);
        var r = Quaternion.Conjugate(q) * a * q;
        return new Vector3(r.X, r.Y, r.Z);
    }

    /// <summary>XMMatrixLookToLH（行主序，行向量右乘），逐项照抄 DirectXMathMatrix.inl。</summary>
    public static Matrix4x4 LookToLh(Vector3 eye, Vector3 direction, Vector3 up)
    {
        var zAxis = Vector3.Normalize(direction);
        var xAxis = Vector3.Normalize(Vector3.Cross(up, zAxis));
        var yAxis = Vector3.Cross(zAxis, xAxis);

        Matrix4x4 m = default;
        m.M11 = xAxis.X;
        m.M12 = xAxis.Y;
        m.M13 = xAxis.Z;
        m.M21 = yAxis.X;
        m.M22 = yAxis.Y;
        m.M23 = yAxis.Z;
        m.M31 = zAxis.X;
        m.M32 = zAxis.Y;
        m.M33 = zAxis.Z;
        m.M41 = -Vector3.Dot(xAxis, eye);
        m.M42 = -Vector3.Dot(yAxis, eye);
        m.M43 = -Vector3.Dot(zAxis, eye);
        m.M14 = m.M24 = m.M34 = 0f;
        m.M44 = 1f;
        return m;
    }

    /// <summary>XMMatrixPerspectiveFovLH（行主序），照抄 DirectXMathMatrix.inl。</summary>
    public static Matrix4x4 PerspectiveFovLh(float fovRadians, float aspect, float nearZ, float farZ)
    {
        var sinFov = MathF.Sin(0.5f * fovRadians);
        var cosFov = MathF.Cos(0.5f * fovRadians);
        var height = cosFov / sinFov;
        var width = height / aspect;
        var fRange = farZ / (farZ - nearZ);

        Matrix4x4 m = default;
        m.M11 = width;
        m.M22 = height;
        m.M33 = fRange;
        m.M34 = 1f;
        m.M43 = -fRange * nearZ;
        m.M44 = 0f;
        return m;
    }

    /// <summary>正交旋转矩阵求逆（视矩阵恒为旋转+平移）；随后把平移行清零，与原实现一致。</summary>
    public static Matrix4x4 InverseViewNoTranslation(Matrix4x4 view)
    {
        Matrix4x4 inv = default;
        inv.M11 = view.M11;
        inv.M12 = view.M21;
        inv.M13 = view.M31;
        inv.M21 = view.M12;
        inv.M22 = view.M22;
        inv.M23 = view.M32;
        inv.M31 = view.M13;
        inv.M32 = view.M23;
        inv.M33 = view.M33;
        inv.M44 = 1f;
        return inv;
    }
}
