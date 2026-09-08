using Source2.Compiler.Io;
using ValveResourceFormat;
using ValveResourceFormat.CompiledShader;
using ValveResourceFormat.IO;

namespace Source2.Compiler.Io;

/// <summary>
/// Resolves VRF resource references against files on the local filesystem.
/// Tries the VRF-style relative path first (e.g. "models/foo.vmesh_c" → BaseDir/models/foo.vmesh_c),
/// then falls back to a filename-only search through the whole tree.
/// </summary>
public sealed class DirFileLoader(string baseDir) : IFileLoader
{
    public string BaseDir { get; } = baseDir;

    public Resource? LoadFile(string file)
    {
        var path = Resolve(file);
        if (path is null)
            return null;
        var r = new Resource { FileName = file };
        r.Read(path);
        return r;
    }

    public Resource? LoadFileCompiled(string file)
    {
        var compiled = file.EndsWith("_c", StringComparison.OrdinalIgnoreCase) ? file : file + "_c";
        return LoadFile(compiled) ?? LoadFile(file);
    }

    public ShaderCollection? LoadShader(string shaderName) => null;

    /// <summary>Opens a raw (uncompiled) file for reading. Goes through the same
    /// <see cref="Resolve"/> as every other lookup, so the SafePath traversal
    /// guard covers it too - VRF calls this with strings taken from a resource
    /// that may be user-uploaded.</summary>
    public Stream? GetFileStream(string file)
    {
        if (string.IsNullOrEmpty(file))
            return null;
        var path = Resolve(file);
        return path is null ? null : File.OpenRead(path);
    }

    // Filename -> full path, built once on first use. The old filename fallback
    // re-scanned the whole tree per unresolved reference (O(tree) each time)
    // and returned a non-deterministic first match; the index makes it one
    // scan total with a deterministic pick on a duplicate filename.
    private Dictionary<string, string>? _byFileName;

    private Dictionary<string, string> FileNameIndex()
    {
        if (_byFileName is not null)
            return _byFileName;
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(BaseDir, "*", SearchOption.AllDirectories)
                                      .OrderBy(p => p, StringComparer.Ordinal))
            index.TryAdd(Path.GetFileName(path), path);
        return _byFileName = index;
    }

    private string? Resolve(string vrfPath)
    {
        // SafePath guard: vrfPath comes from a parsed resource's external references,
        // and that resource can be user-uploaded (an upload endpoint, a URL
        // import) — a crafted "../../../etc/passwd" must NOT resolve outside BaseDir.
        // Strip a leading separator first (some VRF refs are root-relative, e.g.
        // "/models/foo") so legit paths still resolve, then TryJoinUnderRoot rejects
        // any "../"/rooted escape.
        var rel = vrfPath.TrimStart('/', '\\');
        if (SafePath.TryJoinUnderRoot(BaseDir, rel, out var full) && File.Exists(full))
            return full;

        // Filename-only fallback returns ONLY paths enumerated under BaseDir, so it
        // is already traversal-safe (the filename strips any directory component).
        return FileNameIndex().GetValueOrDefault(Path.GetFileName(vrfPath));
    }
}
