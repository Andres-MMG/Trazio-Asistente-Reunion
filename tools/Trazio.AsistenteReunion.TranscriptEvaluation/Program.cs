using System.Text.Json;

namespace Trazio.AsistenteReunion.TranscriptEvaluation;

public static class Program
{
    public static int Main(string[] args) => Run(args, Console.Error, Console.Out);

    public static int Run(string[] args, TextWriter stderr, TextWriter stdout)
    {
        if (args.Length != 4 || args[0] != "--manifest" || args[2] != "--output")
        {
            stderr.WriteLine("Uso: dotnet run --project tools/Trazio.AsistenteReunion.TranscriptEvaluation -- --manifest <privado.json> --output <reporte-privado.json>");
            return 2;
        }

        try
        {
            var manifestPath = Path.GetFullPath(args[1]);
            var outputPath = Path.GetFullPath(args[3]);
            if (!Path.GetFileName(outputPath).EndsWith(".evaluation.private.json", StringComparison.OrdinalIgnoreCase))
                throw new EvaluationException("La salida debe terminar en .evaluation.private.json para quedar excluida de Git.");
            if (Path.GetFullPath(manifestPath).Equals(outputPath, StringComparison.OrdinalIgnoreCase))
                throw new EvaluationException("La salida no puede sobrescribir el manifiesto.");

            var report = EvaluationRunner.EvaluateFile(manifestPath, DateTimeOffset.UtcNow);
            var json = JsonSerializer.Serialize(report, EvaluationJson.Options);
            using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(output);
            writer.Write(json);
            stdout.WriteLine("Evaluación offline terminada. Revisa el reporte privado en la ruta indicada.");
            return 0;
        }
        catch (EvaluationException error)
        {
            stderr.WriteLine($"No se pudo evaluar: {error.Message}");
            return 1;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            stderr.WriteLine("No se pudo evaluar: archivo privado inaccesible o formato inválido.");
            return 1;
        }
    }
}
