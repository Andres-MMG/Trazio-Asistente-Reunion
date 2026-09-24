using System.Xml.Linq;
using Trazio.AsistenteReunion.App;
using Trazio.AsistenteReunion.Core;
using Trazio.AsistenteReunion.VisualEvaluation;

namespace Trazio.AsistenteReunion.Tests;

public sealed class VisualEvaluationIsolationTests
{
    [Fact]
    [Trait("Area", "VisualCapture")]
    public void SharedAndEvaluatorProjects_ArePackageFreeAndPlatformNeutral()
    {
        var root = FindRepositoryRoot();
        var shared = LoadProject(root, "src", "Trazio.AsistenteReunion.VisualAnalysis", "Trazio.AsistenteReunion.VisualAnalysis.csproj");
        var evaluator = LoadProject(root, "tools", "Trazio.AsistenteReunion.VisualEvaluation", "Trazio.AsistenteReunion.VisualEvaluation.csproj");

        AssertPureProject(shared, expectedOutputType: null);
        AssertPureProject(evaluator, expectedOutputType: "Exe");
        Assert.Empty(shared.Descendants("ProjectReference"));
        var evaluatorReference = Assert.Single(evaluator.Descendants("ProjectReference"));
        Assert.EndsWith(
            "src\\Trazio.AsistenteReunion.VisualAnalysis\\Trazio.AsistenteReunion.VisualAnalysis.csproj",
            evaluatorReference.Attribute("Include")!.Value,
            StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void ProductionVisualTypes_ResolveFromExpectedAssemblies()
    {
        const string shared = "Trazio.AsistenteReunion.VisualAnalysis";
        const string core = "Trazio.AsistenteReunion.Core";

        Assert.Equal(shared, typeof(DeterministicVisualActivityDetector).Assembly.GetName().Name);
        Assert.Equal(shared, typeof(AnonymousVisualEvidenceInterval).Assembly.GetName().Name);
        Assert.Equal(shared, typeof(AudioSourceKind).Assembly.GetName().Name);
        Assert.Equal(core, typeof(AnonymousVisualActivityCorrelator).Assembly.GetName().Name);
        Assert.Equal(core, typeof(TranscriptSegment).Assembly.GetName().Name);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void SharedAndEvaluatorAssemblies_ExposeNoRichTranscriptContract()
    {
        var shared = typeof(AudioSourceKind).Assembly;
        var evaluator = typeof(VisualEvaluationRunner).Assembly;
        var segmentType = shared.GetType(
            "Trazio.AsistenteReunion.Core.AnonymousVisualCorrelationSegment",
            throwOnError: true)!;

        Assert.Null(shared.GetType("Trazio.AsistenteReunion.Core.TranscriptSegment"));
        Assert.Equal(
            ["AudioSourceKind", "End", "SessionId", "Start"],
            segmentType.GetProperties().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(
            evaluator.GetTypes().SelectMany(type => type.GetMembers()),
            member => MemberReferences(member, typeof(TranscriptSegment)));
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void CoreFacade_PreservesOriginalAbiAndForwardsExactSharedTypes()
    {
        var core = typeof(TranscriptSegment).Assembly;
        var facade = typeof(AnonymousVisualActivityCorrelator);
        var constructor = Assert.Single(facade.GetConstructors());
        Assert.Equal([typeof(AnonymousVisualCorrelationPolicy)], constructor.GetParameters().Select(parameter => parameter.ParameterType));
        var correlate = Assert.Single(
            facade.GetMethods(),
            method => method.Name == nameof(AnonymousVisualActivityCorrelator.Correlate));
        Assert.Equal(
            [typeof(TranscriptSegment), typeof(AnonymousVisualEvidenceReadResult)],
            correlate.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.Equal(typeof(AnonymousVisualCorrelation), correlate.ReturnType);

        var expected = new[]
        {
            typeof(AudioSourceKind),
            typeof(MeetingProvider),
            typeof(AnonymousVisualEvidenceVersions),
            typeof(AnonymousVisualEvidenceKind),
            typeof(AnonymousVisualAnalysisAvailability),
            typeof(AnonymousVisualEvidenceProvenance),
            typeof(AnonymousVisualEvidenceInterval),
            typeof(AnonymousVisualEvidenceReadStatus),
            typeof(AnonymousVisualEvidenceReadResult),
            typeof(AnonymousVisualCorrelationOutcome),
            typeof(AnonymousVisualCorrelationReason),
            typeof(AnonymousVisualCorrelation),
            typeof(AnonymousVisualCorrelationPolicy)
        }.OrderBy(type => type.FullName, StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, core.GetForwardedTypes().OrderBy(type => type.FullName, StringComparer.Ordinal));
        foreach (var type in expected)
        {
            var resolved = Type.GetType($"{type.FullName}, {core.GetName().Name}", throwOnError: true);
            Assert.Same(type, resolved);
        }
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void ApplicationPublishGraph_IncludesSharedAssemblyAndExcludesEvaluator()
    {
        var root = FindRepositoryRoot();
        var app = LoadProject(root, "src", "Trazio.AsistenteReunion.App", "Trazio.AsistenteReunion.App.csproj");
        var core = LoadProject(root, "src", "Trazio.AsistenteReunion.Core", "Trazio.AsistenteReunion.Core.csproj");
        var appReferences = app.Descendants("ProjectReference").Select(Include).ToArray();
        var coreReferences = core.Descendants("ProjectReference").Select(Include).ToArray();

        Assert.Contains(appReferences, reference => reference.Contains("Trazio.AsistenteReunion.VisualAnalysis", StringComparison.Ordinal));
        Assert.Contains(coreReferences, reference => reference.Contains("Trazio.AsistenteReunion.VisualAnalysis", StringComparison.Ordinal));
        Assert.DoesNotContain(appReferences, reference => reference.Contains("VisualEvaluation", StringComparison.Ordinal));
        Assert.DoesNotContain(coreReferences, reference => reference.Contains("VisualEvaluation", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void CleanEvaluatorOutput_HasExactInventoryAndDependencyDenylist()
    {
        var root = FindRepositoryRoot();
        var releaseRoot = Path.Combine(root, "tools", "Trazio.AsistenteReunion.VisualEvaluation", "bin", "Release");
        Assert.Equal(["net10.0"], Directory.GetDirectories(releaseRoot).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        var output = Path.Combine(releaseRoot, "net10.0");
        var files = Directory.GetFiles(output).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        var expected = new[]
        {
            "Trazio.AsistenteReunion.VisualAnalysis.dll",
            "Trazio.AsistenteReunion.VisualEvaluation.deps.json",
            "Trazio.AsistenteReunion.VisualEvaluation.dll",
            "Trazio.AsistenteReunion.VisualEvaluation.exe",
            "Trazio.AsistenteReunion.VisualEvaluation.runtimeconfig.json"
        }.Order(StringComparer.Ordinal);

        Assert.Equal(expected, files);
        var forbidden = new[]
        {
            "Trazio.AsistenteReunion.Core",
            "Trazio.AsistenteReunion.App",
            "SQLite",
            "NAudio",
            "WinRT",
            "Windows.SDK",
            "capability-manifest"
        };
        var allReleaseFiles = Directory.GetFiles(releaseRoot, "*", SearchOption.AllDirectories);
        foreach (var path in allReleaseFiles)
        {
            foreach (var token in forbidden)
                Assert.DoesNotContain(token, Path.GetFileName(path), StringComparison.OrdinalIgnoreCase);
            if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
            var contents = File.ReadAllText(path);
            foreach (var token in forbidden)
                Assert.DoesNotContain(token, contents, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void ApplicationReleaseOutput_ContainsSharedAssemblyButNotEvaluator()
    {
        var root = FindRepositoryRoot();
        var output = Path.Combine(
            root,
            "src",
            "Trazio.AsistenteReunion.App",
            "bin",
            "Release",
            "net10.0-windows10.0.19041.0",
            "win-x64");

        Assert.True(File.Exists(Path.Combine(output, "Trazio.AsistenteReunion.VisualAnalysis.dll")));
        Assert.False(File.Exists(Path.Combine(output, "Trazio.AsistenteReunion.VisualEvaluation.dll")));
    }

    [Theory]
    [Trait("Area", "VisualCapture")]
    [InlineData("missing-corpus-001.json")]
    [InlineData("\0")]
    public void Cli_PathFailures_ReturnStableSanitizedError(string path)
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = VisualEvaluationCli.Run(["evaluate", "--corpus", path], output, error);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Equal(VisualEvaluationCli.CorpusRejectedError + Environment.NewLine, error.ToString());
        Assert.DoesNotContain(path, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void Cli_DirectoryPath_ReturnsStableSanitizedError()
    {
        var path = FindRepositoryRoot();
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = VisualEvaluationCli.Run(["evaluate", "--corpus", path], output, error);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Equal(VisualEvaluationCli.CorpusRejectedError + Environment.NewLine, error.ToString());
        Assert.DoesNotContain(path, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void Cli_UnreadableCorpusFailure_DoesNotEchoExceptionDetail()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = VisualEvaluationCli.Run(
            ["evaluate", "--corpus", "corpus-001.json"],
            output,
            error,
            _ => throw new UnauthorizedAccessException("private path and detail"));

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Equal(VisualEvaluationCli.CorpusRejectedError + Environment.NewLine, error.ToString());
        Assert.DoesNotContain("private", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void Cli_InvalidCommand_ReturnsStableErrorWithoutUsageDetails()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = VisualEvaluationCli.Run([], output, error);

        Assert.Equal(2, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Equal(VisualEvaluationCli.InvalidCommandError + Environment.NewLine, error.ToString());
    }

    private static void AssertPureProject(XDocument project, string? expectedOutputType)
    {
        Assert.Equal("net10.0", Assert.Single(project.Descendants("TargetFramework")).Value);
        Assert.Equal("false", Assert.Single(project.Descendants("IsPackable")).Value);
        Assert.Equal(expectedOutputType, project.Descendants("OutputType").SingleOrDefault()?.Value);
        Assert.Empty(project.Descendants("PackageReference"));
        Assert.Empty(project.Descendants("Content"));
        Assert.Empty(project.Descendants("RuntimeIdentifier"));
        Assert.Empty(project.Descendants("UseWPF"));
        Assert.Empty(project.Descendants("AllowUnsafeBlocks"));
        Assert.DoesNotContain("-windows", project.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static XDocument LoadProject(string root, params string[] relativePath) =>
        XDocument.Load(Path.Combine([root, .. relativePath]));

    private static string Include(XElement reference) => reference.Attribute("Include")!.Value;

    private static bool MemberReferences(System.Reflection.MemberInfo member, Type forbiddenType) => member switch
    {
        System.Reflection.PropertyInfo property => property.PropertyType == forbiddenType,
        System.Reflection.FieldInfo field => field.FieldType == forbiddenType,
        System.Reflection.MethodInfo method => method.ReturnType == forbiddenType ||
                                               method.GetParameters().Any(parameter => parameter.ParameterType == forbiddenType),
        System.Reflection.ConstructorInfo constructor =>
            constructor.GetParameters().Any(parameter => parameter.ParameterType == forbiddenType),
        _ => false
    };

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Trazio.AsistenteReunion.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
