namespace Source2.Compiler.Compiling;

/// <summary>
/// One source to compile, the way resourcecompiler sees it: a content-relative path
/// (<c>materials/foo.vmat</c>, <c>soundevents/bar.vsndevts</c>) and the bytes found there.
/// </summary>
/// <param name="SourcePath">Content-relative, forward slashes. Recorded in RED2 and used to
/// name the output.</param>
/// <param name="Source">The source file's bytes.</param>
public sealed record CompileRequest(string SourcePath, byte[] Source)
{
    /// <summary>The content tree RC would have compiled from, recorded with every input
    /// dependency: <c>csgo</c>, or <c>csgo_addons/&lt;addon&gt;</c>.</summary>
    public string SearchPath { get; init; } = "csgo";

    /// <summary>Where files the source refers to are read from. Null: the source stands alone.</summary>
    public IContentSource? Content { get; init; }

    /// <summary>Content-relative output path; null takes the compiler's default.</summary>
    public string? OutputPath { get; init; }

    /// <summary>Settings a compiler accepts beyond its source (for example
    /// <see cref="ImageTextureSettings"/>); each compiler documents its own type.</summary>
    public object? Settings { get; init; }
}

/// <summary>What a compile produced, or why it did not.</summary>
public sealed class CompileResult
{
    /// <summary>Content-relative path of the compiled resource (<c>..._c</c>).</summary>
    public required string OutputPath { get; init; }

    /// <summary>The compiled resource; null when the compile failed.</summary>
    public byte[]? Output { get; init; }

    /// <summary>Further resources the compile wrote (a map's children, for instance).</summary>
    public IReadOnlyList<CompiledFile> Children { get; init; } = [];

    /// <summary>The input dependencies recorded in the output's RED2, in order.</summary>
    public IReadOnlyList<InputDependency> InputDependencies { get; init; } = [];

    public IReadOnlyList<CompileDiagnostic> Diagnostics { get; init; } = [];

    public bool Succeeded => Output is not null && !Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
}

/// <summary>A compiled file beside the main output.</summary>
public sealed record CompiledFile(string Path, byte[] Bytes);

/// <summary>A file a compile read (or looked for), as RED2 records it.</summary>
public sealed record InputDependency(string Path, string SearchPath, uint Crc, bool Optional, bool Exists);

public enum DiagnosticSeverity { Info, Warning, Error }

/// <summary>A message about a compile, with a location when it has one (1-based; 0 = none).</summary>
public sealed record CompileDiagnostic(DiagnosticSeverity Severity, string Message, string? Path = null, int Line = 0, int Column = 0)
{
    public override string ToString()
        => (Path is null ? "" : Line > 0 ? $"{Path}({Line},{Column}): " : $"{Path}: ")
           + $"{Severity.ToString().ToLowerInvariant()}: {Message}";
}

/// <summary>Reads content-relative files for a compile (the files a source refers to).</summary>
public interface IContentSource
{
    /// <summary>The file at <paramref name="contentPath"/> (forward slashes), or null when absent.</summary>
    byte[]? Read(string contentPath);
}

/// <summary>Content from one or more directories, searched in order, the way RC searches its
/// mod trees (an addon's content first, then the game's).</summary>
public sealed class DirectoryContentSource(params string[] roots) : IContentSource
{
    public byte[]? Read(string contentPath)
    {
        var relative = contentPath.Replace('\\', '/').TrimStart('/');
        if (relative.Split('/').Any(part => part == ".."))
            return null;
        foreach (var root in roots)
        {
            var full = Path.Combine(root, relative);
            if (File.Exists(full))
                return File.ReadAllBytes(full);
        }
        return null;
    }
}

/// <summary>Content held in memory, keyed by content-relative path (case-insensitive).</summary>
public sealed class MemoryContentSource : IContentSource
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);

    public MemoryContentSource Add(string contentPath, byte[] bytes)
    {
        _files[contentPath.Replace('\\', '/').TrimStart('/')] = bytes;
        return this;
    }

    public byte[]? Read(string contentPath)
        => _files.TryGetValue(contentPath.Replace('\\', '/').TrimStart('/'), out var b) ? b : null;
}
