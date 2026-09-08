using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace Source2.Compiler;

/// <summary>
/// Normalizes an SVG so CS2's Panorama SVG renderer accepts it. The engine's
/// parser is far stricter than a browser's, and it differs in two ways that make
/// icons that look fine in a browser render blank in-game:
///
/// <list type="number">
///   <item><b>Path data.</b> It chokes on the minified path data game-icons.net
///   (and most icon CDNs) emit - omitted leading zeros (<c>.5</c>), no separators
///   (<c>11.856.5</c>), elliptical-arc commands, and relative / h / v / s
///   shorthands. The icons CS2 DOES render ship clean, space-separated, absolute
///   <c>M/L/C/Z</c>.</item>
///   <item><b>Fill inheritance.</b> CS2 does NOT inherit <c>fill</c> from a parent
///   <c>&lt;g fill="…"&gt;</c> (or the root <c>&lt;svg&gt;</c>) the way a browser
///   does, and it only reliably draws <c>&lt;path&gt;</c> geometry - not
///   <c>&lt;rect&gt;/&lt;circle&gt;/&lt;polygon&gt;…</c> primitives. So a glyph
///   whose colour lives on the group instead of each path (e.g. our authored
///   zombie glyphs) renders with the default fill (black) → invisible on the dark
///   killfeed, even though every browser inherits the group fill and the picker
///   preview looks right. This was confirmed against the two icons CS2 actually
///   renders (Mapeadores <c>knife_zombie</c>, GFL <c>zombie_walking</c>): both put
///   <c>fill</c> directly on each <c>&lt;path&gt;</c> (attribute or inline
///   <c>style</c>), never on a container.</item>
/// </list>
///
/// <para><see cref="ForPanorama(byte[])"/> lists the passes that follow from
/// this. It runs inside
/// <see cref="ResourceBuilder.BuildPanoramaSvg(byte[], string?)"/>, so every icon
/// compiled here is normalized whether or not the caller thought to ask.</para>
/// </summary>
public static class SvgSanitizer
{
    public static byte[] ForPanorama(byte[] svg)
    {
        if (svg is null || svg.Length == 0)
            return svg ?? [];
        string text;
        try
        { text = Encoding.UTF8.GetString(svg); }
        catch { return svg; }
        if (!text.Contains("<svg", StringComparison.OrdinalIgnoreCase))
            return svg;

        var fill = ResolveDefaultFill(text);     // the colour fill-less paths should take
        text = ConvertPrimitivesToPaths(text);   // rect/circle/ellipse/line/poly* → <path>
        text = RewritePathData(text);            // normalize every d to absolute M/L/C/Z
        text = EnsurePathFills(text, fill);      // every <path> gets an explicit visible fill
        text = ExpandShortHexColors(text);       // #abc → #aabbcc (incl. injected/carried fills)
        text = EnsureWidthHeight(text);          // width/height from viewBox when absent
        return Encoding.UTF8.GetBytes(text);
    }

    /// <summary>Convenience string overload for tooling/tests.</summary>
    public static string ForPanorama(string svg) =>
        Encoding.UTF8.GetString(ForPanorama(Encoding.UTF8.GetBytes(svg)));

    // path data

    private static readonly Regex DAttr = new(@"\bd\s*=\s*(""[^""]*""|'[^']*')", RegexOptions.Compiled);

    private static string RewritePathData(string svg) => DAttr.Replace(svg, m =>
    {
        var quote = m.Value[m.Value.IndexOf('"') >= 0 ? m.Value.IndexOf('"') : m.Value.IndexOf('\'')];
        var raw = m.Groups[1].Value;
        var inner = raw.Substring(1, raw.Length - 2);
        var clean = NormalizePathData(inner);
        return clean is null ? m.Value : $"d={quote}{clean}{quote}";
    });

    /// <summary>Parse via Skia (handles minified / relative / arc / quad / smooth
    /// input) and re-emit as clean absolute <c>M/L/C/Z</c> only. Null on a path
    /// Skia can't parse (left untouched).</summary>
    private static string? NormalizePathData(string d)
    {
        if (string.IsNullOrWhiteSpace(d))
            return null;
        SKPath? path = null;
        try
        {
            path = SKPath.ParseSvgPathData(d);
            if (path is null || path.IsEmpty)
                return null;

            var sb = new StringBuilder(d.Length + 16);
            using var it = path.CreateRawIterator();
            var pts = new SKPoint[4];
            SKPathVerb verb;
            while ((verb = it.Next(pts)) != SKPathVerb.Done)
            {
                switch (verb)
                {
                    case SKPathVerb.Move:
                        sb.Append('M').Append(P(pts[0]));
                        break;
                    case SKPathVerb.Line:
                        sb.Append('L').Append(P(pts[1]));
                        break;
                    case SKPathVerb.Cubic:
                        sb.Append('C').Append(P(pts[1])).Append(' ').Append(P(pts[2])).Append(' ').Append(P(pts[3]));
                        break;
                    case SKPathVerb.Quad:
                        AppendCubicFromQuad(sb, pts[0], pts[1], pts[2]);
                        break;
                    case SKPathVerb.Conic:
                        // Arc / rational-quad → exact-ish cubics: lower to quads first.
                        var quads = SKPath.ConvertConicToQuads(pts[0], pts[1], pts[2], it.ConicWeight(), 2);
                        for (int i = 0; i + 2 < quads.Length; i += 2)
                            AppendCubicFromQuad(sb, quads[i], quads[i + 1], quads[i + 2]);
                        break;
                    case SKPathVerb.Close:
                        sb.Append('Z');
                        break;
                }
                if (verb != SKPathVerb.Close && sb.Length > 0 && sb[^1] != ' ')
                    sb.Append(' ');
            }
            var outStr = sb.ToString().Trim();
            return outStr.Length == 0 ? null : outStr;
        }
        catch { return null; }
        finally { path?.Dispose(); }
    }

    /// <summary>Quadratic (p0,c,p1) → cubic Bézier, control points at 2/3 toward c.</summary>
    private static void AppendCubicFromQuad(StringBuilder sb, SKPoint p0, SKPoint c, SKPoint p1)
    {
        var c1 = new SKPoint(p0.X + 2f / 3f * (c.X - p0.X), p0.Y + 2f / 3f * (c.Y - p0.Y));
        var c2 = new SKPoint(p1.X + 2f / 3f * (c.X - p1.X), p1.Y + 2f / 3f * (c.Y - p1.Y));
        sb.Append('C').Append(P(c1)).Append(' ').Append(P(c2)).Append(' ').Append(P(p1));
    }

    private static string P(SKPoint p) => $"{N(p.X)} {N(p.Y)}";
    private static string N(float v) => MathF.Round(v, 3).ToString("0.###", CultureInfo.InvariantCulture);

    // primitives → paths
    //
    // CS2's Panorama renderer only reliably draws <path>; rect/circle/ellipse/
    // line/polygon/polyline silently don't render. Convert each to an equivalent
    // <path>, carrying over its presentation attributes (fill / style / transform
    // / stroke …) and dropping only the geometry attributes. Rounded rects and
    // round shapes emit arcs, which RewritePathData then lowers to cubics.

    // Match an attr by exact name (not a -suffixed cousin like fill-rule).
    private static string? Attr(string tag, string name)
    {
        var m = Regex.Match(tag, $@"(?<![\w-]){Regex.Escape(name)}\s*=\s*(""[^""]*""|'[^']*')",
            RegexOptions.IgnoreCase);
        if (!m.Success)
            return null;
        var v = m.Groups[1].Value;
        return v.Substring(1, v.Length - 2);
    }

    private static float F(string? s) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0f;

    /// <summary>Re-emit a primitive's non-geometry attributes (fill, style,
    /// transform, stroke, fill-rule, opacity, class …) for the replacement
    /// &lt;path&gt;, dropping the shape-specific ones in <paramref name="drop"/>.</summary>
    private static string CarryAttrs(string tag, HashSet<string> drop)
    {
        var sb = new StringBuilder();
        foreach (Match m in Regex.Matches(tag, @"([\w:-]+)\s*=\s*(""[^""]*""|'[^']*')"))
        {
            var name = m.Groups[1].Value;
            if (drop.Contains(name.ToLowerInvariant()))
                continue;
            sb.Append(' ').Append(name).Append('=').Append(m.Groups[2].Value);
        }
        return sb.ToString();
    }

    private static readonly Regex RectTag = new(@"<rect\b[^>]*?/?>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CircleTag = new(@"<circle\b[^>]*?/?>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex EllipseTag = new(@"<ellipse\b[^>]*?/?>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LineTag = new(@"<line\b[^>]*?/?>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PolygonTag = new(@"<polygon\b[^>]*?/?>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PolylineTag = new(@"<polyline\b[^>]*?/?>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static string ConvertPrimitivesToPaths(string svg)
    {
        svg = RectTag.Replace(svg, m =>
        {
            var t = m.Value;
            float x = F(Attr(t, "x")), y = F(Attr(t, "y")), w = F(Attr(t, "width")), h = F(Attr(t, "height"));
            if (w <= 0 || h <= 0)
                return "";   // a zero-area rect draws nothing
            var rxS = Attr(t, "rx");
            var ryS = Attr(t, "ry");
            float rx = F(rxS), ry = F(ryS);
            if (rxS is null && ryS is not null)
                rx = ry;
            if (ryS is null && rxS is not null)
                ry = rx;
            rx = MathF.Min(rx, w / 2);
            ry = MathF.Min(ry, h / 2);
            string d;
            if (rx > 0 && ry > 0)
                d = $"M{C(x + rx)} {C(y)} L{C(x + w - rx)} {C(y)} A{C(rx)} {C(ry)} 0 0 1 {C(x + w)} {C(y + ry)} " +
                    $"L{C(x + w)} {C(y + h - ry)} A{C(rx)} {C(ry)} 0 0 1 {C(x + w - rx)} {C(y + h)} " +
                    $"L{C(x + rx)} {C(y + h)} A{C(rx)} {C(ry)} 0 0 1 {C(x)} {C(y + h - ry)} " +
                    $"L{C(x)} {C(y + ry)} A{C(rx)} {C(ry)} 0 0 1 {C(x + rx)} {C(y)} Z";
            else
                d = $"M{C(x)} {C(y)} L{C(x + w)} {C(y)} L{C(x + w)} {C(y + h)} L{C(x)} {C(y + h)} Z";
            return $"<path{CarryAttrs(t, RectGeom)} d=\"{d}\"/>";
        });

        svg = CircleTag.Replace(svg, m =>
        {
            var t = m.Value;
            float cx = F(Attr(t, "cx")), cy = F(Attr(t, "cy")), r = F(Attr(t, "r"));
            if (r <= 0)
                return "";
            var d = $"M{C(cx - r)} {C(cy)} A{C(r)} {C(r)} 0 1 0 {C(cx + r)} {C(cy)} A{C(r)} {C(r)} 0 1 0 {C(cx - r)} {C(cy)} Z";
            return $"<path{CarryAttrs(t, CircleGeom)} d=\"{d}\"/>";
        });

        svg = EllipseTag.Replace(svg, m =>
        {
            var t = m.Value;
            float cx = F(Attr(t, "cx")), cy = F(Attr(t, "cy")), rx = F(Attr(t, "rx")), ry = F(Attr(t, "ry"));
            if (rx <= 0 || ry <= 0)
                return "";
            var d = $"M{C(cx - rx)} {C(cy)} A{C(rx)} {C(ry)} 0 1 0 {C(cx + rx)} {C(cy)} A{C(rx)} {C(ry)} 0 1 0 {C(cx - rx)} {C(cy)} Z";
            return $"<path{CarryAttrs(t, EllipseGeom)} d=\"{d}\"/>";
        });

        svg = LineTag.Replace(svg, m =>
        {
            var t = m.Value;
            var d = $"M{C(F(Attr(t, "x1")))} {C(F(Attr(t, "y1")))} L{C(F(Attr(t, "x2")))} {C(F(Attr(t, "y2")))}";
            return $"<path{CarryAttrs(t, LineGeom)} d=\"{d}\"/>";
        });

        svg = PolygonTag.Replace(svg, m => PolyToPath(m.Value, closed: true));
        svg = PolylineTag.Replace(svg, m => PolyToPath(m.Value, closed: false));
        return svg;
    }

    private static string PolyToPath(string tag, bool closed)
    {
        var pts = Attr(tag, "points") ?? "";
        var nums = Regex.Matches(pts, @"[-+]?[0-9]*\.?[0-9]+(?:[eE][-+]?[0-9]+)?")
                        .Select(m => F(m.Value)).ToArray();
        if (nums.Length < 4)
            return "";   // need at least two points
        var sb = new StringBuilder();
        sb.Append('M').Append(C(nums[0])).Append(' ').Append(C(nums[1]));
        for (int i = 2; i + 1 < nums.Length; i += 2)
            sb.Append(" L").Append(C(nums[i])).Append(' ').Append(C(nums[i + 1]));
        if (closed)
            sb.Append(" Z");
        var drop = closed ? PolygonGeom : PolylineGeom;
        return $"<path{CarryAttrs(tag, drop)} d=\"{sb}\"/>";
    }

    private static string C(float v) => MathF.Round(v, 3).ToString("0.###", CultureInfo.InvariantCulture);

    private static readonly HashSet<string> RectGeom = new(StringComparer.OrdinalIgnoreCase) { "x", "y", "width", "height", "rx", "ry" };
    private static readonly HashSet<string> CircleGeom = new(StringComparer.OrdinalIgnoreCase) { "cx", "cy", "r" };
    private static readonly HashSet<string> EllipseGeom = new(StringComparer.OrdinalIgnoreCase) { "cx", "cy", "rx", "ry" };
    private static readonly HashSet<string> LineGeom = new(StringComparer.OrdinalIgnoreCase) { "x1", "y1", "x2", "y2" };
    private static readonly HashSet<string> PolygonGeom = new(StringComparer.OrdinalIgnoreCase) { "points" };
    private static readonly HashSet<string> PolylineGeom = new(StringComparer.OrdinalIgnoreCase) { "points" };

    // fill resolution

    // A plain `fill="…"` on an opening tag (not fill-rule / fill-opacity).
    private static readonly Regex FillAttr = new(@"(?<![\w-])fill\s*=\s*[""']([^""']*)[""']", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // A `fill:` inside a style="…" value.
    private static readonly Regex FillInStyle = new(@"fill\s*:\s*([^;""']+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ContainerTag = new(@"<(?:svg|g)\b[^>]*>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PathOpenTag = new(@"<path\b[^>]*?(/?)>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>The colour a fill-less path should take: the (innermost) explicit
    /// <c>fill</c> declared on a <c>&lt;g&gt;</c>/<c>&lt;svg&gt;</c> container - the
    /// value a browser would inherit - else white. Killfeed equipment icons are
    /// white silhouettes, so white is the right "nothing specified" default and it
    /// guarantees the glyph is visible on the dark HUD.</summary>
    private static string ResolveDefaultFill(string svg)
    {
        string? found = null;
        foreach (Match c in ContainerTag.Matches(svg))
        {
            var f = FillAttr.Match(c.Value);
            if (f.Success && !IsNoneOrCurrent(f.Groups[1].Value))
                found = f.Groups[1].Value.Trim();        // keep the last (innermost) container fill
        }
        return found ?? "#ffffff";
    }

    private static bool IsNoneOrCurrent(string v)
    {
        v = v.Trim();
        return v.Length == 0
            || v.Equals("none", StringComparison.OrdinalIgnoreCase)
            || v.Equals("currentColor", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Give every <c>&lt;path&gt;</c> an explicit, visible fill. A path
    /// that already declares a real fill (attribute or inline <c>style</c>) is left
    /// alone; one with no fill, or <c>fill="none"</c>/<c>currentColor</c>, gets
    /// <paramref name="defaultFill"/> injected as a <c>fill</c> attribute. This is
    /// the fix for icons whose colour lived on a parent group (CS2 doesn't inherit
    /// it) - without it those paths render black/invisible in-game.</summary>
    private static string EnsurePathFills(string svg, string defaultFill) => PathOpenTag.Replace(svg, m =>
    {
        var tag = m.Value;

        // Inline style fill takes precedence in SVG; honour a real one.
        var styleM = Regex.Match(tag, @"style\s*=\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase);
        if (styleM.Success)
        {
            var fm = FillInStyle.Match(styleM.Groups[1].Value);
            if (fm.Success && !IsNoneOrCurrent(fm.Groups[1].Value))
                return tag;   // real style fill - leave it
        }

        var fa = FillAttr.Match(tag);
        if (fa.Success && !IsNoneOrCurrent(fa.Groups[1].Value))
            return tag;        // real fill attr - leave it

        // No usable fill anywhere on this path → inject one. Replace an existing
        // fill="none"/"currentColor" attribute in place; otherwise add a new attr
        // just after `<path`.
        if (fa.Success)
            return tag.Remove(fa.Index, fa.Length).Insert(fa.Index, $"fill=\"{defaultFill}\"");
        return tag.Insert("<path".Length, $" fill=\"{defaultFill}\"");
    });

    // wrapper normalization

    // #abc -> #aabbcc (CS2's colour parser wants full 6-digit hex). Only matches
    // a 3-hex value bounded by a quote/space/paren so we never touch 6-digit ones.
    private static readonly Regex ShortHex = new(@"#([0-9a-fA-F])([0-9a-fA-F])([0-9a-fA-F])(?=[""'\s;)])", RegexOptions.Compiled);
    private static string ExpandShortHexColors(string svg) =>
        ShortHex.Replace(svg, m => $"#{m.Groups[1].Value}{m.Groups[1].Value}{m.Groups[2].Value}{m.Groups[2].Value}{m.Groups[3].Value}{m.Groups[3].Value}");

    private static readonly Regex SvgTag = new(@"<svg\b[^>]*>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ViewBox = new(@"viewBox\s*=\s*[""']\s*[\d.+-]+[\s,]+[\d.+-]+[\s,]+([\d.+-]+)[\s,]+([\d.+-]+)\s*[""']", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex HasWidth = new(@"\bwidth\s*=", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Add explicit width/height (from the viewBox) to the root &lt;svg&gt;
    /// when absent - the icons CS2 renders all carry them; some viewBox-only icons
    /// size to nothing in Panorama.</summary>
    private static string EnsureWidthHeight(string svg)
    {
        var tag = SvgTag.Match(svg);
        if (!tag.Success || HasWidth.IsMatch(tag.Value))
            return svg;
        var vb = ViewBox.Match(tag.Value);
        if (!vb.Success)
            return svg;
        var w = vb.Groups[1].Value;
        var h = vb.Groups[2].Value;
        var newTag = tag.Value.Insert(4, $" width=\"{w}\" height=\"{h}\"");
        return svg.Remove(tag.Index, tag.Length).Insert(tag.Index, newTag);
    }
}
