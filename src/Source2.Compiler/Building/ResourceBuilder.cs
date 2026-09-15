using System.Diagnostics;
using System.Text.Json;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using SkiaSharp;
using ValvePak;
using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;
using ValveResourceFormat.Utils;

namespace Source2.Compiler;

/// <summary>
/// Builds compiled Source 2 resources - <c>.vtex_c</c>, <c>.vmat_c</c>,
/// <c>.vsnd_c</c>, <c>.vsvg_c</c>, <c>.vmdl_c</c> - through
/// ValveResourceFormat's serializer, with no game tooling involved.
///
/// <para>Every type here authors its own container. A template of the same type
/// is accepted but never required, and supplies only the header frame; anything
/// a caller passes is mutated and must not be reused.</para>
/// </summary>
public static class ResourceBuilder
{
    // Public descriptor types

    /// <summary>All data needed to emit a .vmat_c.</summary>
    public sealed class MaterialDef
    {
        /// <summary>Virtual path of this material, e.g. <c>materials/player/zombie.vmat</c>.</summary>
        public string Name { get; set; } = "";

        /// <summary>Shader name, e.g. <c>complex.vfx</c>.</summary>
        public string Shader { get; set; } = "";

        /// <summary>Texture parameters: shader param name → .vtex path (no _c suffix).</summary>
        public Dictionary<string, string> TextureParams { get; } = [];

        /// <summary>Integer shader parameters (F_* flags etc.).</summary>
        public Dictionary<string, long> IntParams { get; } = [];

        /// <summary>Float shader parameters.</summary>
        public Dictionary<string, float> FloatParams { get; } = [];

        /// <summary>Vector4 shader parameters.</summary>
        public Dictionary<string, System.Numerics.Vector4> VectorParams { get; } = [];

        /// <summary>Integer material attributes.</summary>
        public Dictionary<string, long> IntAttributes { get; } = [];

        /// <summary>String material attributes.</summary>
        public Dictionary<string, string> StringAttributes { get; } = [];
    }

    /// <summary>
    /// All data needed to emit a reference-based .vmdl_c
    /// (one whose meshes live in external .vmesh_c files).
    /// </summary>
    public sealed class ModelDef
    {
        /// <summary>Model name stored inside the compiled file, e.g. <c>models/player/zombie</c>.</summary>
        public string Name { get; set; } = "";

        /// <summary>
        /// External mesh references.
        /// Each entry is (vmesh path without _c, LoD mask).
        /// Use <c>LodMask = -1</c> (0xFFFFFFFFFFFFFFFF) to include the mesh at all LoD levels.
        /// </summary>
        public List<(string Path, long LodMask)> RefMeshes { get; } = [];

        /// <summary>
        /// Material groups, ordered.  The first group named "default" is used when no
        /// skin override is active.  Each group lists .vmat paths (without _c suffix).
        /// </summary>
        public List<(string GroupName, string[] Materials)> MaterialGroups { get; } = [];

        /// <summary>Optional paths to external animation group files (.vagrp, no _c).</summary>
        public List<string> AnimationGroups { get; } = [];

        /// <summary>Optional paths to physics aggregate files (.vphys, no _c).</summary>
        public List<string> PhysicsNames { get; } = [];
    }

    /// <summary>GPU block-compression format for texture output.</summary>
    public enum TextureCompression
    {
        /// <summary>BC7 RGBA - highest quality, 16 B per 4×4 block. Recommended default for color.</summary>
        BC7,
        /// <summary>BC5 / ATI2N - two-channel (RG), 16 B per block. Normal / roughness-metalness maps.</summary>
        BC5,
        /// <summary>BC4 / ATI1N - single-channel (R), 8 B per block. AO / mask maps.</summary>
        BC4,
        /// <summary>BC3 / DXT5 - RGB + interpolated alpha, 16 B per 4×4 block.</summary>
        BC3,
        /// <summary>BC1 / DXT1 - RGB only (1-bit alpha), 8 B per 4×4 block. Smallest output.</summary>
        BC1,
        /// <summary>No compression - raw BGRA8888. Largest output, instant encoding.</summary>
        None,
    }

    /// <summary>
    /// Which encoder computes BC7 blocks - the pipeline's heaviest CPU cost.
    /// Only affects <see cref="TextureCompression.BC7"/> output.
    /// </summary>
    public enum Bc7EncoderMode
    {
        /// <summary>CPU only - native <c>bc7enc</c>, BCnEncoder.Net fallback.
        /// The production default; safe inside a Docker container with no GPU.</summary>
        Cpu,
        /// <summary>Prefer <see cref="GpuBc7Encoder"/> when it is
        /// available; transparently fall back to the CPU path when it is not.
        /// For the branched-out compositor host that has a GPU.</summary>
        Gpu,
    }

    /// <summary>
    /// All data needed to emit a .vtex_c from raw image bytes.
    /// Supports any format decodable by SkiaSharp (PNG, JPG, TGA, BMP, WebP, …).
    /// </summary>
    public sealed class TextureDef
    {
        /// <summary>Raw image file bytes (PNG, JPG, TGA, BMP, WebP …).</summary>
        public byte[] ImageBytes { get; set; } = [];

        /// <summary>Decoded RGBA8888 pixels - set this (with <see cref="RawWidth"/>
        /// / <see cref="RawHeight"/>) instead of <see cref="ImageBytes"/> to skip
        /// the PNG encode/decode round-trip when the source is already raw.</summary>
        public byte[]? RawRgba { get; set; }
        public int RawWidth { get; set; }
        public int RawHeight { get; set; }

        /// <summary>BC7 encoder quality. <c>Fast</c> is ~10× quicker than
        /// <c>Balanced</c> with a small quality cost - good for previews/bulk.</summary>
        public CompressionQuality Quality { get; set; } = CompressionQuality.Balanced;

        /// <summary>Content-relative source path recorded in the authored RED2's
        /// input dependency (e.g. <c>"materials/vpkedit/foo_color.png"</c>). Leave
        /// null and the author synthesizes a neutral <c>vpkeditor/</c> name - what
        /// matters is that it is OURS, not a donor's. See
        /// <see cref="Source2ContainerAuthor.BuildTextureEditInfo"/>.</summary>
        public string? SourceName { get; set; }

        /// <summary>
        /// When true (default) a full mip chain is generated via SkiaSharp bilinear
        /// downsampling down to 1×1.  Set false to emit a single mip level.
        /// </summary>
        public bool GenerateMipmaps { get; set; } = true;

        /// <summary>
        /// Block-compression format.  Defaults to <see cref="TextureCompression.BC7"/>
        /// (highest quality, 4× smaller than BGRA8888).
        /// </summary>
        public TextureCompression Compression { get; set; } = TextureCompression.BC7;

        /// <summary>
        /// Which encoder computes BC7 blocks. Defaults to
        /// <see cref="Bc7EncoderMode.Cpu"/>; only consulted when
        /// <see cref="Compression"/> is <see cref="TextureCompression.BC7"/>.
        /// Hosts wire this from their own configuration. <see cref="Bc7EncoderMode.Gpu"/>
        /// falls back to the CPU encoder until <see cref="GpuBc7Encoder"/> is built.
        /// </summary>
        public Bc7EncoderMode Bc7Mode { get; set; } = Bc7EncoderMode.Cpu;

        /// <summary>
        /// Longest-side cap for the source image, in pixels. A source larger
        /// than this is downscaled before mip generation and block compression.
        /// Defaults to no cap.
        /// </summary>
        public int MaxDimension { get; set; } = int.MaxValue;

        /// <summary>
        /// Raw <c>SHEET</c> extra-data payload (sprite-sheet sequences and frame
        /// UVs) to embed, or null for a plain texture. Build one with
        /// <see cref="SpriteSheet.Write"/>, usually from an <c>.mks</c> script via
        /// <see cref="MksSource"/>. Setting it also adds the
        /// <c>GenerateSheetData</c> compiler dependency stock sheets carry.
        /// </summary>
        public byte[]? SheetData { get; set; }

        /// <summary>
        /// Texture header flags. Stock sprite sheets ship
        /// <see cref="VTexFlags.NO_LOD"/> (0x8), because a mip chain would blend
        /// neighbouring frames of the atlas into each other. Defaults to none.
        /// </summary>
        public ushort Flags { get; set; }

        /// <summary>
        /// Special dependencies describing how these pixels are ENCODED, e.g.
        /// <c>("Texture Compiler Version Mip HemiOctAnisoRoughness",
        /// "CompileTexture", 3)</c> for a packed normal map. They tell consumers
        /// how to decode the texture, so dropping one on a normal map makes every
        /// reader treat it as plain RGB. A template's own set rides along and this
        /// adds to it.
        /// </summary>
        public List<Source2ContainerAuthor.SpecialDep> EncodingSemantics { get; } = [];
    }

    // VTexExtraData entry types, as the DATA block's extra-data table stores them.
    private const uint VTexExtraFallbackBits = 1;
    private const uint VTexExtraSheet = 2;
    private const uint VTexExtraMetadata = 3;
    private const uint VTexExtraCompressedMipSize = 4;

    /// <summary>Texture header flag bits. Only the ones this compiler emits.</summary>
    public static class VTexFlags
    {
        /// <summary>0x8 - no mip chain is used. What stock sprite sheets set.</summary>
        public const ushort NO_LOD = 0x8;
    }

    /// <summary>Parse compiled bytes into a <see cref="Resource"/> the caller owns.</summary>
    private static Resource ReadResource(byte[] bytes)
    {
        var res = new Resource();
        try { res.Read(new MemoryStream(bytes, writable: false)); }
        catch { res.Dispose(); throw; }
        return res;
    }

    // Public build API

    /// <summary>
    /// The BC7 encoder <see cref="BuildTexture(TextureDef)"/> will <i>actually</i> use for a
    /// requested <paramref name="mode"/>: <see cref="Bc7EncoderMode.Gpu"/> only
    /// when <see cref="GpuBc7Encoder"/> reports available, otherwise
    /// <see cref="Bc7EncoderMode.Cpu"/> (the Gpu mode falls back to CPU).
    /// </summary>
    public static Bc7EncoderMode EffectiveBc7Encoder(Bc7EncoderMode mode)
        => mode == Bc7EncoderMode.Gpu && GpuBc7Encoder.Available
            ? Bc7EncoderMode.Gpu
            : Bc7EncoderMode.Cpu;

    /// <summary>
    /// Cache tag for BC7 output under <paramref name="mode"/>, for a host that
    /// caches compiled textures. It names the encoder that actually produced the
    /// bytes, so an entry can never be served to a different encoder:
    /// <list type="bullet">
    ///   <item><c>"bc7gpu"</c> - the GPU encoder.</item>
    ///   <item><c>"bc7cpunative"</c> - the native <c>bc7enc</c> lib (production default).</item>
    ///   <item><c>"bc7cpubcn"</c> - the managed <c>BCnEncoder.Net</c> fallback (no native lib).</item>
    /// </list>
    /// The two CPU encoders emit different (both valid) BC7 streams for the same
    /// pixels, so they must tag distinctly. They once shared <c>"bc7cpu"</c>, and
    /// a texture baked by one was served to the other. Keep tags to 16
    /// alphanumeric characters or fewer.
    /// </summary>
    public static string Bc7CacheTag(Bc7EncoderMode mode)
        => EffectiveBc7Encoder(mode) == Bc7EncoderMode.Gpu ? "bc7gpu"
           : Bc7Native.Available ? "bc7cpunative" : "bc7cpubcn";

    /// <summary>
    /// Produce a <c>.vmat_c</c> binary from <paramref name="def"/>,
    /// using <paramref name="templateBytes"/> (any valid .vmat_c) as the
    /// structural template for the resource header and KV3 format.
    /// </summary>
    public static byte[] BuildMaterial(byte[] templateBytes, MaterialDef def)
    {
        using var template = ReadResource(templateBytes);
        ApplyMaterial(template, def);
        return Serialize(template);
    }

    /// <summary>
    /// Produce a <c>.vmat_c</c> with no donor file: the container is authored by
    /// <see cref="MaterialAuthor.NewContainer"/> and filled the same way. The
    /// caller states the shader's vertex input signature, which is the one part
    /// of a material this compiler cannot derive - see
    /// <see cref="Source2ContainerAuthor.MaterialAuthoring"/>.
    /// </summary>
    public static byte[] BuildMaterial(MaterialDef def, Resource container)
    {
        ArgumentNullException.ThrowIfNull(container);
        ApplyMaterial(container, def);
        return Serialize(container);
    }

    /// <summary>
    /// Produce a <c>.vtex_c</c> with no donor file. The image is decoded, padded
    /// to power-of-two, mipped and block-compressed per
    /// <see cref="TextureDef.Compression"/>, and the stock extra-data set
    /// (FALLBACK_BITS, METADATA, COMPRESSED_MIP_SIZE) is emitted, which is what
    /// CS2's texture streamer expects.
    /// </summary>
    public static byte[] BuildTexture(TextureDef def) => BuildTexture(null, def);

    /// <param name="templateBytes">
    /// Any valid <c>.vtex_c</c>, used for the container frame only, or null to
    /// author the container outright.
    /// </param>
    /// <param name="def">What to compile: the pixels, the format, and the flags.</param>
    public static byte[] BuildTexture(byte[]? templateBytes, TextureDef def)
    {
        if (def.ImageBytes is not { Length: > 0 } && def.RawRgba is not { Length: > 0 })
            throw new ArgumentException("ImageBytes or RawRgba must be set.", nameof(def));

        // RawRgba states its dimensions separately from the buffer, so the two
        // can disagree - and the copy into the bitmap would not say so. A short
        // buffer fills the tail with transparent black, which ships as a texture
        // that is simply wrong from some row down, with nothing raised anywhere.
        if (def.RawRgba is { Length: > 0 })
        {
            if (def.RawWidth <= 0 || def.RawHeight <= 0)
                throw new ArgumentException(
                    $"RawRgba needs positive dimensions (got {def.RawWidth}x{def.RawHeight}).", nameof(def));
            var expected = (long)def.RawWidth * def.RawHeight * 4;
            if (def.RawRgba.Length != expected)
                throw new ArgumentException(
                    $"RawRgba is {def.RawRgba.Length} bytes but {def.RawWidth}x{def.RawHeight} RGBA needs {expected}.",
                    nameof(def));
        }

        var highQuality = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);

        // Decode the source image, downscaled to def.MaxDimension when it
        // exceeds the cap - see DecodeCapped (a >2048² skin composite is an
        // upscale of ≤2048² source art, so it just burns ~4× the BC7 encode).
        using var decoded = DecodeCapped(def, highQuality);

        int actualW = decoded.Width;
        int actualH = decoded.Height;
        int pow2W = NextPow2(actualW);
        int pow2H = NextPow2(actualH);

        // Re-host as Bgra8888 with UNPREMULTIPLIED alpha. SKBitmap.Decode()
        // premultiplies, which scales RGB by A/255 - fine for a photo, wrong for a
        // packed map whose channels are independent data (roughness, metalness,
        // wear), where an alpha of 0.2 would clobber the other three to a fifth of
        // their value. Targeting an explicit Unpremul SKImageInfo divides it back
        // out. Both branches below must go through one, not just the resize.
        var unpremulInfo = new SKImageInfo(pow2W, pow2H, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var baseBitmap = (pow2W == actualW && pow2H == actualH)
            ? new SKBitmap(unpremulInfo)
            : decoded.Resize(unpremulInfo, highQuality)!;
        if (pow2W == actualW && pow2H == actualH)
        {
            // CopyTo / scalePixels converts color type AND un-premultiplies
            // alpha into baseBitmap's Unpremul image info.
            if (!decoded.ScalePixels(baseBitmap, highQuality))
                throw new InvalidOperationException(
                    "SkiaSharp could not re-host the decoded image as unpremultiplied BGRA8888.");
        }

        bool blockCompressed = def.Compression != TextureCompression.None;

        // Mip chain. Block-compressed formats stop at 4×4 (a single BC block) -
        // sub-4×4 BC mips are malformed and CS2's streamer rejects them.
        // Uncompressed goes all the way to 1×1.
        int fullChain = (int)Math.Log2(Math.Max(pow2W, pow2H)) + 1;
        int numMips = !def.GenerateMipmaps ? 1
            : blockCompressed ? Math.Max(1, fullChain - 2)
            : fullChain;
        numMips = Math.Clamp(numMips, 1, 14);

        var mipData = new byte[numMips][];
        mipData[0] = baseBitmap.Bytes;
        for (var i = 1; i < numMips; i++)
        {
            int mw = Math.Max(pow2W >> i, 1);
            int mh = Math.Max(pow2H >> i, 1);
            using var mip = baseBitmap.Resize(
                new SKImageInfo(mw, mh, SKColorType.Bgra8888, SKAlphaType.Unpremul),
                highQuality)!;
            mipData[i] = mip.Bytes;
        }

        // Block-compress each mip. formatByte is the on-disk VTexFormat value.
        byte formatByte;
        if (blockCompressed)
        {
            var (bcFormat, fmt) = def.Compression switch
            {
                TextureCompression.BC1 => (CompressionFormat.Bc1, (byte)1),   // DXT1
                TextureCompression.BC3 => (CompressionFormat.Bc3, (byte)2),   // DXT5
                TextureCompression.BC5 => (CompressionFormat.Bc5, (byte)21),  // ATI2N
                TextureCompression.BC4 => (CompressionFormat.Bc4, (byte)27),  // ATI1N
                _ => (CompressionFormat.Bc7, (byte)20),  // BC7
            };
            formatByte = fmt;

            // BC7 is the slowest step here, so the fastest available encoder
            // wins: GPU if one is built (none is), else native bc7enc, which is
            // dozens of times faster than the managed BCnEncoder.Net at the same
            // quality, else BCnEncoder.Net.
            bool bc7 = def.Compression == TextureCompression.BC7;
            if (bc7 && EffectiveBc7Encoder(def.Bc7Mode) == Bc7EncoderMode.Gpu)
            {
                for (var i = 0; i < numMips; i++)
                {
                    int mw = Math.Max(pow2W >> i, 1);
                    int mh = Math.Max(pow2H >> i, 1);
                    mipData[i] = GpuBc7Encoder.EncodeBgra(mipData[i], mw, mh);
                }
            }
            else if (bc7 && Bc7Native.Available)
            {
                for (var i = 0; i < numMips; i++)
                {
                    int mw = Math.Max(pow2W >> i, 1);
                    int mh = Math.Max(pow2H >> i, 1);
                    mipData[i] = Bc7Native.EncodeBgra(mipData[i], mw, mh);
                }
            }
            else
            {
                var encoder = new BcEncoder();
                encoder.OutputOptions.Quality = def.Quality;
                encoder.OutputOptions.Format = bcFormat;
                encoder.OutputOptions.GenerateMipMaps = false;
                // Encode each mip's blocks across all cores (mip 0 dominates).
                encoder.Options.IsParallel = true;
                encoder.Options.TaskCount = Environment.ProcessorCount;

                for (var i = 0; i < numMips; i++)
                {
                    int mw = Math.Max(pow2W >> i, 1);
                    int mh = Math.Max(pow2H >> i, 1);
                    mipData[i] = encoder.EncodeToRawBytes(mipData[i], mw, mh, PixelFormat.Bgra32)[0];
                }
            }
        }
        else
        {
            formatByte = 28; // VTexFormat.BGRA8888
        }

        int mipDataSize = mipData.Sum(b => b.Length);

        // FALLBACK_BITS - a 32×32 BC7 thumbnail (64 blocks × 16 B = 1024 B), the
        // always-resident low-res copy CS2's texture streamer binds while the
        // full mip chain streams in. resourcecompiler emits this on every vtex;
        // without it the streamer fails to bind the texture and the material
        // that references it FATAL-errors with "error material".
        byte[] fallbackBits;
        {
            using var thumb = baseBitmap.Resize(
                new SKImageInfo(32, 32, SKColorType.Bgra8888, SKAlphaType.Unpremul), highQuality)!;
            var fbEnc = new BcEncoder();
            fbEnc.OutputOptions.Quality = CompressionQuality.Balanced;
            fbEnc.OutputOptions.Format = CompressionFormat.Bc7;
            fbEnc.OutputOptions.GenerateMipMaps = false;
            fallbackBits = fbEnc.EncodeToRawBytes(thumb.Bytes, 32, 32, PixelFormat.Bgra32)[0];
        }

        // DATA block layout (matches resourcecompiler output)
        //   [0..40)     vtex header
        //   [40..)      extradata entry table - one 12 B entry per payload,
        //               in ascending type order:
        //               FALLBACK_BITS(1), SHEET(2) when present, METADATA(3),
        //               COMPRESSED_MIP_SIZE(4)
        //   then        the payloads, in the same order
        //   then        per-mip size array (numMips × u32)
        //
        // Each entry's "offset" field = payloadPos − offsetFieldPos (VRF applies
        // a −8 on read; the +12-after-entry / −8 cancels to this). ExtraDataOffset
        // in the header is likewise 8 (entry table sits 8 B past the field).
        //
        // The table is built as a list rather than hardcoded because a sprite
        // sheet adds a fourth entry, and every entry after it shifts.
        const int header = 40;
        const int metadataSize = 128;
        const int cmsSize = 12;
        int mipSizesSize = numMips * 4;

        // METADATA payload: reserved u16, then the pre-power-of-2 display size.
        var metadataPayload = new byte[metadataSize];
        BitConverter.GetBytes((ushort)actualW).CopyTo(metadataPayload, 2);
        BitConverter.GetBytes((ushort)actualH).CopyTo(metadataPayload, 4);

        var extras = new List<(uint Type, byte[] Payload)> { (VTexExtraFallbackBits, fallbackBits) };
        if (def.SheetData is { Length: > 0 })
            extras.Add((VTexExtraSheet, def.SheetData));
        extras.Add((VTexExtraMetadata, metadataPayload));
        extras.Add((VTexExtraCompressedMipSize, new byte[cmsSize]));   // filled in below

        int entryTable = extras.Count * 12;

        // Positions are fixed once the payload sizes are known, so the
        // COMPRESSED_MIP_SIZE payload (which points forward at the mip-size
        // array) can be computed before anything is written.
        var positions = new int[extras.Count];
        var cursor = header + entryTable;
        for (var i = 0; i < extras.Count; i++) { positions[i] = cursor; cursor += extras[i].Payload.Length; }
        int mipSizesPos = cursor;
        int dataBlockSize = mipSizesPos + mipSizesSize;

        var cmsIndex = extras.Count - 1;
        int cmsPos = positions[cmsIndex];
        var cms = new byte[cmsSize];
        BitConverter.GetBytes(0u).CopyTo(cms, 0);                                  // 0 = mips stored raw
        BitConverter.GetBytes((uint)(mipSizesPos - (cmsPos + 4))).CopyTo(cms, 4);  // → per-mip size array
        BitConverter.GetBytes((uint)numMips).CopyTo(cms, 8);
        extras[cmsIndex] = (VTexExtraCompressedMipSize, cms);

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        // vtex header (40 B).
        w.Write((ushort)1);              // Version
        w.Write((ushort)def.Flags);      // Flags (NO_LOD etc.)
        w.Write(1f);
        w.Write(1f);
        w.Write(1f);
        w.Write(1f); // Reflectivity
        w.Write((ushort)pow2W);          // Width
        w.Write((ushort)pow2H);          // Height
        w.Write((ushort)1);              // Depth
        w.Write(formatByte);             // Format
        w.Write((byte)numMips);          // NumMipLevels
        w.Write(0u);                     // Picmip0Res
        w.Write(8u);                     // ExtraDataOffset
        w.Write((uint)extras.Count);     // ExtraDataCount

        // Entry table - { u32 type, u32 offsetToPayload, u32 size }.
        for (var i = 0; i < extras.Count; i++)
        {
            var offsetFieldPos = header + i * 12 + 4;
            w.Write(extras[i].Type);
            w.Write((uint)(positions[i] - offsetFieldPos));
            w.Write((uint)extras[i].Payload.Length);
        }

        foreach (var (_, payload) in extras)
            w.Write(payload);

        // Per-mip size array, mip-index order (mip 0 = full res). The pixel
        // payload itself is appended smallest-first after the container.
        for (var i = 0; i < numMips; i++)
            w.Write((uint)mipData[i].Length);

        w.Flush();
        var dataBlockBytes = ms.ToArray();
        Debug.Assert(dataBlockBytes.Length == dataBlockSize,
            $"DATA block size mismatch: wrote {dataBlockBytes.Length}, expected {dataBlockSize}");

        // The container. With a template, its header version comes from that file;
        // without one it is authored outright (Version 1, measured invariant across
        // every .vtex_c in the game - see Source2ContainerAuthor.TextureResourceVersion).
        using var template = templateBytes is { Length: > 0 }
            ? ReadResource(templateBytes)
            : new Resource { Version = Source2ContainerAuthor.TextureResourceVersion };

        // Remove any RERL block the template carried - stock textures don't
        // have one (textures reference textures only as runtime samplers,
        // not as resource imports).
        for (var i = template.Blocks.Count - 1; i >= 0; i--)
        {
            if (template.Blocks[i].Type == BlockType.RERL)
                template.Blocks.RemoveAt(i);
        }

        // Author the RED2 rather than inheriting a template's, which would name
        // that file's source path and CRC. Its SPECIAL dependencies do ride along
        // untouched: they say how the pixels are encoded, not who wrote them.
        // See Source2ContainerAuthor.BuildTextureEditInfo.
        var red2Idx = template.Blocks.FindIndex(b => b.Type == BlockType.RED2);
        // BuildTextureEditInfo treats this list as "the template's own set, used
        // instead of the generic fallback". So when there is no template it has to
        // be seeded with the two dependencies every stock texture carries, or
        // adding a single caller-stated one would silently displace them.
        var templateDeps = (template.EditInfo?.SpecialDependencies ?? [])
            .Select(d => new Source2ContainerAuthor.SpecialDep(
                d.String, d.CompilerIdentifier, (int)d.Fingerprint, (int)d.UserData))
            .ToList();
        if (templateDeps.Count == 0)
            templateDeps.AddRange(Source2ContainerAuthor.TextureBaseDeps);

        templateDeps.AddRange(def.EncodingSemantics);

        // A sheet texture states that it has one. Measured on stock: every
        // .vtex_c carrying a SHEET block also carries this special dependency,
        // and no plain texture does.
        if (def.SheetData is { Length: > 0 })
            templateDeps.Add(Source2ContainerAuthor.GenerateSheetDataDep);

        templateDeps = templateDeps.Distinct().ToList();
        var editInfo = Source2ContainerAuthor.BuildTextureEditInfo(
            def.SourceName,
            def.ImageBytes is { Length: > 0 } ? def.ImageBytes : def.RawRgba ?? [],
            templateDeps);
        var red2Block = AuthoredKv3.Block(editInfo, KV3IDLookup.Get("generic"), BlockType.RED2, template);
        if (red2Idx >= 0)
            template.Blocks[red2Idx] = red2Block;
        else
            template.Blocks.Insert(0, red2Block);

        var dataIdx = template.Blocks.FindIndex(b => b.Type == BlockType.DATA);
        var rawBlock = new RawDataBlock(dataBlockBytes) { Resource = template };
        if (dataIdx >= 0)
            template.Blocks[dataIdx] = rawBlock;
        else
            template.Blocks.Add(rawBlock);

        // Serialize the resource container (header + blocks + DATA-with-just-
        // vtex-header). file_size in the result is exactly the bytes up to
        // here - Resource.Serialize patches it to `end - start`.
        var resourceBytes = Serialize(template);

        // Now append mip pixel data. The engine reads mip count + dimensions
        // + format from the vtex header and computes the mip-chain length to
        // read from this position onward.
        var totalBytes = resourceBytes.Length + mipDataSize;
        var output = new byte[totalBytes];
        Buffer.BlockCopy(resourceBytes, 0, output, 0, resourceBytes.Length);

        var offset = resourceBytes.Length;
        // Smallest-first ordering matches stock encoding: VRF's Texture
        // reader walks from the highest mip level (smallest pixels) down to
        // mip 0 (full resolution) when computing read offsets.
        for (var i = numMips - 1; i >= 0; i--)
        {
            Buffer.BlockCopy(mipData[i], 0, output, offset, mipData[i].Length);
            offset += mipData[i].Length;
        }

        return output;
    }

    /// <summary>
    /// Decode <paramref name="def"/>'s source image (or re-host its raw RGBA),
    /// downscaled with <paramref name="sampling"/> so its longest side never
    /// exceeds <see cref="TextureDef.MaxDimension"/> (default: no cap). The
    /// returned bitmap is owned by the caller.
    /// </summary>
    private static SKBitmap DecodeCapped(TextureDef def, SKSamplingOptions sampling)
    {
        var source = def.RawRgba is { Length: > 0 }
            ? new SKBitmap(new SKImageInfo(def.RawWidth, def.RawHeight,
                SKColorType.Rgba8888, SKAlphaType.Unpremul))
            : Imaging.SafeImageDecode.Decode(def.ImageBytes);
        if (def.RawRgba is { Length: > 0 })
            def.RawRgba.AsSpan().CopyTo(source.GetPixelSpan());

        var maxSide = Math.Max(source.Width, source.Height);
        if (maxSide <= def.MaxDimension)
            return source;   // within the cap - caller owns and disposes it

        // Over the cap: downscale proportionally, then drop the full-res source.
        try
        {
            var scale = (double)def.MaxDimension / maxSide;
            var capW = Math.Max(1, (int)Math.Round(source.Width * scale));
            var capH = Math.Max(1, (int)Math.Round(source.Height * scale));
            return source.Resize(
                       new SKImageInfo(capW, capH, source.ColorType, source.AlphaType), sampling)
                   ?? throw new InvalidOperationException(
                       $"SkiaSharp could not downscale the image to the {def.MaxDimension}px cap.");
        }
        finally
        {
            source.Dispose();
        }
    }

    /// <summary>
    /// Compile a PCM WAV into a <c>.vsnd_c</c> with no donor file: resource
    /// version 4 with the <c>RED2 DATA</c> pair, the shape every stock v4 sound
    /// has. <see cref="ModernizeVsnd"/> lifts it to the v5 <c>RED2 CTRL DATA</c>
    /// layout when that is what the caller ships.
    ///
    /// <para>The v4 format constrains the input: PCM only, 8- or 16-bit, mono or
    /// stereo, and a sample rate that fits a uint16 (every common rate through
    /// 48 kHz does). Violations throw <see cref="InvalidOperationException"/>
    /// with a message written to be shown to whoever supplied the file.</para>
    /// </summary>
    /// <param name="wavBytes">PCM WAV to compile.</param>
    /// <param name="sourceName">Content-relative source path recorded in the authored RED2
    /// (e.g. <c>"sounds/vpkedit/foo.wav"</c>); null records a neutral <c>vpkeditor/</c> name.</param>
    public static byte[] BuildSound(byte[] wavBytes, string? sourceName = null)
        => BuildSound(null, wavBytes, sourceName);

    /// <summary>
    /// As <see cref="BuildSound(byte[], string?)"/>, but taking the container
    /// frame from an existing <c>.vsnd_c</c> instead of authoring it.
    /// </summary>
    /// <param name="templateBytes">
    /// Any valid <c>.vsnd_c</c>, used for the container frame only, or null to
    /// author it.
    /// </param>
    /// <param name="wavBytes">PCM WAV to compile.</param>
    /// <param name="sourceName">Content-relative source path recorded in the authored RED2;
    /// null records a neutral <c>vpkeditor/</c> name.</param>
    public static byte[] BuildSound(byte[]? templateBytes, byte[] wavBytes, string? sourceName = null)
    {
        if (wavBytes is not { Length: > 12 })
            throw new InvalidOperationException("WAV file is empty or too small to be valid.");

        var fmt = ParseWavFormat(wavBytes);
        var pcm = ExtractWavData(wavBytes);

        // Constraints - we surface user-readable messages here so the
        // caller can surface them to a user verbatim.
        if (fmt.AudioFormat != 1)
            throw new InvalidOperationException(
                $"Only uncompressed PCM WAV is supported (got format code {fmt.AudioFormat}). " +
                "Re-export your WAV as PCM 16-bit and try again.");
        if (fmt.BitsPerSample is not 8 and not 16)
            throw new InvalidOperationException(
                $"Unsupported bit depth: {fmt.BitsPerSample}-bit. Use 8-bit or 16-bit PCM.");
        if (fmt.NumChannels is not 1 and not 2)
            throw new InvalidOperationException(
                $"Unsupported channel count: {fmt.NumChannels}. Use mono or stereo.");
        if (fmt.SampleRate is 0 or > 65535)
            throw new InvalidOperationException(
                $"Sample rate {fmt.SampleRate} Hz outside the supported 1–65535 range.");

        // Sample count derives from PCM byte length / (channels × bytes-per-sample).
        var bytesPerSample = fmt.BitsPerSample / 8;
        var frameSize = bytesPerSample * fmt.NumChannels;
        if (frameSize == 0 || pcm.Length % frameSize != 0)
            throw new InvalidOperationException(
                "WAV PCM payload size doesn't align with channel/bit-depth - file is malformed.");
        var sampleCount = (uint)(pcm.Length / frameSize);
        var duration = (float)sampleCount / fmt.SampleRate;

        // Format byte for v4: PCM16=0, PCM8=1.
        byte formatByte = fmt.BitsPerSample == 16 ? (byte)0 : (byte)1;

        // Build the v4 Sound DATA block
        // Layout (matches Sound.Read in VRF):
        //   uint16 SampleRate
        //   byte   AudioFormatV4   (0=PCM16, 1=PCM8, 2=MP3, 3=ADPCM)
        //   byte   Channels
        //   int32  LoopStart       (-1 = no loop)
        //   uint32 SampleCount
        //   float  Duration
        //   uint32 SentenceOffset  (0 = no phoneme/sentence data)
        //   uint32 _b              (reserved/size; 0)
        //   int32  HeaderSize      (0 - only ADPCM uses a header)
        //   uint32 StreamingDataSize
        //   uint32 _seekTableA     (0)
        //   uint32 _seekTableB     (0)
        //   uint32 _morphData      (0; v2+)
        //   int32  LoopEnd         (-1; v4 only)
        //   [PCM payload]
        //
        // 48 bytes of metadata. The PCM is NOT part of it: a vsnd's audio lives past the
        // block section entirely, and embedding it here double-counts the bytes so
        // readers run off the end and the clip plays as static.
        using var ms = new MemoryStream(48);
        using var w = new BinaryWriter(ms);
        w.Write((ushort)fmt.SampleRate);
        w.Write(formatByte);
        w.Write((byte)fmt.NumChannels);
        w.Write(-1);                       // LoopStart
        w.Write(sampleCount);
        w.Write(duration);
        w.Write(0u);                       // SentenceOffset
        w.Write(0u);                       // _b
        w.Write(0);                        // HeaderSize
        w.Write((uint)pcm.Length);         // StreamingDataSize
        w.Write(0u);                       // _seekTableA
        w.Write(0u);                       // _seekTableB
        w.Write(0u);                       // _morphData (v2+)
        w.Write(-1);                       // LoopEnd (v4)
        w.Flush();
        var soundBlockBytes = ms.ToArray();

        // Splice into template Resource container
        using var template = templateBytes is { Length: > 0 }
            ? ReadResource(templateBytes)
            : new Resource { Version = Source2ContainerAuthor.SoundResourceVersion };

        var dataIdx = template.Blocks.FindIndex(b => b.Type == BlockType.DATA);
        var rawBlock = new RawDataBlock(soundBlockBytes) { Resource = template };
        if (dataIdx >= 0)
            template.Blocks[dataIdx] = rawBlock;
        else
            template.Blocks.Add(rawBlock);

        // No external resource refs in a freshly-compiled sound file. The RERL
        // block must be DROPPED, not emptied: the serializer lays an (empty)
        // RERL's data after the DATA payload, so the streaming-PCM trim below
        // would cut it and leave a dangling block-index entry pointing into the
        // PCM. VRF's re-read then interprets PCM bytes as the RERL header -
        // silently fine when the clip starts silent (zeros → size 0), an
        // EndOfStreamException when it starts loud. ModernizeVsnd also drops
        // RERL for the CS2-side reason (an empty RERL makes CS2 reject the
        // override); doing it here keeps the intermediate container readable.
        var rerlIdx = template.Blocks.FindIndex(b => b.Type == BlockType.RERL);
        if (rerlIdx >= 0)
            template.Blocks.RemoveAt(rerlIdx);

        // Author the RED2, for the reason given on the texture path above: a
        // template's special dependencies ride along, its provenance does not.
        var sndRed2Idx = template.Blocks.FindIndex(b => b.Type == BlockType.RED2);
        var sndTemplateDeps = (template.EditInfo?.SpecialDependencies ?? [])
            .Select(d => new Source2ContainerAuthor.SpecialDep(
                d.String, d.CompilerIdentifier, (int)d.Fingerprint, (int)d.UserData))
            .ToList();
        if (sndTemplateDeps.Count == 0)
            sndTemplateDeps.AddRange(Source2ContainerAuthor.SoundDeps);
        var sndEditInfo = Source2ContainerAuthor.BuildSoundEditInfo(sourceName, wavBytes, sndTemplateDeps);
        var sndRed2 = AuthoredKv3.Block(sndEditInfo, KV3IDLookup.Get("generic"), BlockType.RED2, template);
        if (sndRed2Idx >= 0)
            template.Blocks[sndRed2Idx] = sndRed2;
        else
            template.Blocks.Insert(0, sndRed2);

        // Serialize the container, then append the PCM as the streaming-data
        // section. Two subtleties:
        //  • VRF's serializer pads the file a few bytes past the last block, but a
        //    vsnd's streaming data must begin EXACTLY at the DATA block's end
        //    (VRF GetSoundStream reads from Offset+Size; the engine reads from
        //    FileSize) - any gap is read as leading audio garbage. So we trim to
        //    the block-section end and append the PCM there.
        //  • FileSize (header uint32 @0) must equal that block-section end so
        //    FullFileSize = FileSize + StreamingDataSize matches the real length.
        var serialized = Serialize(template);
        int audioStart;
        using (var rr = new Resource())
        {
            rr.Read(new MemoryStream(serialized, writable: false), verifyFileSize: false);
            var snd = rr.DataBlock as ValveResourceFormat.ResourceTypes.Sound
                ?? throw new InvalidDataException("Rebuilt sound has no Sound data block.");
            audioStart = (int)(snd.Offset + snd.Size);
        }
        var result = new byte[audioStart + pcm.Length];
        serialized.AsSpan(0, audioStart).CopyTo(result);
        pcm.CopyTo(result.AsSpan(audioStart));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            result.AsSpan(0, 4), (uint)audioStart);   // FileSize = block-section end
        return result;
    }

    /// <summary>
    /// Parsed WAV "fmt " chunk fields we care about. Mirrors the subset of
    /// the WAVE format header used by <see cref="BuildSound(byte[], string?)"/>.
    /// </summary>
    private readonly record struct WavFormat(
        ushort AudioFormat,
        ushort NumChannels,
        uint SampleRate,
        ushort BitsPerSample);

    /// <summary>
    /// Read a WAV byte array's "fmt " chunk and return the fields we need.
    /// Throws <see cref="InvalidDataException"/> for malformed RIFF/WAVE,
    /// which the caller maps to a 400.
    /// </summary>
    private static WavFormat ParseWavFormat(byte[] wav)
    {
        const uint FmtTag = 0x20746d66; // "fmt " (little-endian)

        if (wav.Length < 12)
            throw new InvalidDataException("Not a valid WAV file - too short for RIFF header.");

        // RIFF header at [0..4) = "RIFF", [8..12) = "WAVE". Skip those.
        var pos = 12;
        while (pos + 8 <= wav.Length)
        {
            var tag = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(pos));
            var size = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(pos + 4));
            var body = pos + 8;

            if (tag == FmtTag)
            {
                if (size < 16 || body + 16 > wav.Length)
                    throw new InvalidDataException("WAV 'fmt ' chunk is truncated.");
                var span = wav.AsSpan(body);
                return new WavFormat(
                    AudioFormat: System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(span[0..]),
                    NumChannels: System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(span[2..]),
                    SampleRate: System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(span[4..]),
                    // skip ByteRate (4) + BlockAlign (2) - derivable
                    BitsPerSample: System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(span[14..]));
            }

            pos = body + ((size + 1) & ~1); // RIFF chunks are word-aligned
        }

        throw new InvalidDataException("WAV file has no 'fmt ' chunk.");
    }

    /// <summary>
    /// Produce a reference-based <c>.vmdl_c</c> binary from <paramref name="def"/>.
    /// All mesh geometry must already exist as compiled <c>.vmesh_c</c> files.
    /// </summary>
    public static byte[] BuildModel(byte[] templateBytes, ModelDef def)
    {
        using var ms = new MemoryStream(templateBytes);
        using var template = new Resource();
        template.Read(ms);
        ApplyModel(template, def);
        return Serialize(template);
    }

    /// <summary>
    /// Rename one <c>m_stringAttributes</c> key on a compiled <c>vmat_c</c>, in place.
    ///
    /// <para>The reason this exists is the econ paint override. CS2 composites a
    /// player's equipped skin ONTO the material the model's draw-call binds, and the
    /// composite procedure finds that weapon's mask/AO/surface set by looking up the
    /// target material's <c>composite_inputs</c> string attribute by NAME (the same
    /// <c>CONTAINER_SOURCE_TYPE_MATERIAL_FROM_TARGET_ATTR</c> step our own
    /// <c>CompositeResolver</c> runs offline). Rename that key and the lookup misses,
    /// so the paint has nothing to composite against and the material renders as we
    /// shipped it, whatever the player has equipped.</para>
    ///
    /// <para>Renaming rather than REMOVING is deliberate: a rename is a pure value
    /// mutation of the kind <c>an in-place param swap</c> already proves
    /// safe, and it keeps the attribute array's size and shape identical. Dropping an
    /// element would be a structural KV3 edit, and a from-scratch attribute array is
    /// the shape CS2's material loader rejects.</para>
    /// </summary>
    /// <returns>The re-serialized vmat_c, or the input unchanged when the key is absent.</returns>
    public static byte[] RenameStringAttribute(byte[] vmatC, string from, string to)
    {
        using var ms = new MemoryStream(vmatC, writable: false);
        using var resource = new Resource();
        resource.Read(ms);

        var attrs = GetDataRoot(resource)["m_stringAttributes"];
        if (attrs is null || !attrs.IsArray)
            return vmatC;

        var hit = false;
        foreach (var sa in attrs.AsArraySpan())
        {
            if (sa["m_name"] is not { } nameObj)
                continue;
            if (!string.Equals((string)nameObj, from, StringComparison.Ordinal))
                continue;
            sa["m_name"] = new KVObject(to) { Flag = nameObj.Flag };
            hit = true;
        }
        return hit ? Serialize(resource) : vmatC;
    }

    /// <summary>
    /// Read every string attribute (<c>m_name</c> → value) from a compiled
    /// <c>vmat_c</c>. Diagnostic counterpart to <see cref="RenameStringAttribute"/>:
    /// lets a caller assert what a shipped material does (and does not) still expose
    /// to CS2's compositor.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ReadStringAttributes(byte[] vmatC)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        using var ms = new MemoryStream(vmatC, writable: false);
        using var resource = new Resource();
        resource.Read(ms);

        var attrs = GetDataRoot(resource)["m_stringAttributes"];
        if (attrs is null || !attrs.IsArray)
            return map;

        foreach (var sa in attrs.AsArraySpan())
        {
            if (sa["m_name"] is not { } nameObj)
                continue;
            var name = (string)nameObj;
            if (string.IsNullOrEmpty(name))
                continue;
            map[name] = sa["m_value"] is { } v ? (string)v ?? "" : "";
        }
        return map;
    }

    /// <summary>
    /// Read every texture param (<c>m_name</c> → referenced <c>.vtex</c> path) from a
    /// compiled <c>vmat_c</c>. Used to discover the stock surface maps a glove vmat
    /// keeps (normal/metalness) so they can be shipped verbatim alongside a
    /// colour-only build, instead of relying on the client resolving a base-game
    /// path whose CS2 content-hash suffix drifts across game updates.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ReadTextureParamPaths(byte[] vmatC)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        using var ms = new MemoryStream(vmatC, writable: false);
        using var resource = new Resource();
        resource.Read(ms);

        var root = GetDataRoot(resource);
        var texParams = root["m_textureParams"];
        if (texParams is null || !texParams.IsArray)
            return map;

        foreach (var tp in texParams.AsArraySpan())
        {
            if (tp["m_name"] is not { } nameObj)
                continue;
            var name = (string)nameObj;
            if (string.IsNullOrEmpty(name))
                continue;
            if (tp["m_pValue"] is { } valObj && (string)valObj is { } path && !string.IsNullOrEmpty(path))
                map[name] = path;
        }
        return map;
    }

    /// <summary>
    /// Read a single <c>m_intParams</c> value (a shader feature flag such as
    /// <c>F_OVERRIDE_NORMAL</c>) from a compiled <c>vmat_c</c>, or null when the
    /// param is absent. Used to gate the override-normal ship: only paints that
    /// set <c>F_OVERRIDE_NORMAL=1</c> substitute their own normal for the
    /// weapon's stock one (see <c>SkinCompositeService.ResolveOverrideNormal</c>).
    /// </summary>
    public static int? ReadIntParam(byte[] vmatC, string name)
    {
        using var ms = new MemoryStream(vmatC, writable: false);
        using var resource = new Resource();
        resource.Read(ms);
        var root = GetDataRoot(resource);
        return VmatParams.ReadParamValue(root, "m_intParams", "m_nValue", name) is { } v
            ? (int)Math.Round(v)
            : null;
    }

    /// <summary>
    /// Construct a numeric <see cref="KVObject"/> of the given KV3 value type.
    /// Integer types take the rounded value; floating types take it verbatim.
    /// Returns null for non-numeric target types.
    /// </summary>
    public static KVObject? BuildNumericTyped(KVValueType type, double value) => type switch
    {
        KVValueType.Int16 => new KVObject((short)Math.Round(value)),
        KVValueType.UInt16 => new KVObject((ushort)Math.Round(value)),
        KVValueType.Int32 => new KVObject((int)Math.Round(value)),
        KVValueType.UInt32 => new KVObject((uint)Math.Round(value)),
        KVValueType.Int64 => new KVObject((long)Math.Round(value)),
        KVValueType.UInt64 => new KVObject((ulong)Math.Round(value)),
        KVValueType.FloatingPoint => new KVObject((float)value),
        KVValueType.FloatingPoint64 => new KVObject(value),
        KVValueType.Boolean => new KVObject(value != 0d),
        _ => null,
    };

    // Template discovery helpers

    /// <summary>
    /// Read the raw bytes of the first .vmat_c entry found in <paramref name="pkg"/>.
    /// Returns null if the VPK contains no materials.
    /// </summary>
    public static byte[]? FindMaterialTemplateBytes(Package pkg)
        => FindFirstEntryBytes(pkg, "vmat_c");

    /// <summary>
    /// Read the raw bytes of the first .vmdl_c entry found in <paramref name="pkg"/>.
    /// Returns null if the VPK contains no models.
    /// </summary>
    public static byte[]? FindModelTemplateBytes(Package pkg)
        => FindFirstEntryBytes(pkg, "vmdl_c");

    /// <summary>
    /// Read the raw bytes of the first .vtex_c entry found in <paramref name="pkg"/>.
    /// Returns null if the VPK contains no textures.
    /// </summary>
    public static byte[]? FindTextureTemplateBytes(Package pkg)
        => FindFirstEntryBytes(pkg, "vtex_c");

    // Material mutation

    private static void ApplyMaterial(Resource template, MaterialDef def)
    {
        var root = GetDataRoot(template);
        root.Clear();

        // Field order matters!
        // CS2's material loader validates the DATA-block field order against a
        // fixed schema (likely NTRO-positional reads under the hood). We
        // hex-diffed stock vmat_c output and a stock-shape resourcecompiler
        // vmat_c - both lay out the top-level keys in this exact sequence.
        // VRF's pre-patch order shipped m_textureParams 3rd and pushed
        // m_dynamicParams to the end; CS2 rejected the result with
        // "FATAL ERROR: attempting to render with error material". Keep this
        // sequence stable; reorder ONLY if a future stock dump disagrees.

        root.Add("m_materialName", new KVObject(def.Name));
        root.Add("m_shaderName", new KVObject(def.Shader));

        root.Add("m_intParams", BuildArray(def.IntParams, (k, v) =>
        {
            var e = new KVObject();
            e.Add("m_name", new KVObject(k));
            e.Add("m_nValue", new KVObject(v));
            return e;
        }));

        root.Add("m_floatParams", BuildArray(def.FloatParams, (k, v) =>
        {
            var e = new KVObject();
            e.Add("m_name", new KVObject(k));
            e.Add("m_flValue", new KVObject((double)v));  // CS2 stores as FloatingPoint64
            return e;
        }));

        root.Add("m_vectorParams", BuildArray(def.VectorParams, (k, v) =>
        {
            var e = new KVObject();
            e.Add("m_name", new KVObject(k));
            e.Add("m_value", Vec4Array(v));
            return e;
        }));

        root.Add("m_textureParams", BuildArray(def.TextureParams, (k, v) =>
        {
            var e = new KVObject();
            e.Add("m_name", new KVObject(k));
            // FLAGLESS RESOURCE REFS ARE FATAL (2026-07-22, the L4D2 zoey crash):
            // stock compiles store texture params as resource:"..."; without the
            // Resource flag CS2 refuses to load the material and any model wearing
            // it FATAL-errors with "attempting to render with error material"
            // (crash dump lists every such vmat as failed-to-load). Same lesson as
            // ModelSwapStage draw-call refs / VmatTranslucencyEditor.
            e.Add("m_pValue", new KVObject(v) { Flag = ValveKeyValue.KVFlag.Resource });
            return e;
        }));

        // m_dynamicParams + m_dynamicTextureParams ALWAYS sit between
        // textureParams and the attribute arrays. Empty for our pipeline
        // (these are only populated when a shader has dynamic expressions
        // baked in), but the engine still validates the keys are present.
        root.Add("m_dynamicParams", KVObject.Array());
        root.Add("m_dynamicTextureParams", KVObject.Array());

        root.Add("m_intAttributes", BuildArray(def.IntAttributes, (k, v) =>
        {
            var e = new KVObject();
            e.Add("m_name", new KVObject(k));
            e.Add("m_nValue", new KVObject(v));
            return e;
        }));

        root.Add("m_floatAttributes", KVObject.Array());
        root.Add("m_vectorAttributes", KVObject.Array());
        root.Add("m_textureAttributes", KVObject.Array());

        root.Add("m_stringAttributes", BuildArray(def.StringAttributes, (k, v) =>
        {
            var e = new KVObject();
            e.Add("m_name", new KVObject(k));
            e.Add("m_value", new KVObject(v));
            return e;
        }));

        root.Add("m_renderAttributesUsed", KVObject.Array());

        // RERL: one entry per referenced texture
        // CS2 vmat_c stores texture RERL paths WITHOUT the _c suffix (e.g. "foo.vtex"
        // not "foo.vtex_c").  The game resolves the vtex_c file internally.
        // TextureParams.Values are already expected as .vtex paths; use as-is.
        ReplaceRerl(template, def.TextureParams.Values.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    // Model mutation

    private static void ApplyModel(Resource template, ModelDef def)
    {
        var root = GetDataRoot(template);
        root.Clear();

        root.Add("m_name", new KVObject(def.Name));

        // External mesh references (string array + parallel LoD mask array).
        var meshArr = KVObject.Array();
        var lodMaskArr = KVObject.Array();
        // NOT flagged, deliberately: every m_refMeshes entry in CS2's pak01 is a
        // Resource-flagged EMPTY string (3384 of them, 2026-08-10 scan) because
        // stock models carry their meshes embedded and resolve them through the
        // RERL, so there is no stock example of a real external mesh PATH here to
        // mirror. Same for m_refAnimGroups / m_refPhysicsData: zero string entries
        // anywhere in pak01 or the base packs. Leave them until a real one turns up.
        foreach (var (path, lodMask) in def.RefMeshes)
        {
            meshArr.Add(new KVObject(path));
            lodMaskArr.Add(new KVObject(lodMask));
        }
        root.Add("m_refMeshes", meshArr);
        root.Add("m_refLODGroupMasks", lodMaskArr);

        // Material groups.
        var groups = KVObject.Array();
        foreach (var (groupName, materials) in def.MaterialGroups)
        {
            var group = new KVObject();
            group.Add("m_name", new KVObject(groupName));

            // Same flagless-resource-ref fatal as m_textureParams above: stock
            // vmdl_c stores every material-group entry as resource:"..." (dumped
            // from glove_bloodhound.vmdl_c and CS2's own pak01). A plain string
            // is a bare string to CS2, the material never resolves, and the model
            // renders with the error material.
            var mats = KVObject.Array();
            foreach (var m in materials)
                mats.Add(new KVObject(m) { Flag = ValveKeyValue.KVFlag.Resource });
            group.Add("m_materials", mats);

            groups.Add(group);
        }
        root.Add("m_materialGroups", groups);

        // Animation group references (string array).
        var animArr = KVObject.Array();
        foreach (var ag in def.AnimationGroups)
            animArr.Add(new KVObject(ag));
        root.Add("m_refAnimGroups", animArr);

        // Physics references (string array).
        var physArr = KVObject.Array();
        foreach (var ph in def.PhysicsNames)
            physArr.Add(new KVObject(ph));
        root.Add("m_refPhysicsData", physArr);

        // m_modelInfo sub-object - required for the skeleton/keyvalue path
        // in Model.cs even when empty.
        var modelInfo = new KVObject();
        modelInfo.Add("m_keyValueText", new KVObject(""));
        root.Add("m_modelInfo", modelInfo);

        // RERL: meshes + materials
        var refs = def.RefMeshes.Select(m => EnsureCompiled(m.Path))
            .Concat(def.MaterialGroups.SelectMany(g => g.Materials).Select(EnsureCompiled))
            .Concat(def.AnimationGroups.Select(EnsureCompiled))
            .Concat(def.PhysicsNames.Select(EnsureCompiled))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        ReplaceRerl(template, refs);
    }

    // Shared helpers

    /// <summary>Get the KV3 root object from a resource's DATA block.
    /// Internal so <c>another caller</c> shares the
    /// same unwrap instead of growing another copy.</summary>
    public static KVObject GetDataRoot(Resource resource)
    {
        var dataBlock = resource.GetBlockByType(BlockType.DATA)
            ?? throw new InvalidOperationException("Template resource has no DATA block.");

        // vmat_c DATA blocks are typed as Material (a KeyValuesOrNTRO subclass) which
        // wraps an inner BinaryKV3 - AsKeyValueCollection() throws for these.
        // vmdl_c DATA blocks may be raw BinaryKV3 or also KeyValuesOrNTRO.
        // KeyValuesOrNTRO.Data IS the mutatable KVObject; Serialize() delegates to the
        // inner BinaryKV3, so mutating Data is reflected in serialization.
        if (dataBlock is KeyValuesOrNTRO kvn)
            return kvn.Data;

        return dataBlock.AsKeyValueCollection()
            ?? throw new InvalidOperationException($"Template DATA block ({dataBlock.GetType().Name}) is not KV3-backed.");
    }

    /// <summary>
    /// Replace the RERL block's entries with <paramref name="paths"/>, deriving
    /// each entry's 64-bit resource id via <see cref="Source2ResourceId"/>.
    ///
    /// <para>CS2's loader resolves referenced resources by this id and
    /// FATAL-errors on a zero/wrong one. The id is MurmurHash64B of the
    /// lowercase path (reverse-engineered - see <see cref="Source2ResourceId"/>),
    /// so brand-new texture paths a fresh vmat introduces resolve correctly
    /// without Valve's resourcecompiler.exe.</para>
    /// </summary>
    private static void ReplaceRerl(Resource resource, IEnumerable<string> paths)
    {
        var rerl = resource.ExternalReferences;
        if (rerl is null)
        {
            rerl = new ResourceExtRefList { Resource = resource };
            resource.Blocks.Add(rerl);
        }

        rerl.ResourceRefInfoList.Clear();

        foreach (var path in paths)
        {
            rerl.ResourceRefInfoList.Add(new ResourceExtRefList.ResourceReferenceInfo
            {
                Id = Source2ResourceId.ForPath(path),
                Name = path,
            });
        }
    }

    /// <summary>
    /// Serialize a mutated resource to a byte array. Public so callers
    /// outside this class (e.g. <c>WeaponsVData.SaveCompiled</c>,
    /// <c>HitmarkerService</c>) can route their writes through the same
    /// canonical path the Build* helpers use.
    /// </summary>
    public static byte[] Serialize(Resource resource)
    {
        AuthoredKv3.ChooseCompression(resource);
        using var ms = new MemoryStream();
        resource.Serialize(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Re-serialize a model resource re-encoding ONLY the DATA block; every other
    /// block (PHYS, ANIM/ASEQ/AGRP, meshes, CTRL, RERL, RED2) is written
    /// byte-identical to <paramref name="originalBytes"/> rather than letting VRF
    /// re-encode it.
    ///
    /// <para><b>Why this exists.</b> VRF's <c>PhysAggregateData</c> (PHYS) and
    /// animation writers do NOT round-trip CS2's data: a plain
    /// <see cref="Serialize(Resource)"/> silently rewrites the physics aggregate
    /// (observed: a character model's PHYS grew 11,907 → 13,689 B), producing a
    /// ragdoll CS2 cannot build - at runtime the physics-body handle resolves to
    /// null and the client crashes (access violation in vphysics2). A DATA-only
    /// edit (mesh-group fold / mask patch) must therefore leave the physics and
    /// animation blocks untouched. The DATA block still re-encodes from its KV3 so
    /// the edit lands.</para>
    /// </summary>
    public static byte[] SerializeModelDataOnly(Resource resource, byte[] originalBytes) =>
        SerializeModelSingleBlock(resource, originalBytes, BlockType.DATA);

    /// <summary>Generalization of <see cref="SerializeModelDataOnly"/>: re-encode ONLY
    /// <paramref name="keepLive"/> (carrying the caller's KV3 mutation); every other
    /// block is written byte-identical from <paramref name="originalBytes"/>. Used with
    /// <c>BlockType.CTRL</c> for controller edits (e.g. unbinding a mesh's morph block).</summary>
    public static byte[] SerializeModelSingleBlock(Resource resource, byte[] originalBytes, BlockType keepLive) =>
        SerializeModelKeepBlocksLive(resource, originalBytes, keepLive);

    /// <summary>Like <see cref="SerializeModelSingleBlock"/> but keeps SEVERAL block types live
    /// (re-encoded from their parsed state); every other block is written byte-identical from
    /// <paramref name="originalBytes"/>. Used for edits that span e.g. CTRL (mesh name / buffer
    /// descriptors) AND MDAT (draw calls) while leaving MVTX/MIDX/DATA/PHYS/anim untouched.</summary>
    public static byte[] SerializeModelKeepBlocksLive(Resource resource, byte[] originalBytes, params BlockType[] liveTypes)
    {
        var live = new HashSet<BlockType>(liveTypes);
        for (var i = 0; i < resource.Blocks.Count; i++)
        {
            var b = resource.Blocks[i];
            if (live.Contains(b.Type))
                continue;             // re-encodes (carries the edit)
            long off = b.Offset, len = b.Size;
            if (off < 0 || len < 0 || off + len > originalBytes.Length)
                continue; // unexpected - let VRF handle it
            resource.Blocks[i] = new TypedRawBlock(b.Type, originalBytes.AsSpan((int)off, (int)len).ToArray()) { Resource = resource };
        }
        return Serialize(resource);
    }

    /// <summary>Rebuild a model resource from an EXACT, ordered set of blocks, each written
    /// BYTE-FOR-BYTE (no re-encode) via VRF's own container serializer - so the header,
    /// block table, 16-byte alignment and padding match what the CS2 engine accepts. Used to
    /// graft a block (e.g. a missing <c>ASEQ</c>) into a community model: hand-rolling the
    /// container produced a model s2v read but CS2 rejected as an ERROR MODEL, and letting VRF
    /// re-encode the typed blocks corrupts complex community models - this does neither (VRF
    /// lays out the container; every block stays raw). <paramref name="template"/> supplies the
    /// header version fields.</summary>
    public static byte[] RebuildModelRaw(byte[] template, IReadOnlyList<(BlockType Type, byte[] Bytes)> blocks)
    {
        using var ms = new MemoryStream(template, writable: false);
        using var resource = new Resource { FileName = "model.vmdl_c" };
        resource.Read(ms);
        resource.Blocks.Clear();
        foreach (var (type, bytes) in blocks)
            resource.Blocks.Add(new TypedRawBlock(type, bytes) { Resource = resource });
        return Serialize(resource);
    }

    /// <summary>Raw bytes of a resource's PHYS (PhysAggregateData) block, or null if absent.</summary>
    public static byte[]? ExtractPhysBlock(Resource resource, byte[] originalBytes)
    {
        var b = resource.GetBlockByType(BlockType.PHYS);
        if (b is null || b.Offset < 0 || b.Size < 0 || b.Offset + b.Size > originalBytes.Length)
            return null;
        return originalBytes.AsSpan((int)b.Offset, (int)b.Size).ToArray();
    }

    /// <summary>Binary-replace a model's PHYS block with <paramref name="donorPhysBytes"/>, keeping
    /// EVERY other block (DATA, meshes, anim, RERL) byte-identical. Pure byte surgery on the
    /// resource block table - VRF is deliberately NOT used to re-serialize, because its DATA/PHYS
    /// re-encode is lossy for complex MMD models (corrupts materials + animgraph, observed in-game
    /// as a nude/white/T-posed model). Rewrites only the PHYS bytes, the block-table relative
    /// offsets, and the file-size header. Mirrors <c>data/_trans_preview/_kf/swap_phys.py</c>.</summary>
    public static byte[] SwapPhysBlockBinary(byte[] model, byte[] donorPhysBytes)
    {
        int bo = (int)BitConverter.ToUInt32(model, 8);
        int bc = (int)BitConverter.ToUInt32(model, 12);
        int arr = 8 + bo;
        var blocks = new List<(int TableOff, string Type, int Abs, int Size)>(bc);
        for (var i = 0; i < bc; i++)
        {
            var o = arr + i * 12;
            var type = System.Text.Encoding.ASCII.GetString(model, o, 4);
            var reloff = (int)BitConverter.ToUInt32(model, o + 4);
            var size = (int)BitConverter.ToUInt32(model, o + 8);
            blocks.Add((o, type, (o + 4) + reloff, size));
        }
        var physIdx = blocks.FindIndex(b => b.Type == "PHYS");
        if (physIdx < 0)
            return model;                 // no PHYS - nothing to swap
        var physAbs = blocks[physIdx].Abs;

        using var ms = new MemoryStream();
        ms.Write(model, 0, physAbs);                    // header + table + every block before PHYS, verbatim
        var newAbs = new Dictionary<int, int>();
        foreach (var b in blocks.OrderBy(b => b.Abs))
        {
            if (b.Abs < physAbs) { newAbs[b.TableOff] = b.Abs; continue; }
            while (ms.Length % 16 != 0)
                ms.WriteByte(0); // 16-align, matching the compiler's layout
            newAbs[b.TableOff] = (int)ms.Length;
            if (b.Type == "PHYS")
                ms.Write(donorPhysBytes, 0, donorPhysBytes.Length);
            else
                ms.Write(model, b.Abs, b.Size);
        }
        var outBytes = ms.ToArray();
        foreach (var b in blocks)
        {
            var na = newAbs[b.TableOff];
            BitConverter.GetBytes((uint)(na - (b.TableOff + 4))).CopyTo(outBytes, b.TableOff + 4);
            BitConverter.GetBytes((uint)(b.Type == "PHYS" ? donorPhysBytes.Length : b.Size)).CopyTo(outBytes, b.TableOff + 8);
        }
        BitConverter.GetBytes((uint)outBytes.Length).CopyTo(outBytes, 0);
        return outBytes;
    }

    /// <summary>Binary-remove a model's PHYS block entirely (drop its block-table entry, shift the
    /// remaining blocks up, recompute every relative offset + the block count + the file size).
    /// Unlike <see cref="SwapPhysBlockBinary"/> (which substitutes a donor ragdoll) this leaves the
    /// model with NO embedded physics aggregate at all, so the engine builds no bone→part map for it.
    /// <para><b>Why.</b> AG2 "sk2model" agents embed a FeModel (jiggle/cloth) whose nodes claim
    /// physics parts beyond the rigid ragdoll's part count; CS2's first-person physics setup
    /// (GFL spawn) indexes those out-of-range parts and null-derefs (`vphysics2` part lookup in
    /// client.dll CalcAnimationState). A donor swap doesn't fix it (the donor's bone hashes resolve
    /// to different model-bone indices, same class of out-of-range map); removing the block removes
    /// the bad map outright. Cost: no death ragdoll for these models (acceptable on the viewmodel
    /// agents). Requires the model's DATA to carry no embedded physics reference - sk2 models ship
    /// <c>m_refPhysicsData = [ ]</c>, so nothing is orphaned.</para></summary>
    public static byte[] RemovePhysBlockBinary(byte[] model)
    {
        int bo = (int)BitConverter.ToUInt32(model, 8);
        int bc = (int)BitConverter.ToUInt32(model, 12);
        int arr = 8 + bo;
        var blocks = new List<(string Type, int Abs, int Size)>(bc);
        for (var i = 0; i < bc; i++)
        {
            var o = arr + i * 12;
            blocks.Add((
                System.Text.Encoding.ASCII.GetString(model, o, 4),
                (o + 4) + (int)BitConverter.ToUInt32(model, o + 4),
                (int)BitConverter.ToUInt32(model, o + 8)));
        }
        int physIdx = blocks.FindIndex(b => b.Type == "PHYS");
        if (physIdx < 0)
            return model;                         // no PHYS - nothing to remove
        var kept = blocks.Where((_, i) => i != physIdx).ToList();
        int newBc = kept.Count;

        using var ms = new MemoryStream();
        ms.Write(model, 0, arr);                                // header verbatim (fix bc + size below)
        var tableOffs = new int[newBc];
        for (var i = 0; i < newBc; i++)                         // block table: type now, offsets after
        {
            tableOffs[i] = (int)ms.Length;
            ms.Write(System.Text.Encoding.ASCII.GetBytes(kept[i].Type), 0, 4);
            ms.Write(new byte[8], 0, 8);
        }
        var newAbs = new int[newBc];
        foreach (var x in kept.Select((b, i) => (b, i)).OrderBy(x => x.b.Abs))
        {
            while (ms.Length % 16 != 0)
                ms.WriteByte(0);        // 16-align, matching the compiler
            newAbs[x.i] = (int)ms.Length;
            ms.Write(model, x.b.Abs, x.b.Size);
        }
        var outBytes = ms.ToArray();
        BitConverter.GetBytes((uint)newBc).CopyTo(outBytes, 12);
        for (var i = 0; i < newBc; i++)
        {
            BitConverter.GetBytes((uint)(newAbs[i] - (tableOffs[i] + 4))).CopyTo(outBytes, tableOffs[i] + 4);
            BitConverter.GetBytes((uint)kept[i].Size).CopyTo(outBytes, tableOffs[i] + 8);
        }
        BitConverter.GetBytes((uint)outBytes.Length).CopyTo(outBytes, 0);
        return outBytes;
    }

    /// <summary>The number of procedural jiggle bones at/above which a model reliably crashes CS2's
    /// round-transition ragdoll teardown (vphysics2 null-deref on the freed physics body). Set from
    /// PHYS dumps + in-game confirmation: GFL/stock BASE content carries 0-1 jiggle bones and runs
    /// fine live (zombies, vector, isaac_clarke, master_chief all = 0-1); the confirmed crasher EXG
    /// `shinano_kotori` carries 57-152; across the EXG library there's a clean empty gap (~6 → ~59
    /// bones). 16 sits in that gap - above all base content and the light cluster, below every
    /// confirmed crasher - so the strip never touches a benign base-game model. A non-empty
    /// soft-body solver (actual ropes) is treated as dynamic regardless of bone count (rare, but a
    /// genuine live simulation). Earlier this was a substring match for jigglebone/m_ropes/etc.,
    /// which false-positived on any model with even ONE jiggle bone - those tokens are schema field
    /// names printed even when empty (`m_Ropes = [ ]`). Count, not presence, is the real signal.</summary>
    public const int JiggleBoneCrashThreshold = 16;

    /// <summary>Parse a model and report its physics in one pass: <c>Readable</c> (a valid Source 2
    /// resource), <c>HasPhys</c> (a PHYS block is present), <c>JiggleBones</c> (count of procedural
    /// jiggle bones), <c>HasSoftBody</c> (a non-empty rope/cloth solver) and <c>PhysBytes</c>.
    /// Never throws. Reads with verifyFileSize:false so repacked VPKs that pad model entries to a
    /// fixed slot still parse (we only need the block table + PHYS, which sit within the declared
    /// size; a genuinely truncated/corrupt model still throws → caught).</summary>
    public static (bool Readable, bool HasPhys, int JiggleBones, bool HasSoftBody, int PhysBytes) AnalyzePhysics(byte[] model)
    {
        try
        {
            using var res = new Resource();
            res.Read(new MemoryStream(model, writable: false), verifyFileSize: false);
            if (res.GetBlockByType(BlockType.PHYS) is not { } phys)
                return (true, false, 0, false, 0);
            var w = new ValveResourceFormat.Utils.IndentedTextWriter();
            phys.WriteText(w);
            var s = w.ToString();
            // One "m_jiggleBone =" sub-object per jiggle bone. The plural array header
            // "m_JiggleBones =" does NOT match (the char after "m_jigglebone" is 's', not ' ').
            var jiggleBones = CountOccurrences(s, "m_jiggleBone =");
            // A live soft-body / cloth solver has a non-zero rope count (it's "= 0" when absent).
            var hasSoftBody = System.Text.RegularExpressions.Regex.IsMatch(
                s, @"m_nRopeCount\s*=\s*[1-9]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return (true, true, jiggleBones, hasSoftBody, (int)phys.Size);
        }
        catch { return (false, false, 0, false, 0); }
    }

    /// <summary>True if a model carries a dynamic-physics setup heavy enough to crash CS2's
    /// round-transition ragdoll teardown - a jiggle-bone count at/above
    /// <see cref="JiggleBoneCrashThreshold"/>, or an active soft-body solver. A plain rigid ragdoll
    /// or a base model with a handful of jiggle bones returns false (it runs fine live; stripping
    /// it would needlessly drop working physics).</summary>
    public static bool HasDynamicPhysics(byte[] model)
    {
        var a = AnalyzePhysics(model);
        return a.JiggleBones >= JiggleBoneCrashThreshold || a.HasSoftBody;
    }

    /// <summary>Count non-overlapping, case-insensitive occurrences of <paramref name="needle"/>.</summary>
    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.OrdinalIgnoreCase)) >= 0) { count++; i += needle.Length; }
        return count;
    }

    /// <summary>
    /// Compile a raw SVG (UTF-8 bytes) into a Source 2 <c>.vsvg_c</c> with no
    /// donor file. The container is authored: resource version 2 and a fresh
    /// Panorama block with an empty name table, which is what every stock vector
    /// graphic has (400 of 400 sampled). The SVG is normalized on the way in
    /// (see <see cref="SvgSanitizer.ForPanorama(byte[])"/>) and the Panorama
    /// CRC32 is computed over what is actually stored.
    ///
    /// <para>Used for custom killfeed / HUD knife icons
    /// (<c>panorama/images/icons/equipment/*.vsvg_c</c>).</para>
    /// </summary>
    /// <param name="svgBytes">The SVG document, as UTF-8 bytes.</param>
    /// <param name="sourceName">Content-relative source path recorded in the authored RED2;
    /// null records a neutral <c>vpkeditor/</c> name.</param>
    public static byte[] BuildPanoramaSvg(byte[] svgBytes, string? sourceName = null)
        => BuildPanoramaSvg(null, svgBytes, sourceName);

    /// <summary>
    /// As <see cref="BuildPanoramaSvg(byte[], string?)"/>, but taking the
    /// container frame and its name-entry header from an existing
    /// <c>.vsvg_c</c> instead of authoring them.
    /// </summary>
    /// <param name="templateVsvgC">
    /// Any valid <c>.vsvg_c</c>, used for the container frame and its name-entry
    /// header, or null to author both.
    /// </param>
    /// <param name="svgBytes">The SVG document, as UTF-8 bytes.</param>
    /// <param name="sourceName">Content-relative source path recorded in the authored RED2;
    /// null records a neutral <c>vpkeditor/</c> name.</param>
    public static byte[] BuildPanoramaSvg(byte[]? templateVsvgC, byte[] svgBytes, string? sourceName = null)
    {
        Panorama panorama;
        Resource resource;

        if (templateVsvgC is { Length: > 0 })
        {
            resource = new Resource();
            resource.Read(new MemoryStream(templateVsvgC, writable: false));
            if (resource.DataBlock is not Panorama fromTemplate)
            {
                resource.Dispose();
                throw new InvalidDataException("Template is not a Panorama (.vsvg_c) resource.");
            }
            panorama = fromTemplate;
        }
        else
        {
            // Authored outright. The name table is what a template was otherwise
            // contributing, and stock ships it empty without exception, so there
            // is nothing here a donor could add.
            resource = new Resource { Version = Source2ContainerAuthor.PanoramaVectorGraphicResourceVersion };
            panorama = new Panorama([], []) { Resource = resource };
            resource.Blocks.Add(panorama);
        }

        using var _ = resource;

        // An SVG is the whole content of a .vsvg_c, and the sanitizer below is a
        // best-effort normalizer that passes anything it does not recognize
        // through untouched. Without this check, a non-SVG upload compiles into
        // a well-formed container holding bytes the engine cannot draw - an icon
        // that is simply missing in game, with no failure anywhere upstream.
        if (svgBytes is not { Length: > 0 })
            throw new InvalidDataException("SVG input is empty.");
        if (!System.Text.Encoding.UTF8.GetString(svgBytes).Contains("<svg", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "Input is not an SVG document (no <svg> element). A .vsvg_c holds vector art, " +
                "so a raster image has to be converted to SVG first.");

        // Replace the SVG payload; keep the template's name-entry header.
        // Sanitize first: CS2's Panorama SVG parser rejects the minified / arc /
        // relative path data most icon CDNs emit, so this normalizes ANY icon
        // (current, future, user-uploaded) to the clean subset the engine draws.
        panorama.Data = SvgSanitizer.ForPanorama(svgBytes);

        // Upstream VRF's Panorama.Serialize writes the STORED checksum verbatim
        // (it does not recompute), and both VRF's reader and the engine validate
        // it against the data - refresh it to match the new payload.
        panorama.CRC32 = System.IO.Hashing.Crc32.HashToUInt32(panorama.Data);

        // Author our OWN RED2: the embedded template.vsvg_c names its own source
        // svg and CRC, which every killfeed icon we build has been shipping.
        // Provenance only - the template's special dependencies ride along (see
        // the vtex lesson in Source2ContainerAuthor.BuildBinaryEditInfo). The CRC
        // recorded is of the SANITIZED bytes we actually compiled, not the raw
        // upload, because that is what the DATA block contains.
        var svgRed2Idx = resource.Blocks.FindIndex(b => b.Type == BlockType.RED2);
        var svgTemplateDeps = (resource.EditInfo?.SpecialDependencies ?? [])
            .Select(d => new Source2ContainerAuthor.SpecialDep(
                d.String, d.CompilerIdentifier, (int)d.Fingerprint, (int)d.UserData))
            .ToList();
        if (svgTemplateDeps.Count == 0)
            svgTemplateDeps.AddRange(Source2ContainerAuthor.VectorGraphicDeps);
        var svgEditInfo = Source2ContainerAuthor.BuildVectorGraphicEditInfo(
            sourceName, panorama.Data, svgTemplateDeps);
        var svgRed2 = AuthoredKv3.Block(svgEditInfo, KV3IDLookup.Get("generic"), BlockType.RED2, resource);
        if (svgRed2Idx >= 0)
            resource.Blocks[svgRed2Idx] = svgRed2;
        else
            resource.Blocks.Insert(0, svgRed2);

        return Serialize(resource);
    }

    /// <summary>
    /// Re-serialize a compiled <c>.vpcf_c</c> with every string value matching
    /// a key in <paramref name="pathMap"/> swapped for the corresponding new
    /// value. Walks the DATA block KV3 tree, the RED2 metadata block, and the
    /// RERL external-reference list. The KV3 string table is rebuilt from
    /// scratch by VRF's serializer, so length changes are handled correctly.
    ///
    /// Originally lived in <c>HitmarkerService</c>; lifted here so any future
    /// "rewrite paths inside a `.vpcf_c`" use case (presets, custom particle
    /// uploads, particle copy-into-a-different-folder) shares one
    /// implementation.
    /// </summary>
    public static byte[] RewriteParticlePaths(byte[] vpcfBytes, IDictionary<string, string> pathMap,
        double radiusScale = 1.0, (byte R, byte G, byte B)? constantColor = null, bool normalizeBaseRadius = false,
        double? alphaScale = null, double? textureScaleU = null)
    {
        using var ms = new MemoryStream(vpcfBytes, writable: false);
        using var resource = new Resource();
        resource.Read(ms);

        // DATA block - particle systems extend KeyValuesOrNTRO whose Data
        // property is the mutable KVObject root. AsKeyValueCollection() throws
        // for KeyValuesOrNTRO subclasses, so unwrap manually.
        var dataBlock = resource.GetBlockByType(BlockType.DATA);
        KVObject? dataRoot = dataBlock switch
        {
            KeyValuesOrNTRO kvn => kvn.Data,
            BinaryKV3 bkv when bkv.Data is { } doc => doc.Root,
            _ => null,
        };
        if (dataRoot is not null)
        {
            ReplaceStringsRecursive(dataRoot, pathMap);
            // Scale the on-screen sprite size. The size of a screen-space sprite
            // particle is RADIUS-driven (C_OP_RenderSprites.m_flRadiusScale), not
            // texture-resolution-driven - so this is the lever that actually
            // resizes the in-game marker. factor 1.0 = no-op.
            if (radiusScale is not 1.0)
            {
                // On-screen size = particle Radius × m_flRadiusScale, and the per-
                // frame Radius starts at the definition's m_flConstantRadius - which
                // DEFAULTS TO 5.0 in Source 2 (the Valve Dev wiki "Radius" property
                // default), NOT 1.0. (VRF's preview renderer approximates the default
                // as 1.0, which is exactly what historically misread this - a
                // decompile-only check saw the head's explicit 7.5 against an assumed
                // body default of 1.0 and concluded the head was 7.5× the body.) In
                // reality the body template OMITS the key (→ engine default 5.0) and
                // the head bakes 7.5, i.e. only 1.5× that shared baseline. So divide
                // the render scale by each template's radius-to-default RATIO: body
                // (5.0/5.0 = 1.0) is untouched, head (7.5/5.0 = 1.5) is brought back
                // in line - both land at the same on-screen size and the user Size
                // mult scales them identically.
                var factor = normalizeBaseRadius ? radiusScale / ParticleBaseRadiusRatio(dataRoot) : radiusScale;
                ScaleRenderSpriteRadius(dataRoot, factor);
            }
            // Set the sprite's peak alpha ceiling (C_OP_RenderSprites.m_flAlphaScale).
            // The hitmarker templates bake 0.75, so the marker never renders fully
            // opaque; SETTING (not scaling) it lets opacity=1.0 mean truly 100%.
            if (alphaScale is { } a)
                SetRenderSpriteAlpha(dataRoot, a);
            // Cancel CS2's un-aspect-corrected screen-space sprite stretch - see
            // SetRenderSpriteTextureScaleU. Null = leave the template alone.
            if (textureScaleU is { } su)
                SetRenderSpriteTextureScaleU(dataRoot, su);
            // Neutralise / set the definition's MULTIPLY-blend constant colour so
            // the sprite shows its texture's own colour (the head template bakes a
            // dark-red constant that turns any non-red tint near-black).
            if (constantColor is { } cc)
                SetParticleConstantColor(dataRoot, cc.R, cc.G, cc.B);
        }

        // RED2 - source-asset metadata. The legacy REDI is RawBinary (no KV3
        // to walk); only RED2 carries strings worth rewriting.
        if (resource.GetBlockByType(BlockType.RED2) is ResourceEditInfo2 redi2 &&
            redi2.Data is { } redoc)
        {
            ReplaceStringsRecursive(redoc.Root, pathMap);
        }

        // RERL - external reference list. The Name is only half of an entry: the
        // engine resolves a reference by its 64-bit path-hash Id, so a renamed
        // entry MUST be re-hashed or the entry points at the old asset. This was
        // missed until the 2026-08-10 ref audit found both shipped hitmarker
        // particles carrying the id of the original materials/mac/... path under
        // a materials/vpkedit_hm/... name. Every other RERL rewrite in the
        // pipeline (a skin-material builder, a mesh-group patcher's
        // anim-include repoint, ModelSwapStage) already re-ids; this one did not.
        // The invariant holds in 3953 stock CS2 resources and both base packs
        // with zero exceptions - see ResourceRefIntegrityTests.
        if (resource.ExternalReferences is { } rerl)
        {
            foreach (var entry in rerl.ResourceRefInfoList)
            {
                if (pathMap.TryGetValue(entry.Name, out var newName))
                {
                    entry.Name = newName;
                    entry.Id = Source2ResourceId.ForPath(newName);
                }
            }
        }

        return Serialize(resource);
    }

    /// <summary>
    /// Recursively walk a KV3 tree replacing every <see cref="KVValueType.String"/>
    /// child whose value matches a key in <paramref name="pathMap"/>. Mutates
    /// in place; callers re-serialize via <see cref="Serialize"/>. Internal so
    /// <c>another caller</c> reuses the walker.
    /// </summary>
    public static void ReplaceStringsRecursive(KVObject obj, IDictionary<string, string> pathMap)
    {
        if (obj is null)
            return;

        if (obj.ValueType == KVValueType.Collection)
        {
            // Snapshot keys: the dictionary backing can't be mutated mid-
            // enumeration. The indexer either replaces an existing entry or
            // appends to a list-backed collection - both are safe after the
            // snapshot.
            var keys = obj.Keys.ToList();
            foreach (var key in keys)
            {
                var child = obj[key];
                if (child.ValueType == KVValueType.String)
                {
                    var s = (string)child;
                    // Preserve the KV3 value flag (Resource/Panorama/etc.) -
                    // a flagless replacement breaks CS2's reference resolution.
                    if (s is not null && pathMap.TryGetValue(s, out var newStr))
                        obj[key] = new KVObject(newStr) { Flag = child.Flag };
                }
                else if (child.IsCollection || child.IsArray)
                {
                    ReplaceStringsRecursive(child, pathMap);
                }
            }
        }
        else if (obj.ValueType == KVValueType.Array)
        {
            var span = obj.AsArraySpan();
            for (var i = 0; i < span.Length; i++)
            {
                var child = span[i];
                if (child.ValueType == KVValueType.String)
                {
                    var s = (string)child;
                    if (s is not null && pathMap.TryGetValue(s, out var newStr))
                        span[i] = new KVObject(newStr) { Flag = child.Flag };
                }
                else if (child.IsCollection || child.IsArray)
                {
                    ReplaceStringsRecursive(child, pathMap);
                }
            }
        }
    }

    /// <summary>Deep-clone a <see cref="KVObject"/> (collections, arrays, scalars),
    /// preserving value type and flag - so a cloned node can be inserted elsewhere in
    /// the tree and then mutated independently. Cloning a real sibling is how we
    /// synthesise a structurally-valid node for a key a template omits: hand-building
    /// a ~40-field Source 2 float-input node risks a shape the engine chokes on.</summary>
    public static KVObject CloneKv(KVObject src)
    {
        KVObject dst;
        switch (src.ValueType)
        {
            case KVValueType.Collection:
                dst = KVObject.Collection();
                foreach (var kv in src.Children)
                    dst.Add(kv.Key, CloneKv(kv.Value));
                break;
            case KVValueType.Array:
                dst = KVObject.Array();
                foreach (var v in src.Values)
                    dst.Add(CloneKv(v));
                break;
            case KVValueType.String:
                dst = new KVObject((string)src);
                break;
            case KVValueType.Boolean:
                dst = new KVObject((bool)src);
                break;
            case KVValueType.Null:
                dst = KVObject.Null();
                break;
            case KVValueType.BinaryBlob:
                dst = KVObject.Blob((byte[])src);
                break;
            default: // numeric (Int16/32/64, UInt*, FloatingPoint(64), Pointer)
                dst = BuildNumericTyped(src.ValueType, (double)src) ?? new KVObject((double)src);
                break;
        }
        dst.Flag = src.Flag;
        return dst;
    }

    /// <summary>
    /// Set every <c>C_OP_RenderSprites</c> texture's <c>m_TextureControls.m_flFinalTextureScaleU</c>
    /// literal to <paramref name="scaleU"/>, creating the key when the template omits it.
    ///
    /// <para>This is the aspect-compensation lever. CS2 does NOT aspect-correct
    /// screen-space sprite quads - a hitmarker renders ~1.78x wider than tall on 16:9
    /// (confirmed in-game 2026-08-12 with a square-outline calibration sprite that came
    /// back a rectangle). GFL's own three hitmarker particles all bake
    /// <c>m_flFinalTextureScaleU = 0.5</c> with <c>m_bClampUVs = true</c>, which is
    /// evidently how that pack cancels the stretch - and it costs no texture resolution,
    /// unlike pre-squeezing the source art. Our "mac" templates omit the key entirely,
    /// which is why our markers ship stretched.</para>
    ///
    /// <para>The node is cloned from the renderer's own <c>m_flSelfIllumAmount</c> (the
    /// same <c>CParticleCollectionRendererFloatInput</c> shape) so the synthesised key is
    /// structurally identical to one the particle compiler would have emitted.</para>
    ///
    /// <para>Mutates in place; the caller re-serializes. Returns how many texture inputs
    /// were touched, so a caller can tell a silent miss from a real edit.</para>
    /// </summary>
    private static int SetRenderSpriteTextureScaleU(KVObject obj, double scaleU)
    {
        if (obj is null)
            return 0;
        var touched = 0;

        if (obj.ValueType == KVValueType.Collection)
        {
            var keys = obj.Keys.ToList();
            if (keys.Contains("_class") && (string)obj["_class"] == "C_OP_RenderSprites")
            {
                // Shape donor: any full float-input node on this renderer. Both hitmarker
                // templates carry m_flSelfIllumAmount; fall back to m_flRadiusScale.
                var donor = obj.GetSubCollection("m_flSelfIllumAmount")
                         ?? obj.GetSubCollection("m_flRadiusScale");
                var textures = keys.Contains("m_vecTexturesInput") ? obj["m_vecTexturesInput"] : null;
                if (donor is not null && textures is not null && textures.IsArray)
                {
                    var span = textures.AsArraySpan();
                    for (var i = 0; i < span.Length; i++)
                    {
                        if (span[i].ValueType != KVValueType.Collection)
                            continue;
                        var controls = span[i].GetSubCollection("m_TextureControls");
                        if (controls is null)
                            continue;

                        var node = CloneKv(donor);
                        var repl = BuildNumericTyped(node["m_flLiteralValue"].ValueType, scaleU);
                        if (repl is null)
                            continue;
                        node["m_flLiteralValue"] = repl;
                        controls["m_flFinalTextureScaleU"] = node;
                        touched++;
                    }
                }
            }
            foreach (var key in keys)
            {
                var child = obj[key];
                if (child.IsCollection || child.IsArray)
                    touched += SetRenderSpriteTextureScaleU(child, scaleU);
            }
        }
        else if (obj.ValueType == KVValueType.Array)
        {
            var span = obj.AsArraySpan();
            for (var i = 0; i < span.Length; i++)
                if (span[i].IsCollection || span[i].IsArray)
                    touched += SetRenderSpriteTextureScaleU(span[i], scaleU);
        }
        return touched;
    }

    /// <summary>
    /// Read the first <c>C_OP_RenderSprites</c> renderer's <c>m_flRadiusScale</c>
    /// literal out of a compiled <c>.vpcf_c</c>, or null when the particle has no
    /// sprite renderer or the renderer omits the key (engine default 1.0). This is
    /// the one number that sets a screen-space marker's on-screen size, so it is
    /// what an A/B pair of builds has to differ in - read it back rather than
    /// decompiling to confirm a size actually reached the particle.
    /// </summary>
    public static double? ReadRenderSpriteRadiusScale(byte[] vpcfBytes)
    {
        using var ms = new MemoryStream(vpcfBytes, writable: false);
        using var resource = new Resource();
        resource.Read(ms);
        KVObject? root = resource.GetBlockByType(BlockType.DATA) switch
        {
            KeyValuesOrNTRO kvn => kvn.Data,
            BinaryKV3 bkv when bkv.Data is { } doc => doc.Root,
            _ => null,
        };
        return root is null ? null : FindRenderSpriteRadiusScale(root);
    }

    private static double? FindRenderSpriteRadiusScale(KVObject obj)
    {
        if (obj is null)
            return null;
        if (obj.ValueType == KVValueType.Collection)
        {
            var keys = obj.Keys.ToList();
            if (keys.Contains("_class") && (string)obj["_class"] == "C_OP_RenderSprites")
            {
                var rs = obj.GetSubCollection("m_flRadiusScale");
                if (rs is not null && rs.Keys.Contains("m_flLiteralValue"))
                    return rs.GetFloatProperty("m_flLiteralValue");
            }
            foreach (var key in keys)
            {
                var child = obj[key];
                if ((child.IsCollection || child.IsArray) && FindRenderSpriteRadiusScale(child) is { } v)
                    return v;
            }
        }
        else if (obj.ValueType == KVValueType.Array)
        {
            var span = obj.AsArraySpan();
            for (var i = 0; i < span.Length; i++)
                if ((span[i].IsCollection || span[i].IsArray) && FindRenderSpriteRadiusScale(span[i]) is { } v)
                    return v;
        }
        return null;
    }

    /// <summary>
    /// Recursively scale every <c>C_OP_RenderSprites</c> renderer's
    /// <c>m_flRadiusScale</c> literal by <paramref name="factor"/>. For a
    /// screen-space sprite particle (the hitmarker), this multiplier is what sets
    /// the marker's on-screen size - the source texture's resolution only affects
    /// sharpness, not size. Mutates in place; the caller re-serializes.
    /// </summary>
    private static void ScaleRenderSpriteRadius(KVObject obj, double factor)
    {
        if (obj is null)
            return;

        if (obj.ValueType == KVValueType.Collection)
        {
            var keys = obj.Keys.ToList();
            if (keys.Contains("_class") && (string)obj["_class"] == "C_OP_RenderSprites")
            {
                var rs = obj.GetSubCollection("m_flRadiusScale");
                if (rs is not null && rs.Keys.Contains("m_flLiteralValue"))
                {
                    var lit = rs["m_flLiteralValue"];
                    var scaled = BuildNumericTyped(lit.ValueType, rs.GetFloatProperty("m_flLiteralValue") * factor);
                    if (scaled is not null)
                        rs["m_flLiteralValue"] = scaled;
                }
            }
            foreach (var key in keys)
            {
                var child = obj[key];
                if (child.IsCollection || child.IsArray)
                    ScaleRenderSpriteRadius(child, factor);
            }
        }
        else if (obj.ValueType == KVValueType.Array)
        {
            var span = obj.AsArraySpan();
            for (var i = 0; i < span.Length; i++)
                if (span[i].IsCollection || span[i].IsArray)
                    ScaleRenderSpriteRadius(span[i], factor);
        }
    }

    /// <summary>
    /// SET every <c>C_OP_RenderSprites</c> renderer's <c>m_flAlphaScale</c> literal
    /// to <paramref name="alpha"/> (clamped 0..1). On-screen alpha = particle.Alpha
    /// × m_flAlphaScale, so this is the ceiling the fade animation tops out at. The
    /// hitmarker templates bake 0.75 (never fully opaque); SETTING lets the user
    /// pick a true 100%. Mutates in place; the caller re-serializes.
    /// </summary>
    private static void SetRenderSpriteAlpha(KVObject obj, double alpha)
    {
        alpha = Math.Clamp(alpha, 0.0, 1.0);
        if (obj is null)
            return;
        if (obj.ValueType == KVValueType.Collection)
        {
            var keys = obj.Keys.ToList();
            if (keys.Contains("_class") && (string)obj["_class"] == "C_OP_RenderSprites")
            {
                var asc = obj.GetSubCollection("m_flAlphaScale");
                if (asc is not null && asc.Keys.Contains("m_flLiteralValue"))
                {
                    var repl = BuildNumericTyped(asc["m_flLiteralValue"].ValueType, alpha);
                    if (repl is not null)
                        asc["m_flLiteralValue"] = repl;
                }
            }
            foreach (var key in keys)
            {
                var child = obj[key];
                if (child.IsCollection || child.IsArray)
                    SetRenderSpriteAlpha(child, alpha);
            }
        }
        else if (obj.ValueType == KVValueType.Array)
        {
            var span = obj.AsArraySpan();
            for (var i = 0; i < span.Length; i++)
                if (span[i].IsCollection || span[i].IsArray)
                    SetRenderSpriteAlpha(span[i], alpha);
        }
    }

    /// <summary>
    /// Source 2's default particle radius when a definition omits
    /// <c>m_flConstantRadius</c>: the Valve Dev wiki documents the "Radius"
    /// property default as <b>5</b> world units, and that is what CS2's client
    /// spawns particles at. NOT 1.0 - VRF's preview renderer hard-codes 1.0 as
    /// its own approximation, so reading a decompile alone misreads the real
    /// in-game base radius. A template that omits the key therefore renders at
    /// the same base radius as one that bakes <c>m_flConstantRadius = 5</c>.
    /// </summary>
    private const double Source2DefaultConstantRadius = 5.0;

    /// <summary>
    /// The definition's base sprite radius expressed as a RATIO to the Source 2
    /// engine default (<see cref="Source2DefaultConstantRadius"/>). A definition
    /// that omits <c>m_flConstantRadius</c> spawns at the engine default → ratio
    /// <c>1.0</c> (no normalisation); the hitmarker head bakes <c>7.5</c> →
    /// <c>1.5</c>. On-screen sprite size is base × <c>m_flRadiusScale</c>, so
    /// dividing the render scale by this ratio collapses every template onto one
    /// on-screen baseline - the body (which omits the key) is left untouched and
    /// the head's 1.5× base is compensated, so the two render at the same size.
    ///
    /// <para>The earlier version returned the raw radius with a <c>1.0</c>
    /// fallback for the absent case, which wrongly treated the body's base as
    /// 1.0 instead of the engine's 5.0 - so the head (7.5) was divided by the
    /// full 7.5 while the body was divided by 1.0, shrinking the headshot marker
    /// ~5× and making it nearly invisible in-game (the web preview is a flat Skia
    /// canvas that never exercises the radius, so it hid the bug).</para>
    /// </summary>
    private static double ParticleBaseRadiusRatio(KVObject root)
    {
        var effective = Source2DefaultConstantRadius;
        try
        {
            if (root.ValueType == KVValueType.Collection && root.Keys.Contains("m_flConstantRadius"))
            {
                var v = root.GetFloatProperty("m_flConstantRadius");
                if (v > 0.0001)
                    effective = v;
            }
        }
        catch { /* not all definitions expose it as a readable float - treat as the engine default */ }
        return effective / Source2DefaultConstantRadius;
    }

    /// <summary>
    /// Set a particle definition's root <c>m_ConstantColor</c> RGB (alpha kept).
    /// CS2 MULTIPLY-blends this over every sprite the system renders, so the
    /// hitmarker head template's baked dark-red constant (<c>[139,0,0,255]</c>)
    /// turns a texture tinted any non-red colour near-black. Setting the constant
    /// to the chosen colour (or white) lets the texture's own colour show. The
    /// body template carries no constant (modulated by white), so this is a no-op
    /// there. Mutates the 4-element <c>[r,g,b,a]</c> array in place.
    /// </summary>
    private static void SetParticleConstantColor(KVObject root, byte r, byte g, byte b)
    {
        if (root is null || root.ValueType != KVValueType.Collection)
            return;
        if (!root.Keys.Contains("m_ConstantColor"))
            return;
        var col = root["m_ConstantColor"];
        if (col.ValueType != KVValueType.Array)
            return;
        var span = col.AsArraySpan();
        if (span.Length < 3)
            return;
        ReadOnlySpan<byte> rgb = [r, g, b];
        for (var i = 0; i < 3; i++)
        {
            // KV3 stores these as small ints (the head template's are Int32);
            // BuildNumericTyped covers the integer types, with a 64-bit fallback
            // for any element type it doesn't recognise.
            var repl = BuildNumericTyped(span[i].ValueType, rgb[i]) ?? new KVObject((long)rgb[i]);
            repl.Flag = span[i].Flag;
            span[i] = repl;
        }
    }

    /// <summary>
    /// Splice a new PCM payload into a <c>.vsnd_c</c>, preserving every byte of
    /// the resource container (header, metadata, RERL) up to <c>sound.Offset +
    /// sound.Size</c>. Used by the volume-scaling pipeline to swap a scaled WAV
    /// back into its original `_c` wrapper without going through
    /// <c>Sound.Serialize</c>, which throws.
    ///
    /// Originally <c>SoundProcessor.WavToVsndC</c>; lifted here so all
    /// resource-byte writes funnel through one file.
    /// </summary>
    /// <param name="vsndcTemplate">The original `.vsnd_c` whose PCM tail will be replaced.</param>
    /// <param name="scaledWav">A fresh WAV file whose PCM payload matches the template's format byte-for-byte in length.</param>
    /// <exception cref="InvalidOperationException">If the scaled WAV's PCM payload byte-length doesn't match the template - would mean the format changed.</exception>
    public static byte[] RebuildSound(byte[] vsndcTemplate, byte[] scaledWav)
    {
        using var ms = new MemoryStream(vsndcTemplate, writable: false);
        using var resource = new Resource { FileName = "audio.vsnd_c" };
        resource.Read(ms);

        if (resource.DataBlock is not ValveResourceFormat.ResourceTypes.Sound sound)
            throw new InvalidDataException("vsnd_c does not contain a Sound data block.");

        // Everything up to this offset is the resource structure we keep intact.
        var audioStart = (int)(sound.Offset + sound.Size);
        var pcmBytes = ExtractWavData(scaledWav);
        var expectedSize = (int)sound.StreamingDataSize;

        if (pcmBytes.Length != expectedSize)
            throw new InvalidOperationException(
                $"Scaled WAV PCM payload is {pcmBytes.Length} bytes but template expects " +
                $"{expectedSize}. Ensure the WAV format matches the original.");

        var result = new byte[audioStart + pcmBytes.Length];
        vsndcTemplate.AsSpan(0, audioStart).CopyTo(result);
        pcmBytes.AsSpan().CopyTo(result.AsSpan(audioStart));
        return result;
    }

    /// <summary>
    /// Convert a LEGACY-container compiled sound (<c>RED2</c>+<c>DATA</c>, no <c>CTRL</c>) into the
    /// MODERN <c>CVoiceContainer</c> form (<c>RED2</c>+<c>CTRL</c>+<c>DATA</c>) by synthesising the
    /// <c>CTRL</c> block (<c>CVoiceContainerDefault</c>) from the legacy DATA params. The DATA
    /// metadata block and the streaming PCM tail are preserved BYTE-FOR-BYTE - only a CTRL block
    /// is inserted ahead of DATA.
    ///
    /// <para><b>Why it matters (in-game confirmed).</b> CS2 still ships some stock sounds in the
    /// legacy container, notably the original knife clips, and loads them through a back-compat
    /// path. A content-VPK override of one of those at the stock path is <b>silently ignored</b>
    /// unless the override itself carries a CTRL block: a fully silenced legacy
    /// <c>knife_stab.vsnd_c</c> still played at full volume, while the same audio recompiled
    /// modern muted correctly. So run every sound override through this.</para>
    ///
    /// <para>PCM8/PCM16 only. Anything else, or a file that already has a CTRL block, is returned
    /// unchanged.</para>
    /// </summary>
    public static byte[] ModernizeVsnd(byte[] vsndc)
    {
        using var resource = new Resource { FileName = "audio.vsnd_c" };
        resource.Read(new MemoryStream(vsndc, writable: false), verifyFileSize: false);

        // Already modern (carries a CVoiceContainer CTRL block) - nothing to do.
        if (resource.GetBlockByType(BlockType.CTRL) is not null)
            return vsndc;
        if (resource.DataBlock is not ValveResourceFormat.ResourceTypes.Sound snd)
            return vsndc;

        // We only synthesise a CTRL for uncompressed PCM (what the scaler ships); leave
        // anything else byte-identical. Modern MP3/ADPCM clips already carry a CTRL above.
        if (snd.SoundType != ValveResourceFormat.ResourceTypes.Sound.AudioFileType.WAV ||
            snd.AudioFormat != ValveResourceFormat.ResourceTypes.Sound.WaveAudioFormat.PCM)
            return vsndc;

        // Streaming PCM tail lives after the block section; preserve it verbatim.
        var audioStart = (int)(snd.Offset + snd.Size);
        if (audioStart < 0 || audioStart > vsndc.Length)
            return vsndc;
        var pcm = vsndc.AsSpan(audioStart).ToArray();

        // Build the CVoiceContainerDefault CTRL block from the legacy params
        // Field set + types mirror what the CS2 compiler emits (verified against a stock
        // modern knife clip's CTRL via VRF). VRF's Sound.ConstructFromCtrl reads these back.
        var vsound = new KVObject();
        vsound.Add("m_nRate", new KVObject((int)snd.SampleRate));
        vsound.Add("m_nFormat", new KVObject(snd.Bits == 8 ? "PCM8" : "PCM16"));
        vsound.Add("m_nChannels", new KVObject((int)snd.Channels));
        vsound.Add("m_nLoopStart", new KVObject(snd.LoopStart));
        vsound.Add("m_nSampleCount", new KVObject((int)snd.SampleCount));
        vsound.Add("m_flDuration", new KVObject(snd.Duration));
        vsound.Add("m_Sentences", KVObject.Array());
        vsound.Add("m_nStreamingSize", new KVObject((int)snd.StreamingDataSize));
        vsound.Add("m_nSeekTable", KVObject.Array());
        vsound.Add("m_nLoopEnd", new KVObject(snd.LoopEnd));
        vsound.Add("m_encodedHeader", KVObject.Blob([]));

        var ctrlRoot = new KVObject();
        ctrlRoot.Add("_class", new KVObject("CVoiceContainerDefault"));
        ctrlRoot.Add("m_vSound", vsound);
        ctrlRoot.Add("m_pEnvelopeAnalyzer", KVObject.Null());

        var ctrl = AuthoredKv3.Block(ctrlRoot, KV3IDLookup.Get("generic"), BlockType.CTRL, resource);

        // Rebuild the block list to MATCH the layout CS2's modern compiler emits (verified against
        // a stock modern clip): RED2(+any others), then an EMPTY DATA block (size 0 - the modern
        // container carries ALL sound metadata in CTRL, not DATA), then the synthesised CTRL.
        // Non-DATA blocks (RED2) are preserved byte-for-byte; the legacy 48-byte Sound metadata in
        // the old DATA block is DROPPED (the CTRL replaces it).
        var originals = resource.Blocks
            .Select(b => (b.Type, Bytes: vsndc.AsSpan((int)b.Offset, (int)b.Size).ToArray()))
            .ToList();
        resource.Blocks.Clear();
        foreach (var (type, bytes) in originals)
        {
            if (type == BlockType.DATA)
            {
                resource.Blocks.Add(new TypedRawBlock(BlockType.DATA, []) { Resource = resource }); // empty DATA
                resource.Blocks.Add(ctrl);                                                          // CTRL after DATA
            }
            // Drop RERL. A standalone modern sound is RED2+DATA+CTRL with NO external-reference list
            // (verified against pak01's own modern clips + the working community release). Our MP3→PCM
            // rebuild path (BuildSound) leaves an empty RERL behind; left in, the file decodes in VRF
            // but CS2 REJECTS the override and falls back to the stock (full-volume) clip - observed
            // in-game as the loud "ka-chink" on a knife hit while the (RERL-free) PCM swings muted fine.
            else if (type != BlockType.RERL)
            {
                resource.Blocks.Add(new TypedRawBlock(type, bytes) { Resource = resource });
            }
        }

        // Serialize the block section, trim VRF's trailing pad, and re-append the PCM streaming
        // tail exactly at the block-section end (= FileSize), same pattern as BuildSound.
        var serialized = Serialize(resource);
        var blockEnd = LastBlockEnd(serialized);
        var modern = new byte[blockEnd + pcm.Length];
        serialized.AsSpan(0, blockEnd).CopyTo(modern);
        pcm.AsSpan().CopyTo(modern.AsSpan(blockEnd));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(modern.AsSpan(0, 4), (uint)blockEnd);
        // Stamp the resource VERSION to 5 (header uint16 @6). The modern CVoiceContainer container
        // is vsnd version 5; the legacy source we converted from is v4. CS2's sound loader picks its
        // parser by this version: left at v4 it runs the LEGACY vsound-header path, fails on the
        // now-empty DATA block, and spams "[SoundSystem] WARNING: KV3 failed to parse legacy vsound
        // header" for every clip (it still falls through to the CTRL, so audio plays, but the console
        // floods). v5 makes CS2 read the CTRL directly - matching pak01's own modern clips (resVer 5)
        // and the working community release. VRF reads both via Sound.ConstructFromCtrl.
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(modern.AsSpan(6, 2), 5);
        return modern;
    }

    /// <summary>End offset of the last block in a serialized resource - i.e. where the vsnd
    /// streaming PCM must begin. Parses the block table directly: VRF's Sound block mutates its
    /// own <c>Offset</c> when a CTRL is present, so <c>snd.Offset</c> can't be trusted here.</summary>
    private static int LastBlockEnd(byte[] resource)
    {
        var span = resource.AsSpan();
        int blockOffset = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(span[8..]);
        int blockCount = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(span[12..]);
        int tableStart = 8 + blockOffset;
        int end = 0;
        for (int i = 0; i < blockCount; i++)
        {
            int o = tableStart + i * 12;
            int relOff = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(span[(o + 4)..]);
            int size = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(span[(o + 8)..]);
            end = Math.Max(end, (o + 4) + relOff + size);
        }
        return end;
    }

    /// <summary>
    /// Parse a WAV byte array and return only the raw PCM bytes from the
    /// 'data' chunk. Local copy of <c>SoundProcessor.ExtractWavData</c>; kept
    /// here to keep <see cref="RebuildSound"/> self-contained inside the
    /// resource-encode hub.
    /// </summary>
    private static byte[] ExtractWavData(byte[] wav)
    {
        const uint DataTag = 0x61746164; // "data"

        var span = wav.AsSpan();
        if (span.Length < 12)
            throw new InvalidDataException("Not a valid WAV file.");

        var pos = 12; // skip RIFF/file-size/WAVE
        while (pos + 8 <= span.Length)
        {
            var tag = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(span[pos..]);
            var size = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(span[(pos + 4)..]);
            var body = pos + 8;

            if (tag == DataTag)
                return span.Slice(body, Math.Min(size, span.Length - body)).ToArray();

            pos = body + ((size + 1) & ~1); // RIFF chunks are word-aligned
        }

        throw new InvalidDataException("WAV file has no data chunk.");
    }

    /// <summary>
    /// Build a KV3 Array of sub-objects from <paramref name="dict"/>,
    /// using <paramref name="entryFactory"/> to construct each element.
    /// </summary>
    private static KVObject BuildArray<TValue>(
        Dictionary<string, TValue> dict,
        Func<string, TValue, KVObject> entryFactory)
    {
        var arr = KVObject.Array(dict.Count);
        foreach (var (k, v) in dict)
            arr.Add(entryFactory(k, v));
        return arr;
    }

    /// <summary>Build a KV3 Array of four double elements from a Vector4.</summary>
    /// <remarks>CS2 stores vector components as FloatingPoint64 (f64), not f32.</remarks>
    private static KVObject Vec4Array(System.Numerics.Vector4 v)
    {
        var arr = KVObject.Array(4);
        arr.Add(new KVObject((double)v.X));
        arr.Add(new KVObject((double)v.Y));
        arr.Add(new KVObject((double)v.Z));
        arr.Add(new KVObject((double)v.W));
        return arr;
    }

    private static string EnsureCompiled(string path)
        => path.EndsWith("_c", StringComparison.OrdinalIgnoreCase) ? path : path + "_c";

    private static byte[]? FindFirstEntryBytes(Package pkg, string typeExtension)
    {
        // VpkService.EntriesOf throws if the package's entry table is
        // null, which would mean the Package was never Read(). Replaces
        // the previous `pkg.Entries!` null-forgiving operator that
        // silenced the warning without surfacing a useful error.
        if (!Io.VpkEntries.ByExtension(pkg).TryGetValue(typeExtension, out var list))
            return null;
        if (list is not { Count: > 0 })
            return null;

        var entry = list[0];
        pkg.ReadEntry(entry, out var bytes);
        return bytes;
    }

    private static int NextPow2(int n)
    {
        if (n <= 1)
            return 1;
        n--;
        n |= n >> 1;
        n |= n >> 2;
        n |= n >> 4;
        n |= n >> 8;
        n |= n >> 16;
        return n + 1;
    }

    /// <summary>
    /// A Block implementation that writes pre-built raw bytes as its serialized form.
    /// Used to embed a custom vtex_c DATA payload into a Resource container without
    /// going through Texture.Serialize() (which throws NotImplementedException).
    /// </summary>
    private sealed class RawDataBlock : Block
    {
        private readonly byte[] _data;

        public RawDataBlock(byte[] data) => _data = data;

        public override BlockType Type => BlockType.DATA;

        public override void Read(BinaryReader reader) { }

        public override void Serialize(Stream stream) => stream.Write(_data);

        public override void WriteText(IndentedTextWriter writer) { }
    }

    /// <summary>A block written verbatim from captured bytes under an arbitrary
    /// <see cref="BlockType"/> - used by <see cref="SerializeModelDataOnly"/> to
    /// pass PHYS/anim blocks through untouched.</summary>
    private sealed class TypedRawBlock : Block
    {
        private readonly byte[] _data;
        private readonly BlockType _type;

        public TypedRawBlock(BlockType type, byte[] data) { _type = type; _data = data; }

        public override BlockType Type => _type;

        public override void Read(BinaryReader reader) { }

        public override void Serialize(Stream stream) => stream.Write(_data);

        public override void WriteText(IndentedTextWriter writer) { }
    }
}
