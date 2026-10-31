using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using Source2.Compiler.Maps;

namespace Source2.Compiler.Cubemaps;

/// <summary>
/// The binner's barn light entries for a cube face (CUBEMAPS.md F.1, ledger 193), from the map's light_barn keys:
/// which lights the face gathers and, for each, the 0x640-byte entry <see cref="BarnLightRecords.Record"/> reads.
/// <list type="bullet">
/// <item>Gathered: the light's snapped bounds (<see cref="BarnLightBounds"/>) pass the face frustum
/// (<see cref="AggregateCulling.Outside"/>; measured for lights, the culling function is not identified), the
/// distance fade (18019fe30) is above 0, and 1801aa3f0's footprints of the frustum and of the illumination box pass
/// 1801abf30 (<see cref="LightBinnerItems.Bounds(LightBinnerItems.View, float[], Vector3[], float[])"/>).</item>
/// <item>Ordered by 1801a9e70 (std::sort in 1801cc930): the screen size descending, then the squared distance to the
/// camera, then the colour's luminance, then the view direction against the light's x axis; the last tie (1804a2230
/// on the descriptions) is refused. At most 315 (180923b68's default not read: refused above).</item>
/// <item>The entry: the description at +0x18, the transform at +0x144, the screen size +0x1bc (18004c390 over the
/// snapped box's centre and half diagonal: measured, ledger 193), the fades +0x1b8 / +0x1e0 / +0x1e4, the keys'
/// shadow priority, baked shadow slot (clamped to -1 .. 4), cast shadows and min roughness, the illumination box at
/// +0x340. Measured on ze_hold_em_p's 218 captured entries, not read: scale 1 at +0x1c0, 0 at +0x1c4, 0xffff at
/// +0x1fc and the flags 0x110 at +0x214 (bit 8: the illumination box).</item>
/// <item>Not ported, left 0: the shadow atlas tile rect +0x174 .. +0x183 and +0x1dc (1801a2720, from 1801a1030's
/// time-smoothed size). With the rect 0 the record clears its shadow terms (1801b14b0).</item>
/// </list>
/// Refused, when such a light passes a face's box test: a light style, no illumination box. Not gathered at all: a
/// light whose description is refused (a cookie), and every other light class (CUBEMAPS.md F.1).
/// </summary>
public static class BarnLightEntries
{
    /// <summary>A light_barn as the binner reads it.</summary>
    public sealed record Light(LightPrecompute.KeyReader Key, byte[] Description, float[] Transform, Vector3 Min, Vector3 Max,
                               Vector4 BoxRotation, Vector3 BoxPosition, Vector3 BoxHalf, string? Refused = null);

    /// <summary>Every light_barn of the lumps, in lump order.</summary>
    public static List<Light> Lights(IEnumerable<(string Class, LightPrecompute.KeyReader Key)> entities)
    {
        var lights = new List<Light>();
        foreach (var (_, key) in entities.Where(e => e.Class == "light_barn"))
        {
            var refused = key("precomputedfieldsvalid") != "1" ? "a light_barn with no illumination box (precomputedfieldsvalid): the entry's +0x214 is not read"
                : key("lightcookie") is { Length: > 0 } || key("light_style") is { Length: > 0 } ? "a light_barn with a cookie or a light style is not ported" : null;
            byte[] description;
            try
            {
                description = BarnLightDescriptions.Description(key);
            }
            catch (NotSupportedException)
            {
                // A light the description port refuses (a cookie): not gathered, like light_omni2 (CUBEMAPS.md F.1; no
                // such light reaches a cube face in s2c_l5probe's capture).
                continue;
            }
            var transform = BarnLightDescriptions.Transform(key);
            var (min, max) = BarnLightBounds.Frustum(description, transform);
            (min, max) = BarnLightBounds.Snap(min, max);
            lights.Add(refused != null ? new Light(key, description, transform, min, max, default, default, default, refused)
                : new Light(key, description, transform, min, max, CubemapFaceViews.AngleQuaternion(V(key("precomputedobbangles"))),
                    V(key("precomputedobborigin")), V(key("precomputedobbextent"))));
        }
        return lights;
    }

    /// <summary>A gathered light: its entry and its two binner footprints (1801aa3f0's +0x370 and +0x474).</summary>
    public sealed record Gathered(byte[] Entry, LightBinnerItems.Footprint Frustum, LightBinnerItems.Footprint Box);

    /// <summary>The face's entries in the binner's order (CullItems, g_BarnLights).</summary>
    public static List<Gathered> Face(IReadOnlyList<Light> lights, CubemapFaceViews.View view, LightBinnerItems.View bin)
    {
        var frustum = AggregateCulling.FaceFrustum(view);
        var forward = new Vector3(view.CameraToWorld[0], view.CameraToWorld[4], view.CameraToWorld[8]);
        var kept = new List<(Gathered Light, float D, float Distance, float Luminance, float Axis)>();
        foreach (var light in lights)
        {
            if (AggregateCulling.Outside(frustum, light.Min, light.Max))
                continue;
            if (light.Refused != null)
                throw new NotSupportedException(light.Refused);
            var d = Size(light, view.Origin);
            var key = light.Key;
            var fade = Fade(F(key("fade_size_start")), F(key("fade_size_end")), d);
            if (fade <= 0f)
                continue;
            var (planes, corners) = BarnLightBounds.FrustumPlanes(light.Description);
            if (LightBinnerItems.Bounds(bin, planes, corners, light.Transform) is not { } frustumPrint)
                continue;
            if (LightBinnerItems.Bounds(bin, -light.BoxHalf, light.BoxHalf, BarnLightBounds.QuaternionMatrix(light.BoxRotation, light.BoxPosition)) is not { } boxPrint)
                continue;
            var e = Entry(light, d, fade, Fade(F(key("shadowfade_size_start")), F(key("shadowfade_size_end")), d));
            var t = light.Transform;
            var (px, py, pz) = (t[3] - view.Origin.X, t[7] - view.Origin.Y, t[11] - view.Origin.Z);
            float E(int o) => BinaryPrimitives.ReadSingleLittleEndian(e.AsSpan(o));
            kept.Add((new Gathered(e, frustumPrint, boxPrint), d, (py * py + px * px) + pz * pz, E(0xd0) * 0.7152f + E(0xcc) * 0.2126f + E(0xd4) * 0.0722f,
                (forward.Z * t[8] + forward.Y * t[4]) + forward.X * t[0]));
        }
        if (kept.Count > 315)
            throw new NotSupportedException("more than 315 barn lights in one face: the trim convar 180923b68 is not read");
        // 1801a9e70 as a strict order; equal keys down to the axis would reach 1804a2230, which is not ported.
        kept.Sort((a, b) => a.D != b.D ? b.D.CompareTo(a.D) : a.Distance != b.Distance ? a.Distance.CompareTo(b.Distance)
            : a.Luminance != b.Luminance ? b.Luminance.CompareTo(a.Luminance) : a.Axis != b.Axis ? a.Axis.CompareTo(b.Axis)
            : throw new NotSupportedException("two barn lights tie down to 1804a2230's description compare, which is not ported"));
        return [.. kept.Select(k => k.Light)];
    }

    /// <summary>The entry's screen size from a camera (measured, ledger 193).</summary>
    public static float Size(Light light, Vector3 camera)
    {
        var (min, max) = (light.Min, light.Max);
        var centre = new Vector3((max.X + min.X) * 0.5f, (max.Y + min.Y) * 0.5f, (max.Z + min.Z) * 0.5f);
        var (dx, dy, dz) = (min.X - max.X, min.Y - max.Y, min.Z - max.Z);
        return AggregateCulling.ScreenSize(camera, 1f, centre, MathF.Sqrt((dx * dx + dy * dy) + dz * dz) * 0.5f);
    }

    /// <summary>18019fe30's fade: 1 - clamp((start - d) k, 0, 1), k = 1 / max(0.0001, start - end).</summary>
    public static float Fade(float start, float end, float d) => 1f - Math.Clamp((start - d) * (1f / MathF.Max(0.0001f, start - end)), 0f, 1f);

    /// <summary>The 0x640-byte entry (the fields 1801b14b0 reads; the rest 0).</summary>
    public static byte[] Entry(Light light, float d, float fade, float shadowFade)
    {
        var e = new byte[BarnLightRecords.EntrySize];
        void Put(int o, float v) => BinaryPrimitives.WriteSingleLittleEndian(e.AsSpan(o), v);
        var key = light.Key;
        light.Description.CopyTo(e.AsSpan(0x18));
        for (var i = 0; i < 12; i++)
            Put(0x144 + 4 * i, light.Transform[i]);
        Put(0x1b8, fade);
        Put(0x1bc, d);
        Put(0x1c0, 1f);
        Put(0x1e0, shadowFade);
        Put(0x1e4, shadowFade);
        BinaryPrimitives.WriteInt16LittleEndian(e.AsSpan(0x1e8), (short)int.Parse(key("shadowpriority") ?? "-1", CultureInfo.InvariantCulture));
        e[0x1ea] = (byte)(sbyte)Math.Clamp(int.Parse(key("bakedshadowindex") ?? "-1", CultureInfo.InvariantCulture), -1, 4);
        e[0x1f8] = key("castshadows") is "1" ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(0x1fc), 0xffff);
        Put(0x210, F(key("minroughness")));
        BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(0x214), 0x110);
        Put(0x340, light.BoxRotation.X);
        Put(0x344, light.BoxRotation.Y);
        Put(0x348, light.BoxRotation.Z);
        Put(0x34c, light.BoxRotation.W);
        Put(0x350, light.BoxPosition.X);
        Put(0x354, light.BoxPosition.Y);
        Put(0x358, light.BoxPosition.Z);
        Put(0x35c, light.BoxHalf.X);
        Put(0x360, light.BoxHalf.Y);
        Put(0x364, light.BoxHalf.Z);
        return e;
    }

    private static float F(string? t) => float.Parse(t ?? "0", CultureInfo.InvariantCulture);

    private static Vector3 V(string? t) => (t ?? throw new InvalidDataException("a key is missing")).Split(' ', StringSplitOptions.RemoveEmptyEntries) is [var x, var y, var z]
        ? new Vector3(F(x), F(y), F(z)) : throw new InvalidDataException($"not a vector: {t}");
}
