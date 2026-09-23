using System.Reflection;
using System.Text;
using Trazio.AsistenteReunion.App;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.Tests;

public sealed class HistoryWorkspacePublicationTests
{
    [Fact]
    public void SelectingSavedSession_CompiledApplicationContainsCompleteReviewWorkspace()
    {
        var state = HistoryPresenter.Create(
            true,
            SessionState.Completed,
            [new TranscriptSegment("segment", "session", AudioSourceKind.Microphone, 0, TimeSpan.Zero, TimeSpan.FromSeconds(1), "text", DateTimeOffset.UtcNow)],
            [],
            AudioSourceKind.Microphone,
            _ => "formatted");
        var compiledApplication = Encoding.UTF8.GetString(File.ReadAllBytes(typeof(MainWindow).Assembly.Location));

        Assert.True(state.HasSelection);
        Assert.True(state.CanCorrectTranscript);
        Assert.Equal("formatted", state.TranscriptContent);
        Assert.Contains("Detalles de la sesión", compiledApplication);
        Assert.Contains("Revisar segmento seleccionado", compiledApplication);
        Assert.Contains("Reemplazos de palabras detectados", compiledApplication);
        Assert.Contains("Agregar términos seleccionados", compiledApplication);
        Assert.Contains("Almacenamiento local", compiledApplication);
        Assert.Contains("Original · revisión humana", compiledApplication);
        Assert.Contains("Retranscribir audio", compiledApplication);
        Assert.Contains("Cancelar retranscripción", compiledApplication);
        Assert.Contains("Tu nombre en esta reunión (opcional)", compiledApplication);
        Assert.Contains("Guardar título", compiledApplication);
        Assert.Contains("Comparar transcripciones", compiledApplication);
        Assert.Contains("Primera versión", compiledApplication);
        Assert.Contains("Segunda versión", compiledApplication);
        Assert.Contains("Reproductor de la reunión", compiledApplication);
        Assert.Contains("Micrófono · mi voz", compiledApplication);
        Assert.Contains("Audio del equipo · participantes", compiledApplication);
        Assert.Contains("Escuchar fragmento", compiledApplication);
        Assert.Contains("Retroceder 10 s", compiledApplication);
        Assert.Contains("Avanzar 10 s", compiledApplication);
        Assert.Contains("Exportar a Obsidian…", compiledApplication);
        Assert.NotNull(typeof(MainWindow).GetField("ExportObsidianButton", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
        Assert.NotNull(typeof(MainWindow).GetMethod("ExportObsidian_Click", BindingFlags.Instance | BindingFlags.NonPublic));
    }

    [Fact]
    public void AudioRetention_IsRequiredForNewAndLegacySettings()
    {
        var legacySettings = new AppSettings(KeepEncryptedAudio: false);
        var compiledApplication = Encoding.UTF8.GetString(File.ReadAllBytes(typeof(MainWindow).Assembly.Location));

        Assert.True(AudioRetentionPolicy.Enforce(new AppSettings()).KeepEncryptedAudio);
        Assert.True(AudioRetentionPolicy.Enforce(legacySettings).KeepEncryptedAudio);
        Assert.Contains("El audio cifrado siempre se guarda para reproducirlo y retranscribirlo", compiledApplication);
        Assert.Contains("Solo una exportación WAV explícita crea un archivo reproducible por otras aplicaciones", compiledApplication);
    }
}

