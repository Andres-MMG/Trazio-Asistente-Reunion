using Trazio.AsistenteReunion.App;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.Tests;

public sealed class GlossaryWorkspacePresentationTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    [Fact]
    public void Create_OrdersNewestFirstAndUsesPublicOrdinalWithoutInternalIdentifiers()
    {
        var state = GlossaryWorkspacePresenter.Create(
            [
                Entry("internal-old", "Teams", "NTeams", "Producto", true, 1),
                Entry("internal-new", "Meet", "Need", "Organización", false, 2)
            ],
            null,
            GlossaryEntryActivityFilter.All,
            Utc);

        Assert.Equal(["#1", "#2"], state.Items.Select(item => item.OrdinalLabel));
        Assert.Equal(["Need → Meet", "NTeams → Teams"], state.Items.Select(item => item.ReplacementLabel));
        Assert.DoesNotContain("internal-new", state.Items[0].AutomationName, StringComparison.Ordinal);
        Assert.DoesNotContain("correction-2", state.Items[0].AutomationName, StringComparison.Ordinal);
        Assert.Contains("Origen Corrección de transcripción", state.Items[0].AutomationName, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("reunion", 1)]
    [InlineData("REUNIÓN", 1)]
    [InlineData("tecnico", 1)]
    [InlineData("producto", 1)]
    [InlineData("ausente", 0)]
    public void Create_FiltersBothTermsAndCategoryIgnoringCaseAndDiacritics(string filter, int expected)
    {
        var state = GlossaryWorkspacePresenter.Create(
            [
                Entry("one", "Reunión", "Reunion", "Término técnico", true, 1),
                Entry("two", "Trazio", "Trasio", "Producto", false, 2)
            ],
            filter,
            GlossaryEntryActivityFilter.All,
            Utc);

        Assert.Equal(expected, state.Items.Count);
    }

    [Fact]
    public void Create_ActivityFiltersKeepLegacyDuplicatesIndependent()
    {
        var entries = new[]
        {
            Entry("duplicate-a", "Meet", "Need", "Producto", true, 1),
            Entry("duplicate-b", "Meet", "Need", "Producto", false, 2)
        };

        var active = GlossaryWorkspacePresenter.Create(entries, null, GlossaryEntryActivityFilter.Active, Utc);
        var inactive = GlossaryWorkspacePresenter.Create(entries, null, GlossaryEntryActivityFilter.Inactive, Utc);

        Assert.Single(active.Items);
        Assert.True(active.Items[0].IsActive);
        Assert.Single(inactive.Items);
        Assert.False(inactive.Items[0].IsActive);
        Assert.Equal(2, active.TotalCount);
        Assert.Equal(1, active.ActiveCount);
    }

    [Fact]
    public void Create_EmptyAndNoMatchStatusesExposeVisibleTotalAndActiveCounts()
    {
        var empty = GlossaryWorkspacePresenter.Create([], null, GlossaryEntryActivityFilter.All, Utc);
        var oneVisible = GlossaryWorkspacePresenter.Create(
            [Entry("one", "Meet", "Need", "Producto", true, 1)],
            null,
            GlossaryEntryActivityFilter.All,
            Utc);
        var noMatch = GlossaryWorkspacePresenter.Create(
            [Entry("one", "Meet", "Need", "Producto", true, 1)],
            "Zoom",
            GlossaryEntryActivityFilter.All,
            Utc);

        Assert.Equal("0 visibles de 0 entradas; 0 activas. El diccionario todavía no tiene entradas.", empty.Status);
        Assert.Equal("1 visible de 1 entrada; 1 activa.", oneVisible.Status);
        Assert.Equal("0 visibles de 1 entrada; 1 activa. No hay coincidencias para el filtro actual.", noMatch.Status);
    }

    [Fact]
    public void Create_CapsVisibleItemsAndReportsTruncation()
    {
        var entries = Enumerable.Range(1, GlossaryWorkspacePresenter.MaximumVisibleItems + 3)
            .Select(index => Entry($"id-{index}", $"Preferred {index}", $"Mistaken {index}", "Producto", index % 2 == 0, index))
            .ToArray();

        var state = GlossaryWorkspacePresenter.Create(entries, null, GlossaryEntryActivityFilter.All, Utc);

        Assert.Equal(GlossaryWorkspacePresenter.MaximumVisibleItems, state.Items.Count);
        Assert.Equal(entries.Length, state.MatchingCount);
        Assert.True(state.IsTruncated);
        Assert.Contains("Se muestran las primeras 120 de 123 coincidencias", state.Status, StringComparison.Ordinal);
    }

    private static GlossaryEntry Entry(
        string id,
        string preferred,
        string mistaken,
        string category,
        bool active,
        int minute) =>
        new(id, preferred, mistaken, category, active, $"correction-{minute}",
            new DateTimeOffset(2026, 9, 24, 10, 0, 0, TimeSpan.Zero).AddMinutes(minute));
}
