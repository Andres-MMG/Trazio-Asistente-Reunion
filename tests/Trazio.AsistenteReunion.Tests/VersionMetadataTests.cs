using System.Reflection;
using Trazio.AsistenteReunion.App;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.Tests;

public sealed class VersionMetadataTests
{
    [Fact]
    public void Assemblies_UseMvpVersionAndProductName()
    {
        var assemblies = new[] { typeof(MainWindow).Assembly, typeof(MeetingSession).Assembly };
        foreach (var assembly in assemblies)
        {
            Assert.Equal(new Version(0, 1, 1, 0), assembly.GetName().Version);
            Assert.Equal("Trazio Asistente Reunión", assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product);
        }
    }
}
