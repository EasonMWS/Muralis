using Muralis.Core.Motion;
using Xunit;

namespace Muralis.Core.Tests.Motion;

/// <summary>
/// Who is on the dock's rail — the rule that decides which icons the motion is allowed to drive.
/// </summary>
/// <remarks>
/// <para>
/// The dock used to re-derive this every layout pass from the icons' current drawn positions, which are the
/// motion's own output, so a wave that lifted an icon could push it out of the set that the wave was driving.
/// These tests pin the rule to the two properties that fix actually rests on: membership may only ever be
/// decided from a pose the motion cannot have written, and once an icon is on the rail it is never removed
/// because the wave moved it.
/// </para>
/// <para>
/// The numbers here are the ones measured on the running dock in Stage B rather than invented. At rest every
/// icon shares one row and the histogram of measured tops is {18: 45}; under a wave the same row smears to
/// {16: 1, 62: 2, 67: 42} and the dock window expands, shifting every one of those readings down by 49.
/// </para>
/// </remarks>
public sealed class DockRailMembershipTests
{
    private const int IconCount = 45;

    /// <summary>
    /// The top every icon shares at rest, and the one they share once the dock has expanded. Measured on the
    /// running dock: the expansion shifts the whole row by 49, so a pass never mixes these two readings.
    /// </summary>
    private const double RestingRow = 18;

    private const double ExpandedRow = 67;

    /// <summary>Every icon at the same top, which is what the dock actually measures when nothing is lifted.</summary>
    private static Dictionary<int, double> AtRest(double row = RestingRow) =>
        Enumerable.Range(0, IconCount).ToDictionary(i => i, _ => row);

    /// <summary>
    /// The measured smear under a wave. The row stays the row; the three icons the wave is working on are lifted
    /// by the amounts the profiler actually recorded for the rejected set — 4.59, 7.13 and 8.75 units below it,
    /// to two decimal places. Each of them is still on the rail; they are the icons the wave itself is driving.
    /// </summary>
    private static Dictionary<int, double> UnderWave(double row = RestingRow)
    {
        var tops = new Dictionary<int, double>();
        for (var i = 0; i < IconCount; i++)
        {
            tops[i] = i switch
            {
                0 => row - 4.59,
                1 => row - 7.13,
                2 => row - 8.75,
                _ => row,
            };
        }

        return tops;
    }

    /// <summary>
    /// What the old rule would have admitted: the same band, around the live median instead of a baseline. Used
    /// to prove the regression input can actually tell the two rules apart.
    /// </summary>
    private static int AdmittedByTheOldRule(IReadOnlyDictionary<int, double> tops)
    {
        var ordered = tops.Values.OrderBy(v => v).ToArray();
        var liveMedian = ordered[ordered.Length / 2];
        return tops.Values.Count(v => Math.Abs(v - liveMedian) <= DockRailMembership.ToleranceDip);
    }

    [Fact]
    public void AnAtRestPassEstablishesTheReferenceAndAdmitsEveryone()
    {
        var rail = new DockRailMembership();

        var members = rail.Observe(AtRest(), allAtRest: true);

        Assert.True(rail.HasReference);
        Assert.Equal(18, rail.ReferenceTop, 9);
        Assert.Equal(IconCount, members.Count);
        Assert.Equal(IconCount, rail.Count);
    }

    [Fact]
    public void AMeasuredSmearDoesNotEvictIconsThatAreStillOnTheRail()
    {
        // The regression, stated as the rule actually protects it. The three icons the wave is working on end up
        // outside the four-unit band — that is measured, not assumed, and it is why a per-pass rule loses them.
        // They were admitted while the dock was at rest, and membership is held by identity, so the wave moving
        // them cannot take them away. Before the fix this pass dropped them, they stopped being driven, and
        // because the dock only releases the icons it is driving they were never released either.
        var rail = new DockRailMembership();
        rail.Observe(AtRest(), allAtRest: true);
        Assert.Equal(IconCount, rail.Count);

        var wave = UnderWave();
        foreach (var lifted in new[] { 0, 1, 2 })
        {
            Assert.True(
                Math.Abs(wave[lifted] - rail.ReferenceTop) > DockRailMembership.ToleranceDip,
                $"icon {lifted} is inside the band, so this input does not exercise eviction at all");
        }

        var members = rail.Observe(wave, allAtRest: false);

        Assert.Equal(IconCount, members.Count);
        Assert.Equal(IconCount, rail.Count);
        Assert.True(rail.Contains(0));
        Assert.True(rail.Contains(1));
        Assert.True(rail.Contains(2));
    }

    [Fact]
    public void TheContaminatedRuleWouldHaveLostIconsOnTheSameInput()
    {
        // A guard on the guard. If this input could not tell the two rules apart then the regression test above
        // would pass whatever the implementation did. The old rule is restated as a band around the live median,
        // and on exactly the pass the fix loses nobody it has to lose somebody.
        var tops = UnderWave();

        var admitted = AdmittedByTheOldRule(tops);

        Assert.True(
            admitted < IconCount,
            $"the old rule admitted {admitted} of {IconCount}, so this input cannot distinguish the two rules");
    }

    [Fact]
    public void TheExpansionShiftDoesNotEvictTheWholeRail()
    {
        // The dock expands when the pointer arrives, and that shifts the entire row by forty-nine units without
        // anything being lifted. On that pass every icon measures far from a reference taken before the shift, so
        // a rule that re-decides membership from the current pose throws the whole rail away at once. Membership
        // is held across passes, which is what makes the transition safe.
        var rail = new DockRailMembership();
        rail.Observe(AtRest(RestingRow), allAtRest: true);
        Assert.Equal(IconCount, rail.Count);

        var members = rail.Observe(AtRest(ExpandedRow), allAtRest: false);

        Assert.Equal(IconCount, members.Count);
        Assert.Equal(IconCount, rail.Count);

        // Counting alone is not enough: a regression that pruned the member set while still returning the
        // accumulated order would leave both counts at forty-five. Membership itself has to be checked.
        Assert.True(rail.Contains(0));
        Assert.True(rail.Contains(IconCount - 1));
        Assert.True(rail.Contains(IconCount / 2));
    }

    [Fact]
    public void ALiftedIconIsStillAMember()
    {
        // The specific identity that used to be thrown away, at the value the profiler recorded for it.
        var rail = new DockRailMembership();
        rail.Observe(AtRest(), allAtRest: true);

        var tops = AtRest();
        tops[7] = -33.3699;
        rail.Observe(tops, allAtRest: false);

        Assert.True(rail.Contains(7));

        // And it stays a member once the wave has passed, rather than being stranded outside the set forever.
        rail.Observe(AtRest(ExpandedRow), allAtRest: true);
        Assert.True(rail.Contains(7));
    }

    [Fact]
    public void NoMemberIsEverLostWhileTheStructureIsUnchanged()
    {
        // A long run of moving poses, which is what sweeping the pointer across the dock produces. The membership
        // is what the dock draws with, so it may not shrink while the dock still holds the same icons.
        var rail = new DockRailMembership();
        rail.Observe(AtRest(), allAtRest: true);
        var established = rail.Count;

        var random = new Random(20260918);
        for (var pass = 0; pass < 500; pass++)
        {
            var tops = new Dictionary<int, double>();
            for (var i = 0; i < IconCount; i++)
            {
                // Anything from a full lift to the expansion shift, with the odd settled pass among them.
                tops[i] = random.Next(4) == 0 ? RestingRow : RestingRow + ((random.NextDouble() * 60) - 10);
            }

            rail.Observe(tops, allAtRest: false);
            Assert.Equal(established, rail.Count);
        }

        // The set was established from the at-rest row, so a wave around a different row must not have added to
        // it either: membership is allowed to be conservative, never wrong.
        Assert.Equal(IconCount, established);
    }

    [Fact]
    public void WithoutAnAtRestPassEveryIconIsKeptRatherThanGuessedAt()
    {
        // The dock can be entered before it has ever been seen at rest. There is no honest reference then, so
        // nothing may be dropped on a guess: magnifying one icon too many still leaves a working dock.
        var rail = new DockRailMembership();

        var members = rail.Observe(UnderWave(), allAtRest: false);

        Assert.False(rail.HasReference);
        Assert.Equal(IconCount, members.Count);
        Assert.Equal(IconCount, rail.AdmittedInLastPass);

        // And the first honest pass afterwards supplies the reference without taking anyone away.
        rail.Observe(AtRest(), allAtRest: true);
        Assert.True(rail.HasReference);
        Assert.Equal(RestingRow, rail.ReferenceTop, 9);
        Assert.Equal(IconCount, rail.Count);
    }

    [Fact]
    public void AChangedIconCountStartsTheStructureAgain()
    {
        // A shelf item appearing or a pinned app launching really does change the dock, so the previous decision
        // describes nothing and the members of the old shape are not carried into the new one.
        var rail = new DockRailMembership();
        rail.Observe(AtRest(), allAtRest: true);

        var fewer = Enumerable.Range(0, 20).ToDictionary(i => i, _ => 18.0);
        var members = rail.Observe(fewer, allAtRest: true);

        Assert.Equal(20, members.Count);
        Assert.Equal(20, rail.Count);
        Assert.True(rail.Contains(0));
        Assert.True(rail.Contains(19));
    }

    [Fact]
    public void AnEmptyPassEstablishesNothing()
    {
        // The dock drops icons that measured to nothing before membership is ever asked, so the policy only sees
        // icons that are really there. An empty pass must not establish a reference or invent a member, which is
        // what taking a reference from a median of nothing would do. This exercises the policy's own guard; the
        // dock's size filter itself lives in the app and is covered by the desktop suite, not from here.
        var rail = new DockRailMembership();

        var members = rail.Observe(new Dictionary<int, double>(), allAtRest: true);

        Assert.Empty(members);
        Assert.False(rail.HasReference);
        Assert.Equal(0, rail.Count);
    }

    [Fact]
    public void OnlyIconsAtTheReferenceAreAdmitted()
    {
        // An icon genuinely far from the rail is still left out. The members come back in the order the keys were
        // offered, which here is ascending because the fixture is built that way rather than because the policy
        // sorts: the dock sorts its own layers afterwards and does not rely on this.
        var rail = new DockRailMembership();

        var tops = new Dictionary<int, double>
        {
            [0] = 18,
            [1] = 18.5,
            [2] = 18,
            [3] = 90,
            [4] = 21,
        };

        var members = rail.Observe(tops, allAtRest: true);

        Assert.Equal([0, 1, 2, 4], members);
        Assert.True(rail.Contains(4));
        Assert.False(rail.Contains(3));
    }
}
