namespace Source2.Compiler.Compiling;

/// <summary>KV3 text documents: <c>.vsndevts</c>, <c>.vdata</c>, <c>.vpcf</c>, <c>.vagrp</c>.
/// Output: the source path plus <c>_c</c>.</summary>
public sealed class Kv3DocumentCompiler : IResourceCompiler
{
    /// <inheritdoc/>
    public IReadOnlyCollection<string> SourceExtensions { get; } =
        Source2ContainerAuthor.SpecByExtension.Where(kv => !kv.Value.ChildResource).Select(kv => kv.Key).ToArray();

    /// <inheritdoc/>
    public string OutputPathFor(string sourcePath) => sourcePath + "_c";

    /// <inheritdoc/>
    public CompileOutput Compile(CompileRequest request, ICollection<CompileDiagnostic> diagnostics)
        => new(Kv3SourceCompiler.Compile(request.Source, Path.GetExtension(request.SourcePath), request.SourcePath, request.SearchPath));
}

/// <summary>Settings for <see cref="SoundCompiler"/>.</summary>
/// <param name="Template">A <c>.vsnd_c</c> whose container frame to keep, or null to author one.</param>
public sealed record SoundSettings(byte[]? Template = null);

/// <summary>PCM WAV to <c>.vsnd_c</c> (same path, <c>.vsnd_c</c>).</summary>
public sealed class SoundCompiler : IResourceCompiler
{
    /// <inheritdoc/>
    public IReadOnlyCollection<string> SourceExtensions { get; } = [".wav"];

    /// <inheritdoc/>
    public string OutputPathFor(string sourcePath) => ResourceCompiler.ReplaceExtension(sourcePath, ".vsnd_c");

    /// <inheritdoc/>
    public CompileOutput Compile(CompileRequest request, ICollection<CompileDiagnostic> diagnostics)
        => new(ResourceBuilder.BuildSound((request.Settings as SoundSettings)?.Template, request.Source, request.SourcePath, request.SearchPath));
}

/// <summary>Settings for <see cref="VectorGraphicCompiler"/>.</summary>
/// <param name="Template">A <c>.vsvg_c</c> whose container frame to keep, or null to author one.</param>
public sealed record VectorGraphicSettings(byte[]? Template = null);

/// <summary>Panorama SVG to <c>.vsvg_c</c> (same path, <c>.vsvg_c</c>).</summary>
public sealed class VectorGraphicCompiler : IResourceCompiler
{
    /// <inheritdoc/>
    public IReadOnlyCollection<string> SourceExtensions { get; } = [".svg"];

    /// <inheritdoc/>
    public string OutputPathFor(string sourcePath) => ResourceCompiler.ReplaceExtension(sourcePath, ".vsvg_c");

    /// <inheritdoc/>
    public CompileOutput Compile(CompileRequest request, ICollection<CompileDiagnostic> diagnostics)
        => new(ResourceBuilder.BuildPanoramaSvg((request.Settings as VectorGraphicSettings)?.Template, request.Source, request.SourcePath, request.SearchPath));
}

/// <summary>Settings for <see cref="ImageTextureCompiler"/>: the texture options an image
/// compiles with when there is no <c>.vtex</c> describing it.</summary>
/// <param name="Texture">The options (format, mips, flags, BC7 encoder, sheet, mip chain...). Its
/// image, source name and search path are taken from the request; the object is not changed.</param>
/// <param name="Template">A <c>.vtex_c</c> whose container frame to keep, or null to author one.</param>
public sealed record ImageTextureSettings(ResourceBuilder.TextureDef? Texture = null, byte[]? Template = null);

/// <summary>
/// An image (PNG, TGA, JPEG, BMP, WebP) to <c>.vtex_c</c> (same path, <c>.vtex_c</c>), with
/// <see cref="ImageTextureSettings"/>. This is the in-house texture path, not yet
/// resourcecompiler's bytes; the byte-exact one compiles <c>.vtex</c> sources.
/// </summary>
public sealed class ImageTextureCompiler : IResourceCompiler
{
    /// <inheritdoc/>
    public IReadOnlyCollection<string> SourceExtensions { get; } = [".png", ".tga", ".jpg", ".jpeg", ".bmp", ".webp"];

    /// <inheritdoc/>
    public string OutputPathFor(string sourcePath) => ResourceCompiler.ReplaceExtension(sourcePath, ".vtex_c");

    /// <inheritdoc/>
    public CompileOutput Compile(CompileRequest request, ICollection<CompileDiagnostic> diagnostics)
    {
        var settings = request.Settings as ImageTextureSettings ?? new ImageTextureSettings();
        var def = (settings.Texture ?? new ResourceBuilder.TextureDef()).CopyFor(request.Source, request.SourcePath, request.SearchPath);
        return new(ResourceBuilder.BuildTexture(settings.Template, def));
    }
}
