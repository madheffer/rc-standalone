using System.Text;

namespace Source2.Compiler;

/// <summary>
/// The engine's string token: the 32-bit hash Source 2 refers to a name by (a bone, a shader
/// parameter, an attribute), MurmurHash2 of the lowercased name with seed 0x31415926.
/// </summary>
public static class StringToken
{
    /// <summary>The seed of the token hash.</summary>
    public const uint Seed = 0x31415926;

    /// <summary>The token of <paramref name="name"/>, ignoring case.</summary>
    public static uint Get(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return MurmurHash2.Hash32(Encoding.UTF8.GetBytes(name.ToLowerInvariant()), Seed);
    }
}
