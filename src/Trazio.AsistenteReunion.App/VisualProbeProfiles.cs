using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.App;

internal static class VisualProbeProfiles
{
    private static readonly IVisualProbeProfile GoogleMeet = new ProductionVisualProbeProfile(
        MeetingProvider.GoogleMeet,
        VisualProbeProfileValidationState.Unvalidated);

    private static readonly IVisualProbeProfile MicrosoftTeams = new ProductionVisualProbeProfile(
        MeetingProvider.MicrosoftTeams,
        VisualProbeProfileValidationState.Unvalidated);

    private static readonly IVisualProbeProfile Other = new ProductionVisualProbeProfile(
        MeetingProvider.Other,
        VisualProbeProfileValidationState.Unsupported);

    private static readonly IVisualProbeProfile NotSelected = new ProductionVisualProbeProfile(
        MeetingProvider.NotSelected,
        VisualProbeProfileValidationState.Unsupported);

    public static IVisualProbeProfile GetProduction(MeetingProvider provider) => provider switch
    {
        MeetingProvider.GoogleMeet => GoogleMeet,
        MeetingProvider.MicrosoftTeams => MicrosoftTeams,
        MeetingProvider.Other => Other,
        MeetingProvider.NotSelected => NotSelected,
        _ => throw new ArgumentOutOfRangeException(nameof(provider))
    };

    private sealed record ProductionVisualProbeProfile(
        MeetingProvider Provider,
        VisualProbeProfileValidationState ValidationState) : IVisualProbeProfile
    {
        public int Version => AnonymousVisualEvidenceVersions.CurrentProfile;
        public int ExpectedFeatureCount => 0;
        public VisualProbeDetectionPolicy? DetectionPolicy => null;
    }
}
