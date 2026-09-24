using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.App;

internal sealed class DeterministicVisualActivityDetector : IVisualProbeDetector
{
    private readonly string _sessionId;
    private ProfileFingerprint? _profileFingerprint;
    private AnonymousVisualEvidenceProvenance? _provenance;
    private VisualProbeDetectionPolicy? _policy;
    private long _surfaceRevision;
    private TimeSpan? _lastObservation;
    private TimeSpan? _coverageStart;
    private AnonymousVisualAnalysisAvailability _coverageAvailability;
    private AnonymousVisualEvidenceProvenance? _coverageProvenance;
    private double _coverageConfidence;
    private TimeSpan? _activationStart;
    private double _activationConfidence = 1;
    private TimeSpan? _activityStart;
    private double _activityConfidence = 1;
    private TimeSpan? _releaseStart;
    private bool _completed;

    public DeterministicVisualActivityDetector(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        _sessionId = sessionId;
    }

    public VisualProbeDetectionResult Observe(
        VisualProbeObservation observation,
        ReadOnlySpan<VisualProbeFeature> features)
    {
        if (_completed) throw new InvalidOperationException("The visual detector is already complete.");
        if (observation.Status == VisualProbeObservationStatus.Completed)
        {
            if (!features.IsEmpty)
                throw new ArgumentException("Completion cannot contain aggregate features.", nameof(features));
            return Complete(observation.Offset);
        }

        if (_lastObservation is { } last && observation.Offset <= last)
            throw new ArgumentOutOfRangeException(nameof(observation), "Observation offsets must increase.");
        if (observation.Status == VisualProbeObservationStatus.Unavailable && !features.IsEmpty)
            throw new ArgumentException("Unavailable observations cannot contain aggregate features.", nameof(features));

        var profile = observation.Profile!;
        var fingerprint = ProfileFingerprint.From(profile);
        var provenance = CreateProvenance(profile);
        var intervals = new List<AnonymousVisualEvidenceInterval>();
        var boundaryReason = PrepareContext(observation, fingerprint, intervals);

        _profileFingerprint = fingerprint;
        _provenance = provenance;
        _policy = profile.DetectionPolicy;
        _surfaceRevision = observation.SurfaceRevision;
        _lastObservation = observation.Offset;

        if (observation.Status == VisualProbeObservationStatus.Unavailable)
        {
            MarkUnavailable(observation.Offset, provenance, intervals);
            return VisualProbeDetectionResult.Abstained(
                VisualProbeAbstentionReason.SourceUnavailable,
                intervals);
        }

        var profileReason = ValidateProfile(profile);
        if (profileReason != VisualProbeAbstentionReason.None)
        {
            MarkUnavailable(observation.Offset, provenance, intervals);
            return VisualProbeDetectionResult.Abstained(profileReason, intervals);
        }

        var classification = Classify(features, profile, _policy!, out var coherence);
        if (classification == FeatureClassification.Ambiguous)
        {
            MarkUnavailable(observation.Offset, provenance, intervals);
            return VisualProbeDetectionResult.Abstained(
                VisualProbeAbstentionReason.AmbiguousFeatures,
                intervals);
        }

        if (classification == FeatureClassification.Insufficient)
        {
            MarkUnavailable(observation.Offset, provenance, intervals);
            return VisualProbeDetectionResult.Abstained(
                VisualProbeAbstentionReason.InsufficientCoherence,
                intervals);
        }

        TransitionCoverage(
            observation.Offset,
            AnonymousVisualAnalysisAvailability.Available,
            provenance,
            confidence: 1,
            intervals);
        AdvanceActivity(classification, observation.Offset, coherence, intervals);

        return boundaryReason == VisualProbeAbstentionReason.None
            ? VisualProbeDetectionResult.Observed(intervals)
            : VisualProbeDetectionResult.Abstained(boundaryReason, intervals);
    }

    private VisualProbeDetectionResult Complete(TimeSpan offset)
    {
        if (offset < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(offset));
        if (_lastObservation is { } last && offset < last)
            throw new ArgumentOutOfRangeException(nameof(offset), "Completion cannot precede the last observation.");

        var intervals = new List<AnonymousVisualEvidenceInterval>();
        if (_lastObservation is { } prior &&
            _policy is { } policy &&
            offset - prior > policy.MaxObservationGap)
        {
            CloseForDiscontinuity(prior, offset, intervals);
        }
        else
        {
            CloseActivity(offset, intervals);
            CloseCoverage(offset, intervals);
        }

        ResetActivity();
        _completed = true;
        return VisualProbeDetectionResult.Completed(intervals);
    }

    private VisualProbeAbstentionReason PrepareContext(
        VisualProbeObservation observation,
        ProfileFingerprint fingerprint,
        List<AnonymousVisualEvidenceInterval> intervals)
    {
        if (_lastObservation is not { } last || _profileFingerprint is not { } priorFingerprint)
            return VisualProbeAbstentionReason.None;

        if (priorFingerprint != fingerprint)
        {
            CloseForDiscontinuity(last, observation.Offset, intervals);
            return VisualProbeAbstentionReason.ProfileChanged;
        }

        if (_surfaceRevision != observation.SurfaceRevision)
        {
            CloseForDiscontinuity(last, observation.Offset, intervals);
            return VisualProbeAbstentionReason.SurfaceChanged;
        }

        if (_policy is { } policy && observation.Offset - last > policy.MaxObservationGap)
        {
            CloseForDiscontinuity(last, observation.Offset, intervals);
            return VisualProbeAbstentionReason.ObservationGap;
        }

        return VisualProbeAbstentionReason.None;
    }

    private void CloseForDiscontinuity(
        TimeSpan lastReliableOffset,
        TimeSpan nextOffset,
        List<AnonymousVisualEvidenceInterval> intervals)
    {
        CloseActivity(lastReliableOffset, intervals);
        if (_coverageStart is not null &&
            _coverageAvailability == AnonymousVisualAnalysisAvailability.Unavailable)
        {
            CloseCoverage(nextOffset, intervals);
        }
        else
        {
            CloseCoverage(lastReliableOffset, intervals);
            if (_provenance is not null && nextOffset > lastReliableOffset)
            {
                intervals.Add(CreateCoverage(
                    lastReliableOffset,
                    nextOffset,
                    AnonymousVisualAnalysisAvailability.Unavailable,
                    confidence: 0,
                    _provenance));
            }
        }

        ResetActivity();
    }

    private void MarkUnavailable(
        TimeSpan offset,
        AnonymousVisualEvidenceProvenance provenance,
        List<AnonymousVisualEvidenceInterval> intervals)
    {
        CloseActivity(offset, intervals);
        ResetActivity();
        TransitionCoverage(
            offset,
            AnonymousVisualAnalysisAvailability.Unavailable,
            provenance,
            confidence: 0,
            intervals);
    }

    private static VisualProbeAbstentionReason ValidateProfile(IVisualProbeProfile profile)
    {
        if (profile.Version <= 0 || profile.ExpectedFeatureCount < 0)
            return VisualProbeAbstentionReason.UnsupportedProfile;
        if (profile.ValidationState == VisualProbeProfileValidationState.Unsupported)
            return VisualProbeAbstentionReason.UnsupportedProfile;
        if (profile.ValidationState != VisualProbeProfileValidationState.Validated ||
            profile.DetectionPolicy is null)
            return VisualProbeAbstentionReason.UnvalidatedProfile;
        if (profile.ExpectedFeatureCount < profile.DetectionPolicy.MinimumCoherentPatches)
            return VisualProbeAbstentionReason.InsufficientCoherence;
        return VisualProbeAbstentionReason.None;
    }

    private static FeatureClassification Classify(
        ReadOnlySpan<VisualProbeFeature> features,
        IVisualProbeProfile profile,
        VisualProbeDetectionPolicy policy,
        out double coherence)
    {
        coherence = 0;
        if (features.Length != profile.ExpectedFeatureCount || features.IsEmpty)
            return FeatureClassification.Insufficient;

        var enterCount = 0;
        var exitCount = 0;
        foreach (var feature in features)
        {
            if (feature.ActivityScore >= policy.EnterThreshold) enterCount++;
            else if (feature.ActivityScore <= policy.ExitThreshold) exitCount++;
        }

        if (enterCount >= policy.MinimumCoherentPatches &&
            exitCount >= policy.MinimumCoherentPatches)
            return FeatureClassification.Ambiguous;
        if (enterCount >= policy.MinimumCoherentPatches)
        {
            coherence = (double)enterCount / features.Length;
            return FeatureClassification.Enter;
        }
        if (exitCount >= policy.MinimumCoherentPatches)
        {
            coherence = (double)exitCount / features.Length;
            return FeatureClassification.Exit;
        }
        if (enterCount == 0 && exitCount == 0) return FeatureClassification.Band;
        return enterCount > 0 && exitCount > 0
            ? FeatureClassification.Ambiguous
            : FeatureClassification.Insufficient;
    }

    private void AdvanceActivity(
        FeatureClassification classification,
        TimeSpan offset,
        double coherence,
        List<AnonymousVisualEvidenceInterval> intervals)
    {
        switch (classification)
        {
            case FeatureClassification.Enter:
                _releaseStart = null;
                if (_activityStart is not null)
                {
                    _activityConfidence = Math.Min(_activityConfidence, coherence);
                    return;
                }

                if (_activationStart is null)
                {
                    _activationStart = offset;
                    _activationConfidence = coherence;
                }
                else
                {
                    _activationConfidence = Math.Min(_activationConfidence, coherence);
                }

                if (offset - _activationStart.Value >= _policy!.ActivationHold)
                {
                    _activityStart = _activationStart;
                    _activityConfidence = _activationConfidence;
                    _activationStart = null;
                    _activationConfidence = 1;
                }
                return;

            case FeatureClassification.Exit:
                _activationStart = null;
                _activationConfidence = 1;
                if (_activityStart is null) return;
                _releaseStart ??= offset;
                if (offset - _releaseStart.Value >= _policy!.ReleaseHold)
                    CloseActivity(_releaseStart.Value, intervals);
                return;

            case FeatureClassification.Band:
                _activationStart = null;
                _activationConfidence = 1;
                _releaseStart = null;
                return;

            default:
                throw new InvalidOperationException("Only coherent classifications can advance activity state.");
        }
    }

    private void TransitionCoverage(
        TimeSpan offset,
        AnonymousVisualAnalysisAvailability availability,
        AnonymousVisualEvidenceProvenance provenance,
        double confidence,
        List<AnonymousVisualEvidenceInterval> intervals)
    {
        if (_coverageStart is not null &&
            _coverageAvailability == availability &&
            _coverageProvenance == provenance)
        {
            _coverageConfidence = Math.Min(_coverageConfidence, confidence);
            return;
        }

        CloseCoverage(offset, intervals);
        _coverageStart = offset;
        _coverageAvailability = availability;
        _coverageProvenance = provenance;
        _coverageConfidence = confidence;
    }

    private void CloseActivity(
        TimeSpan end,
        List<AnonymousVisualEvidenceInterval> intervals)
    {
        if (_activityStart is { } start && end > start && _provenance is not null)
        {
            intervals.Add(AnonymousVisualEvidenceInterval.Activity(
                CreateStableId(
                    AnonymousVisualEvidenceKind.Activity,
                    AnonymousVisualAnalysisAvailability.Available,
                    start,
                    end,
                    _activityConfidence,
                    _provenance),
                _sessionId,
                start,
                end,
                _activityConfidence,
                _provenance));
        }

        _activityStart = null;
        _activityConfidence = 1;
        _releaseStart = null;
    }

    private void CloseCoverage(
        TimeSpan end,
        List<AnonymousVisualEvidenceInterval> intervals)
    {
        if (_coverageStart is { } start &&
            end > start &&
            _coverageProvenance is not null)
        {
            intervals.Add(CreateCoverage(
                start,
                end,
                _coverageAvailability,
                _coverageConfidence,
                _coverageProvenance));
        }

        _coverageStart = null;
        _coverageProvenance = null;
        _coverageConfidence = 0;
    }

    private AnonymousVisualEvidenceInterval CreateCoverage(
        TimeSpan start,
        TimeSpan end,
        AnonymousVisualAnalysisAvailability availability,
        double confidence,
        AnonymousVisualEvidenceProvenance provenance) =>
        AnonymousVisualEvidenceInterval.Coverage(
            CreateStableId(
                AnonymousVisualEvidenceKind.Coverage,
                availability,
                start,
                end,
                confidence,
                provenance),
            _sessionId,
            start,
            end,
            availability,
            confidence,
            provenance);

    private Guid CreateStableId(
        AnonymousVisualEvidenceKind kind,
        AnonymousVisualAnalysisAvailability availability,
        TimeSpan start,
        TimeSpan end,
        double confidence,
        AnonymousVisualEvidenceProvenance provenance)
    {
        var identity = string.Join('|', new[]
        {
            _sessionId.Length.ToString(CultureInfo.InvariantCulture),
            _sessionId,
            ((int)kind).ToString(CultureInfo.InvariantCulture),
            ((int)availability).ToString(CultureInfo.InvariantCulture),
            start.Ticks.ToString(CultureInfo.InvariantCulture),
            end.Ticks.ToString(CultureInfo.InvariantCulture),
            BitConverter.DoubleToInt64Bits(confidence).ToString(CultureInfo.InvariantCulture),
            ((int)provenance.Provider).ToString(CultureInfo.InvariantCulture),
            provenance.ProfileVersion.ToString(CultureInfo.InvariantCulture),
            provenance.EvidenceVersion.ToString(CultureInfo.InvariantCulture),
            provenance.DetectorVersion.ToString(CultureInfo.InvariantCulture),
            provenance.PolicyVersion.ToString(CultureInfo.InvariantCulture)
        });
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        var idBytes = hash.AsSpan(0, 16).ToArray();
        if (idBytes.AsSpan().IndexOfAnyExcept((byte)0) < 0) idBytes[0] = 1;
        return new Guid(idBytes);
    }

    private static AnonymousVisualEvidenceProvenance CreateProvenance(IVisualProbeProfile profile)
    {
        var policy = profile.DetectionPolicy;
        return new(
            profile.Provider,
            profile.Version > 0 ? profile.Version : AnonymousVisualEvidenceVersions.CurrentProfile,
            policy?.EvidenceVersion ?? AnonymousVisualEvidenceVersions.CurrentEvidence,
            policy?.DetectorVersion ?? AnonymousVisualEvidenceVersions.CurrentDetector,
            policy?.PolicyVersion ?? AnonymousVisualEvidenceVersions.CurrentPolicy);
    }

    private void ResetActivity()
    {
        _activationStart = null;
        _activationConfidence = 1;
        _activityStart = null;
        _activityConfidence = 1;
        _releaseStart = null;
    }

    private enum FeatureClassification
    {
        Enter,
        Exit,
        Band,
        Ambiguous,
        Insufficient
    }

    private readonly record struct ProfileFingerprint(
        MeetingProvider Provider,
        int Version,
        VisualProbeProfileValidationState ValidationState,
        int ExpectedFeatureCount,
        VisualProbeDetectionPolicy? Policy)
    {
        public static ProfileFingerprint From(IVisualProbeProfile profile) => new(
            profile.Provider,
            profile.Version,
            profile.ValidationState,
            profile.ExpectedFeatureCount,
            profile.DetectionPolicy);
    }
}
