using Trazio.AsistenteReunion.App;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.Tests;

public sealed class MeetingWindowSelectionTests
{
    [Theory]
    [InlineData("Daily · Google Meet", "chrome")]
    [InlineData("MEET.GOOGLE.COM — Reunión", "msedge")]
    public void Classify_WithGoogleMeetMarker_ReturnsGoogleMeet(string title, string processName)
    {
        var provider = MeetingWindowClassifier.Classify(title, processName);

        Assert.Equal(MeetingProvider.GoogleMeet, provider);
    }

    [Theory]
    [InlineData("Proyecto · Microsoft Teams", "msedge")]
    [InlineData("TEAMS.MICROSOFT.COM", "chrome")]
    [InlineData("Ventana sin marca", "ms-teams")]
    [InlineData("Ventana sin marca", "TEAMS.EXE")]
    public void Classify_WithMicrosoftTeamsMarker_ReturnsMicrosoftTeams(string title, string processName)
    {
        var provider = MeetingWindowClassifier.Classify(title, processName);

        Assert.Equal(MeetingProvider.MicrosoftTeams, provider);
    }

    [Theory]
    [InlineData("Reunión de equipo", "chrome")]
    [InlineData("Meet del trimestre", "myteams")]
    [InlineData("Teams de producto", "not-teams")]
    [InlineData("", "explorer")]
    public void Classify_WithInsufficientOrGenericSignals_ReturnsOther(string title, string processName)
    {
        var provider = MeetingWindowClassifier.Classify(title, processName);

        Assert.Equal(MeetingProvider.Other, provider);
    }

    [Theory]
    [InlineData("Google Meet · Microsoft Teams", "chrome")]
    [InlineData("Google Meet", "ms-teams")]
    public void Classify_WithConflictingSignals_ReturnsOther(string title, string processName)
    {
        var provider = MeetingWindowClassifier.Classify(title, processName);

        Assert.Equal(MeetingProvider.Other, provider);
    }

    [Fact]
    public void MeetingWindowSelection_ContainsOnlyStableInMemoryIdentity()
    {
        var selection = Selection(MeetingProvider.GoogleMeet);
        var propertyNames = typeof(MeetingWindowSelection)
            .GetProperties()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["Handle", "ProcessId", "Provider"], propertyNames);
        Assert.Equal((nint)123, selection.Handle);
        Assert.Equal((uint)456, selection.ProcessId);
        Assert.Equal(MeetingProvider.GoogleMeet, selection.Provider);
    }

    [Fact]
    public void ControllerConstruction_DoesNotEnumerateWindows()
    {
        var catalog = new FakeMeetingWindowCatalog();
        _ = new MeetingWindowSelectionController(catalog);

        Assert.Equal(0, catalog.EnumerationCount);
    }

    [Fact]
    public void ResolveProviderForStart_WithValidSelection_ReturnsDetectedProvider()
    {
        var catalog = new FakeMeetingWindowCatalog { Available = true };
        var controller = new MeetingWindowSelectionController(catalog);
        controller.Select(Selection(MeetingProvider.GoogleMeet));

        var provider = controller.ResolveProviderForStart();

        Assert.Equal(MeetingProvider.GoogleMeet, provider);
        Assert.NotNull(controller.Selection);
        Assert.False(controller.IsLost);
    }

    [Fact]
    public void ResolveProviderForStart_WhenWindowWasLost_ClearsSelectionAndReturnsNotSelected()
    {
        var catalog = new FakeMeetingWindowCatalog { Available = false };
        var controller = new MeetingWindowSelectionController(catalog);
        controller.Select(Selection(MeetingProvider.MicrosoftTeams));

        var provider = controller.ResolveProviderForStart();

        Assert.Equal(MeetingProvider.NotSelected, provider);
        Assert.Null(controller.Selection);
        Assert.True(controller.IsLost);
    }

    [Fact]
    public void ResolveProviderForStart_WhenCatalogThrows_FailsClosedWithoutPropagatingDetails()
    {
        var catalog = new FakeMeetingWindowCatalog
        {
            ValidationException = new InvalidOperationException("sensitive window detail")
        };
        var controller = new MeetingWindowSelectionController(catalog);
        controller.Select(Selection(MeetingProvider.GoogleMeet));

        var provider = controller.ResolveProviderForStart();

        Assert.Equal(MeetingProvider.NotSelected, provider);
        Assert.Null(controller.Selection);
        Assert.True(controller.IsLost);
    }

    [Fact]
    public void CheckWhileRecording_WhenWindowDisappears_MarksLostWithoutReassigningSelection()
    {
        var catalog = new FakeMeetingWindowCatalog { Available = true };
        var controller = new MeetingWindowSelectionController(catalog);
        controller.Select(Selection(MeetingProvider.GoogleMeet));
        catalog.Available = false;

        var availability = controller.CheckWhileRecording();

        Assert.Equal(MeetingWindowAvailability.Lost, availability);
        Assert.Equal(MeetingProvider.GoogleMeet, controller.Selection?.Provider);
        Assert.True(controller.IsLost);
        Assert.Equal(1, catalog.ValidationCount);
    }

    [Fact]
    public void CheckWhileRecording_WhenCatalogThrows_FailsClosedAndKeepsSessionProviderImmutable()
    {
        var catalog = new FakeMeetingWindowCatalog
        {
            ValidationException = new InvalidOperationException("sensitive window detail")
        };
        var controller = new MeetingWindowSelectionController(catalog);
        controller.Select(Selection(MeetingProvider.MicrosoftTeams));

        var availability = controller.CheckWhileRecording();

        Assert.Equal(MeetingWindowAvailability.Lost, availability);
        Assert.Equal(MeetingProvider.MicrosoftTeams, controller.Selection?.Provider);
        Assert.True(controller.IsLost);
    }

    [Fact]
    public void FinishSession_AfterInterruptedOrFailedStop_ConsumesSelection()
    {
        var controller = new MeetingWindowSelectionController(new FakeMeetingWindowCatalog());
        controller.Select(Selection(MeetingProvider.GoogleMeet));

        controller.FinishSession();

        Assert.Null(controller.Selection);
        Assert.False(controller.IsLost);
    }

    [Theory]
    [InlineData(false, false, false, "Sin seleccionar", true, false)]
    [InlineData(true, false, false, "Google Meet", true, true)]
    [InlineData(true, false, true, "Google Meet", false, false)]
    [InlineData(true, true, true, "ya no está disponible", false, false)]
    public void Presenter_ReflectsSelectionLossAndRecordingState(
        bool hasSelection,
        bool isLost,
        bool isRecording,
        string expectedStatus,
        bool expectedCanSelect,
        bool expectedCanClear)
    {
        var selection = hasSelection ? Selection(MeetingProvider.GoogleMeet) : null;

        var state = MeetingWindowSelectionPresenter.Create(selection, isLost, isRecording, isReady: true);

        Assert.Contains(expectedStatus, state.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(expectedCanSelect, state.CanSelect);
        Assert.Equal(expectedCanClear, state.CanClear);
    }

    [Fact]
    public void MeetingProvider_UsesStablePersistedValues()
    {
        Assert.Equal(0, (int)MeetingProvider.NotSelected);
        Assert.Equal(1, (int)MeetingProvider.GoogleMeet);
        Assert.Equal(2, (int)MeetingProvider.MicrosoftTeams);
        Assert.Equal(3, (int)MeetingProvider.Other);
    }

    private static MeetingWindowSelection Selection(MeetingProvider provider) =>
        new((nint)123, 456, provider);

    private sealed class FakeMeetingWindowCatalog : IMeetingWindowCatalog
    {
        public bool Available { get; set; }
        public Exception? ValidationException { get; set; }
        public int ValidationCount { get; private set; }
        public int EnumerationCount { get; private set; }

        public IReadOnlyList<MeetingWindowCandidate> Enumerate()
        {
            EnumerationCount++;
            return [];
        }

        public bool IsAvailable(MeetingWindowSelection selection)
        {
            ValidationCount++;
            if (ValidationException is not null) throw ValidationException;
            return Available;
        }
    }
}