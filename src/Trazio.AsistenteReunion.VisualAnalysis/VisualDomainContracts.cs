namespace Trazio.AsistenteReunion.Core;

public enum AudioSourceKind { Microphone, SystemOutput }
public enum MeetingProvider
{
    NotSelected = 0,
    GoogleMeet = 1,
    MicrosoftTeams = 2,
    Other = 3
}

internal readonly record struct AnonymousVisualCorrelationSegment(
    string SessionId,
    AudioSourceKind AudioSourceKind,
    TimeSpan Start,
    TimeSpan End);
