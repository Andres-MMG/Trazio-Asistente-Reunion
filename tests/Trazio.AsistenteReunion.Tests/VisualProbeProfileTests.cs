using System.Buffers;
using Trazio.AsistenteReunion.App;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.Tests;

public sealed class VisualProbeProfileTests
{
    [Theory]
    [InlineData(MeetingProvider.GoogleMeet)]
    [InlineData(MeetingProvider.MicrosoftTeams)]
    [Trait("Area", "VisualCapture")]
    public void GetProduction_ForKnownProvider_IsUnvalidatedAndContainsNoDetectionConfiguration(
        MeetingProvider provider)
    {
        var profile = VisualProbeProfiles.GetProduction(provider);

        Assert.Equal(provider, profile.Provider);
        Assert.Equal(AnonymousVisualEvidenceVersions.CurrentProfile, profile.Version);
        Assert.Equal(VisualProbeProfileValidationState.Unvalidated, profile.ValidationState);
        Assert.Equal(0, profile.ExpectedFeatureCount);
        Assert.Null(profile.DetectionPolicy);
    }

    [Theory]
    [InlineData(MeetingProvider.NotSelected)]
    [InlineData(MeetingProvider.Other)]
    [Trait("Area", "VisualCapture")]
    public void GetProduction_ForUnsupportedProvider_DeclaresUnsupportedWithoutDetectionConfiguration(
        MeetingProvider provider)
    {
        var profile = VisualProbeProfiles.GetProduction(provider);

        Assert.Equal(VisualProbeProfileValidationState.Unsupported, profile.ValidationState);
        Assert.Equal(0, profile.ExpectedFeatureCount);
        Assert.Null(profile.DetectionPolicy);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void Dispose_WithRentedFeatures_ClearsAndReturnsBufferExactlyOnce()
    {
        var pool = new TrackingFeaturePool();
        var profile = new SyntheticValidatedProfile(7, 2, TestPolicy());
        var observation = VisualProbeObservation.Available(TimeSpan.Zero, 1, profile);
        var lease = VisualProbeFeatureLease.Create(
            observation,
            [new(900), new(850)],
            pool);

        Assert.Equal([new VisualProbeFeature(900), new VisualProbeFeature(850)], lease.Features.ToArray());

        lease.Dispose();
        lease.Dispose();

        Assert.Equal(1, pool.ReturnCount);
        Assert.All(pool.ReturnedSnapshot!, feature => Assert.Equal(default, feature));
        Assert.Throws<ObjectDisposedException>(() => lease.CopyFeatures());
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void ProbeContracts_ExposeOnlyAggregateNumericEvidence()
    {
        Type[] contractTypes =
        [
            typeof(VisualProbeFeature),
            typeof(VisualProbeObservation),
            typeof(VisualProbeFeatureLease),
            typeof(IVisualProbeProfile),
            typeof(VisualProbeDetectionPolicy)
        ];
        string[] forbiddenNames = ["Pixel", "Bgra", "Image", "Video", "Text", "Region", "Roi"];

        var properties = contractTypes.SelectMany(type => type.GetProperties()).ToArray();

        Assert.DoesNotContain(properties, property => property.PropertyType == typeof(byte[]));
        Assert.DoesNotContain(properties, property =>
            forbiddenNames.Any(forbidden => property.Name.Contains(forbidden, StringComparison.OrdinalIgnoreCase)));
    }

    private static VisualProbeDetectionPolicy TestPolicy() => new(
        enterThreshold: 800,
        exitThreshold: 200,
        activationHold: TimeSpan.FromSeconds(1),
        releaseHold: TimeSpan.FromSeconds(1),
        maxObservationGap: TimeSpan.FromSeconds(2),
        minimumCoherentPatches: 2,
        evidenceVersion: 3,
        detectorVersion: 4,
        policyVersion: 5);

    private sealed record SyntheticValidatedProfile(
        int Version,
        int ExpectedFeatureCount,
        VisualProbeDetectionPolicy DetectionPolicy) : IVisualProbeProfile
    {
        public MeetingProvider Provider => MeetingProvider.GoogleMeet;
        public VisualProbeProfileValidationState ValidationState => VisualProbeProfileValidationState.Validated;
        VisualProbeDetectionPolicy? IVisualProbeProfile.DetectionPolicy => DetectionPolicy;
    }

    private sealed class TrackingFeaturePool : ArrayPool<VisualProbeFeature>
    {
        public int ReturnCount { get; private set; }
        public VisualProbeFeature[]? ReturnedSnapshot { get; private set; }

        public override VisualProbeFeature[] Rent(int minimumLength) =>
            new VisualProbeFeature[Math.Max(minimumLength, 4)];

        public override void Return(VisualProbeFeature[] array, bool clearArray = false)
        {
            ReturnCount++;
            ReturnedSnapshot = [.. array];
        }
    }
}
