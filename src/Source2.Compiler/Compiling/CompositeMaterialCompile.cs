using Source2.Compiler.Io;
using ValveKeyValue;

namespace Source2.Compiler.Compiling;

/// <summary>
/// resourcecompiler's composite material kit compile (<c>.vcompmat</c> to <c>.vcompmat_c</c>),
/// CResourceCompilerVCompMat vf11: rc 0923 18015aa40 (1103 18015acf0). It refuses a kit without
/// <c>m_Points</c>, then runs two passes over it.
///
/// <para>The first (180159960), ported: <c>m_KVthumbnail</c> is removed from the root, and from
/// every point the seven preview members (m_ModelName, m_nSequenceIndex, m_flCycle,
/// m_nMeshGroupMask, m_bEnableChildModel, m_ChildModelName, m_KVModelStateChoices, 181eb70f0).
/// In every assembly procedure, an input container or property mutator whose <c>m_bEnabled</c>
/// is false (absent reads true, 181eae3f0) is removed. RC also checks that every
/// <c>m_vecCompMatIncludes</c> entry and every loose variable's
/// <c>m_strSpecificContainerMaterial</c> exists in the content tree and fails the compile when one
/// does not; that check changes no byte and is not made here.</para>
///
/// <para>The second (18015a270) compiles a child texture for every loose variable with a
/// <c>m_strTextureContentAssetPath</c>, from its <c>m_strTextureCompilationVtexTemplate</c>, and
/// points <c>m_strTextureRuntimeResourcePath</c> at it. Not ported: such a kit is refused.</para>
/// </summary>
public static class CompositeMaterialCompile
{
    private static readonly string[] PointPreviewKeys =
        ["m_ModelName", "m_nSequenceIndex", "m_flCycle", "m_nMeshGroupMask", "m_bEnableChildModel", "m_ChildModelName", "m_KVModelStateChoices"];

    /// <summary>Compile a parsed <c>.vcompmat</c> root.</summary>
    /// <exception cref="InvalidDataException">The kit has no m_Points.</exception>
    /// <exception cref="InvalidOperationException">The kit needs a child texture compile.</exception>
    public static KVObject Compile(KVObject root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (!root.IsCollection || Get(root, "m_Points") is not { } points)
            throw new InvalidDataException("resourcecompiler: a composite material kit needs m_Points.");

        return Rebuild(root, ["m_KVthumbnail"], (key, value) =>
            Tier0Strings.EqualsIgnoreCase(key, "m_Points") && value.IsArray
                ? KVObject.Array(value.Values.Select(Point).ToList())
                : value);
    }

    private static KVObject Point(KVObject point)
    {
        if (!point.IsCollection)
            return point;
        return Rebuild(point, PointPreviewKeys, (key, value) =>
            Tier0Strings.EqualsIgnoreCase(key, "m_vecCompositeMaterialAssemblyProcedures") && value.IsArray
                ? KVObject.Array(value.Values.Select(Procedure).ToList())
                : value);
    }

    private static KVObject Procedure(KVObject procedure)
    {
        if (!procedure.IsCollection)
            return procedure;
        return Rebuild(procedure, [], (key, value) =>
        {
            if (!value.IsArray)
                return value;
            if (Tier0Strings.EqualsIgnoreCase(key, "m_vecCompositeInputContainers"))
                return KVObject.Array(value.Values.Where(Enabled).Select(Container).ToList());
            if (Tier0Strings.EqualsIgnoreCase(key, "m_vecPropertyMutators"))
                return KVObject.Array(value.Values.Where(Enabled).ToList());
            return value;
        });
    }

    private static KVObject Container(KVObject container)
    {
        if (Get(container, "m_vecLooseVariables") is { IsArray: true } variables)
            foreach (var variable in variables.Values)
                if (Get(variable, "m_strTextureContentAssetPath") is { ValueType: KVValueType.String } path && ((string)path).Length > 0)
                    throw new InvalidOperationException(
                        $"A composite material kit's texture input (\"{(string)path}\") is not ported: resourcecompiler compiles a child "
                      + "texture from its m_strTextureCompilationVtexTemplate (18015a270).");
        return container;
    }

    // 181eae3f0 with default true. A string is read by tier0's own parse, which is not ported.
    private static bool Enabled(KVObject entry)
    {
        if (Get(entry, "m_bEnabled") is not { } v)
            return true;
        return v.ValueType switch
        {
            KVValueType.Boolean => (bool)v,
            KVValueType.Int16 or KVValueType.Int32 or KVValueType.Int64 => (long)v != 0,
            KVValueType.UInt16 or KVValueType.UInt32 or KVValueType.UInt64 => (ulong)v != 0,
            _ => throw new InvalidOperationException($"An m_bEnabled of type {v.ValueType} is not ported."),
        };
    }

    private static KVObject? Get(KVObject table, string key)
    {
        if (!table.IsCollection)
            return null;
        foreach (var child in table.Children)
            if (Tier0Strings.EqualsIgnoreCase(child.Key, key))
                return child.Value;
        return null;
    }

    private static KVObject Rebuild(KVObject table, string[] remove, Func<string, KVObject, KVObject> map)
    {
        var result = KVObject.Collection();
        foreach (var child in table.Children)
            if (!remove.Any(r => Tier0Strings.EqualsIgnoreCase(r, child.Key)))
                result.Add(child.Key, map(child.Key, child.Value));
        return result;
    }
}
