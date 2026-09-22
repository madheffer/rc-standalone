using System.Numerics;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// The sampler loop and the batch tracer, pinned to what <c>180018f10</c> and
/// <c>180016010</c> do with a ray rather than to any map's numbers.
/// </summary>
public class VisSamplerTests(ITestOutputHelper output)
{
    /// <summary>A generator that hands out one fixed list and then stops.</summary>
    private sealed class Fixed(IReadOnlyList<VisSampler.Ray> rays) : VisSampler.IGenerator
    {
        private int _given;

        public string Name => "FixedRayGenerator";

        public bool HasMoreWork => _given < rays.Count;

        public int Generate(Span<VisSampler.Ray> into)
        {
            var take = Math.Min(into.Length, rays.Count - _given);
            for (var i = 0; i < take; i++)
                into[i] = rays[_given + i];
            _given += take;
            return take;
        }
    }

    [Fact]
    public void TheBatchIsTheFourThousandNinetySixTheGeneratorIsAskedFor()
        => Assert.Equal(0x1000, VisSampler.BatchSize);

    /// <summary>
    /// A ray that leaves the scene records the whole line, but only when it was
    /// cast to measure sight. A surface ray that hits nothing records nothing at
    /// all, which is the <c>mode == 2</c> test on the miss path.
    /// </summary>
    [Fact]
    public void AMissRecordsTheFullLineOnlyForASightRay()
    {
        if (VisFixtures.RayTraceScene("s2c_rc_probe", "probe01") is not var (rte, _))
            return;

        var reach = (rte.Maxs - rte.Mins).Length();
        var outside = rte.Maxs + new Vector3(4096f, 4096f, 4096f);
        var away = Vector3.Normalize(new Vector3(1f, 1f, 1f));

        var sight = VisSampler.Trace(
            rte, [new VisSampler.Ray(outside, away, VisSampler.Purpose.Sight, true)], reach);
        var surface = VisSampler.Trace(
            rte, [new VisSampler.Ray(outside, away, VisSampler.Purpose.Surface, true)], reach);

        var line = Assert.Single(sight.Segments);
        Assert.Equal(outside, line.From);
        Assert.Equal(reach, (line.To - line.From).Length(), 1);
        Assert.True(sight.AnySight);

        Assert.Empty(surface.Segments);
        Assert.Empty(surface.Landings);
        Assert.False(surface.AnySight);
    }

    /// <summary>
    /// The two offsets the tracer applies, which are opposite and both one world
    /// unit: a segment stops one normal SHORT of the surface, and a landing is
    /// pushed one normal OUT of it.
    /// </summary>
    [Fact]
    public void ASegmentStopsShortOfTheSurfaceAndALandingSitsOutsideIt()
    {
        if (VisFixtures.RayTraceScene("s2c_rc_probe", "probe01") is not var (rte, _))
            return;

        var reach = (rte.Maxs - rte.Mins).Length();
        var centre = (rte.Mins + rte.Maxs) * 0.5f;

        var found = 0;
        foreach (var direction in VisClusterSample.Sphere)
        {
            if (rte.Trace(centre, direction, reach) is not { } hit)
                continue;

            var point = centre + (direction * hit.Distance);
            var front = Vector3.Dot(hit.Normal, direction) < 0f;
            var through = (rte.RawFlags(hit.Triangle) & RayTraceEnvironment.NoDrawInFile) != 0;

            var sight = VisSampler.Trace(
                rte, [new VisSampler.Ray(centre, direction, VisSampler.Purpose.Sight, true)], reach);
            var surface = VisSampler.Trace(
                rte, [new VisSampler.Ray(centre, direction, VisSampler.Purpose.Surface, true)], reach);

            var landing = Assert.Single(surface.Landings);
            Assert.Equal(point + hit.Normal, landing.Point);
            Assert.Equal(hit.Normal, landing.Normal);
            Assert.Equal(!front && !through, landing.Blocking);

            // A front face the ray may stop on ends the line; otherwise only a
            // see-through triangle records one on a sight ray.
            if ((front && !through) || through)
            {
                var line = Assert.Single(sight.Segments);
                Assert.Equal(point - hit.Normal, line.To);
            }
            else
            {
                Assert.Empty(sight.Segments);
            }

            found++;
        }

        Assert.True(found > 0, "no direction from the scene centre hit anything");
        output.WriteLine($"probe01  {found} of {VisClusterSample.Sphere.Length} directions hit");
    }

    /// <summary>The loop drains a generator in batches and totals what it cast.</summary>
    [Fact]
    public void TheLoopDrainsTheGeneratorAndCountsEveryRay()
    {
        if (VisFixtures.RayTraceScene("s2c_rc_probe", "probe01") is not var (rte, _))
            return;

        var centre = (rte.Mins + rte.Maxs) * 0.5f;
        var rays = Enumerable.Range(0, (VisSampler.BatchSize * 2) + 17)
            .Select(i => new VisSampler.Ray(
                centre, VisClusterSample.Sphere[i % VisClusterSample.Sphere.Length],
                VisSampler.Purpose.Sight, true))
            .ToList();

        var batches = 0;
        var generator = new Fixed(rays);
        var tally = VisSampler.Run(rte, generator, batch =>
        {
            batches++;
            return batch.Segments.Count;
        });

        Assert.False(generator.HasMoreWork);
        Assert.Equal(rays.Count, tally.Cast);
        Assert.Equal(3, batches);
        Assert.True(tally.Useful > 0, "every ray from the scene centre escaped, which cannot be");
        output.WriteLine($"probe01  cast {tally.Cast:n0} in {batches} batches,"
                       + $" {tally.Useful:n0} useful");
    }
}
