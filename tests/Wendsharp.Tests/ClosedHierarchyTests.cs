using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging.Abstractions;

namespace WendSharp.Tests;

// Closed hierarchies need LangVersion=preview and System.Runtime.CompilerServices.IsClosedTypeAttribute,
// which does not ship until .NET 11. Compiled in memory here rather than pushing a preview language
// version and a hand-rolled BCL attribute into the sample project.
public class ClosedHierarchyTests
{
    const string ClosedSource = """
        namespace System.Runtime.CompilerServices
        {
            [AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface | AttributeTargets.Struct)]
            public sealed class IsClosedTypeAttribute : System.Attribute
            {
                public IsClosedTypeAttribute() { }
            }
        }
        namespace Fixture
        {
            public closed class Shape { }
            public sealed class Circle : Shape { }
            public sealed class Square : Shape { }

            public abstract class OpenBase { }
            public sealed class OpenDerived : OpenBase { }
        }
        """;

    static Compilation CompileClosedFixture()
    {
        var coreDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        MetadataReference[] refs =
        [
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(Path.Combine(coreDir, "System.Runtime.dll")),
        ];

        var tree = CSharpSyntaxTree.ParseText(
            ClosedSource, new CSharpParseOptions(LanguageVersion.Preview));

        return CSharpCompilation.Create(
            "ClosedFixture", [tree], refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    static INamedTypeSymbol GetType(Compilation compilation, string metadataName) =>
        compilation.GetTypeByMetadataName(metadataName)
        ?? throw new InvalidOperationException($"Fixture type '{metadataName}' not found.");

    [Test]
    public async Task ClosedFixture_CompilesWithoutErrors()
    {
        // If a future compiler rejects this syntax the tests below would silently degrade into
        // "nothing is closed" and keep passing for the wrong reason.
        var errors = CompileClosedFixture().GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => $"{d.Id}: {d.GetMessage()}")
            .ToArray();

        await Assert.That(errors).IsEmpty();
    }

    [Test]
    public async Task ClosedType_ReportsExhaustiveDerivedSet()
    {
        var compilation = CompileClosedFixture();
        var shape = GetType(compilation, "Fixture.Shape");

        var info = WendSharpTools.DescribeClosedHierarchy(shape, CancellationToken.None);

        await Assert.That(info.IsClosed).IsTrue();
        await Assert.That(info.IsExhaustive).IsTrue();
        await Assert.That(info.DirectDerivedTypes).Contains("Fixture.Circle");
        await Assert.That(info.DirectDerivedTypes).Contains("Fixture.Square");
        await Assert.That(info.DirectDerivedTypes.Length).IsEqualTo(2);
    }

    [Test]
    public async Task OpenType_IsNeverReportedExhaustive()
    {
        var compilation = CompileClosedFixture();
        var openBase = GetType(compilation, "Fixture.OpenBase");

        var info = WendSharpTools.DescribeClosedHierarchy(openBase, CancellationToken.None);

        await Assert.That(info.IsClosed).IsFalse();
        await Assert.That(info.IsExhaustive).IsFalse();
        await Assert.That(info.DirectDerivedTypes).IsEmpty();
    }

    [Test]
    public async Task SealedLeafOfClosedHierarchy_IsNotItselfClosed()
    {
        var compilation = CompileClosedFixture();
        var circle = GetType(compilation, "Fixture.Circle");

        var info = WendSharpTools.DescribeClosedHierarchy(circle, CancellationToken.None);

        await Assert.That(info.IsClosed).IsFalse();
        await Assert.That(info.IsExhaustive).IsFalse();
    }

    [Test]
    public async Task GetImplementations_OrdinaryInterface_ReportsNotExhaustive()
    {
        var repoRoot = FindRepoRoot();
        var session = new WorkspaceSession(
            NullLogger<WorkspaceSession>.Instance,
            Path.Combine(repoRoot, "sample", "Sample.slnx"));
        var tools = new WendSharpTools(session);
        await session.InitializeAsync(CancellationToken.None);

        var result = await tools.GetImplementations("App", "SampleApp.IComputable", CancellationToken.None);

        await Assert.That(result.Total).IsGreaterThan(0);
        await Assert.That(result.Closed.IsClosed).IsFalse();
        await Assert.That(result.Closed.IsUnion).IsFalse();
        await Assert.That(result.Closed.IsExhaustive).IsFalse();
        await Assert.That(result.Closed.DirectDerivedTypes).IsEmpty();
    }

    static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "sample", "Sample.slnx")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException("Could not locate repo root with sample/Sample.slnx");
    }
}
