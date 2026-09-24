using Trazio.AsistenteReunion.App;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.Tests;

public sealed class HistorySearchPresentationTests
{
    private static readonly DateTimeOffset StartedAt = new(2026, 9, 24, 12, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Normalize_IgnoresCaseAndDiacriticsButKeepsLiteralWords()
    {
        var query = HistorySearchText.Normalize("reunión café");

        Assert.True(HistorySearchText.Contains("REUNION CAFE", query));
        Assert.False(HistorySearchText.Contains("reuniones cafetería", query));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData(" á ")]
    public void PrepareQuery_LessThanTwoNormalizedCharacters_IsRejected(string query)
    {
        var error = Assert.Throws<ArgumentException>(() => HistorySearchText.PrepareQuery(query));

        Assert.Contains("al menos 2", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateSnippet_LongText_KeepsLateMatchVisibleWithinBound()
    {
        var text = new string('x', 170) + " reunión café confirmada" + new string('y', 80);

        var snippet = HistorySearchText.CreateSnippet(
            text,
            HistorySearchText.Normalize("reunion cafe"),
            maximumLength: 120);

        Assert.Contains("reunión café", snippet, StringComparison.Ordinal);
        Assert.True(snippet.Length <= 120);
        Assert.StartsWith("…", snippet, StringComparison.Ordinal);
        Assert.EndsWith("…", snippet, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_ResultRowsExposeMeetingDateSourceTimeAndTruncation()
    {
        var hit = new HistorySearchHit(
            "session",
            "Planificación",
            StartedAt,
            "segment",
            AudioSourceKind.SystemOutput,
            TimeSpan.FromSeconds(75),
            "Texto coincidente",
            HistorySearchMatchKind.TranscriptSegment);
        var result = new HistorySearchResult([hit], 143, IsTruncated: true);

        var presentation = HistorySearchPresenter.Create(result, TimeZoneInfo.Utc);
        var item = Assert.Single(presentation.Items);

        Assert.Equal("Planificación", item.Title);
        Assert.Equal("2026-09-24 12:30 · Audio del equipo · 01:15", item.Details);
        Assert.Equal("Texto coincidente", item.Snippet);
        Assert.Contains("primeros 1 de 143", presentation.Status, StringComparison.Ordinal);
        Assert.True(presentation.IsTruncated);
    }

    [Fact]
    public void NavigationIntent_AlwaysUsesOriginalRevisionAndNeverAutoplays()
    {
        var hit = new HistorySearchHit(
            "session",
            "Título",
            StartedAt,
            "segment",
            AudioSourceKind.Microphone,
            TimeSpan.FromSeconds(8),
            "texto",
            HistorySearchMatchKind.TranscriptSegment);

        var intent = HistorySearchNavigationIntent.From(hit);

        Assert.Equal("session", intent.SessionId);
        Assert.Equal("segment", intent.SegmentId);
        Assert.Equal(AudioSourceKind.Microphone, intent.Source);
        Assert.True(intent.UseOriginalRevision);
        Assert.False(intent.AutoPlay);
    }
}
