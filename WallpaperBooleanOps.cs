using Avalonia;
using Clipper2Lib;

namespace ClassIslandInjector;

/// <summary>矢量形状逻辑运算（布尔运算）。</summary>
public enum WallpaperBooleanOp
{
    /// <summary>结合（并集，A ∪ B）。</summary>
    Union,
    /// <summary>组合（排除重叠，对称差，A ⊕ B）。</summary>
    Exclude,
    /// <summary>拆分（结合后把互不相连的块拆成独立图层）。</summary>
    Split,
    /// <summary>相交（交集，A ∩ B）。</summary>
    Intersect,
    /// <summary>减除（差集，A − B，第一个减其余）。</summary>
    Subtract
}

/// <summary>
/// 对多个矢量形状做布尔运算，返回结果图层（不改动输入图层，由调用方负责删除与撤销）。
/// 每个形状先转成「主界面坐标」的多边形（含旋转），用 Clipper2 求并 / 差 / 交 / 异或；结果
/// 保留内外环（洞）结构，打包成 ShapeType=Custom + PathRings 的新图层。
/// </summary>
public static class WallpaperBooleanOps
{
    /// <summary>
    /// 对选中的矢量形状执行布尔运算。输入至少 2 个形状；返回 0 个或多个结果图层
    /// （拆分可能产生多个）。无结果 / 失败返回空列表。
    /// </summary>
    public static List<WallpaperLayerItem> Apply(
        WallpaperBooleanOp op,
        IReadOnlyList<WallpaperLayerItem> shapes,
        double islandWidth, double islandHeight)
    {
        if (shapes.Count < 2)
        {
            return [];
        }

        var polys = shapes.Select(s => ToIslandPolygon(s, islandWidth, islandHeight)).ToList();
        var template = shapes[0];
        var result = op switch
        {
            WallpaperBooleanOp.Union or WallpaperBooleanOp.Split => SingleCall(ClipType.Union, polys, null),
            WallpaperBooleanOp.Subtract => SubtractAll(polys),
            WallpaperBooleanOp.Intersect => Sequential(ClipType.Intersection, polys),
            WallpaperBooleanOp.Exclude => Sequential(ClipType.Xor, polys),
            _ => new PolyTreeD()
        };

        var layers = new List<WallpaperLayerItem>();
        for (var i = 0; i < result.Count; i++)
        {
            var outer = result[i];
            if (outer.IsHole || outer.Polygon is not { } outerPoly)
            {
                continue;
            }

            var rings = new List<PathD> { outerPoly };
            CollectHoles(outer, rings);
            var layer = ToLayer(rings, template);
            if (layer != null)
            {
                layers.Add(layer);
            }
        }

        return layers;
    }

    /// <summary>单次裁剪：subject 全部并集 + 可选 clip，输出 PolyTree（保留洞）。</summary>
    private static PolyTreeD SingleCall(ClipType type, List<PathD> polys, PathsD? clip)
    {
        var cl = new ClipperD();
        foreach (var p in polys)
        {
            cl.AddSubject(p);
        }

        if (clip != null)
        {
            cl.AddClip(clip);
        }

        var tree = new PolyTreeD();
        cl.Execute(type, FillRule.NonZero, tree);
        return tree;
    }

    /// <summary>减除：第一个形状减去其余（其余先并集）。</summary>
    private static PolyTreeD SubtractAll(List<PathD> polys)
    {
        var clip = new PathsD();
        for (var i = 1; i < polys.Count; i++)
        {
            clip.Add(polys[i]);
        }

        return SingleCall(ClipType.Difference, [polys[0]], clip);
    }

    /// <summary>依次两两运算（交集 / 对称差对多个形状需要逐个取，避免把「交并」混为一谈）。</summary>
    private static PolyTreeD Sequential(ClipType type, List<PathD> polys)
    {
        var current = new PathsD { polys[0] };
        for (var i = 1; i < polys.Count; i++)
        {
            var cl = new ClipperD();
            cl.AddSubject(current);
            cl.AddClip(polys[i]);
            var paths = new PathsD();
            if (!cl.Execute(type, FillRule.NonZero, paths) || paths.Count == 0)
            {
                return new PolyTreeD();
            }

            current = paths;
        }

        // 把最终路径重建为树（保留洞结构）。
        return RebuildTree(current);
    }

    /// <summary>把路径集合重建为 PolyTree（Clipper2 按缠绕方向判定内外环）。</summary>
    private static PolyTreeD RebuildTree(PathsD paths)
    {
        var cl = new ClipperD();
        cl.AddSubject(paths);
        var tree = new PolyTreeD();
        cl.Execute(ClipType.Union, FillRule.NonZero, tree);
        return tree;
    }

    /// <summary>收集某个外环下的所有洞环。</summary>
    private static void CollectHoles(PolyPathD node, List<PathD> rings)
    {
        for (var i = 0; i < node.Count; i++)
        {
            var child = node[i];
            if (child.IsHole && child.Polygon is { } hole)
            {
                rings.Add(hole);
            }

            CollectHoles(child, rings);
        }
    }

    /// <summary>把形状转成主界面坐标的多边形（局部坐标 + 绕中心旋转 + 平移到锚点位置）。</summary>
    private static PathD ToIslandPolygon(WallpaperLayerItem layer, double islandWidth, double islandHeight)
    {
        var rect = WallpaperLayerLayout.ComputeRect(layer, islandWidth, islandHeight, null);
        var local = ShapePolygonPoints(layer);
        var cx = rect.X + rect.Width / 2;
        var cy = rect.Y + rect.Height / 2;
        var rad = layer.Rotation * Math.PI / 180;
        var cos = Math.Cos(rad);
        var sin = Math.Sin(rad);
        var path = new PathD(local.Count);
        foreach (var p in local)
        {
            var px = rect.X + p.X;
            var py = rect.Y + p.Y;
            if (layer.Rotation != 0)
            {
                var dx = px - cx;
                var dy = py - cy;
                px = cx + dx * cos - dy * sin;
                py = cy + dx * sin + dy * cos;
            }

            path.Add(new PointD(px, py));
        }

        return path;
    }

    /// <summary>把结果环（外环 + 洞）转成 Custom 形状图层。</summary>
    private static WallpaperLayerItem? ToLayer(List<PathD> rings, WallpaperLayerItem template)
    {
        if (rings.Count == 0 || rings[0].Count < 3)
        {
            return null;
        }

        var outer = rings[0];
        var minX = outer.Min(p => p.x);
        var minY = outer.Min(p => p.y);
        var maxX = outer.Max(p => p.x);
        var maxY = outer.Max(p => p.y);
        var w = maxX - minX;
        var h = maxY - minY;
        if (w < 1 && h < 1)
        {
            return null;
        }

        return new WallpaperLayerItem
        {
            Id = Guid.NewGuid().ToString("N"),
            GroupId = string.Empty,
            Name = template.Name,
            Visible = true,
            Opacity = 1,
            Source = WallpaperSource.None,
            Kind = WallpaperLayerKind.Shape,
            ShapeType = WallpaperShapeType.Custom,
            SizeMode = WallpaperLayerSizeMode.Custom,
            Width = Math.Max(1, w),
            Height = Math.Max(1, h),
            AnchorX = WallpaperLayerAnchorX.Left,
            AnchorY = WallpaperLayerAnchorY.Top,
            OffsetX = minX,
            OffsetY = minY,
            Rotation = 0,
            PathRings = WallpaperLayerItem.EncodePathRings(
                rings.Select(r => r.Select(p => new Point(p.x - minX, p.y - minY)).ToList()).ToList()),
            FillColor = template.FillColor,
            FillUsesThemeColor = template.FillUsesThemeColor,
            StrokeColor = template.StrokeColor,
            StrokeUsesThemeColor = template.StrokeUsesThemeColor,
            StrokeThickness = template.StrokeThickness
        };
    }

    // ---- 各种形状 → 局部坐标多边形（与 WallpaperLayerVisual 的几何一致）----

    private static List<Point> ShapePolygonPoints(WallpaperLayerItem layer)
    {
        var w = Math.Max(1, layer.Width);
        var h = Math.Max(1, layer.Height);
        switch (layer.ShapeType)
        {
            case WallpaperShapeType.Rectangle:
                return [new Point(0, 0), new Point(w, 0), new Point(w, h), new Point(0, h)];
            case WallpaperShapeType.RoundedRectangle:
                return RoundedRectPoints(w, h, layer.ShapeCornerRadius);
            case WallpaperShapeType.Ellipse:
                return EllipsePoints(w, h);
            case WallpaperShapeType.Triangle:
                return RegularPoints(w, h, 3);
            case WallpaperShapeType.Diamond:
                return RegularPoints(w, h, 4);
            case WallpaperShapeType.Pentagon:
                return RegularPoints(w, h, 5);
            case WallpaperShapeType.Hexagon:
                return RegularPoints(w, h, 6);
            case WallpaperShapeType.Star:
                return StarPoints(w, h, layer.ShapeStarPoints, layer.ShapeStarInset);
            case WallpaperShapeType.Heart:
                return HeartPoints(w, h);
            case WallpaperShapeType.Parallelogram:
                return [new Point(w * 0.25, 0), new Point(w, 0), new Point(w * 0.75, h), new Point(0, h)];
            case WallpaperShapeType.Custom:
                return WallpaperLayerItem.DecodePathRings(layer.PathRings) is { Count: > 0 } rings
                    ? rings[0]
                    : [new Point(0, 0), new Point(w, 0), new Point(w, h), new Point(0, h)];
            default:
                return [new Point(0, 0), new Point(w, 0), new Point(w, h), new Point(0, h)];
        }
    }

    /// <summary>正多边形（首个顶点朝上，与 BuildRegularPolygon 一致）。</summary>
    private static List<Point> RegularPoints(double w, double h, int sides)
    {
        sides = Math.Max(3, sides);
        var cx = w / 2;
        var cy = h / 2;
        var r = Math.Max(1, Math.Min(w, h) / 2);
        var pts = new List<Point>(sides);
        for (var i = 0; i < sides; i++)
        {
            var a = -Math.PI / 2 + i * 2 * Math.PI / sides;
            pts.Add(new Point(cx + r * Math.Cos(a), cy + r * Math.Sin(a)));
        }

        return pts;
    }

    /// <summary>椭圆（64 段采样）。</summary>
    private static List<Point> EllipsePoints(double w, double h)
    {
        const int n = 64;
        var cx = w / 2;
        var cy = h / 2;
        var rx = w / 2;
        var ry = h / 2;
        var pts = new List<Point>(n);
        for (var i = 0; i < n; i++)
        {
            var a = 2 * Math.PI * i / n;
            pts.Add(new Point(cx + rx * Math.Cos(a), cy + ry * Math.Sin(a)));
        }

        return pts;
    }

    /// <summary>星形（外 / 内顶点交替，与 BuildStarGeometry 一致）。</summary>
    private static List<Point> StarPoints(double w, double h, int points, double inset)
    {
        points = Math.Clamp(points, 3, 16);
        inset = Math.Clamp(inset, 0.1, 0.95);
        var cx = w / 2;
        var cy = h / 2;
        var outer = Math.Max(1, Math.Min(w, h) / 2);
        var inner = outer * inset;
        var pts = new List<Point>(points * 2);
        for (var i = 0; i < points * 2; i++)
        {
            var r = i % 2 == 0 ? outer : inner;
            var a = -Math.PI / 2 + i * Math.PI / points;
            pts.Add(new Point(cx + r * Math.Cos(a), cy + r * Math.Sin(a)));
        }

        return pts;
    }

    /// <summary>心形（两段三次贝塞尔展平成多边形）。</summary>
    private static List<Point> HeartPoints(double w, double h)
    {
        var cx = w / 2;
        var start = new Point(cx, h * 0.96);
        var pts = new List<Point> { start };
        SampleCubic(pts, start, new Point(w * 0.02, h * 0.58), new Point(w * 0.2, h * 0.12), new Point(cx, h * 0.38), 24);
        SampleCubic(pts, new Point(cx, h * 0.38), new Point(w * 0.8, h * 0.12), new Point(w * 0.98, h * 0.58), start, 24);
        return pts;
    }

    private static void SampleCubic(List<Point> pts, Point p0, Point p1, Point p2, Point p3, int steps)
    {
        for (var i = 1; i <= steps; i++)
        {
            var t = i / (double)steps;
            var mt = 1 - t;
            pts.Add(new Point(
                mt * mt * mt * p0.X + 3 * mt * mt * t * p1.X + 3 * mt * t * t * p2.X + t * t * t * p3.X,
                mt * mt * mt * p0.Y + 3 * mt * mt * t * p1.Y + 3 * mt * t * t * p2.Y + t * t * t * p3.Y));
        }
    }

    /// <summary>圆角矩形（四个角弧采样成多边形）。</summary>
    private static List<Point> RoundedRectPoints(double w, double h, double radius)
    {
        radius = Math.Clamp(radius, 0, Math.Min(w, h) / 2);
        if (radius <= 0)
        {
            return [new Point(0, 0), new Point(w, 0), new Point(w, h), new Point(0, h)];
        }

        const int steps = 6;
        var pts = new List<Point>();
        // 右上角：-π/2 → 0
        for (var i = 0; i <= steps; i++)
        {
            var a = -Math.PI / 2 + i * Math.PI / 2 / steps;
            pts.Add(new Point(w - radius + radius * Math.Cos(a), radius + radius * Math.Sin(a)));
        }

        // 右下角：0 → π/2
        for (var i = 0; i <= steps; i++)
        {
            var a = i * Math.PI / 2 / steps;
            pts.Add(new Point(w - radius + radius * Math.Cos(a), h - radius + radius * Math.Sin(a)));
        }

        // 左下角：π/2 → π
        for (var i = 0; i <= steps; i++)
        {
            var a = Math.PI / 2 + i * Math.PI / 2 / steps;
            pts.Add(new Point(radius + radius * Math.Cos(a), h - radius + radius * Math.Sin(a)));
        }

        // 左上角：π → 3π/2
        for (var i = 0; i <= steps; i++)
        {
            var a = Math.PI + i * Math.PI / 2 / steps;
            pts.Add(new Point(radius + radius * Math.Cos(a), radius + radius * Math.Sin(a)));
        }

        return pts;
    }
}
