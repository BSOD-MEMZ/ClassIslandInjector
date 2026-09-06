using System.Numerics;
using System.Text.Json;

namespace ClassIslandInjector.Pjsk;

/// <summary>
/// 把 sekai-mmw-preview-web 导出的 Unity 粒子 JSON（effects/*.json）解析为
/// <see cref="PjskParticleDef"/> 树。字段与解析语义逐项对应 ResourceManager::readParticle，
/// 包括原实现的两个怪癖：MinMaxColor 的 constant 取自 colorMax；inTangent/outTangent
/// 可为字符串 "Infinity"。
/// </summary>
internal static class PjskEffectLoader
{
    public static PjskParticleDef Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ReadParticle(doc.RootElement);
    }

    private static PjskParticleDef ReadParticle(JsonElement j)
    {
        var p = new PjskParticleDef
        {
            Name = j.GetProperty("name").GetString() ?? string.Empty
        };

        var transform = j.GetProperty("transform");
        p.Position = TryGetVector3(transform, "position", default);
        p.Rotation = TryGetVector3(transform, "rotation", default);
        p.Scale = TryGetVector3(transform, "scale", Vector3.One);

        p.StartSize = ReadMinMax3(j.GetProperty("startSize"));
        p.StartRotation = ReadMinMax3(j.GetProperty("startRotation"));
        p.StartDelay = ReadMinMax(j.GetProperty("startDelay"));
        p.StartLifeTime = ReadMinMax(j.GetProperty("startLifetime"));
        p.StartSpeed = ReadMinMax(j.GetProperty("startSpeed"));
        p.StartColor = ReadMinMaxColor(j.GetProperty("startColor"));
        p.GravityModifier = ReadMinMax(j.GetProperty("gravityModifier"));
        p.Looping = TryGetBool(j, "loop", false);
        p.MaxParticles = TryGetInt(j, "maxParticles", 1);
        p.ScalingMode = (PjskScalingMode)TryGetInt(j, "scalingMode", 1);
        p.Duration = TryGetFloat(j, "duration", 1f);
        p.FlipRotation = TryGetFloat(j, "flipRotation", 0f);
        p.SimulationSpace = (PjskTransformSpace)TryGetInt(j, "simulationSpace", 0);
        p.RandomSeed = (uint)TryGetInt(j, "randomSeed", 0);
        p.UseAutoRandomSeed = TryGetBool(j, "useAutoRandomSeed", true);

        var emission = j.GetProperty("emission");
        p.Emission.RateOverTime = ReadMinMax(emission.GetProperty("rateOverTime"));
        p.Emission.RateOverDistance = ReadMinMax(emission.GetProperty("rateOverDistance"));

        foreach (var burst in emission.GetProperty("bursts").EnumerateArray())
        {
            p.Emission.Bursts.Add(new PjskBurst
            {
                Time = TryGetFloat(burst, "time", 0),
                Count = TryGetInt(burst, "countMax", 0),
                Cycles = TryGetInt(burst, "cycleCount", 0),
                Interval = TryGetFloat(burst, "repeatInterval", 0),
                Probability = TryGetFloat(burst, "probability", 1f)
            });
        }

        var shape = j.GetProperty("shape");
        var shapeTransform = shape.GetProperty("transform");
        p.Emission.ShapePosition = TryGetVector3(shapeTransform, "position", default);
        p.Emission.ShapeRotation = TryGetVector3(shapeTransform, "rotation", default);
        p.Emission.ShapeScale = TryGetVector3(shapeTransform, "scale", Vector3.One);
        p.Emission.Shape = (PjskEmissionShape)TryGetInt(shape, "shapeType", 10);
        p.Emission.Radius = TryGetFloat(shape, "radius", 0);
        p.Emission.RadiusThickness = TryGetFloat(shape, "radiusThickness", 0);
        p.Emission.Angle = TryGetFloat(shape, "angle", 0);
        p.Emission.Arc = TryGetFloat(shape, "arc", 0);
        p.Emission.ArcSpeed = ReadMinMax(shape.GetProperty("arcSpeed"));
        p.Emission.RandomizeDirection = TryGetFloat(shape, "randomizeDirection", 0);
        p.Emission.RandomizePosition = TryGetFloat(shape, "randomizePosition", 0);
        p.Emission.SpherizeDirection = TryGetFloat(shape, "spherizeDirection", 0);
        p.Emission.ArcMode = (PjskArcMode)TryGetInt(shape, "arcMode", 0);

        if (j.TryGetProperty("textureSheetAnimation", out var textureSheet))
        {
            p.TextureSplitX = TryGetInt(textureSheet, "numTilesX", 1);
            p.TextureSplitY = TryGetInt(textureSheet, "numTilesY", 1);
            p.StartFrame = ReadMinMax(textureSheet.GetProperty("startFrame"));
            p.FrameOverTime = ReadMinMax(textureSheet.GetProperty("frameOverTime"));
        }

        var renderer = j.GetProperty("renderer");
        p.Pivot = TryGetVector3(renderer, "pivot", default);
        p.Order = TryGetInt(renderer, "order", 50);
        p.SpeedScale = TryGetFloat(renderer, "speedScale", 0);
        p.LengthScale = TryGetFloat(renderer, "lengthScale", 0);
        p.RenderMode = (PjskRenderMode)TryGetInt(renderer, "mode", 0);
        p.Alignment = (PjskAlignmentMode)TryGetInt(renderer, "alignment", 0);

        if (j.TryGetProperty("customData", out var customData))
        {
            var blend = TryGetFloat(customData, "blend", 0);
            p.AdditiveBlend = blend >= 0.5f;
        }

        if (j.TryGetProperty("velocityOverLifetime", out var velocityOverLifetime))
        {
            p.VelocityOverLifetime = ReadMinMax3(velocityOverLifetime.GetProperty("linear"));
            p.VelocitySpace = (PjskTransformSpace)TryGetInt(velocityOverLifetime, "space", 0);
        }

        if (j.TryGetProperty("limitVelocityOverLifetime", out var limitVelocityOverLifetime))
        {
            p.LimitVelocityOverLifetime = ReadMinMax3(limitVelocityOverLifetime.GetProperty("speed"));
            p.LimitVelocityDampen = TryGetFloat(limitVelocityOverLifetime, "dampen", 0);
        }

        if (j.TryGetProperty("forceOverLifetime", out var forceOverLifetime))
        {
            p.ForceOverLifetime = ReadMinMax3(forceOverLifetime.GetProperty("value"));
            p.ForceSpace = (PjskTransformSpace)TryGetInt(forceOverLifetime, "space", 0);
        }

        if (j.TryGetProperty("colorOverLifetime", out var colorOverLifetime))
        {
            p.ColorOverLifetime = ReadMinMaxColor(colorOverLifetime);
        }

        if (j.TryGetProperty("sizeOverLifetime", out var sizeOverLifetime))
        {
            p.SizeOverLifetime = ReadMinMax3(sizeOverLifetime);
        }

        if (j.TryGetProperty("rotationOverLifetime", out var rotationOverLifetime))
        {
            p.RotationOverLifetime = ReadMinMax3(rotationOverLifetime);
        }

        if (j.TryGetProperty("children", out var children))
        {
            foreach (var child in children.EnumerateArray())
            {
                p.Children.Add(ReadParticle(child));
            }
        }

        return p;
    }

    private static PjskMinMax ReadMinMax(JsonElement j)
    {
        var minmax = new PjskMinMax
        {
            Constant = j.GetProperty("constant").GetSingle(),
            Min = j.GetProperty("randomMin").GetSingle(),
            Max = j.GetProperty("randomMax").GetSingle(),
            Mode = (PjskMinMaxMode)TryGetInt(j, "mode", 0)
        };

        if (j.TryGetProperty("curveMin", out var curveMin))
        {
            foreach (var entry in curveMin.EnumerateArray())
            {
                minmax.AddKeyFrame(ReadKeyFrame(entry), false);
            }
        }

        if (j.TryGetProperty("curveMax", out var curveMax))
        {
            foreach (var entry in curveMax.EnumerateArray())
            {
                minmax.AddKeyFrame(ReadKeyFrame(entry), true);
            }
        }

        minmax.SortKeyFrames();
        return minmax;
    }

    private static PjskMinMax3 ReadMinMax3(JsonElement j)
    {
        return new PjskMinMax3
        {
            Enabled = true,
            Is3D = TryGetBool(j, "is3D", false),
            X = ReadMinMax(j.GetProperty("x")),
            Y = ReadMinMax(j.GetProperty("y")),
            Z = ReadMinMax(j.GetProperty("z"))
        };
    }

    private static PjskKeyFrame ReadKeyFrame(JsonElement j)
    {
        float inTangent = 0;
        if (j.TryGetProperty("inTangent", out var inTangentElement))
        {
            inTangent = inTangentElement.ValueKind == JsonValueKind.String
                ? float.PositiveInfinity
                : inTangentElement.GetSingle();
        }

        float outTangent = 0;
        if (j.TryGetProperty("outTangent", out var outTangentElement))
        {
            outTangent = outTangentElement.ValueKind == JsonValueKind.String
                ? float.PositiveInfinity
                : outTangentElement.GetSingle();
        }

        return new PjskKeyFrame(
            TryGetFloat(j, "time", 0),
            TryGetFloat(j, "value", 0),
            inTangent,
            outTangent,
            TryGetFloat(j, "inWeight", 0),
            TryGetFloat(j, "outWeight", 0));
    }

    private static PjskMinMaxColor ReadMinMaxColor(JsonElement j)
    {
        var minmax = new PjskMinMaxColor
        {
            Mode = (PjskMinMaxColorMode)TryGetInt(j, "mode", 0)
        };

        var colorMin = TryGetColor(j, "colorMin", new PjskColorF(1, 1, 1, 1));
        var colorMax = TryGetColor(j, "colorMax", new PjskColorF(1, 1, 1, 1));
        minmax.Min = colorMin;
        minmax.Max = colorMax;
        // 原实现怪癖：constant 读的是 colorMax。
        minmax.Constant = colorMax;

        if (j.TryGetProperty("gradientKeysMin", out var gradientKeysMin))
        {
            foreach (var entry in gradientKeysMin.EnumerateArray())
            {
                minmax.AddKeyFrame(TryGetFloat(entry, "time", 0), ReadColor(entry), false);
            }
        }

        if (j.TryGetProperty("gradientKeysMax", out var gradientKeysMax))
        {
            foreach (var entry in gradientKeysMax.EnumerateArray())
            {
                minmax.AddKeyFrame(TryGetFloat(entry, "time", 0), ReadColor(entry), true);
            }
        }

        minmax.SortKeyFrames();
        return minmax;
    }

    private static PjskColorF ReadColor(JsonElement j) => new(
        TryGetFloat(j, "r", 1f),
        TryGetFloat(j, "g", 1f),
        TryGetFloat(j, "b", 1f),
        TryGetFloat(j, "a", 1f));

    private static Vector3 TryGetVector3(JsonElement j, string name, Vector3 fallback)
    {
        if (!j.TryGetProperty(name, out var v))
        {
            return fallback;
        }

        return new Vector3(
            TryGetFloat(v, "x", fallback.X),
            TryGetFloat(v, "y", fallback.Y),
            TryGetFloat(v, "z", fallback.Z));
    }

    private static PjskColorF TryGetColor(JsonElement j, string name, PjskColorF fallback)
    {
        if (!j.TryGetProperty(name, out var v))
        {
            return fallback;
        }

        return ReadColor(v);
    }

    private static float TryGetFloat(JsonElement j, string name, float fallback) =>
        j.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : fallback;

    private static int TryGetInt(JsonElement j, string name, int fallback)
    {
        // JSON 里整数字段可能写成浮点（如 randomSeed: 2.0），GetInt32 会抛 FormatException。
        if (!j.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number)
        {
            return fallback;
        }

        return (int)MathF.Round(v.GetSingle());
    }

    private static bool TryGetBool(JsonElement j, string name, bool fallback)
    {
        if (!j.TryGetProperty(name, out var v))
        {
            return fallback;
        }

        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => fallback
        };
    }
}
