using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.Tests;

public sealed class GlossaryPromptPlannerTests
{
    private static readonly DateTimeOffset Utc = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_UsesOnlyActiveUnambiguousPreferredTerms()
    {
        var plan = GlossaryPromptPlanner.Create(
        [
            Entry("a", "Meet", "Need", true),
            Entry("b", "Teams", "NTeams", true),
            Entry("c", "Trazio", "Trazzio", false),
            Entry("d", "Mawida", "Need", true),
            Entry("e", "MEET", "another mistake", true)
        ]);

        Assert.True(plan.HasPrompt);
        Assert.Equal(4, plan.ActiveCount);
        Assert.Equal(2, plan.ExcludedAmbiguousCount);
        Assert.Equal(2, plan.Included.Count);
        Assert.Equal(["MEET", "Teams"], plan.Included.Select(item => item.PreferredTerm));
        Assert.Contains("MEET", plan.Prompt, StringComparison.Ordinal);
        Assert.Contains("Teams", plan.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("NTeams", plan.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Need", plan.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Trazio", plan.Prompt, StringComparison.Ordinal);
        Assert.StartsWith("glossary-v1:", plan.Version, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_IsDeterministicAndVersionChangesWithPrompt()
    {
        var entries = new[]
        {
            Entry("b", "Teams", "NTeams", true),
            Entry("a", "Meet", "Need", true)
        };

        var first = GlossaryPromptPlanner.Create(entries);
        var reordered = GlossaryPromptPlanner.Create(entries.Reverse().ToArray());
        var changed = GlossaryPromptPlanner.Create(
        [
            Entry("a", "Meet Pro", "Need", true),
            Entry("b", "Teams", "NTeams", true)
        ]);

        Assert.Equal(first.Prompt, reordered.Prompt);
        Assert.Equal(first.Version, reordered.Version);
        Assert.NotEqual(first.Version, changed.Version);
        Assert.Equal(["Meet", "Teams"], first.Included.Select(item => item.PreferredTerm));
    }

    [Fact]
    public void Create_EnforcesEntryAndCharacterLimits()
    {
        var entries = Enumerable.Range(0, GlossaryPromptPlanner.MaximumIncludedTerms + 10)
            .Select(index => Entry(
                index.ToString("D3"),
                $"Término-{index:D3}-" + new string('x', 20),
                $"error-{index:D3}",
                true))
            .ToArray();

        var plan = GlossaryPromptPlanner.Create(entries);

        Assert.InRange(plan.Included.Count, 1, GlossaryPromptPlanner.MaximumIncludedTerms);
        Assert.NotNull(plan.Prompt);
        Assert.True(plan.Prompt!.Length <= GlossaryPromptPlanner.MaximumPromptCharacters);
        Assert.Equal(entries.Length - plan.Included.Count, plan.ExcludedByLimitCount);
    }

    [Fact]
    public void Create_WithoutUsableEntries_ReturnsNoPrompt()
    {
        var inactive = GlossaryPromptPlanner.Create([Entry("a", "Meet", "Need", false)]);
        var conflicting = GlossaryPromptPlanner.Create(
        [
            Entry("a", "Meet", "Need", true),
            Entry("b", "Mawida", "Néed", true)
        ]);

        Assert.Same(GlossaryPromptPlan.None, inactive);
        Assert.False(conflicting.HasPrompt);
        Assert.Equal(GlossaryPromptPlan.NoGlossaryVersion, conflicting.Version);
        Assert.Equal(2, conflicting.ExcludedAmbiguousCount);
    }

    private static GlossaryEntry Entry(
        string id,
        string preferred,
        string mistaken,
        bool active) =>
        new(
            id,
            preferred,
            mistaken,
            "Producto",
            active,
            GlossaryEntryOrigin.ImportedFile,
            null,
            "batch",
            Utc);
}
