using System.Security;
using System.Text.Json;
using Trazio.AsistenteReunion.VisualEvaluation;

return VisualEvaluationCli.Run(args, Console.Out, Console.Error);

public static class VisualEvaluationCli
{
    public const string InvalidCommandError = "VE001 invalid-command";
    public const string CorpusRejectedError = "VE100 corpus-rejected";

    public static int Run(
        string[] args,
        TextWriter output,
        TextWriter error,
        Func<string, VisualEvaluationCorpus>? corpusLoader = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (args.Length != 3 ||
            !string.Equals(args[0], "evaluate", StringComparison.Ordinal) ||
            !string.Equals(args[1], "--corpus", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(args[2]))
        {
            error.WriteLine(InvalidCommandError);
            return 2;
        }

        try
        {
            var corpus = (corpusLoader ?? VisualEvaluationCorpusLoader.Load)(args[2]);
            var report = new VisualEvaluationRunner().Evaluate(corpus);
            output.Write(VisualEvaluationReportSerializer.Serialize(report));
            return 0;
        }
        catch (Exception exception) when (exception is
            IOException or
            JsonException or
            UnauthorizedAccessException or
            ArgumentException or
            NotSupportedException or
            SecurityException or
            VisualEvaluationCorpusException)
        {
            error.WriteLine(CorpusRejectedError);
            return 1;
        }
    }
}
