using Trazio.AsistenteReunion.App;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.Tests;

public sealed class SegmentAnnotationPresentationTests
{
    [Theory]
    [InlineData(SegmentAnnotationKind.Note, "Nota")]
    [InlineData(SegmentAnnotationKind.Decision, "Decisión")]
    [InlineData(SegmentAnnotationKind.FollowUp, "Seguimiento")]
    public void Item_UsesClearSpanishKindLabels(SegmentAnnotationKind kind, string expected)
    {
        var item = SegmentAnnotationItem.From(Annotation(kind));

        Assert.Equal(expected, item.KindLabel);
        Assert.Equal(kind == SegmentAnnotationKind.FollowUp ? "Pendiente" : "Guardada", item.StatusLabel);
        Assert.Equal(kind == SegmentAnnotationKind.FollowUp, item.CanToggleStatus);
    }

    [Fact]
    public void Summary_CountsAnnotationsAndOpenFollowUps()
    {
        var annotations = new[]
        {
            Annotation(SegmentAnnotationKind.Note),
            Annotation(SegmentAnnotationKind.FollowUp),
            Annotation(SegmentAnnotationKind.FollowUp) with { Status = SegmentAnnotationStatus.Completed }
        };

        var summary = SegmentAnnotationPresenter.Summary(annotations);

        Assert.Equal("3 anotaciones · 1 seguimiento pendiente", summary);
    }

    [Fact]
    public void Presenter_SortsNewestFirstAndBoundsVisibleRows()
    {
        var annotations = Enumerable.Range(0, SegmentAnnotationPresenter.MaximumVisibleItems + 3)
            .Select(index => Annotation(SegmentAnnotationKind.Note) with
            {
                Id = index.ToString(),
                CreatedAt = DateTimeOffset.UnixEpoch.AddMinutes(index),
                UpdatedAt = DateTimeOffset.UnixEpoch.AddMinutes(index)
            })
            .ToArray();

        var state = SegmentAnnotationPresenter.Create(annotations);

        Assert.Equal(SegmentAnnotationPresenter.MaximumVisibleItems, state.Items.Count);
        Assert.Equal((annotations.Length - 1).ToString(), state.Items[0].Annotation.Id);
        Assert.True(state.IsTruncated);
    }

    private static SegmentAnnotation Annotation(SegmentAnnotationKind kind) => new(
        Guid.NewGuid().ToString("N"),
        "segment",
        "session",
        kind,
        "Texto de prueba",
        SegmentAnnotationStatus.Open,
        DateTimeOffset.Parse("2026-09-24T10:00:00Z"),
        DateTimeOffset.Parse("2026-09-24T10:00:00Z"));
}
