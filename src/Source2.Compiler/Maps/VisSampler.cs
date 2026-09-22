using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// The visibility sampler's ray loop, which is <c>180018f10</c> driving
/// <c>180016010</c>.
///
/// <para>The shipped loop is a thread pool: <c>180018f10</c> queues
/// <c>RayProcessJob</c> workers, keeps <c>threads * 2 + 64</c> batches of 4,096
/// rays in flight, and traces a batch on the calling thread too whenever the
/// pending queue passes 800. None of that changes the result, so what is ported
/// here is the order the work happens in, not the scheduling: pull batches off a
/// generator until it is done, trace each one, hand the segments to the
/// accumulator.</para>
///
/// <para>Everything the loop does with the hits beyond that lives in
/// <c>180016c60</c>, and it is debug only: both of its halves are gated on the
/// sampler's debug fields (<c>+0x108</c> line of sight capture, <c>+0x148</c> a
/// single probe cluster), so neither reaches the PVS.</para>
/// </summary>
public static class VisSampler
{
    /// <summary>Rays per batch, the <c>0x1000</c> the generator is asked for.</summary>
    public const int BatchSize = 4096;

    /// <summary>
    /// What a ray is for, the byte at <c>+0x1c</c> of the 32 byte ray record.
    /// </summary>
    public enum Purpose : byte
    {
        /// <summary>Collect the surface it lands on, and nothing else.</summary>
        Surface = 0,

        /// <summary>Record the line of sight it traced, which is what feeds the PVS.</summary>
        Sight = 2,
    }

    /// <param name="Origin">Where it starts, at <c>+0x00</c>.</param>
    /// <param name="Direction">Unit direction, at <c>+0x10</c>.</param>
    /// <param name="Use">The byte at <c>+0x1c</c>.</param>
    /// <param name="TwoSided">The byte at <c>+0x1d</c>, which lets a front face stop it.</param>
    public readonly record struct Ray(Vector3 Origin, Vector3 Direction, Purpose Use, bool TwoSided);

    /// <summary>One clear line, the 24 byte record the accumulator is handed.</summary>
    public readonly record struct Segment(Vector3 From, Vector3 To);

    /// <param name="Point">The hit point pushed one unit out along the normal.</param>
    /// <param name="Normal">The triangle's normal.</param>
    /// <param name="Blocking">The byte at <c>+0x18</c>: a back face that is not see through.</param>
    public readonly record struct Landing(Vector3 Point, Vector3 Normal, bool Blocking);

    /// <summary>What one traced batch produced.</summary>
    public sealed class Batch
    {
        /// <summary>The segments, which are the only thing the PVS is built from.</summary>
        public List<Segment> Segments { get; } = [];

        /// <summary>The surfaces <see cref="Purpose.Surface"/> rays landed on.</summary>
        public List<Landing> Landings { get; } = [];

        /// <summary>Whether any ray in it was a <see cref="Purpose.Sight"/> ray.</summary>
        public bool AnySight { get; set; }

        /// <summary>The count at <c>+0x30</c>, which the accumulator fills in.</summary>
        public int Useful { get; set; }
    }

    /// <summary>A source of rays, the object behind the generator vtable.</summary>
    public interface IGenerator
    {
        /// <summary>Its name, vtable <c>+0x18</c>, which the completion log prints.</summary>
        string Name { get; }

        /// <summary>Vtable <c>+0x08</c>, asked before every batch and after every one.</summary>
        bool HasMoreWork { get; }

        /// <summary>Vtable <c>+0x20</c>, only ever used to decide on a progress bar.</summary>
        int Estimate => 0;

        /// <summary>Vtable <c>+0x10</c>: fill up to <paramref name="into"/>, return how many.</summary>
        int Generate(Span<Ray> into);
    }

    /// <summary>What one generator's run cost, which is what the per generator log line reports.</summary>
    /// <param name="Cast">Rays generated and traced.</param>
    /// <param name="Useful">Rays the accumulator counted, summed over the batches.</param>
    public readonly record struct Tally(long Cast, long Useful);

    /// <summary>
    /// Run one generator to exhaustion. <paramref name="accumulate"/> stands in
    /// for the scene's vtable <c>+0x18</c>, which is handed the batch's segments
    /// and returns how many of them were useful.
    /// </summary>
    /// <param name="scene">The trace scene, whose diagonal sets the ray reach.</param>
    /// <param name="generator">The rays to cast.</param>
    /// <param name="accumulate">Turns a batch's segments into visibility.</param>
    public static Tally Run(
        RayTraceEnvironment scene, IGenerator generator, Func<Batch, int> accumulate)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(accumulate);

        // Every ray is cast the full diagonal of the scene box, computed once
        // at the top of 180018f10 and passed down to each trace.
        var reach = (scene.Maxs - scene.Mins).Length();
        var rays = new Ray[BatchSize];
        long cast = 0, useful = 0;

        while (generator.HasMoreWork)
        {
            var count = generator.Generate(rays);
            if (count <= 0)
                break;

            cast += count;
            var batch = Trace(scene, rays.AsSpan(0, count), reach);
            batch.Useful = accumulate(batch);
            useful += batch.Useful;
        }

        return new Tally(cast, useful);
    }

    /// <summary>
    /// <c>180016010</c>: trace one batch and sort what it found into segments and
    /// landings.
    /// </summary>
    /// <param name="scene">The trace scene.</param>
    /// <param name="rays">The batch.</param>
    /// <param name="reach">How far a ray runs, the scene diagonal.</param>
    public static Batch Trace(RayTraceEnvironment scene, ReadOnlySpan<Ray> rays, float reach)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var batch = new Batch();

        foreach (var ray in rays)
        {
            batch.AnySight |= ray.Use == Purpose.Sight;
            var hit = scene.Trace(ray.Origin, ray.Direction, reach);
            if (hit is null)
            {
                if (ray.Use == Purpose.Sight)
                    batch.Segments.Add(new Segment(ray.Origin, ray.Origin + (ray.Direction * reach)));
                continue;
            }

            var (distance, triangle, normal, _) = hit.Value;
            var point = ray.Origin + (ray.Direction * distance);
            var front = Vector3.Dot(normal, ray.Direction) < 0f;
            var through = (scene.RawFlags(triangle) & RayTraceEnvironment.NoDrawInFile) != 0;

            if (ray.Use == Purpose.Surface)
                batch.Landings.Add(new Landing(point + normal, normal, !front && !through));

            // A front face that the ray was allowed to stop on always ends the
            // line here. Anything else only records one when the ray was cast to
            // measure sight in the first place.
            if ((front && ray.TwoSided && !through) || (through && ray.Use == Purpose.Sight))
                batch.Segments.Add(new Segment(ray.Origin, point - normal));
        }

        return batch;
    }
}
