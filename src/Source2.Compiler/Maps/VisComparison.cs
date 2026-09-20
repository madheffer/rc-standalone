using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// Score one visibility build against another by asking the question the game
/// asks: can a viewer at A see a point at B.
///
/// <para>Two builds of the same map do not agree on cluster numbering or even
/// cluster count, so their PVS matrices cannot be diffed bit for bit. Point-pair
/// visibility is well defined in both, so it is the axis an error margin lives
/// on, and it splits the error into two very unequal halves. A HOLE - the
/// reference can see it and the candidate cannot - culls geometry the player
/// should see, and shows up in game as the world disappearing. OVERDRAW is only
/// a performance cost. A replacement is allowed to be looser and is not allowed
/// to be tighter.</para>
/// </summary>
public static class VisComparison
{
    /// <summary>
    /// Sample point pairs and compare visibility. Points that either build
    /// declines to place in a cluster are excluded from the pair statistics and
    /// counted separately, because a clustering difference is not a visibility
    /// difference and folding the two together would hide both.
    /// </summary>
    /// <param name="reference">The build treated as correct, normally Valve's.</param>
    /// <param name="candidate">The build being scored.</param>
    /// <param name="points">How many points to draw from the reference's occupied space; every unordered pair that both builds place is compared.</param>
    /// <param name="seed">Fixed so a run is reproducible.</param>
    public static VisReport Compare(VoxelVisibility reference, VoxelVisibility candidate,
                                    int points = 1500, int seed = 20260920)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(candidate);

        var left = new VoxelVisibilityQuery(reference);
        var right = new VoxelVisibilityQuery(candidate);
        var random = new Random(seed);

        // Draw from the space the REFERENCE says exists. Uniform sampling of the
        // bounding box lands in a cluster about 0.5% of the time on a real map,
        // and the reference is the definition of where space is anyway.
        var drawn = left.SampleOccupiedPoints(points, random);

        var placed = new List<(int Reference, int Candidate)>(drawn.Length);
        int referenceOnly = 0, candidateOnly = 0, neither = 0;
        foreach (var p in drawn)
        {
            var a = left.ClusterAt(p);
            var b = right.ClusterAt(p);
            if (a >= 0 && b >= 0)
                placed.Add((a, b));
            else if (a >= 0)
                referenceOnly++;
            else if (b >= 0)
                candidateOnly++;
            else
                neither++;
        }

        long agreeVisible = 0, agreeHidden = 0, holes = 0, overdraw = 0;
        for (var i = 0; i < placed.Count; i++)
        {
            for (var j = i + 1; j < placed.Count; j++)
            {
                var want = reference.CanSee((uint)placed[i].Reference, (uint)placed[j].Reference);
                var got = candidate.CanSee((uint)placed[i].Candidate, (uint)placed[j].Candidate);
                if (want && got) agreeVisible++;
                else if (!want && !got) agreeHidden++;
                else if (want) holes++;
                else overdraw++;
            }
        }

        return new VisReport(
            Attempts: drawn.Length,
            PlacedPoints: placed.Count,
            ReferenceOnlyPoints: referenceOnly,
            CandidateOnlyPoints: candidateOnly,
            NeitherPoints: neither,
            AgreeVisible: agreeVisible,
            AgreeHidden: agreeHidden,
            Holes: holes,
            Overdraw: overdraw);
    }
}

/// <summary>The outcome of <see cref="VisComparison.Compare"/>.</summary>
/// <param name="Attempts">Points drawn from the reference's occupied space.</param>
/// <param name="PlacedPoints">Points BOTH builds put in a cluster; pairs are drawn from these.</param>
/// <param name="ReferenceOnlyPoints">Points only the reference placed.</param>
/// <param name="CandidateOnlyPoints">Points only the candidate placed.</param>
/// <param name="NeitherPoints">Points neither placed; should be near zero, since the reference chose them.</param>
/// <param name="AgreeVisible">Pairs both builds call visible.</param>
/// <param name="AgreeHidden">Pairs both builds call hidden.</param>
/// <param name="Holes">Reference visible, candidate hidden. The failure that shows in game.</param>
/// <param name="Overdraw">Reference hidden, candidate visible. Costs frame time only.</param>
public readonly record struct VisReport(
    int Attempts,
    int PlacedPoints,
    int ReferenceOnlyPoints,
    int CandidateOnlyPoints,
    int NeitherPoints,
    long AgreeVisible,
    long AgreeHidden,
    long Holes,
    long Overdraw)
{
    /// <summary>Pairs compared.</summary>
    public long Pairs => AgreeVisible + AgreeHidden + Holes + Overdraw;

    /// <summary>Fraction of pairs the two builds answer the same way.</summary>
    public double Agreement => Pairs == 0 ? 1 : (double)(AgreeVisible + AgreeHidden) / Pairs;

    /// <summary>
    /// Holes as a fraction of what the reference calls visible. This is the
    /// number that has to be at or near zero: it is the share of genuinely
    /// visible geometry the candidate would cull away.
    /// </summary>
    public double HoleRate => AgreeVisible + Holes == 0 ? 0 : (double)Holes / (AgreeVisible + Holes);

    /// <summary>
    /// Overdraw as a fraction of what the reference calls hidden: the share of
    /// correctly culled geometry the candidate would draw anyway.
    /// </summary>
    public double OverdrawRate => AgreeHidden + Overdraw == 0 ? 0 : (double)Overdraw / (AgreeHidden + Overdraw);

    /// <summary>
    /// Fraction of drawn points the two builds disagree about placing at all.
    /// High here means the builds disagree on where space IS, which makes the
    /// pair statistics a comparison of two different maps.
    /// </summary>
    public double PlacementDisagreement
    {
        get
        {
            var placedByEither = PlacedPoints + ReferenceOnlyPoints + CandidateOnlyPoints;
            return placedByEither == 0 ? 0 : (double)(ReferenceOnlyPoints + CandidateOnlyPoints) / placedByEither;
        }
    }
}
