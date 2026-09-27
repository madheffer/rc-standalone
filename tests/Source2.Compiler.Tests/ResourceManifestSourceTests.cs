using System.IO.Hashing;
using System.Text;
using Xunit;

namespace Source2.Compiler.Tests;

/// <summary>
/// The .vrman source RC saves before compiling a physics manifest
/// (<see cref="ResourceManifestAuthor.SourceText"/>): its CRC32 is the
/// m_nFileCRC in Valve's world_physics.vrman_c, read from each map's package.
/// </summary>
public class ResourceManifestSourceTests
{
    [Theory]
    [InlineData("atixref", 1205653286u)]
    [InlineData("ze_hold_em_p", 1042875400u)]
    [InlineData("ze_hold_em_paint", 1232725920u)]
    [InlineData("ze_hold_em_nb", 3609167039u)]
    public void PhysicsManifestCrc(string map, uint crc)
        => Assert.Equal(crc, Crc32.HashToUInt32(Encoding.UTF8.GetBytes(ResourceManifestAuthor.SourceText([$"maps/{map}/world_physics.vmdl"]))));
}
