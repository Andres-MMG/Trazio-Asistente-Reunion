using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.Tests;

public sealed class LocalProfileTests
{
    [Fact]
    public void EnsureDefault_WithoutProfile_UsesWindowsAccountAndRequiresConfirmation()
    {
        var result = LocalProfile.EnsureDefault(new AppSettings(), "andrea.windows");

        Assert.Equal("andrea.windows", result.LocalDisplayName);
        Assert.False(result.LocalProfileConfirmed);
    }

    [Fact]
    public void EnsureDefault_WithExistingProfile_PreservesProfile()
    {
        var settings = new AppSettings(LocalDisplayName: "Andrea", LocalOrganization: "Trazio", LocalProfileConfirmed: true);

        Assert.Same(settings, LocalProfile.EnsureDefault(settings, "windows-account"));
    }

    [Fact]
    public void ResolveMeetingDisplayName_WithConfirmedProfile_ReturnsProfileName()
    {
        var settings = new AppSettings(LocalDisplayName: " Andrea ", LocalProfileConfirmed: true);

        Assert.Equal("Andrea", LocalProfile.ResolveMeetingDisplayName(settings, null));
    }

    [Fact]
    public void ResolveMeetingDisplayName_WithOverride_ReturnsOverrideWithoutChangingProfile()
    {
        var settings = new AppSettings(LocalDisplayName: "Andrea", LocalProfileConfirmed: true);

        Assert.Equal("Invitada", LocalProfile.ResolveMeetingDisplayName(settings, " Invitada "));
        Assert.Equal("Andrea", settings.LocalDisplayName);
    }

    [Fact]
    public void ResolveMeetingDisplayName_WithUnconfirmedDefault_RequiresConfirmation()
    {
        var settings = new AppSettings(LocalDisplayName: "windows-account", LocalProfileConfirmed: false);

        var error = Assert.Throws<InvalidOperationException>(() => LocalProfile.ResolveMeetingDisplayName(settings, null));

        Assert.Contains("Confirma tu nombre visible", error.Message);
    }

    [Fact]
    public void SaveConfirmedProfile_WithValidInput_TrimsAndPersistsOnlyProfileFields()
    {
        var settings = new AppSettings(ModelPath: "model.bin");

        var result = LocalProfile.SaveConfirmedProfile(settings, " Andrea ", " Trazio ", true);

        Assert.Equal("Andrea", result.LocalDisplayName);
        Assert.Equal("Trazio", result.LocalOrganization);
        Assert.True(result.LocalProfileConfirmed);
        Assert.Equal("model.bin", result.ModelPath);
    }

    [Theory]
    [InlineData("", true, "Ingresa un nombre visible")]
    [InlineData("Andrea", false, "Confirma el nombre visible")]
    public void SaveConfirmedProfile_WithInvalidInput_RejectsSave(string displayName, bool confirmed, string expectedMessage)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            LocalProfile.SaveConfirmedProfile(new AppSettings(), displayName, null, confirmed));

        Assert.Contains(expectedMessage, error.Message);
    }}