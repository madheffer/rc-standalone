using System.Buffers.Binary;
using System.Text.Json;
using Source2.Compiler.Simulation;
using Xunit;

namespace Source2.Compiler.Tests;

/// <summary>
/// Joints in the island manager against Valve's settle of s2c_ragdollprobe:
/// the aggregates' bodies and joints added and made dynamic in the order the
/// capture recorded (settle_joints.js), then both islands at step 0, slot by
/// slot (bodies with their holes, joints in island order) and each joint's
/// header (active, island index, solver indices, colour).
///
/// <para>Set RAGDOLL to the capture's events.jsonl (capture_settle.py
/// s2c_rc_probe s2c_ragdollprobe --settle-only with settle_bundle.js and
/// settle_joints.js); without it the test does nothing.</para>
/// </summary>
public class RagdollIslandsCaptureTests
{
    [Fact]
    public void IslandsAtStepZeroEqualTheCapture()
    {
        if (Environment.GetEnvironmentVariable("RAGDOLL") is not { } path || !File.Exists(path))
            return;
        var aggregates = new List<List<(string Wrapper, string Rn, byte[] Body)>>();
        JsonElement? joints = null, islands = null;
        var typeTwo = new List<string>();
        foreach (var line in File.ReadLines(path))
        {
            using var doc = JsonDocument.Parse(line);
            var e = doc.RootElement;
            switch (e.GetProperty("ev").GetString())
            {
                case "aggregate":
                    aggregates.Add([.. e.GetProperty("bodies").EnumerateArray()
                        .Select(b => (b.GetProperty("wrapper").GetString()!, b.GetProperty("rn").GetString()!,
                                      Convert.FromHexString(b.GetProperty("body").GetString()!)))]);
                    break;
                case "build" when e.GetProperty("kind").GetString() == "type" && e.GetProperty("type").GetInt32() == 2:
                    typeTwo.Add(e.GetProperty("wrapper").GetString()!);
                    break;
                case "joints" when joints == null:
                    joints = e.GetProperty("joints").Clone();
                    break;
                case "islands" when islands == null:
                    islands = e.GetProperty("islands").Clone();
                    break;
            }
            if (joints != null && islands != null)
                break;
        }
        Assert.NotNull(joints);
        Assert.NotNull(islands);

        // The bodies as the aggregate build makes them: created, keyframed
        // (SetType 1 in 180044bc0) and awake, aggregate by aggregate.
        var w = new RnWorld();
        var byRn = new Dictionary<string, RnBody>();
        var byWrapper = new Dictionary<string, RnBody>();
        var name = new Dictionary<RnBody, string>();
        for (var k = 0; k < aggregates.Count; k++)
            for (var i = 0; i < aggregates[k].Count; i++)
            {
                var raw = aggregates[k][i].Body;
                var b = w.AddBody(new RnBodyState
                {
                    BodyType = 0, Flags249 = 1,
                    MinVelocityIterations = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(0x238)),
                    MinPositionIterations = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(0x23c)),
                });
                w.SetType(b, 1);
                ContactLifecycle.AddAwake(w, b);
                byRn[aggregates[k][i].Rn] = b;
                byWrapper[aggregates[k][i].Wrapper] = b;
                name[b] = $"{k}:{i}";
            }

        // The joints in world order, with the captured type, flags and size.
        var ours = new List<RnJoint>();
        var byPtr = new Dictionary<string, RnJoint>();
        var raws = new List<byte[]>();
        foreach (var j in joints.Value.EnumerateArray())
        {
            var raw = Convert.FromHexString(j.GetProperty("raw").GetString()!);
            raws.Add(raw);
            var type = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(0xc));
            var joint = type == RnJoint.ConicalType
                ? RnJoint.Of(new ConicalJointState { Type = type, Size = 0x1e8 })
                : RnJoint.Of(new RevoluteJointState { Type = type, Size = 0x1ac });
            ref var c = ref joint.Common;
            c.Flags = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(0x28));
            c.WorldIndex = c.BucketIndex = c.IslandIndex = c.SolverA = c.SolverB = -1;
            c.Colour = 0xff;
            Assert.Equal(BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(0xa8)), c.Size);
            w.AddJoint(joint, byRn[j.GetProperty("b1").GetString()!], byRn[j.GetProperty("b2").GetString()!]);
            ours.Add(joint);
            byPtr[j.GetProperty("ptr").GetString()!] = joint;
        }

        foreach (var wrapper in typeTwo)
            if (byWrapper.TryGetValue(wrapper, out var b))
                w.MakeDynamic(b);

        var theirs = islands.Value.GetProperty("all").EnumerateArray().Where(n => n.GetProperty("kind").GetInt32() == 1).ToList();
        var mine = w.Islands.All.OfType<RnIsland>().ToList();
        Assert.Equal(theirs.Count, mine.Count);
        foreach (var island in theirs)
        {
            var bodies = island.GetProperty("bodies").EnumerateArray()
                .Select(b => b.GetString() == "0x0" ? "-" : name[byRn[b.GetString()!]]).ToList();
            var match = mine.Single(m => m.Bodies.FirstOrDefault(b => b != null) is { } first && name[first] == bodies.First(b => b != "-"));
            Assert.Equal(string.Join(" ", bodies), string.Join(" ", match.Bodies.Select(b => b == null ? "-" : name[b])));
            Assert.Equal(string.Join(" ", island.GetProperty("joints").EnumerateArray().Select(j => byPtr[j.GetString()!].Common.WorldIndex)),
                         string.Join(" ", match.Joints.Select(j => j.Common.WorldIndex)));
            Assert.Equal(island.GetProperty("awake").GetInt32(), match.AwakeCount);
            Assert.Equal(island.GetProperty("iters")[0].GetInt32(), match.VelocityIterations);
        }
        for (var i = 0; i < ours.Count; i++)
        {
            ref var c = ref ours[i].Common;
            var raw = raws[i];
            Assert.Equal(raw[0x41], c.Active);
            Assert.Equal(raw[0x40], c.Colour);
            if (c.Active == 0)
                continue;
            Assert.Equal(BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(0x3c)), c.IslandIndex);
            Assert.Equal(BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(0x68)), c.SolverA);
            Assert.Equal(BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(0x6c)), c.SolverB);
        }
        Assert.Equal(theirs.Count, w.Islands.Serial.Count);
    }
}
