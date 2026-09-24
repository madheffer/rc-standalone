using ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;

namespace Source2.Compiler.Maps;

/// <summary>
/// The material settings that decide whether a surface reaches the ray trace
/// environment, read from the compiled material the way the world renderer
/// reads them into a mesh entry's flags (<c>FUN_180252ae0</c>):
/// <c>mapbuilder.nodraw</c> or <c>mapbuilder.occluder</c> set bit 0,
/// <c>mapbuilder.visblocker</c> 0x10, <c>mapbuilder.acceptvis</c> 0x20 and
/// <c>mapbuilder.sky</c> 0x100. The .rte collector (<c>FUN_1802839d0</c>) drops
/// an entry whose flags have bit 0 without any of the other three.
/// </summary>
public sealed record MaterialVisFlags(bool NoDraw, bool Occluder, bool VisBlocker, bool AcceptVis, bool Sky)
{
    /// <summary>A material with none of the settings.</summary>
    public static readonly MaterialVisFlags None = new(false, false, false, false, false);

    /// <summary>Whether a surface in this material is left out of the .rte.</summary>
    public bool LeftOutOfTrace => (NoDraw || Occluder) && !(VisBlocker || AcceptVis || Sky);

    /// <summary>The flags of a compiled <c>.vmat_c</c>.</summary>
    public static MaterialVisFlags FromCompiled(byte[] bytes)
    {
        using var resource = new Resource();
        resource.Read(new MemoryStream(bytes));
        var ints = ((Material)resource.DataBlock!).IntAttributes;
        bool On(string key) => ints.TryGetValue(key, out var v) && v != 0;
        return new MaterialVisFlags(On("mapbuilder.nodraw"), On("mapbuilder.occluder"), On("mapbuilder.visblocker"),
                                    On("mapbuilder.acceptvis"), On("mapbuilder.sky"));
    }

    /// <summary>
    /// A lookup over compiled materials: loose files under the given
    /// directories first, then the given packages. Unknown materials read as
    /// <see cref="None"/>. Caches by name.
    /// </summary>
    public sealed class Source(IReadOnlyList<string> directories, IReadOnlyList<Package> packages)
    {
        private readonly Dictionary<string, MaterialVisFlags> _cache = new(StringComparer.OrdinalIgnoreCase);

        public MaterialVisFlags this[string material]
        {
            get
            {
                if (_cache.TryGetValue(material, out var known))
                    return known;
                var compiled = material.Replace('\\', '/') + "_c";
                MaterialVisFlags found = None;
                var loose = directories.Select(d => Path.Combine(d, compiled)).FirstOrDefault(File.Exists);
                if (loose is not null)
                    found = FromCompiled(File.ReadAllBytes(loose));
                else
                {
                    foreach (var package in packages)
                    {
                        if (package.FindEntry(compiled) is not { } entry)
                            continue;
                        package.ReadEntry(entry, out var bytes);
                        found = FromCompiled(bytes);
                        break;
                    }
                }
                return _cache[material] = found;
            }
        }
    }
}
