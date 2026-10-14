// From ValveResourceFormat (MIT, https://github.com/ValveResourceFormat/ValveResourceFormat), RenderSlotType.cs.

namespace Source2.Compiler.Meshes;

/// <summary>
/// Render slot types for vertex data.
/// </summary>
/// <seealso href="https://s2v.app/SchemaExplorer/cs2/modellib/RenderSlotType_t">RenderSlotType_t</seealso>
public enum RenderSlotType
{
#pragma warning disable CS1591
    RENDER_SLOT_INVALID = -1,
    RENDER_SLOT_PER_VERTEX = 0,
    RENDER_SLOT_PER_INSTANCE = 1
#pragma warning restore CS1591
}
