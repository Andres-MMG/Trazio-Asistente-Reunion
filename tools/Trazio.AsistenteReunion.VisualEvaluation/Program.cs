using System.Security;
using System.Text;
using System.Text.Json;
using Trazio.AsistenteReunion.VisualEvaluation;

return VisualEvaluationCli.Run(args, Console.Out, Console.Error);

public static class VisualEvaluationCli
{
    public const string InvalidCommandError = "VE001 invalid-command";
    public const string CorpusRejectedError = "VE100 corpus-rejected";
    public const string GoldenMismatchError = "VE200 golden-mismatch";
    public const string VerifiedMessage = "VE000 verified";

    public static int Run(
        string[] args,
        TextWriter output,
        TextWriter error,
        Func<string, VisualEvaluationCorpus>? corpusLoader = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        var evaluate = args.Length == 3 &&
                       string.Equals(args[0], "evaluate", StringComparison.Ordinal) &&
                       string.Equals(args[1], "--corpus", StringComparison.Ordinal) &&
                       !string.IsNullOrWhiteSpace(args[2]);
        var verify = args.Length == 5 &&
                     string.Equals(args[0], "verify", StringComparison.Ordinal) &&
                     string.Equals(args[1], "--corpus", StringComparison.Ordinal) &&
                     !string.IsNullOrWhiteSpace(args[2]) &&
                     string.Equals(args[3], "--golden", StringComparison.Ordinal) &&
                     !string.IsNullOrWhiteSpace(args[4]);
        if (!evaluate && !verify)
        {
            error.WriteLine(InvalidCommandError);
            return 2;
        }

        try
        {
            var corpus = (corpusLoader ?? VisualEvaluationCorpusLoader.Load)(args[2]);
            var report = new VisualEvaluationRunner().Evaluate(corpus);
            var canonicalReport = VisualEvaluationReportSerializer.Serialize(report);
            if (evaluate)
            {
                output.Write(canonicalReport);
                return 0;
            }

            if (!GoldenMatches(args[4], canonicalReport))
            {
                error.WriteLine(GoldenMismatchError);
                return 1;
            }

            output.WriteLine(VerifiedMessage);
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

    private static bool GoldenMatches(string path, string canonicalReport)
    {
        var expected = Encoding.UTF8.GetBytes(canonicalReport);

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 8 * 1024,
            FileOptions.SequentialScan);
        if (stream.Length != expected.Length) return false;

        var buffer = new byte[8 * 1024];
        var offset = 0;
        while (offset < expected.Length)
        {
            var read = stream.Read(buffer, 0, Math.Min(buffer.Length, expected.Length - offset));
            if (read == 0 || !expected.AsSpan(offset, read).SequenceEqual(buffer.AsSpan(0, read)))
                return false;
            offset += read;
        }

        return stream.ReadByte() == -1;
    }
}
