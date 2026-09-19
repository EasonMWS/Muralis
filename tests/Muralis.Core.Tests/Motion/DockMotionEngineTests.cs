using Muralis.Core.Motion;
using Xunit;

namespace Muralis.Core.Tests.Motion;

/// <summary>
/// The Nexus Motion Engine's arithmetic. Every test here is about the shape of the wave and the space it
/// opens up, because those are the two things the dock's hand-feel is made of and the two things that
/// cannot be checked by looking at a screenshot.
/// </summary>
/// <remarks>
/// The model these pin down: the icon under the pointer keeps the size and the place it rests at, every
/// other icon grows by the influence curve, and every other icon is pushed aside by exactly the room the
/// icons between it and the peak have taken. That last part is what holds the dock's width still and keeps
/// the whole run's proportions — the constant gap between two ordinary neighbours survives the wave.
/// </remarks>
public sealed class DockMotionEngineTests
{
    /// <summary>The product baseline: 48 DIP icons, 4 DIP apart, at most 1.8 times as large.</summary>
    private static readonly DockMotionProfile Profile = DockMotionProfile.Default;

    private const double IconSize = 48;
    private const double Spacing = 4;
    private const double Pitch = IconSize + Spacing;

    /// <summary>Nine evenly spaced icons, which is enough to have icons outside the wave on both sides.</summary>
    private static DockMotionLayout Run(int count = 9)
    {
        var centres = new double[count];
        var widths = new double[count];
        for (var i = 0; i < count; i++)
        {
            centres[i] = i * Pitch;
            widths[i] = IconSize;
        }

        return new DockMotionLayout(centres, widths);
    }

    private static DockMotionEngine Applied(DockMotionLayout layout, double? pointer, DockMotionProfile? profile = null)
    {
        var engine = new DockMotionEngine(layout.Count);
        engine.Apply(layout, pointer, profile ?? Profile);
        return engine;
    }

    // ---------------------------------------------------------------------------------------------
    // The peak follows the pointer, not an icon.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void PointerOnAnIconCentreGivesThatIconTheFullScale()
    {
        var layout = Run();
        var engine = Applied(layout, layout.CentreAt(4));

        Assert.Equal(Profile.MaxScale, engine.Samples[4].Scale, 9);
        Assert.Equal(Profile.MaximumLift, engine.Samples[4].Lift, 9);
        Assert.Equal(1.0, engine.Samples[4].Influence, 9);

        // It is the largest, not merely large.
        Assert.All(engine.Samples, sample => Assert.True(sample.Scale <= engine.Samples[4].Scale + 1e-12));
    }

    [Fact]
    public void TheWaveStaysCentredOnThePointer()
    {
        // The peak has to give way for its own two neighbours, so it is not the icon that stays put. What
        // must hold is that the whole run opens outwards from the pointer rather than leaning to one side.
        var layout = Run(15);
        for (var i = 1; i < layout.Count - 1; i++)
        {
            var engine = Applied(layout, layout.CentreAt(i));

            // The icons left of the peak all move left, the icons right of it all move right.
            for (var j = 0; j < i; j++)
            {
                Assert.True(engine.Samples[j].TranslateX <= 1e-12, $"#{j} moved the wrong way for peak #{i}");
            }

            for (var j = i + 1; j < layout.Count; j++)
            {
                Assert.True(engine.Samples[j].TranslateX >= -1e-12, $"#{j} moved the wrong way for peak #{i}");
            }

            // And the peak sits exactly between its two neighbours' shifts, so the two halves of the room
            // it made are the same and the icon under the pointer does not slide towards either of them.
            var left = engine.Samples[i - 1].TranslateX;
            var right = engine.Samples[i + 1].TranslateX;
            Assert.Equal((left + right) / 2.0, engine.Samples[i].TranslateX, 9);

            // That also means the point halfway between the two neighbours — which is where the pointer is
            // when the pointer is on the icon's centre — is exactly the pointer.
            var between = ((layout.CentreAt(i - 1) + left) + (layout.CentreAt(i + 1) + right)) / 2.0;
            Assert.Equal(layout.CentreAt(i), between, 9);

            // However far the run moves, it stays inside the room the engine publishes for it.
            Assert.True(
                Math.Abs(engine.Samples[i].TranslateX) <= engine.HorizontalReach + 1e-9,
                $"the peak of #{i} moved {engine.Samples[i].TranslateX} with only {engine.HorizontalReach} reserved");
        }
    }

    [Fact]
    public void PointerBetweenTwoIconsGivesThemTheSameScale()
    {
        // This is the difference between a continuous wave and a hover: parked halfway between two icons,
        // neither of them may win.
        var layout = Run();
        var halfway = (layout.CentreAt(3) + layout.CentreAt(4)) / 2.0;
        var engine = Applied(layout, halfway);

        Assert.Equal(engine.Samples[3].Scale, engine.Samples[4].Scale, 12);
        Assert.Equal(engine.Samples[3].Lift, engine.Samples[4].Lift, 12);

        // The pointer is exactly between them and the run opens around it: the two icons move apart by the
        // room they both grew into the gap they share, which keeps that gap at the dock's own spacing.
        var gap = (layout.CentreAt(4) + engine.Samples[4].TranslateX - (IconSize * engine.Samples[4].Scale / 2.0))
            - (layout.CentreAt(3) + engine.Samples[3].TranslateX + (IconSize * engine.Samples[3].Scale / 2.0));
        Assert.Equal(Spacing, gap, 9);

        // The two of them are the top of the wave, and the icons either side are visibly smaller. Neither
        // reaches the ceiling: the ceiling belongs to an icon the pointer is exactly on, and the pointer is
        // between these two.
        Assert.Equal(engine.Samples[3].Scale, engine.PeakScale, 12);
        Assert.True(engine.Samples[3].Scale < Profile.MaxScale);
        Assert.True(engine.Samples[3].Scale > 1.6);
        Assert.True(engine.Samples[3].Scale > engine.Samples[2].Scale);
        Assert.True(engine.Samples[4].Scale > engine.Samples[5].Scale);
    }

    [Fact]
    public void ThePeakMovesSmoothlyAsThePointerMoves()
    {
        var layout = Run();
        var previous = Applied(layout, layout.CentreAt(4)).Samples[4].Scale;

        // A tenth of a pitch at a time, from one icon to the next. Nothing may jump.
        for (var step = 1; step <= 10; step++)
        {
            var pointer = layout.CentreAt(4) + (step * Pitch / 10.0);
            var engine = Applied(layout, pointer);
            var current = engine.Samples[4].Scale;

            Assert.True(current <= previous + 1e-12, $"the icon grew while the pointer walked away from it at step {step}");

            // A tenth of a pitch is 5.2 DIP, which is the largest step a single pointer update can produce
            // at this dock's spacing. A tenth of an icon's worth of growth across that step is far below
            // anything that could read as a jump; the measured worst case is about half of it.
            Assert.True(previous - current < 0.1, $"the wave jumped by {previous - current} at step {step}");
            previous = current;
        }

        // And the icon the pointer has reached is now the peak.
        var atFive = Applied(layout, layout.CentreAt(5));
        Assert.Equal(Profile.MaxScale, atFive.Samples[5].Scale, 9);
    }

    // ---------------------------------------------------------------------------------------------
    // The curve.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void TheMeasuredEnvelopeReadsAsAWave()
    {
        // The shape the product is aiming for, measured in drawn DIP at the peak and either side of it.
        var envelope = new[] { 3.0, 2.0, 1.5, 1.0, 0.5, 0.25, 0.0, 0.25, 0.5, 1.0, 1.5, 2.0, 3.0 }
            .Select(pitches => IconSize * DockMotionEngine.ScaleAt(pitches * Pitch, Profile))
            .ToArray();

        // The middle is the peak, 86.4 DIP, and three pitches out the dock is exactly as it was.
        Assert.Equal(IconSize * Profile.MaxScale, envelope[6], 6);
        Assert.Equal(IconSize, envelope[0], 6);
        Assert.Equal(IconSize, envelope[^1], 6);

        // It rises to the peak and falls away from it, with no step anywhere.
        for (var i = 1; i <= 6; i++)
        {
            Assert.True(envelope[i] > envelope[i - 1], $"the envelope did not grow towards the peak at {i}");
        }

        for (var i = 7; i < envelope.Length; i++)
        {
            Assert.True(envelope[i] < envelope[i - 1], $"the envelope did not fall away from the peak at {i}");
        }

        // Nobody is switched on: the icon beside the peak is most of the way there, the ones beyond it are
        // visibly part of the same wave, and only the third one out is close to resting.
        Assert.InRange(envelope[5], IconSize * 1.7, IconSize * Profile.MaxScale);
        Assert.InRange(envelope[7], IconSize * 1.7, IconSize * Profile.MaxScale);
        Assert.InRange(envelope[4], IconSize * 1.6, IconSize * 1.65);
        Assert.InRange(envelope[8], IconSize * 1.6, IconSize * 1.65);
        Assert.InRange(envelope[3], IconSize * 1.3, IconSize * 1.32);
        Assert.InRange(envelope[9], IconSize * 1.3, IconSize * 1.32);
        Assert.InRange(envelope[2], IconSize * 1.08, IconSize * 1.1);
        Assert.InRange(envelope[10], IconSize * 1.08, IconSize * 1.1);

        // Two pitches out the dock is within 2% of its resting size, which is what keeps the wave local
        // instead of the whole dock breathing.
        Assert.InRange(envelope[1], IconSize, IconSize * 1.02);
        Assert.InRange(envelope[11], IconSize, IconSize * 1.02);
    }

    [Fact]
    public void BeyondTheInfluenceRadiusNothingGrows()
    {
        var layout = Run(30);
        var engine = Applied(layout, layout.CentreAt(15));

        for (var i = 0; i < layout.Count; i++)
        {
            if (Math.Abs(layout.CentreAt(i) - layout.CentreAt(15)) >= Profile.InfluenceRadius)
            {
                Assert.Equal(1.0, engine.Samples[i].Scale, 12);
                Assert.Equal(0.0, engine.Samples[i].Lift, 12);
                Assert.Equal(0.0, engine.Samples[i].Influence, 12);
            }
        }

        // The wave is local: whatever else the run does, no icon outside the radius is any larger than it
        // rests at, and no icon inside it is left at rest.
        for (var i = 0; i < layout.Count; i++)
        {
            var inside = Math.Abs(layout.CentreAt(i) - layout.CentreAt(15)) < Profile.InfluenceRadius;
            Assert.Equal(inside, engine.Samples[i].Scale > 1.0);
        }

        // An icon that has not grown may still have been pushed: it has to make room for the wave beside
        // it. What it may never do is move further than the dock published it would.
        Assert.All(engine.Samples, sample =>
            Assert.True(Math.Abs(sample.TranslateX) <= engine.HorizontalReach + 1e-9));
    }

    [Fact]
    public void TheCurveFallsOffMonotonically()
    {
        var distances = new[] { 0.0, 6, 12, 24, 36, 48, 60, 90, 120, Profile.InfluenceRadius };
        var previous = double.MaxValue;

        foreach (var distance in distances)
        {
            var scale = DockMotionEngine.ScaleAt(distance, Profile);
            Assert.True(scale <= previous + 1e-12, $"the curve rose between distances at {distance}");
            previous = scale;
        }

        Assert.Equal(Profile.MaxScale, DockMotionEngine.ScaleAt(0, Profile), 12);
        Assert.Equal(1.0, DockMotionEngine.ScaleAt(Profile.InfluenceRadius, Profile), 12);
        Assert.Equal(1.0, DockMotionEngine.ScaleAt(Profile.InfluenceRadius * 4, Profile), 12);
    }

    [Fact]
    public void TheCurveHasNoCornerAtEitherEnd()
    {
        // A corner at the rim is what makes a far icon pop into existence; a corner at the top is what
        // makes the icon under the pointer look switched on. Both are measured as a derivative that does
        // not jump.
        const double step = 0.01;

        var atRim = DockMotionEngine.ScaleAt(Profile.InfluenceRadius - step, Profile) - 1.0;
        Assert.True(atRim < 0.001, $"the curve still had {atRim} of its range left one step inside the rim");
        Assert.Equal(1.0, DockMotionEngine.ScaleAt(Profile.InfluenceRadius + step, Profile), 12);

        var atTop = Profile.MaxScale - DockMotionEngine.ScaleAt(step, Profile);
        var atTwice = DockMotionEngine.ScaleAt(step, Profile) - DockMotionEngine.ScaleAt(2 * step, Profile);
        Assert.True(atTop < atTwice * 0.9, $"the curve is steeper at the peak ({atTop}) than just off it ({atTwice})");
    }

    [Fact]
    public void ScaleIsClampedToTheProfileCeiling()
    {
        var layout = Run(12);
        for (var i = 0; i < layout.Count; i++)
        {
            var engine = Applied(layout, layout.CentreAt(i));
            Assert.All(engine.Samples, sample => Assert.InRange(sample.Scale, 1.0, Profile.MaxScale + 1e-9));
            Assert.Equal(Profile.MaxScale, engine.PeakScale, 9);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Symmetry.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void TheWaveIsSymmetricAboutThePointer()
    {
        var layout = Run(11);
        var engine = Applied(layout, layout.CentreAt(5));

        for (var offset = 1; offset <= 5; offset++)
        {
            var left = engine.Samples[5 - offset];
            var right = engine.Samples[5 + offset];

            Assert.Equal(left.Scale, right.Scale, 12);
            Assert.Equal(left.Lift, right.Lift, 12);
            Assert.Equal(left.TranslateX, -right.TranslateX, 12);
        }
    }

    [Fact]
    public void MirroringTheDockMirrorsTheResult()
    {
        // Reflecting the dock in its own middle has to reflect every answer with it: the same place on the
        // rail gets the same size, and is pushed exactly as far the other way. Reflecting reverses the
        // order of the icons, so the icon at index i is the one at index count-1-i afterwards.
        var layout = Run(9);
        var pointer = layout.CentreAt(3) - (Pitch / 3.0);
        var engine = Applied(layout, pointer);

        var axis = (layout.CentreAt(0) + layout.CentreAt(layout.Count - 1)) / 2.0;
        var mirrored = new double[layout.Count];
        for (var i = 0; i < layout.Count; i++)
        {
            // Reflecting reverses the order, so the reflected run is written back left to right.
            mirrored[i] = (2 * axis) - layout.CentreAt(layout.Count - 1 - i);
        }

        var mirroredLayout = new DockMotionLayout(mirrored, [.. layout.Widths]);
        Assert.True(mirroredLayout.IsSane);
        var mirroredEngine = Applied(mirroredLayout, (2 * axis) - pointer);

        for (var i = 0; i < layout.Count; i++)
        {
            // The reflection maps index i to index count-1-i, and the engine has to agree exactly.
            var reflected = mirroredEngine.Samples[i];
            Assert.Equal(engine.Samples[layout.Count - 1 - i].Scale, reflected.Scale, 12);
            Assert.Equal(engine.Samples[layout.Count - 1 - i].Lift, reflected.Lift, 12);
            Assert.Equal(engine.Samples[layout.Count - 1 - i].TranslateX, -reflected.TranslateX, 12);
        }

        // And the whole thing really did move: a mirrored dock of resting icons would prove nothing.
        Assert.True(engine.IsActive);
        Assert.True(mirroredEngine.IsActive);
    }

    // ---------------------------------------------------------------------------------------------
    // Neighbour translation.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void NeighboursArePushedAwayAndThePushSaturates()
    {
        var layout = Run(15);
        var engine = Applied(layout, layout.CentreAt(7));

        // Icons left of the peak are pushed left, icons right of it are pushed right.
        for (var i = 0; i < 7; i++)
        {
            Assert.True(engine.Samples[i].TranslateX <= 1e-12, engine.Samples[i].ToString());
        }

        for (var i = 8; i < 15; i++)
        {
            Assert.True(engine.Samples[i].TranslateX >= -1e-12, engine.Samples[i].ToString());
        }

        // The push grows towards the peak and then levels off: the icons past the edge of the wave all
        // move by the same amount, because they are all making room for the same wave beside them.
        var magnitudes = engine.Samples.Select(sample => Math.Abs(sample.TranslateX)).ToArray();
        Assert.True(magnitudes[6] < magnitudes[5], "the push did not grow towards the peak");
        Assert.True(magnitudes[5] < magnitudes[4], "the push did not grow towards the peak");
        Assert.Equal(magnitudes[4], magnitudes[0], 9);
        Assert.Equal(magnitudes[4], magnitudes[3], 9);

        // Symmetric, because the wave is.
        for (var i = 0; i < 7; i++)
        {
            Assert.Equal(magnitudes[i], magnitudes[14 - i], 9);
        }
    }

    [Fact]
    public void NoTwoIconsEverOverlap()
    {
        var layout = Run(24);

        // Every pointer position that matters, including every gap between two icons.
        for (var step = 0; step <= 240; step++)
        {
            var pointer = layout.CentreAt(0) + (step * Pitch / 10.0);
            var engine = Applied(layout, pointer);
            AssertNoOverlap(engine, layout, pointer);
        }
    }

    [Fact]
    public void TheRunKeepsItsGapsRatherThanTearingAtThePeak()
    {
        // Every gap in the run is exactly the gap the dock was laid out with, including the two beside the
        // peak. That is the whole point of pushing each icon by the two half-widths that meet at its gap:
        // the run keeps its proportions instead of the peak bursting through its neighbours.
        var layout = Run(15);
        var engine = Applied(layout, layout.CentreAt(7));

        for (var i = 0; i < layout.Count - 1; i++)
        {
            var gap = LeftEdge(engine, layout, i + 1) - RightEdge(engine, layout, i);
            Assert.Equal(Spacing, gap, 6);
        }

        // And the run really did open: the icons at the ends of the dock are pushed by more than the
        // difference between the peak's size and its resting size, because they carry the room the whole
        // wave took rather than only the peak's own share of it.
        Assert.True(
            engine.HorizontalReach > (Profile.PeakIconSize - IconSize) / 2.0,
            $"the run only reached {engine.HorizontalReach} for a peak that grew by {Profile.PeakIconSize - IconSize}");
    }

    [Fact]
    public void NothingIsDrawnWhereTheDockEnds()
    {
        // The outermost icon may only stick out by as much as the wave says, and the engine has to
        // publish that number: it is what the dock window is sized from.
        var layout = Run(20);
        var engine = Applied(layout, layout.CentreAt(10));

        var expected = Math.Max(Math.Abs(engine.Samples[0].TranslateX), Math.Abs(engine.Samples[^1].TranslateX));
        Assert.Equal(expected, engine.HorizontalReach, 9);
        Assert.True(engine.HorizontalReach > 0);

        // At rest there is nothing to reserve.
        var atRest = Applied(layout, null);
        Assert.Equal(0.0, atRest.HorizontalReach, 12);
    }

    [Fact]
    public void TheDockKeepsItsWidthWhenNothingIsMagnified()
    {
        // The run's extent grows by the room the enlarged icons have taken and by nothing else, so a dock
        // cannot thrash its own width while the pointer sweeps across it.
        var layout = Run(13);
        var restingExtent = Extent(Applied(layout, null), layout, 0, layout.Count - 1);

        var widest = 0.0;
        for (var i = 0; i < layout.Count; i++)
        {
            widest = Math.Max(widest, Extent(Applied(layout, layout.CentreAt(i)), layout, 0, layout.Count - 1));
        }

        Assert.True(widest > restingExtent, "a magnified run should reach further than a resting one");

        // The extra is the room the enlarged icons took, and the same wave on either side of the pointer
        // takes the same amount: the run grows symmetrically, it does not lean.
        var growth = widest - restingExtent;
        var peakGrowth = Profile.PeakIconSize - IconSize;
        Assert.InRange(growth, peakGrowth, peakGrowth * (layout.Count - 1));

        var engine = Applied(layout, layout.CentreAt(6));
        var leftGrowth = layout.CentreAt(0) - IconSize / 2.0 - LeftEdge(engine, layout, 0);
        var rightGrowth = RightEdge(engine, layout, layout.Count - 1) - (layout.CentreAt(layout.Count - 1) + (IconSize / 2.0));
        Assert.Equal(leftGrowth, rightGrowth, 9);
    }

    // ---------------------------------------------------------------------------------------------
    // Direct manipulation: no memory, no lag, no state.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void TheSamePointerAlwaysGivesTheSameAnswer()
    {
        // A wave that has to catch up leaves a trail when the pointer reverses. Exact equality on the
        // return is the strongest statement that the engine has no memory of where the pointer has been.
        var layout = Run(11);
        var forward = new List<double>();
        var origin = layout.CentreAt(1);

        for (var step = 0; step <= 40; step++)
        {
            var at = origin + (step * Pitch / 8.0);
            forward.Add(Applied(layout, at).Samples[5].Scale);
        }

        for (var step = 40; step >= 0; step--)
        {
            var at = origin + (step * Pitch / 8.0);
            Assert.Equal(forward[step], Applied(layout, at).Samples[5].Scale, 15);
        }
    }

    [Fact]
    public void LeftAndRightApproachesAgreeExactly()
    {
        var layout = Run(9);
        var target = layout.CentreAt(4) + 7.5;
        var settled = Applied(layout, target).Samples;

        // Approach the same position from either side and the answer is identical, not merely close.
        var fromLeft = new DockMotionEngine(layout.Count);
        fromLeft.Apply(layout, target - 60, Profile);
        fromLeft.Apply(layout, target, Profile);

        var fromRight = new DockMotionEngine(layout.Count);
        fromRight.Apply(layout, target + 60, Profile);
        fromRight.Apply(layout, target, Profile);

        for (var i = 0; i < layout.Count; i++)
        {
            Assert.Equal(settled[i].Scale, fromLeft.Samples[i].Scale, 15);
            Assert.Equal(settled[i].Scale, fromRight.Samples[i].Scale, 15);
            Assert.Equal(settled[i].TranslateX, fromLeft.Samples[i].TranslateX, 15);
            Assert.Equal(settled[i].TranslateX, fromRight.Samples[i].TranslateX, 15);
        }
    }

    [Fact]
    public void ThePointerPathAllocatesNothing()
    {
        // The hot path runs on every pointer move, on a device that may be moving the mouse three hundred
        // times a second. Nothing here may build a list, a tuple or a closure.
        var layout = Run(30);
        var engine = new DockMotionEngine(layout.Count);
        engine.Apply(layout, layout.CentreAt(15), Profile);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            engine.Apply(layout, layout.CentreAt(0) + (i % 400), Profile);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated == 0, $"a thousand pointer updates allocated {allocated} bytes");
    }

    // ---------------------------------------------------------------------------------------------
    // Away, switched off, and broken input.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void AnAwayPointerPutsEveryIconBackToRest()
    {
        var layout = Run(9);
        var engine = new DockMotionEngine(layout.Count);

        engine.Apply(layout, layout.CentreAt(4), Profile);
        Assert.True(engine.IsActive);

        engine.Apply(layout, null, Profile);
        Assert.False(engine.IsActive);
        Assert.Equal(1.0, engine.PeakScale, 12);
        Assert.All(engine.Samples, sample => Assert.True(sample.IsAtRest, sample.ToString()));
    }

    [Fact]
    public void AReducedMotionProfileNeverMovesAnything()
    {
        var layout = Run(9);
        var engine = Applied(layout, layout.CentreAt(4), DockMotionProfile.Reduced);

        Assert.False(engine.IsActive);
        Assert.Equal(1.0, engine.PeakScale, 12);
        Assert.Equal(0.0, engine.HorizontalReach, 12);
        Assert.All(engine.Samples, sample => Assert.True(sample.IsAtRest, sample.ToString()));
    }

    [Fact]
    public void AProfileThatCannotMagnifyIsTheSameAsBeingAway()
    {
        var layout = Run(9);
        Assert.All(
            new[]
            {
                new DockMotionProfile { MaxScale = 1.0 },
                new DockMotionProfile { InfluenceRadiusInIcons = 0 },
                new DockMotionProfile { IsEnabled = false },
            },
            profile =>
            {
                var engine = Applied(layout, layout.CentreAt(4), profile);
                Assert.False(engine.IsActive);
                Assert.All(engine.Samples, sample => Assert.True(sample.IsAtRest, sample.ToString()));
            });
    }

    [Fact]
    public void AnEmptyDockIsNotAnError()
    {
        var engine = new DockMotionEngine(4);
        engine.Apply(DockMotionLayout.Empty, 10, Profile);

        Assert.Equal(0, engine.Count);
        Assert.False(engine.IsActive);
        Assert.Equal(0.0, engine.HorizontalReach, 12);
    }

    [Fact]
    public void ALayoutThatIsStillSettlingIsLeftAlone()
    {
        // Mid-layout the centres can momentarily collide or run backwards. Acting on that would flicker,
        // so the engine answers with rest and the dock draws its icons where the layout put them.
        var colliding = new DockMotionLayout([0, 64, 64, 192], [48, 48, 48, 48]);
        Assert.False(colliding.IsSane);

        var engine = Applied(colliding, 64);
        Assert.False(engine.IsActive);
        Assert.All(engine.Samples, sample => Assert.True(sample.IsAtRest, sample.ToString()));
    }

    [Fact]
    public void AnEngineSmallerThanTheDockSaysSoRatherThanOverrunning()
    {
        var engine = new DockMotionEngine(2);
        Assert.Throws<ArgumentException>(() => engine.Apply(Run(5), 0, Profile));
    }

    // ---------------------------------------------------------------------------------------------
    // The reserve the window is sized from.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void TheReserveCoversTheWidestWaveTheDockCanProduce()
    {
        foreach (var count in new[] { 2, 5, 10, 20, 30 })
        {
            var reserve = DockMotionEngine.ReserveFor(count, IconSize, Spacing, Profile);
            Assert.True(reserve > 0, $"{count} icons produced no reserve at all");

            // Walk a real draft of that dock and check the reserve really is an upper bound.
            var layout = Run(count);
            var engine = new DockMotionEngine(count);
            for (var step = 0; step <= count * 20; step++)
            {
                engine.Apply(layout, layout.CentreAt(0) + (step * Pitch / 20.0), Profile);
                Assert.True(
                    engine.HorizontalReach <= reserve + 1e-9,
                    $"a {count}-icon dock reached {engine.HorizontalReach} with only {reserve} reserved");
            }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExtremePeakVisualRectFitsInsideTheExpandedClient(bool leftPeak)
    {
        const int count = 30;
        var layout = Run(count);
        var reserve = DockMotionEngine.ReserveFor(count, IconSize, Spacing, Profile);
        var restingLeft = layout.CentreAt(0) - (IconSize / 2);
        var restingRight = layout.CentreAt(count - 1) + (IconSize / 2);
        var clientLeft = restingLeft - reserve;
        var clientRight = restingRight + reserve;
        var clientTop = -IconSize - Profile.VerticalReserve;
        const double clientBottom = 0;
        var engine = Applied(layout, leftPeak ? layout.CentreAt(0) : layout.CentreAt(count - 1));

        for (var i = 0; i < count; i++)
        {
            var sample = engine.Samples[i];
            var visualLeft = layout.CentreAt(i) + sample.TranslateX - (IconSize * sample.Scale / 2);
            var visualRight = layout.CentreAt(i) + sample.TranslateX + (IconSize * sample.Scale / 2);
            var visualTop = -(IconSize * sample.Scale) - sample.Lift;
            var visualBottom = -sample.Lift;

            Assert.True(visualLeft >= clientLeft - 1e-9, $"icon {i} clips left by {clientLeft - visualLeft:F4}");
            Assert.True(visualRight <= clientRight + 1e-9, $"icon {i} clips right by {visualRight - clientRight:F4}");
            Assert.True(visualTop >= clientTop - 1e-9, $"icon {i} clips top by {clientTop - visualTop:F4}");
            Assert.True(visualBottom <= clientBottom + 1e-9, $"icon {i} clips below the client baseline");
        }
    }

    [Fact]
    public void TheReserveIsZeroWhenThereIsNothingToMagnify()
    {
        Assert.Equal(0.0, DockMotionEngine.ReserveFor(0, IconSize, Spacing, Profile), 12);
        Assert.Equal(0.0, DockMotionEngine.ReserveFor(10, IconSize, Spacing, DockMotionProfile.Reduced), 12);
    }

    // ---------------------------------------------------------------------------------------------
    // The shift rule on its own.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void TheShiftRuleHoldsThePeakAndOpensTheRun()
    {
        // Three icons, each needing a different amount of room, with the peak at each end and in the middle.
        var halves = new[] { 19.2, 7.5106, 0.0 };
        var shifts = new double[3];

        // Peak on the first icon. It stays exactly where it rests — the hand is on it — and the run behind
        // it opens by the room each pair of neighbours grew into.
        DockMotionEngine.Shift(shifts, 3, halves, 0, 1.0);
        Assert.Equal(0.0, shifts[0], 12);
        Assert.Equal(halves[0] + halves[1], shifts[1], 12);
        Assert.Equal(halves[0] + (2 * halves[1]) + halves[2], shifts[2], 12);

        // Peak on the last: mirrored, and still held.
        var mirrored = new[] { halves[2], halves[1], halves[0] };
        var mirroredShifts = new double[3];
        DockMotionEngine.Shift(mirroredShifts, 3, mirrored, 2, 1.0);
        Assert.Equal(0.0, mirroredShifts[2], 12);
        Assert.Equal(-(mirrored[1] + mirrored[2]), mirroredShifts[1], 12);
        Assert.Equal(-(mirrored[0] + (2 * mirrored[1]) + mirrored[2]), mirroredShifts[0], 12);

        // Peak in the middle: both sides open, each by its own room.
        var middleShifts = new double[3];
        DockMotionEngine.Shift(middleShifts, 3, halves, 1, 1.0);
        Assert.Equal(0.0, middleShifts[1], 12);
        Assert.Equal(-(halves[0] + halves[1]), middleShifts[0], 12);
        Assert.Equal(halves[1] + halves[2], middleShifts[2], 12);

        // Half the spread is half the movement, so the Lab can dial the whole effect back.
        var halfSpread = new double[3];
        DockMotionEngine.Shift(halfSpread, 3, halves, 0, 0.5);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(shifts[i] / 2.0, halfSpread[i], 12);
        }

        // Nothing magnified means nothing moved.
        var flat = new double[3];
        DockMotionEngine.Shift(flat, 3, [0, 0, 0], 1, 1.0);
        Assert.All(flat, shift => Assert.Equal(0.0, shift, 12));

        // A single icon has nothing to make room for and stays put.
        var alone = new double[1];
        DockMotionEngine.Shift(alone, 1, [19.2], 0, 1.0);
        Assert.Equal(0.0, alone[0], 12);
    }

    // ---------------------------------------------------------------------------------------------
    // The profile itself.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void TheDefaultProfileIsTheConservativeBaseline()
    {
        Assert.Equal(48, Profile.BaseIconSize, 9);
        Assert.InRange(Profile.PeakIconSize, 84, 88);
        Assert.InRange(Profile.MaxScale, 1.75, 1.83);
        Assert.InRange(Profile.InfluenceRadiusInIcons, 2.8, 3.2);
        Assert.InRange(Profile.MaximumLift, 8, 12);
        Assert.InRange(Profile.SettleDurationMilliseconds, 180, 280);
    }

    [Fact]
    public void TheProfileReservesRoomForItsOwnPeak()
    {
        // 86.4 - 48 + 10 = 48.4 DIP above the baseline, which is what the dock window has to leave free.
        Assert.Equal(Profile.PeakIconSize - Profile.BaseIconSize + Profile.MaximumLift, Profile.VerticalReserve, 9);
        Assert.InRange(Profile.VerticalReserve, 40, 56);
    }

    private static double LeftEdge(DockMotionEngine engine, DockMotionLayout layout, int index) =>
        layout.CentreAt(index) + engine.Samples[index].TranslateX - (layout.Widths[index] * engine.Samples[index].Scale / 2.0);

    private static double RightEdge(DockMotionEngine engine, DockMotionLayout layout, int index) =>
        layout.CentreAt(index) + engine.Samples[index].TranslateX + (layout.Widths[index] * engine.Samples[index].Scale / 2.0);

    private static double Extent(DockMotionEngine engine, DockMotionLayout layout, int first, int last) =>
        RightEdge(engine, layout, last) - LeftEdge(engine, layout, first);

    private static void AssertNoOverlap(DockMotionEngine engine, DockMotionLayout layout, double pointer)
    {
        for (var i = 0; i < layout.Count - 1; i++)
        {
            var left = engine.Samples[i];
            var right = engine.Samples[i + 1];

            // The order of the icons is never allowed to change either.
            Assert.True(
                layout.CentreAt(i) + left.TranslateX < layout.CentreAt(i + 1) + right.TranslateX,
                $"icons {i} and {i + 1} swapped at pointer {pointer}");

            var gap = LeftEdge(engine, layout, i + 1) - RightEdge(engine, layout, i);
            Assert.True(
                gap >= -1e-9,
                $"icons {i} and {i + 1} overlapped by {-gap:F4} at pointer {pointer}");
        }
    }
}
