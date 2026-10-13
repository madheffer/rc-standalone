namespace Source2.Compiler.Compiling;

/// <summary>KV3 text documents: <c>.vsndevts</c>, <c>.vdata</c>, <c>.vpcf</c>, <c>.vagrp</c>.
/// Output: the source path plus <c>_c</c>.</summary>
public sealed class Kv3DocumentCompiler : IResourceCompiler
{
    public IReadOnlyCollection<string> SourceExtensions { get; } =
        Source2ContainerAuthor.SpecByExtension.Where(kv => !kv.Value.ChildResource).Select(kv => kv.Key).ToArray();

    public string OutputPathFor(string sourcePath) => sourcePath + "_c";

    public CompileOutput Compile(CompileRequest request, ICollection<CompileDiagnostic> diagnostics)
        => new(Kv3SourceCompiler.Compile(request.Source, Path.GetExtension(request.SourcePath), request.SourcePath, request.SearchPath));
}

/// <summary>Settings for <see cref="SoundCompiler"/>.</summary>
/// <param name="Template">A <c>.vsnd_c</c> whose container frame to keep, or null to author one.</param>
public sealed record SoundSettings(byte[]? Template = null);

/// <summary>PCM WAV to <c>.vsnd_c</c> (same path, <c>.vsnd_c</c>).</summary>
public sealed class SoundCompiler : IResourceCompiler
{
    public IReadOnlyCollection<string> SourceExtensions { get; } = [".wav"];

    public string OutputPathFor(string sourcePath) => ResourceCompiler.ReplaceExtension(sourcePath, ".vsnd_c");

    public CompileOutput Compile(CompileRequest request, ICollection<CompileDiagnostic> diagnostics)
        => new(ResourceBuilder.BuildSound((request.Settings as SoundSettings)?.Template, request.Source, request.SourcePath, request.SearchPath));
}

/// <summary>Settings for <see cref="VectorGraphicCompiler"/>.</summary>
/// <param name="Template">A <c>.vsvg_c</c> whose container frame to keep, or null to author one.</param>
public sealed record VectorGraphicSettings(byte[]? Template = null);

/// <summary>Panorama SVG to <c>.vsvg_c</c> (same path, <c>.vsvg_c</c>).</summary>
public sealed class VectorGraphicCompiler : IResourceCompiler
{
    public IReadOnlyCollection<string> SourceExtensions { get; } = [".svg"];

    public string OutputPathFor(string sourcePath) => ResourceCompiler.ReplaceExtension(sourcePath, ".vsvg_c");

    public CompileOutput Compile(CompileRequest request, ICollection<CompileDiagnostic> diagnostics)
        => new(ResourceBuilder.BuildPanoramaSvg((request.Settings as VectorGraphicSettings)?.Template, request.Source, request.SourcePath, request.SearchPath));
}

/// <summary>Settings for <see cref="ImageTextureCompiler"/>: the texture options an image
/// compiles with when there is no <c>.vtex</c> describing it.</summary>
public sealed record ImageTextureSettings
{
    public ResourceBuilder.TextureCompression Compression { get; init; } = ResourceBuilder.TextureCompression.BC7;
    public bool GenerateMipmaps { get; init; } = true;
    public ushort Flags { get; init; }
    public int MaxDimension { get; init; } = int.MaxValue;
    public ResourceBuilder.Bc7EncoderMode Bc7Mode { get; init; } = ResourceBuilder.Bc7EncoderMode.Cpu;
    public BCnEncoder.Encoder.CompressionQuality Quality { get; init; } = BCnEncoder.Encoder.CompressionQuality.Balanced;
    /// <summary>A <c>SHEET</c> payload (<see cref="SpriteSheet.Write"/>), or null.</summary>
    public byte[]? SheetData { get; init; }
    public IReadOnlyList<Source2ContainerAuthor.SpecialDep> EncodingSemantics { get; init; } = [];
    /// <summary>A <c>.vtex_c</c> whose container frame to keep, or null to author one.</summary>
    public byte[]? Template { get; init; }
}

/// <summary>
/// An image (PNG, TGA, JPEG, BMP, WebP) to <c>.vtex_c</c> (same path, <c>.vtex_c</c>), with
/// <see cref="ImageTextureSettings"/>. This is the in-house texture path, not yet
/// resourcecompiler's bytes; the byte-exact one compiles <c>.vtex</c> sources.
/// </summary>
public sealed class ImageTextureCompiler : IResourceCompiler
{
    public IReadOnlyCollection<string> SourceExtensions { get; } = [".png", ".tga", ".jpg", ".jpeg", ".bmp", ".webp"];

    public string OutputPathFor(string sourcePath) => ResourceCompiler.ReplaceExtension(sourcePath, ".vtex_c");

    public CompileOutput Compile(CompileRequest request, ICollection<CompileDiagnostic> diagnostics)
    {
        var s = request.Settings as ImageTextureSettings ?? new ImageTextureSettings();
        var def = new ResourceBuilder.TextureDef
        {
            ImageBytes = request.Source,
            SourceName = request.SourcePath,
            SearchPath = request.SearchPath,
            Compression = s.Compression,
            GenerateMipmaps = s.GenerateMipmaps,
            Flags = s.Flags,
            MaxDimension = s.MaxDimension,
            Bc7Mode = s.Bc7Mode,
            Quality = s.Quality,
            SheetData = s.SheetData,
        };
        def.EncodingSemantics.AddRange(s.EncodingSemantics);
        return new(ResourceBuilder.BuildTexture(s.Template, def));
    }
}
