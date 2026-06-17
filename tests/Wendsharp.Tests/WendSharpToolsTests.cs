using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging.Abstractions;

namespace WendSharp.Tests;

public class WendSharpToolsTests
{
    static WendSharpToolsTests()
    {
        if (!MSBuildLocator.IsRegistered)
            MSBuildLocator.RegisterDefaults();
    }

    static readonly string RepoRoot = FindRepoRoot();
    static readonly string SampleSolutionPath = Path.Combine(RepoRoot, "sample", "Sample.slnx");
    static readonly string SampleAppPath = Path.Combine(RepoRoot, "sample", "App");

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
    static async Task<WendSharpTools> CreateToolsAsync()
    {
        var session = new WorkspaceSession(NullLogger<WorkspaceSession>.Instance, SampleSolutionPath);
        var tools = new WendSharpTools(session);
        await session.InitializeAsync(CancellationToken.None);
        return tools;
    }

    [Test]
    public async Task FindReferences_Add_ReturnsExactlyTwoReferences()
    {
        var tools = await CreateToolsAsync();
        var result = await tools.FindReferences("App", "Add", CancellationToken.None);

        await Assert.That(result.Total).IsEqualTo(2);
        await Assert.That(result.Symbol).IsEqualTo("SampleApp.Calculator.Add(int, int)");

        foreach (var hit in result.References)
        {
            await Assert.That(hit.FilePath).Contains("Program.cs");
        }
    }

    [Test]
    public async Task DescribeSymbol_Calculator_ShowsResolvedIntTypes()
    {
        var tools = await CreateToolsAsync();
        var desc = await tools.DescribeSymbol("App", "SampleApp.Calculator", CancellationToken.None);

        await Assert.That(desc.FullName).Contains("Calculator");

        var addMember = Array.Find(desc.Members, m => m.Signature.Contains("Add"));
        await Assert.That(addMember.Signature).Contains("int");
        await Assert.That(addMember.Signature).Contains("Add");
    }

    [Test]
    public async Task AnalyzeMember_CalculateTotal_DetectsInternalDependency()
    {
        var tools = await CreateToolsAsync();
        var analysis = await tools.AnalyzeMember(
            "App", "OrderService", "CalculateTotal", ct: CancellationToken.None);

        var hasTaxRate = analysis.InternalDependencies.Any(d => d.Contains("_taxRate"));
        await Assert.That(hasTaxRate).IsTrue();

        await Assert.That(analysis.Source).Contains("CalculateTotal");
    }

    [Test]
    public async Task AnalyzeMember_ExpressionBodied_DataFlowSucceeds()
    {
        var tools = await CreateToolsAsync();

        var analysis = await tools.AnalyzeMember(
            "App", "SampleApp.Calculator", "Add", ct: CancellationToken.None);

        await Assert.That(analysis.ReadVariables).Contains("a");
        await Assert.That(analysis.ReadVariables).Contains("b");
    }

    [Test]
    public async Task AnalyzeMember_BlockBodiedProperty_ReportsAccessorDataFlow()
    {
        var tools = await CreateToolsAsync();

        var analysis = await tools.AnalyzeMember(
            "App", "SampleApp.PropertyFlowExample", "BlockProperty", ct: CancellationToken.None);

        await Assert.That(analysis.ReadVariables.Contains("value")).IsTrue();
        await Assert.That(analysis.ReadVariables.Length).IsGreaterThan(0);

        await Assert.That(analysis.InternalDependencies.Any(d => d.Contains("_backing"))).IsTrue();
    }

    [Test]
    public async Task AnalyzeMember_InitOnlyAccessor_ReportsAccessorDataFlow()
    {
        var tools = await CreateToolsAsync();

        var analysis = await tools.AnalyzeMember(
            "App", "SampleApp.PropertyFlowExample", "InitOnlyProperty", ct: CancellationToken.None);

        await Assert.That(analysis.ReadVariables.Contains("value")).IsTrue();

        await Assert.That(analysis.InternalDependencies.Any(d => d.Contains("_backing"))).IsTrue();
    }

    [Test]
    public async Task AnalyzeMember_AutoProperty_DataFlowIsEmptyWithoutThrowing()
    {
        var tools = await CreateToolsAsync();

        var analysis = await tools.AnalyzeMember(
            "App", "SampleApp.PropertyFlowExample", "AutoProperty", ct: CancellationToken.None);

        await Assert.That(analysis.ReadVariables.Length).IsEqualTo(0);
        await Assert.That(analysis.WrittenVariables.Length).IsEqualTo(0);
    }

    [Test]
    public async Task AnalyzeMember_EventWithAddRemove_ReportsAccessorDataFlow()
    {
        var tools = await CreateToolsAsync();

        var analysis = await tools.AnalyzeMember(
            "App", "SampleApp.EventFlowExample", "CustomEvent", ct: CancellationToken.None);

        await Assert.That(analysis.ReadVariables.Contains("value")).IsTrue();
        await Assert.That(analysis.ReadVariables.Length).IsGreaterThan(0);

        await Assert.That(analysis.InternalDependencies.Any(d => d.Contains("_handler"))).IsTrue();
    }

    [Test]
    public async Task PlanRename_Add_ToSum_ReturnsEditsInBothFilesWithoutWriting()
    {
        var tools = await CreateToolsAsync();

        var sampleFiles = Directory.GetFiles(SampleAppPath, "*.cs");
        var beforeBytes = new Dictionary<string, byte[]>();
        foreach (var f in sampleFiles)
            beforeBytes[f] = await File.ReadAllBytesAsync(f, CancellationToken.None);

        var plan = await tools.PlanRename(
            projectName: "App", fullyQualifiedSymbolName: "Add", newName: "Sum",
            ct: CancellationToken.None);

        await Assert.That(plan.Error).IsNull();
        await Assert.That(plan.NewNameValid).IsTrue();
        await Assert.That(plan.NewName).IsEqualTo("Sum");
        await Assert.That(plan.OldName).IsEqualTo("Add");

        await Assert.That(plan.Edits.Count).IsGreaterThan(0);
        await Assert.That(plan.FileCount).IsGreaterThanOrEqualTo(2);
        await Assert.That(plan.Edits.Any(e => e.FilePath.Contains("Calculator.cs"))).IsTrue();
        await Assert.That(plan.Edits.Any(e => e.FilePath.Contains("Program.cs"))).IsTrue();

        foreach (var e in plan.Edits)
        {
            await Assert.That(e.OldText).IsEqualTo("Add");
            await Assert.That(e.NewText).IsEqualTo("Sum");
            await Assert.That(e.Length).IsGreaterThan(0);
            await Assert.That(e.StartLine).IsGreaterThanOrEqualTo(1);
            await Assert.That(e.StartColumn).IsGreaterThanOrEqualTo(1);
        }

        var programEdit = plan.Edits.First(e => e.FilePath.Contains("Program.cs"));
        var programText = await File.ReadAllTextAsync(
            Path.Combine(SampleAppPath, "Program.cs"), CancellationToken.None);
        await Assert.That(programText.Substring(programEdit.StartOffset, programEdit.Length)).IsEqualTo("Add");

        foreach (var f in sampleFiles)
        {
            var afterBytes = await File.ReadAllBytesAsync(f, CancellationToken.None);
            await Assert.That(afterBytes).IsEquivalentTo(beforeBytes[f]);
        }
    }

    static readonly string ProgramPath = Path.Combine(SampleAppPath, "Program.cs");
    static readonly string KnownGoodProgram = File.ReadAllText(ProgramPath);

    [Test, NotInParallel]
    public async Task Refresh_Then_GetDiagnostics_ReflectsNewContent()
    {
        try
        {
            var tools = await CreateToolsAsync();

            var before = await tools.GetDiagnostics("App", ct: CancellationToken.None);
            await Assert.That(before.ErrorCount).IsEqualTo(0);

            await File.WriteAllTextAsync(ProgramPath,
                "namespace SampleApp;\npublic static class Program\n{\n    public static void Main() { BrokenSyntax }\n}\n",
                CancellationToken.None);

            await tools.Refresh([ProgramPath], CancellationToken.None);

            var after = await tools.GetDiagnostics("App", ct: CancellationToken.None);

            await Assert.That(after.ErrorCount).IsGreaterThan(0);
        }
        finally
        {

            await File.WriteAllTextAsync(ProgramPath, KnownGoodProgram, CancellationToken.None);
        }
    }

    [Test, NotInParallel]
    public async Task GetDiagnostics_FilePaths_Filter_ExcludesErrorsInOtherFiles()
    {
        try
        {
            var tools = await CreateToolsAsync();
            var calcPath = Path.Combine(SampleAppPath, "Calculator.cs");

            await File.WriteAllTextAsync(ProgramPath,
                "namespace SampleApp;\npublic static class Program\n{\n    public static void Main() { BrokenSyntax }\n}\n",
                CancellationToken.None);
            await tools.Refresh([ProgramPath], CancellationToken.None);

            var unfiltered = await tools.GetDiagnostics("App", ct: CancellationToken.None);
            await Assert.That(unfiltered.ErrorCount).IsGreaterThan(0);

            var filtered = await tools.GetDiagnostics(
                "App", filePaths: [calcPath], ct: CancellationToken.None);
            await Assert.That(filtered.ErrorCount).IsEqualTo(0);
            await Assert.That(filtered.Diagnostics.Length).IsEqualTo(0);
        }
        finally
        {
            await File.WriteAllTextAsync(ProgramPath, KnownGoodProgram, CancellationToken.None);
        }
    }

    [Test, NotInParallel]
    public async Task DescribeSymbol_SeesFileAddedViaRefresh_CacheInvalidated()
    {
        var newPath = Path.Combine(SampleAppPath, "_CacheProbeTemp.cs");
        try
        {
            var tools = await CreateToolsAsync();

            await tools.DescribeSymbol("App", "SampleApp.Calculator", CancellationToken.None);

            await File.WriteAllTextAsync(newPath,
                "namespace SampleApp;\npublic static class CacheProbe { public static int Probe() => 7; }\n",
                CancellationToken.None);
            await tools.Refresh([newPath], CancellationToken.None);

            var desc = await tools.DescribeSymbol("App", "SampleApp.CacheProbe", CancellationToken.None);
            await Assert.That(desc.Error).IsNull();
            await Assert.That(desc.Members.Any(m => m.Signature.Contains("Probe"))).IsTrue();
        }
        finally
        {
            if (File.Exists(newPath))
                File.Delete(newPath);
        }
    }

    [Test]
    public async Task ExploreCode_Calculator_StripsBodiesAndInitializers()
    {
        var tools = await CreateToolsAsync();
        var result = await tools.ExploreCode("Calculator.cs", CancellationToken.None);

        await Assert.That(result.Blueprint).Contains("Add");
        await Assert.That(result.Blueprint).Contains("Subtract");

        await Assert.That(result.Blueprint.Contains("=> a + b")).IsFalse();
    }

    [Test]
    public async Task ExploreCode_RelativePathWithDirectory_ResolvesFile()
    {
        var tools = await CreateToolsAsync();

        var result = await tools.ExploreCode("App/Calculator.cs", CancellationToken.None);

        await Assert.That(result.Error).IsNull();
        await Assert.That(result.Blueprint).Contains("Add");
        await Assert.That(result.FilePath).Contains("Calculator.cs");
    }

    [Test]
    public async Task ExploreCode_UnanchoredRelativePath_ReturnsError()
    {
        var tools = await CreateToolsAsync();
        var result = await tools.ExploreCode("p/Calculator.cs", CancellationToken.None);

        await Assert.That(result.Error).IsNotNull();
        await Assert.That(result.Blueprint).IsEmpty();
    }

    [Test]
    public async Task ExploreCode_StripsLocalFunctionBodies()
    {

        var source =
"""
using System;
int x = GetArg("--n");
Console.WriteLine(x);

static int GetArg(string name)
{
    var secret = 42;
    return secret + name.Length;
}
""";
        var tree = CSharpSyntaxTree.ParseText(source);
        var root = await tree.GetRootAsync(CancellationToken.None);
        var blueprint = new StripBodyRewriter().Visit(root)!.NormalizeWhitespace().ToFullString();

        await Assert.That(blueprint).Contains("GetArg");

        await Assert.That(blueprint.Contains("secret + name.Length")).IsFalse();
        await Assert.That(blueprint.Contains("var secret = 42")).IsFalse();
    }

    [Test]
    public async Task PlanRename_Edits_ContainNewName()
    {
        var tools = await CreateToolsAsync();
        var plan = await tools.PlanRename(
            projectName: "App", fullyQualifiedSymbolName: "Add", newName: "Sum",
            ct: CancellationToken.None);

        var hasSumEdit = plan.Edits.Any(e => e.NewText.Contains("Sum"));
        await Assert.That(hasSumEdit).IsTrue();
    }

    [Test]
    public async Task PlanRename_NameCollision_ReportsConflict()
    {
        var tools = await CreateToolsAsync();
        var plan = await tools.PlanRename(
            projectName: "App", fullyQualifiedSymbolName: "Alpha", newName: "Beta",
            ct: CancellationToken.None);

        await Assert.That(plan.HasConflicts).IsTrue();
        await Assert.That(plan.Conflicts.Count(c => c.Severity == "Error")).IsGreaterThanOrEqualTo(1);

        await Assert.That(plan.Conflicts.Any(c => c.Id == "CS0111")).IsTrue();
    }

    [Test]
    public async Task PlanRename_ValidNewName_HasNoConflicts()
    {
        var tools = await CreateToolsAsync();

        var plan = await tools.PlanRename(
            projectName: "App", fullyQualifiedSymbolName: "Alpha", newName: "Gamma",
            ct: CancellationToken.None);

        await Assert.That(plan.Error).IsNull();
        await Assert.That(plan.HasConflicts).IsFalse();
        await Assert.That(plan.Conflicts.Count).IsEqualTo(0);
    }

    [Test]
    public async Task PlanRename_VirtualMethod_ReportsOverrideCascade()
    {
        var tools = await CreateToolsAsync();
        var fixturePath = Path.Combine(SampleAppPath, "RenameFixtures.cs");

        var plan = await tools.PlanRename(
            projectName: "App", filePath: fixturePath, line: 10, column: 25,
            newName: "Execute", ct: CancellationToken.None);

        await Assert.That(plan.Error).IsNull();
        await Assert.That(plan.Cascades.Any(c => c.Reason == "Override")).IsTrue();
    }

    [Test]
    public async Task PlanRename_PartialMethod_ReportsPartialCascade()
    {
        var tools = await CreateToolsAsync();
        var plan = await tools.PlanRename(
            projectName: "App", fullyQualifiedSymbolName: "OnConfigured", newName: "OnReady",
            ct: CancellationToken.None);

        await Assert.That(plan.Error).IsNull();
        await Assert.That(plan.Cascades.Any(c => c.Reason == "PartialDeclaration")).IsTrue();
    }

    [Test]
    public async Task PlanRename_InterfaceMember_ReportsInterfaceCascade()
    {
        var tools = await CreateToolsAsync();
        var fixturePath = Path.Combine(SampleAppPath, "InheritanceFixtures.cs");

        var plan = await tools.PlanRename(
            projectName: "App", filePath: fixturePath, line: 5, column: 9,
            newName: "Evaluate", ct: CancellationToken.None);

        await Assert.That(plan.Error).IsNull();
        await Assert.That(plan.Cascades.Any(c => c.Reason == "InterfaceMember")).IsTrue();
    }

    [Test]
    public async Task PlanRename_NameofAndCref_Classified()
    {
        var tools = await CreateToolsAsync();
        var fixturePath = Path.Combine(SampleAppPath, "RenameFixtures.cs");

        var plan = await tools.PlanRename(
            projectName: "App", filePath: fixturePath, line: 10, column: 25,
            newName: "Execute", ct: CancellationToken.None);

        await Assert.That(plan.Error).IsNull();
        await Assert.That(plan.Edits.Any(e => e.Location == "Nameof")).IsTrue();
        await Assert.That(plan.Edits.Any(e => e.Location == "Cref")).IsTrue();
    }

    [Test]
    public async Task PlanRename_NoCommentEdits_WhenFlagOff()
    {
        var tools = await CreateToolsAsync();
        var plan = await tools.PlanRename(
            projectName: "App", fullyQualifiedSymbolName: "Add", newName: "Sum",
            ct: CancellationToken.None);

        await Assert.That(plan.Error).IsNull();
        await Assert.That(plan.Edits.Any(e => e.Location == "Comment")).IsFalse();
    }

    [Test]
    public async Task PlanRename_InvalidName_ReturnsError()
    {
        var tools = await CreateToolsAsync();
        var plan = await tools.PlanRename(
            projectName: "App", fullyQualifiedSymbolName: "Add", newName: "1foo",
            ct: CancellationToken.None);

        await Assert.That(plan.NewNameValid).IsFalse();
        await Assert.That(plan.Error).IsNotNull();
        await Assert.That(plan.Edits.Count).IsEqualTo(0);
        await Assert.That(plan.HasConflicts).IsFalse();
    }

    [Test]
    public async Task PlanRename_SymbolNotFound_ReturnsError()
    {
        var tools = await CreateToolsAsync();
        var plan = await tools.PlanRename(
            projectName: "App", fullyQualifiedSymbolName: "DoesNotExist", newName: "X",
            ct: CancellationToken.None);

        await Assert.That(plan.Error).IsNotNull();
        await Assert.That(plan.Edits.Count).IsEqualTo(0);
    }

    [Test]
    public async Task PlanRename_AmbiguousSimpleName_ReturnsError()
    {
        var tools = await CreateToolsAsync();

        var plan = await tools.PlanRename(
            projectName: "App", fullyQualifiedSymbolName: "DoWork", newName: "Execute",
            ct: CancellationToken.None);

        await Assert.That(plan.Error).IsNotNull();
        await Assert.That(plan.Error).Contains("ambiguous");
        await Assert.That(plan.Error).Contains("WidgetBase.DoWork");
        await Assert.That(plan.Error).Contains("WidgetDerived.DoWork");
        await Assert.That(plan.Edits.Count).IsEqualTo(0);
    }

    [Test]
    public async Task PlanRename_PositionLocator_ResolvesSymbol()
    {
        var tools = await CreateToolsAsync();
        var calcPath = Path.Combine(SampleAppPath, "Calculator.cs");

        var plan = await tools.PlanRename(
            projectName: "App", filePath: calcPath, line: 5, column: 23,
            newName: "Sum", ct: CancellationToken.None);

        await Assert.That(plan.Error).IsNull();
        await Assert.That(plan.Symbol).Contains("Calculator.Add");
        await Assert.That(plan.FileCount).IsGreaterThanOrEqualTo(2);
        await Assert.That(plan.Edits.Any(e => e.OldText == "Add" && e.NewText == "Sum")).IsTrue();
    }

    [Test]
    public async Task PlanRename_MetadataSymbol_ReturnsError()
    {
        var tools = await CreateToolsAsync();
        var calcPath = Path.Combine(SampleAppPath, "Calculator.cs");

        var plan = await tools.PlanRename(
            projectName: "App", filePath: calcPath, line: 5, column: 21,
            newName: "Sum", ct: CancellationToken.None);

        await Assert.That(plan.Error).IsNotNull();
        await Assert.That(plan.Edits.Count).IsEqualTo(0);
    }

    [Test]
    public async Task PlanRename_CascadePath_LeavesFilesUnchanged()
    {
        var tools = await CreateToolsAsync();
        var sampleFiles = Directory.GetFiles(SampleAppPath, "*.cs");
        var beforeBytes = new Dictionary<string, byte[]>();
        foreach (var f in sampleFiles)
            beforeBytes[f] = await File.ReadAllBytesAsync(f, CancellationToken.None);

        var fixturePath = Path.Combine(SampleAppPath, "RenameFixtures.cs");
        var plan = await tools.PlanRename(
            projectName: "App", filePath: fixturePath, line: 10, column: 25,
            newName: "Execute", ct: CancellationToken.None);

        await Assert.That(plan.Edits.Count).IsGreaterThan(0);

        foreach (var f in sampleFiles)
        {
            var afterBytes = await File.ReadAllBytesAsync(f, CancellationToken.None);
            await Assert.That(afterBytes).IsEquivalentTo(beforeBytes[f]);
        }
    }

    [Test]
    public async Task GetDiagnostics_IncludeWarnings_NoErrors()
    {
        var tools = await CreateToolsAsync();
        var result = await tools.GetDiagnostics("App", includeWarnings: true, ct: CancellationToken.None);

        await Assert.That(result.ErrorCount).IsEqualTo(0);
    }

    [Test]
    public async Task WorkspaceSession_InitializeAsync_ConcurrentCalls_AreSafe()
    {
        var session = new WorkspaceSession(NullLogger<WorkspaceSession>.Instance, SampleSolutionPath);

        var tasks = Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => session.InitializeAsync(CancellationToken.None).AsTask()))
            .ToArray();
        await Task.WhenAll(tasks);

        await Assert.That(session.Solution.Projects.Count()).IsEqualTo(1);

        await Assert.That(session.LoadedSolutionPath).IsNotNull();

        await session.InitializeAsync(CancellationToken.None);
        await Assert.That(session.Solution.Projects.Count()).IsEqualTo(1);
    }

    [Test, NotInParallel]
    public async Task WorkspaceSession_GetProject_IndexFollowsRefreshSwap()
    {
        var session = new WorkspaceSession(NullLogger<WorkspaceSession>.Instance, SampleSolutionPath);
        await session.InitializeAsync(CancellationToken.None);

        var before = session.GetProject("App");
        await Assert.That(before.Name).IsEqualTo("App");

        var existingPath = Path.Combine(SampleAppPath, "Program.cs");
        await session.RefreshAsync([existingPath], CancellationToken.None);

        var after = session.GetProject("App");
        await Assert.That(after.Name).IsEqualTo("App");

        await Assert.That(ReferenceEquals(after.Solution, before.Solution)).IsFalse();
    }

    [Test]
    public async Task WorkspaceSession_GetSourceTreePathsAsync_ConcurrentWithRefresh_IsSafe()
    {
        var session = new WorkspaceSession(NullLogger<WorkspaceSession>.Instance, SampleSolutionPath);
        await session.InitializeAsync(CancellationToken.None);

        var project = session.GetProject("App");

        var existingPath = Path.Combine(SampleAppPath, "Program.cs");

        var refreshes = Enumerable.Range(0, 4)
            .Select(_ => Task.Run(() => session.RefreshAsync([existingPath], CancellationToken.None).AsTask()))
            .ToArray();
        var readers = Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => session.GetSourceTreePathsAsync(project, CancellationToken.None).AsTask()))
            .ToArray();

        await Task.WhenAll(refreshes);
        await Task.WhenAll(readers);

        foreach (var t in readers)
        {
            var paths = await t;
            await Assert.That(paths).IsNotNull();
            await Assert.That(paths.Any(p => p.EndsWith("Program.cs", StringComparison.OrdinalIgnoreCase))).IsTrue();
        }
    }

    [Test]
    public async Task AnalyzeMember_OverloadedMember_NoParameterTypes_ReturnsError()
    {
        var tools = await CreateToolsAsync();

        var analysis = await tools.AnalyzeMember("App", "SampleApp.OverloadExample", "Calculate", ct: CancellationToken.None);

        await Assert.That(analysis.Error).IsNotNull();
        await Assert.That(analysis.Error).Contains("overloaded");
        await Assert.That(analysis.Error).Contains("\"int\"");
        await Assert.That(analysis.Error).Contains("\"string\"");
        await Assert.That(analysis.Error).Contains("parameterTypes");

        await Assert.That(analysis.Source).IsEmpty();
        await Assert.That(analysis.InternalDependencies.Length).IsEqualTo(0);
    }

    [Test]
    public async Task AnalyzeMember_OverloadedMember_StringParameterTypes_ResolvesStringOverload()
    {
        var tools = await CreateToolsAsync();

        var analysis = await tools.AnalyzeMember(
            "App", "SampleApp.OverloadExample", "Calculate", "string", CancellationToken.None);

        await Assert.That(analysis.Source).Contains("Calculate(string");
        await Assert.That(analysis.Source).DoesNotContain("Calculate(int");
    }

    [Test]
    public async Task AnalyzeMember_NonOverloadedMember_ResolvesWithoutParameterTypes()
    {
        var tools = await CreateToolsAsync();

        var analysis = await tools.AnalyzeMember(
            "App", "OrderService", "CalculateTotal", ct: CancellationToken.None);

        await Assert.That(analysis.Source).Contains("CalculateTotal");
    }

    [Test]
    public async Task DescribeSymbol_RecordStruct_ShowsPrimaryConstructorAndPositionalProperties()
    {
        var tools = await CreateToolsAsync();
        var desc = await tools.DescribeSymbol("App", "SampleApp.Point", CancellationToken.None);

        await Assert.That(desc.Members.Length).IsEqualTo(3);

        var ctors = desc.Members.Where(m => m.Kind == "Method").ToArray();
        await Assert.That(ctors.Length).IsEqualTo(1);
        await Assert.That(ctors[0].Signature).Contains("Point");
        await Assert.That(ctors[0].Signature).Contains("X");
        await Assert.That(ctors[0].Signature).Contains("Y");

        var props = desc.Members.Where(m => m.Kind == "Property").Select(m => m.Signature).ToArray();
        await Assert.That(props.Any(s => s.Contains(".X"))).IsTrue();
        await Assert.That(props.Any(s => s.Contains(".Y"))).IsTrue();

        await Assert.That(desc.Members.Any(m => m.Signature.Contains("get_"))).IsFalse();
        await Assert.That(desc.Members.Any(m => m.Signature.Contains("set_"))).IsFalse();
        await Assert.That(desc.Members.Any(m => m.Signature.Contains("EqualityContract"))).IsFalse();
        await Assert.That(desc.Members.Any(m => m.Signature.Contains("Deconstruct"))).IsFalse();
        await Assert.That(desc.Members.Any(m => m.Signature.Contains("<Clone>$"))).IsFalse();
    }

    [Test]
    public async Task DescribeSymbol_RecordClass_HidesEqualityContractAndClone()
    {
        var tools = await CreateToolsAsync();
        var desc = await tools.DescribeSymbol("App", "SampleApp.Tag", CancellationToken.None);

        await Assert.That(desc.Members.Length).IsEqualTo(4);

        await Assert.That(desc.Members.Any(m => m.Signature.Contains("EqualityContract"))).IsFalse();
        await Assert.That(desc.Members.Any(m => m.Signature.Contains("<Clone>$"))).IsFalse();
        await Assert.That(desc.Members.Any(m => m.Signature.Contains("get_Upper"))).IsFalse();
        await Assert.That(desc.Members.Any(m => m.Signature.Contains("PrintMembers"))).IsFalse();

        await Assert.That(desc.Members.Any(m => m.Kind == "Property" && m.Signature.Contains("Upper"))).IsTrue();
    }

    [Test]
    public async Task DescribeSymbol_PlainClass_HidesPropertyAccessors()
    {
        var tools = await CreateToolsAsync();
        var desc = await tools.DescribeSymbol("App", "SampleApp.Counter", CancellationToken.None);

        await Assert.That(desc.Members.Length).IsEqualTo(3);

        await Assert.That(desc.Members.Any(m => m.Signature.Contains("get_Value"))).IsFalse();
        await Assert.That(desc.Members.Any(m => m.Signature.Contains("set_Value"))).IsFalse();
        await Assert.That(desc.Members.Any(m => m.Kind == "Property" && m.Signature.Contains("Value"))).IsTrue();
        await Assert.That(desc.Members.Any(m => m.Signature.Contains("Increment"))).IsTrue();

        await Assert.That(desc.Members.Count(m => m.Kind == "Method")).IsEqualTo(2);
    }

    [Test]
    public async Task DescribeSymbol_AmbiguousSimpleName_ReturnsError()
    {
        var tools = await CreateToolsAsync();

        var desc = await tools.DescribeSymbol("App", "Calculator", CancellationToken.None);

        await Assert.That(desc.Error).IsNotNull();
        await Assert.That(desc.Error).Contains("SampleApp.Calculator");
        await Assert.That(desc.Error).Contains("OtherApp.Calculator");
        await Assert.That(desc.Error).Contains("metadata name");
        await Assert.That(desc.Members.Length).IsEqualTo(0);
        await Assert.That(desc.FullName).IsEmpty();
    }

    [Test]
    public async Task DescribeSymbol_MetadataName_ResolvesAmbiguousType()
    {
        var tools = await CreateToolsAsync();

        var desc = await tools.DescribeSymbol("App", "OtherApp.Calculator", CancellationToken.None);

        await Assert.That(desc.FullName).Contains("OtherApp.Calculator");
    }

    [Test]
    public async Task AnalyzeMember_AmbiguousTypeSimpleName_ReturnsError()
    {
        var tools = await CreateToolsAsync();

        var analysis = await tools.AnalyzeMember("App", "Calculator", "Label", ct: CancellationToken.None);

        await Assert.That(analysis.Error).IsNotNull();
        await Assert.That(analysis.Error).Contains("SampleApp.Calculator");
        await Assert.That(analysis.Error).Contains("OtherApp.Calculator");
        await Assert.That(analysis.Source).IsEmpty();
    }

    [Test]
    public async Task ExploreCode_FileNotFound_ReturnsError()
    {
        var tools = await CreateToolsAsync();
        var result = await tools.ExploreCode("DoesNotExist.cs", CancellationToken.None);

        await Assert.That(result.Error).IsNotNull();
        await Assert.That(result.Error).Contains("DoesNotExist.cs");
        await Assert.That(result.Blueprint).IsEmpty();
        await Assert.That(result.FilePath).IsEmpty();
    }

    [Test]
    public async Task DescribeSymbol_TypeNotFound_ReturnsError()
    {
        var tools = await CreateToolsAsync();
        var desc = await tools.DescribeSymbol("App", "SampleApp.NoSuchType", CancellationToken.None);

        await Assert.That(desc.Error).IsNotNull();
        await Assert.That(desc.Members.Length).IsEqualTo(0);
        await Assert.That(desc.BaseTypes.Length).IsEqualTo(0);
        await Assert.That(desc.RequiredUsings.Length).IsEqualTo(0);
    }

    [Test]
    public async Task AnalyzeMember_MemberNotFound_ReturnsError()
    {
        var tools = await CreateToolsAsync();
        var analysis = await tools.AnalyzeMember(
            "App", "SampleApp.Calculator", "NoSuchMember", ct: CancellationToken.None);

        await Assert.That(analysis.Error).IsNotNull();
        await Assert.That(analysis.Error).Contains("NoSuchMember");
        await Assert.That(analysis.Source).IsEmpty();
        await Assert.That(analysis.Callers.Length).IsEqualTo(0);
    }

    [Test]
    public async Task GetDiagnostics_ProjectNotFound_ReturnsError()
    {
        var tools = await CreateToolsAsync();
        var result = await tools.GetDiagnostics("NoSuchProject", ct: CancellationToken.None);

        await Assert.That(result.Error).IsNotNull();
        await Assert.That(result.Error).Contains("NoSuchProject");
        await Assert.That(result.ErrorCount).IsEqualTo(0);
        await Assert.That(result.Diagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task PlanRename_RenameFile_ReportsFileRename()
    {
        var tools = await CreateToolsAsync();

        var sampleFiles = Directory.GetFiles(SampleAppPath, "*.cs");
        var beforeBytes = new Dictionary<string, byte[]>();
        foreach (var f in sampleFiles)
            beforeBytes[f] = await File.ReadAllBytesAsync(f, CancellationToken.None);

        var plan = await tools.PlanRename(
            projectName: "App", fullyQualifiedSymbolName: "SampleApp.Foo",
            newName: "Bar", renameFile: true, ct: CancellationToken.None);

        await Assert.That(plan.Error).IsNull();

        await Assert.That(plan.FileRenames.Count).IsEqualTo(1);
        var rename = plan.FileRenames[0];
        await Assert.That(rename.OldPath).Contains("Foo.cs");
        await Assert.That(rename.NewPath).Contains("Bar.cs");
        await Assert.That(Path.GetDirectoryName(rename.OldPath)).IsEqualTo(Path.GetDirectoryName(rename.NewPath));

        await Assert.That(plan.Edits.Any(e => e.FilePath.Contains("Foo.cs"))).IsTrue();
        await Assert.That(plan.Edits.Any(e => e.FilePath.Contains("FooConsumer.cs"))).IsTrue();
        await Assert.That(plan.FileCount).IsGreaterThanOrEqualTo(2);

        foreach (var f in sampleFiles)
        {
            var afterBytes = await File.ReadAllBytesAsync(f, CancellationToken.None);
            await Assert.That(afterBytes).IsEquivalentTo(beforeBytes[f]);
        }
    }

    [Test]
    public async Task PlanRename_WithoutRenameFlag_HasNoFileRenames()
    {
        var tools = await CreateToolsAsync();
        var plan = await tools.PlanRename(
            projectName: "App", fullyQualifiedSymbolName: "SampleApp.Foo",
            newName: "Bar", renameFile: false, ct: CancellationToken.None);

        await Assert.That(plan.Error).IsNull();

        await Assert.That(plan.FileRenames.Count).IsEqualTo(0);
        await Assert.That(plan.Edits.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task CSharp14_ExtensionMembers_AreSurfacedByDescribeSymbolAndFindReferences()
    {
        var wencodeSlnx = Path.Combine(RepoRoot, "WendSharp.slnx");
        var session = new WorkspaceSession(NullLogger<WorkspaceSession>.Instance, wencodeSlnx);
        await session.InitializeAsync(CancellationToken.None);
        var tools = new WendSharpTools(session);

        var desc = await tools.DescribeSymbol("WendSharp", "SymbolExtensions", CancellationToken.None);
        await Assert.That(desc.Error).IsNull();
        await Assert.That(desc.Members.Any(m => m.Kind == "Method" && m.Signature.Contains("IsContainedBy"))).IsTrue();
        await Assert.That(desc.Members.Any(m => m.Kind == "Method" && m.Signature.Contains("ToFullSignature"))).IsTrue();

        await Assert.That(desc.Members.Any(m => m.Kind == "NamedType")).IsFalse();

        var isContainedBy = await tools.FindReferences("WendSharp", "IsContainedBy", CancellationToken.None);
        await Assert.That(isContainedBy.Kind).IsEqualTo("Method");

        await Assert.That(isContainedBy.Total).IsGreaterThan(0);

        var toFullSig = await tools.FindReferences("WendSharp", "ToFullSignature", CancellationToken.None);
        await Assert.That(toFullSig.Kind).IsEqualTo("Method");

        await Assert.That(toFullSig.Total).IsGreaterThan(0);
    }

    [Test]
    public async Task DescribeSymbol_GenericAndArrayUsings_RecursesIntoArguments()
    {
        var tools = await CreateToolsAsync();

        var desc = await tools.DescribeSymbol("App", "SampleApp.CustomerRepository", CancellationToken.None);

        await Assert.That(desc.RequiredUsings).Contains("System.Threading.Tasks");
        await Assert.That(desc.RequiredUsings).Contains("SampleApp.Domain");
        await Assert.That(desc.RequiredUsings).Contains("SampleApp.Billing");
    }
}
