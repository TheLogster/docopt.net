#nullable enable

namespace DocoptNet.Tests.CodeGeneration
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.CompilerServices;
    using System.Runtime.Loader;
    using System.Text.Encodings.Web;
    using System.Text.Json;
    using System.Threading;
    using DocoptNet.CodeGeneration;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.Text;
    using NUnit.Framework;
    using static MoreLinq.Extensions.FullJoinExtension;
    using RsAnalyzerConfigOptions = Microsoft.CodeAnalysis.Diagnostics.AnalyzerConfigOptions;

    [TestFixture]
    public class SourceGeneratorTests
    {
        const string NavalFateUsage = @"
Naval Fate.

    Usage:
      naval_fate.exe ship new <name>...
      naval_fate.exe ship <name> move <x> <y> [--speed=<kn>]
      naval_fate.exe ship shoot <x> <y>
      naval_fate.exe mine (set|remove) <x> <y> [--moored | --drifting]
      naval_fate.exe (-h | --help)
      naval_fate.exe --version

    Options:
      -h --help     Show this screen.
      --version     Show version.
      --speed=<kn>  Speed in knots [default: 10].
      --moored      Moored (anchored) mine.
      --drifting    Drifting mine.
";

        [Test]
        public void Generate()
        {
            var source = SourceGenerator.Generate("NavalFate", "Program", SourceText.From(NavalFateUsage)).ToString();
            Assert.That(source, Is.Not.Empty);
        }

        [Test]
        public void Generate_via_driver()
        {
            AssertMatchesSnapshot(new[]
            {
                ("Program.docopt.txt", SourceText.From(NavalFateUsage))
            });
        }

        readonly Dictionary<string, ImmutableArray<byte>> _projectFileHashByPath = new();
        DirectoryInfo? _solutionDir;

        string SolutionDirPath => _solutionDir?.FullName ?? throw new NullReferenceException();

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            _solutionDir = new DirectoryInfo(Path.Combine(TestContext.CurrentContext.TestDirectory,
                                                          "..", "..", "..", "..", ".."));

            var projectSourcesDir = new DirectoryInfo(Path.Combine(SolutionDirPath, "src", "DocoptNet"));
            foreach (var file in projectSourcesDir.EnumerateFiles("*.cs"))
            {
                using var stream = file.OpenRead();
                _projectFileHashByPath.Add(file.Name, SourceText.From(stream).GetChecksum());
            }
        }

        void AssertMatchesSnapshot((string Path, SourceText Text)[] sources,
                                   [CallerMemberName]string? callerName = null)
        {
            var (driver, compilation) = PrepareForGeneration(sources);

            var grr = driver.RunGenerators(compilation).GetRunResult().Results.Single();

            Assert.That(grr.Exception, Is.Null);

            var testPath = Path.Combine(nameof(SourceGeneratorTests), callerName!);
            var actualSourcesPath = Path.Combine(TestContext.CurrentContext.WorkDirectory, testPath);

            // Re-create the directory holding the actual results, deleting anything
            // leftover from a previous run.

            try
            {
                Directory.Delete(actualSourcesPath, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
                // ignore
            }

            Directory.CreateDirectory(actualSourcesPath);

            // If there were diagnostics emitted then write a file with a
            // JSON array where each element is a JSON object representing
            // one diagnostic record.

            if (grr.Diagnostics is { Length: > 0 } diagnostics)
            {
                var ds =
                    from d in diagnostics
                    let ls = d.Location.GetLineSpan()
                    select new
                    {
                        d.Id,
                        Severity = d.Severity.ToString(),
                        d.WarningLevel,
                        d.Descriptor.Category,
                        Title = d.Descriptor.Title.ToString(),
                        Message = d.GetMessage(),
                        ls.StartLinePosition.Line,
                        Char = ls.StartLinePosition.Character,
                    };

                var diagnosticsJson = JsonSerializer.Serialize(ds, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                });

                File.WriteAllText(Path.Combine(actualSourcesPath, "diagnostics.json"),
                                  diagnosticsJson.TrimEnd() + Environment.NewLine);
            }

            // Write the generated source, but skip any sources that are from the
            // the core project and unchanged.

            foreach (var gsr in from e in grr.GeneratedSources
                                where !_projectFileHashByPath.TryGetValue(Path.GetFileName(e.HintName), out var hash)
                                   || !e.SourceText.GetChecksum().SequenceEqual(hash)
                                select e)
            {
                using var sw = File.CreateText(Path.Combine(actualSourcesPath, gsr.HintName));
                gsr.SourceText.Write(sw);
            }

            // Compare the inventory of actual and expected files. Where a file exists
            // in both, compare content to be equal too.

            var expectedSourcesPath = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "CodeGeneration", testPath));

            IEnumerable<string> EnumerateFiles(string dirPath) =>
                from fp in Directory.EnumerateFiles(dirPath)
                where Path.GetFileName(fp) is { } fn
                   && fn[0] != '.' // ignore files starting with a dot (conventionally hidden)
                   && (fn.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                       || fn.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                select Path.GetRelativePath(SolutionDirPath, fp);

            var actualFiles = EnumerateFiles(actualSourcesPath);

            var expectedFiles = Directory.Exists(expectedSourcesPath)
                              ? EnumerateFiles(expectedSourcesPath)
                              : Enumerable.Empty<string>();

            var results =
                expectedFiles.FullJoin(actualFiles,
                                       Path.GetFileName,
                                       ef => (SnapshotComparisonResult)new ExtraFile(ef),
                                       af => new NewFile(Path.GetRelativePath(SolutionDirPath, Path.Combine(expectedSourcesPath, Path.GetFileName(af))), af),
                                       (ef, af) => AreFileContentEqual(Path.Combine(SolutionDirPath, ef), Path.Combine(SolutionDirPath, af))
                                                 ? new MatchingFile(ef, af)
                                                 : new MismatchingFile(ef, af))
                             .ToImmutableArray();

            // If everything was a match then consider it a pass!

            if (results.All(r => r is MatchingFile))
            {
                Assert.Pass();
                return;
            }

            // Otherwise fail, providing a summary of the differences.

            var deltaLines =
                ImmutableArray.CreateRange(
                    from r in results
                    select r switch
                    {
                        ExtraFile(var expected)                   => $"D\t{expected}",
                        NewFile(var expected, var actual)         => $"?\t{expected}\t{actual}",
                        MatchingFile(var expected, var actual)    => $"=\t{expected}\t{actual}",
                        MismatchingFile(var expected, var actual) => $"M\t{expected}\t{actual}",
                        _ => throw new SwitchExpressionException(r),
                    });

            int extraCount = 0, newCount = 0, matchCount = 0, mismatchCount = 0;

            foreach (var r in results)
            {
                switch (r)
                {
                    case ExtraFile: extraCount++; break;
                    case NewFile: newCount++; break;
                    case MatchingFile: matchCount++; break;
                    case MismatchingFile: mismatchCount++; break;
                    default: throw new SwitchExpressionException(r);
                }
            }

            var resultFilePath = Path.Combine(actualSourcesPath, ".testdiff");
            File.WriteAllLines(resultFilePath, deltaLines);
            Assert.Fail($@"Generated code failed expectations:

- {matchCount} matched
- {extraCount} deleted
- {newCount} added
- {mismatchCount} modified
- {grr.Diagnostics.Length} diagnostic(s)

For details, run:

cd ""{SolutionDirPath}""
dotnet tool restore
dotnet script {Path.Combine("tests", "DocoptNet.Tests", "sgss.csx")} inspect -i");
        }

        abstract record SnapshotComparisonResult;
        sealed record ExtraFile(string ExpectedPath) : SnapshotComparisonResult;
        sealed record NewFile(string ExpectedPath, string ActualPath) : SnapshotComparisonResult;
        sealed record MatchingFile(string ExpectedPath, string ActualPath) : SnapshotComparisonResult;
        sealed record MismatchingFile(string ExpectedPath, string ActualPath) : SnapshotComparisonResult;

        static bool AreFileContentEqual(string first, string second)
        {
            using var fs1 = File.OpenRead(first);
            using var fs2 = File.OpenRead(second);
            return fs1.ContentEquals(fs2);
        }

        [Test]
        public void Run_via_driver()
        {
            var generatedProgram = GenerateProgram(NavalFateUsage);
            var args = generatedProgram.Run(args => new NavalFateArgs(args), "ship", "new", "foo", "bar");

            Assert.That(args.Count, Is.EqualTo(15));

            Assert.That(args.CmdShip, Is.True);
            Assert.That(args.CmdNew, Is.True);

            var name = args.ArgName!.Cast<string>().ToList();
            Assert.That(name, Is.Not.Null);
            Assert.That(name.Count, Is.EqualTo(2));
            Assert.That(name[0], Is.EqualTo("foo"));
            Assert.That(name[1], Is.EqualTo("bar"));

            Assert.That(args.CmdMove, Is.False);
            Assert.That(args.ArgX, Is.Null);
            Assert.That(args.ArgY, Is.Null);
            Assert.That(args.OptSpeed, Is.EqualTo("10"));
            Assert.That(args.CmdShoot, Is.False);
            Assert.That(args.CmdMine, Is.False);
            Assert.That(args.CmdSet, Is.False);
            Assert.That(args.CmdRemove, Is.False);
            Assert.That(args.OptMoored, Is.False);
            Assert.That(args.OptDrifting, Is.False);
            Assert.That(args.OptHelp, Is.False);
            Assert.That(args.OptVersion, Is.False);
        }

        sealed record DocoptOptions(bool Help = true,
                                    object? Version = null,
                                    bool OptionsFirst = false,
                                    bool Exit = false)
        {
            public static readonly DocoptOptions Default = new();
        }

        class ProgramArgs
        {
            readonly IDictionary _args;

            public ProgramArgs(IDictionary args) =>
                _args = args ?? throw new ArgumentNullException("args");

            public int Count => _args.Count;

            public T Get<T>(string key, Func<object?, T> selector) =>
                _args.Contains(key)
                ? selector(_args[key])
                : throw new KeyNotFoundException("Key was not present in the dictionary: " + key);
        }

        sealed class NavalFateArgs : ProgramArgs
        {
            public NavalFateArgs(IDictionary args) : base(args) {}

            public bool?        CmdShip        => Get("ship", v => (bool?)v);
            public bool?        CmdNew         => Get("new", v => (bool?)v);
            public IEnumerable? ArgName        => Get("<name>", v => (IEnumerable?)v);
            public bool?        CmdMove        => Get("move", v => (bool?)v);
            public int?         ArgX           => Get("<x>", v => (int?)v);
            public int?         ArgY           => Get("<y>", v => (int?)v);
            public string?      OptSpeed       => Get("--speed", v => (string?)v);
            public bool?        CmdShoot       => Get("shoot", v => (bool?)v);
            public bool?        CmdMine        => Get("mine", v => (bool?)v);
            public bool?        CmdSet         => Get("set", v => (bool?)v);
            public bool?        CmdRemove      => Get("remove", v => (bool?)v);
            public bool?        OptMoored      => Get("--moored", v => (bool?)v);
            public bool?        OptDrifting    => Get("--drifting", v => (bool?)v);
            public bool?        OptHelp        => Get("--help", v => (bool?)v);
            public bool?        OptVersion     => Get("--version", v => (bool?)v);
        }

        sealed class Program
        {
            readonly Type _type;

            public Program(Type type) => _type = type ?? throw new ArgumentNullException(nameof(type));

            public ProgramArgs Run(params string[] argv) =>
                Run(DocoptOptions.Default, argv);

            public ProgramArgs Run(DocoptOptions options, params string[] argv) =>
                Run(options, args => new ProgramArgs(args), argv);

            public T Run<T>(Func<IDictionary, T> selector, params string[] argv) =>
                Run<T>(DocoptOptions.Default, selector, argv);

            public T Run<T>(DocoptOptions options, Func<IDictionary, T> selector, params string[] argv)
            {
                const string applyMethodName = "Apply";
                var method = _type.GetMethod(applyMethodName, BindingFlags.Public | BindingFlags.Static);
                if (method == null)
                    throw new MissingMethodException(_type.Name, applyMethodName);
                var args = (IEnumerable<KeyValuePair<string, object?>>)
                    method.Invoke(null, new[]
                    {
                        argv,
                        options.Help, options.Version,
                        options.OptionsFirst
                    })!;
                Assert.That(args, Is.Not.Null);
                return selector(args.ToDictionary(e => e.Key, e => e.Value));
            }
        }

        const string ProgramArgumentsClassName = nameof(Program) + "Arguments";

        static Program GenerateProgram(string doc)
        {
            const string main = @"
using System.Collections.Generic;
using DocoptNet.Generated;

public partial class " + ProgramArgumentsClassName + @" { }

namespace DocoptNet.Generated
{
    public partial class StringList {}
}
";

            var assembly = GenerateProgram(("Main.cs", SourceText.From(main)),
                                           ("Program.docopt.txt", SourceText.From(doc)));

            return new Program(assembly.GetType(nameof(Program) + "Arguments")!);
        }

        static int _assemblyUniqueCounter;

        static readonly RsAnalyzerConfigOptions DocoptSourceItemTypeConfigOption =
            new AnalyzerConfigOptions(KeyValuePair.Create("build_metadata.AdditionalFiles.SourceItemType", "Docopt"));

        internal static (CSharpGeneratorDriver, CSharpCompilation)
            PrepareForGeneration(params (string Path, SourceText Text)[] sources)
        {
            var references =
                from asm in AppDomain.CurrentDomain.GetAssemblies()
                where !asm.IsDynamic && !string.IsNullOrWhiteSpace(asm.Location)
                select MetadataReference.CreateFromFile(asm.Location);

            var trees = new List<SyntaxTree>();
            var additionalTexts = new List<AdditionalText>();

            foreach (var (path, text) in sources)
            {
                if (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                    trees.Add(CSharpSyntaxTree.ParseText(text, path: path));
                else if (path.EndsWith("docopt.txt", StringComparison.OrdinalIgnoreCase))
                    additionalTexts.Add(new AdditionalTextSource(path, text));
                else
                    throw new NotSupportedException($"Unsupported source path: {path}");
            }

            var compilation =
                CSharpCompilation.Create(FormattableString.Invariant($"test{Interlocked.Increment(ref _assemblyUniqueCounter)}"),
                                         trees, references,
                                         new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            ISourceGenerator generator = new SourceGenerator();

            var optionsProvider =
                new AnalyzerConfigOptionsProvider(
                    from at in additionalTexts
                    select KeyValuePair.Create(at, DocoptSourceItemTypeConfigOption));

            var driver = CSharpGeneratorDriver.Create(new[] { generator },
                                                      additionalTexts,
                                                      optionsProvider: optionsProvider);

            return (driver, compilation);
        }

        internal static Assembly GenerateProgram(params (string Path, SourceText Text)[] sources)
        {
            var (driver, compilation) = PrepareForGeneration(sources);

            driver.RunGeneratorsAndUpdateCompilation(compilation,
                                                     out var outputCompilation,
                                                     out var generateDiagnostics);

            Assert.False(generateDiagnostics.Any(d => d.Severity == DiagnosticSeverity.Error),
                         "Failed: " + generateDiagnostics.FirstOrDefault()?.GetMessage());

            using var ms = new MemoryStream();
            var emitResult = outputCompilation.Emit(ms);

            Assert.False(emitResult.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error),
                         "Failed: " + emitResult.Diagnostics.FirstOrDefault());

            ms.Position = 0;

            return AssemblyLoadContext.Default.LoadFromStream(ms);
        }
    }
}