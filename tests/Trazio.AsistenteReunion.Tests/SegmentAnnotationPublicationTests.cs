using System.Reflection;
using Trazio.AsistenteReunion.App;

namespace Trazio.AsistenteReunion.Tests;

public sealed class SegmentAnnotationPublicationTests
{
    [Fact]
    public void HistoryAnnotations_AreExplicitAccessibleAndLocalOnly()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "Trazio.AsistenteReunion.App", "MainWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(root, "src", "Trazio.AsistenteReunion.App", "MainWindow.xaml.cs"));
        var capabilities = File.ReadAllText(Path.Combine(root, "src", "Trazio.AsistenteReunion.App", "trazio-capabilities.json"));

        Assert.Contains("x:Name=\"SegmentAnnotationsList\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AddSegmentAnnotationButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Agregar nota o seguimiento", xaml, StringComparison.Ordinal);
        Assert.Contains("automation:AutomationProperties.Name=\"Anotaciones del segmento seleccionado\"", xaml, StringComparison.Ordinal);
        Assert.NotNull(typeof(MainWindow).GetMethod("AddSegmentAnnotation_Click", BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.NotNull(typeof(MainWindow).GetMethod("ToggleSegmentAnnotationStatus_Click", BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.NotNull(typeof(MainWindow).GetMethod("DeleteSegmentAnnotation_Click", BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.Contains("ListSegmentAnnotationsAsync", code, StringComparison.Ordinal);
        Assert.DoesNotContain("annotation", capabilities, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        var current = AppContext.BaseDirectory;
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current, "Trazio.AsistenteReunion.slnx"))) return current;
            current = Directory.GetParent(current)?.FullName;
        }
        throw new DirectoryNotFoundException("No se encontró la raíz del repositorio.");
    }
}
