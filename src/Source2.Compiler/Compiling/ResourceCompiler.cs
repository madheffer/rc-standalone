using Source2.Compiler.Kv3;
using ValveKeyValue;

namespace Source2.Compiler.Compiling;

/// <summary>
/// Compiles one kind of source. Implementations are stateless and thread-safe; they throw
/// <see cref="InvalidOperationException"/>, <see cref="InvalidDataException"/> or
/// <see cref="FormatException"/> for a source that cannot compile, which
/// <see cref="ResourceCompiler"/> reports as an error diagnostic.
/// </summary>
public interface IResourceCompiler
{
    /// <summary>Source extensions handled, with the dot (<c>.vdata</c>).</summary>
    IReadOnlyCollection<string> SourceExtensions { get; }

    /// <summary>The content-relative output path for <paramref name="sourcePath"/>.</summary>
    string OutputPathFor(string sourcePath);

    /// <summary>Compile. <paramref name="diagnostics"/> collects warnings and notes.</summary>
    CompileOutput Compile(CompileRequest request, ICollection<CompileDiagnostic> diagnostics);
}

/// <summary>A compiler's product: the resource and any children it wrote.</summary>
public sealed record CompileOutput(byte[] Resource, IReadOnlyList<CompiledFile>? Children = null);

/// <summary>
/// The compiler's single entry point: give it a source path and bytes, get back the compiled
/// resource, its RED2 input dependencies and any diagnostics. Each source extension is handled
/// by a registered <see cref="IResourceCompiler"/>; <see cref="CreateDefault"/> registers every
/// type this library compiles. Thread-safe once built.
/// </summary>
public sealed class ResourceCompiler
{
    private readonly Dictionary<string, IResourceCompiler> _byExtension = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A compiler with every built-in type registered.</summary>
    public static ResourceCompiler CreateDefault() => new ResourceCompiler()
        .Register(new Kv3DocumentCompiler())
        .Register(new SoundCompiler())
        .Register(new VectorGraphicCompiler())
        .Register(new ImageTextureCompiler());

    /// <summary>Register <paramref name="compiler"/> for its extensions, replacing any earlier one.</summary>
    public ResourceCompiler Register(IResourceCompiler compiler)
    {
        foreach (var ext in compiler.SourceExtensions)
            _byExtension[ext] = compiler;
        return this;
    }

    /// <summary>Every source extension a compiler is registered for.</summary>
    public IReadOnlyCollection<string> SourceExtensions => _byExtension.Keys;

    public bool CanCompile(string sourcePath) => _byExtension.ContainsKey(Path.GetExtension(sourcePath));

    public CompileResult Compile(CompileRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var path = request.SourcePath.Replace('\\', '/').TrimStart('/');
        var ext = Path.GetExtension(path);
        var diagnostics = new List<CompileDiagnostic>();
        if (!_byExtension.TryGetValue(ext, out var compiler))
        {
            diagnostics.Add(new(DiagnosticSeverity.Error,
                $"No compiler for '{ext}' sources. Compiled here: {string.Join(", ", _byExtension.Keys.Order())}.", path));
            return new CompileResult { OutputPath = path + "_c", Diagnostics = diagnostics };
        }

        var outputPath = request.OutputPath?.Replace('\\', '/').TrimStart('/') ?? compiler.OutputPathFor(path);
        CompileOutput output;
        try
        {
            output = compiler.Compile(request with { SourcePath = path }, diagnostics);
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or FormatException or ArgumentException)
        {
            diagnostics.Add(new(DiagnosticSeverity.Error, ex.Message, path));
            return new CompileResult { OutputPath = outputPath, Diagnostics = diagnostics };
        }

        return new CompileResult
        {
            OutputPath = outputPath,
            Output = output.Resource,
            Children = output.Children ?? [],
            InputDependencies = ReadInputDependencies(output.Resource),
            Diagnostics = diagnostics,
        };
    }

    /// <summary>RED2's input and additional input dependencies of a compiled resource.</summary>
    public static IReadOnlyList<InputDependency> ReadInputDependencies(byte[] compiled)
    {
        var red2 = CompiledResource.Read(compiled).Kv3("RED2");
        if (red2 is null)
            return [];
        var list = new List<InputDependency>();
        foreach (var key in new[] { "m_InputDependencies", "m_AdditionalInputDependencies" })
        {
            if (red2[key] is not { } deps)
                continue;
            foreach (var d in deps.Children.Select(c => c.Value))
                list.Add(new InputDependency(
                    d.GetStringProperty("m_RelativeFilename", ""),
                    d.GetStringProperty("m_SearchPath", ""),
                    d.TryGetValue("m_nFileCRC", out var crc) ? AsUInt32(crc) : 0,
                    d.TryGetValue("m_bOptional", out var o) && (bool)o,
                    d.TryGetValue("m_bFileExists", out var e) && (bool)e));
        }
        return list;
    }

    private static uint AsUInt32(KVObject v) => v.ValueType switch
    {
        KVValueType.UInt32 => (uint)v,
        KVValueType.Int32 => unchecked((uint)(int)v),
        KVValueType.UInt64 => unchecked((uint)(ulong)v),
        KVValueType.Int64 => unchecked((uint)(long)v),
        _ => 0,
    };

    internal static string ReplaceExtension(string path, string newExtension)
        => (Path.GetExtension(path) is { Length: > 0 } ext ? path[..^ext.Length] : path) + newExtension;
}
