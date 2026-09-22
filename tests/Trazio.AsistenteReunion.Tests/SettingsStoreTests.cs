using System.Text;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.Tests;

public sealed class SettingsStoreTests
{
    [Fact]
    public async Task SaveAndLoad_ProtectsDeviceIdentifiersAtRest()
    {
        var path = Path.Combine(Path.GetTempPath(), $"trazio-settings-{Guid.NewGuid():N}.dat");
        try
        {
            var store = new SettingsStore(path);
            var settings = new AppSettings("private-microphone-id", "private-output-id", "C:\\Models\\model.bin", "es",
                LocalDisplayName: "Private Person", LocalOrganization: "Private Organization", LocalProfileConfirmed: true);
            await store.SaveAsync(settings);
            Assert.Equal(settings, await store.LoadAsync());
            var raw = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path));
            Assert.DoesNotContain("private-microphone-id", raw);
            Assert.DoesNotContain("private-output-id", raw);
            Assert.DoesNotContain("Private Person", raw);
            Assert.DoesNotContain("Private Organization", raw);
        }
        finally { File.Delete(path); }
    }
}
