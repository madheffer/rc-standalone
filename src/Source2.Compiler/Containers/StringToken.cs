using System.Collections.Concurrent;
using System.Reflection;
using System.Text;

namespace Source2.Compiler;

/// <summary>
/// The engine's string token: the 32-bit hash Source 2 refers to a name by (a bone, a shader
/// parameter, an attribute, an entity key), MurmurHash2 of the lowercased name with seed 0x31415926.
/// <see cref="Lookup"/> names a token from the 7,102 entity keys ValveResourceFormat knows
/// (<c>Containers/KnownStrings.txt</c>, MIT) and every name <see cref="Store(string)"/> has seen.
/// </summary>
public static class StringToken
{
    /// <summary>The seed of the token hash.</summary>
    public const uint Seed = 0x31415926;

    private static readonly ConcurrentDictionary<uint, string> Known = new(LoadKnown());

    /// <summary>The token of <paramref name="name"/>, ignoring case.</summary>
    public static uint Get(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return MurmurHash2.Hash32(Encoding.UTF8.GetBytes(name.ToLowerInvariant()), Seed);
    }

    /// <summary>The name of <paramref name="token"/>, or null when it is not known.</summary>
    public static string? Lookup(uint token) => Known.GetValueOrDefault(token);

    /// <summary>Remember <paramref name="name"/> so <see cref="Lookup"/> finds its token; a token already known keeps its name.</summary>
    public static uint Store(string name)
    {
        var token = Get(name);
        Known.TryAdd(token, name);
        return token;
    }

    /// <summary>Remember several names.</summary>
    public static void Store(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        foreach (var name in names)
            Store(name);
    }

    private static Dictionary<uint, string> LoadKnown()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Source2.Compiler.KnownStrings.txt")
            ?? throw new InvalidOperationException("The known-strings resource is missing from the assembly.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var known = new Dictionary<uint, string>();
        while (reader.ReadLine() is { } line)
            if (line.Length > 0)
                known.TryAdd(Get(line), line);
        return known;
    }
}
