using Microsoft.Extensions.Logging.Abstractions;

namespace WendSharp.Tests;

/// <summary>The agent-facing contract: ambiguity and "not found" always come back as Error plus
/// Candidates the agent can pass straight back, lists are paged, projectName is optional, edits on
/// disk are picked up without Refresh, and PlanRename is shaped for text-matching edit tools.</summary>
public class AgentContractTests
{
    static readonly string RepoRoot = FindRepoRoot();
    static readonly string SampleSolutionPath = Path.Combine(RepoRoot, "sample", "Sample.slnx");
    static readonly string SampleAppPath = Path.Combine(RepoRoot, "sample", "App");
    static readonly string ProgramPath = Path.Combine(SampleAppPath, "Program.cs");
    static readonly string CalculatorPath = Path.Combine(SampleAppPath, "Calculator.cs");

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

    // --- Ambiguity is reported with candidates, never as "not found" ---------------------------

    [Test]
    public async Task GetImplementations_AmbiguousTypeName_ReturnsCandidatesInsteadOfNotFound()
    {
        var tools = await CreateToolsAsync();

        var result = await tools.GetImplementations("Calculator", ct: CancellationToken.None);

        await Assert.That(result.Kind).IsEqualTo("Ambiguous");
        await Assert.That(result.Error).IsNotNull();
        var names = result.Candidates!.Select(c => c.Name).ToList();
        await Assert.That(names).Contains("SampleApp.Calculator");
        await Assert.That(names).Contains("OtherApp.Calculator");
    }

    [Test]
    public async Task GetOverrides_AmbiguousTypeName_ReturnsCandidatesInsteadOfTypeNotFound()
    {
        var tools = await CreateToolsAsync();

        var result = await tools.GetOverrides("Calculator", "Add", projectName: "App", ct: CancellationToken.None);

        await Assert.That(result.Kind).IsEqualTo("Ambiguous");
        await Assert.That(result.Error).IsNotNull();
        await Assert.That(result.Candidates!.Length).IsEqualTo(2);
    }

    [Test]
    public async Task FindReferences_AmbiguousTypeName_SetsErrorAndCandidates()
    {
        var tools = await CreateToolsAsync();

        var result = await tools.FindReferences("Calculator", projectName: "App", ct: CancellationToken.None);

        await Assert.That(result.Kind).IsEqualTo("Ambiguous");
        await Assert.That(result.Error).IsNotNull();
        await Assert.That(result.Symbol).IsEqualTo("Calculator");
        await Assert.That(result.Candidates!.Select(c => c.Name).ToList()).Contains("SampleApp.Calculator");
    }

    [Test]
    public async Task FindReferences_SameMethodNameOnDifferentTypes_IsAmbiguousNotOverloaded()
    {
        var tools = await CreateToolsAsync();

        // IComputable.Compute, ComputableImpl.Compute and OverloadableBase.Compute are different
        // members, not overloads: parameterTypes cannot tell them apart, the type can.
        var result = await tools.FindReferences("Compute", projectName: "App", ct: CancellationToken.None);

        await Assert.That(result.Kind).IsEqualTo("Ambiguous");
        await Assert.That(result.Error!).DoesNotContain("overloaded");
        var names = result.Candidates!.Select(c => c.Name).ToList();
        await Assert.That(names).Contains("SampleApp.IComputable.Compute");
        await Assert.That(names).Contains("SampleApp.ComputableImpl.Compute");
    }

    [Test]
    public async Task FindReferences_CandidatePassedBack_Resolves()
    {
        var tools = await CreateToolsAsync();
        var ambiguous = await tools.FindReferences("Compute", projectName: "App", ct: CancellationToken.None);
        var pick = ambiguous.Candidates!.First(c => c.Name == "SampleApp.IComputable.Compute");

        var result = await tools.FindReferences(
            pick.Name, pick.ParameterTypes, projectName: pick.Project, ct: CancellationToken.None);

        await Assert.That(result.Error).IsNull();
        await Assert.That(result.Kind).IsEqualTo("Method");
    }

    [Test]
    public async Task FindReferences_UnknownName_SuggestsSimilarSymbols()
    {
        var tools = await CreateToolsAsync();

        var result = await tools.FindReferences("Subtrac", ct: CancellationToken.None);

        await Assert.That(result.Kind).IsEqualTo("NotFound");
        await Assert.That(result.Error).IsNotNull();
        await Assert.That(result.Candidates!.Select(c => c.Name).ToList()).Contains("SampleApp.Calculator.Subtract");
    }

    // --- projectName is optional -----------------------------------------------------------------

    [Test]
    public async Task FindReferences_WithoutProjectName_FindsDeclaringProject()
    {
        var tools = await CreateToolsAsync();

        var result = await tools.FindReferences("SampleApp.Calculator.Add", ct: CancellationToken.None);

        await Assert.That(result.Error).IsNull();
        await Assert.That(result.Total).IsEqualTo(2);
    }

    [Test]
    public async Task DescribeSymbol_WithoutProjectName_ReportsTypeFileOnceNotPerMember()
    {
        var tools = await CreateToolsAsync();

        var desc = await tools.DescribeSymbol("SampleApp.Calculator", ct: CancellationToken.None);

        await Assert.That(desc.Error).IsNull();
        await Assert.That(Path.GetFileName(desc.FilePath)).IsEqualTo("Calculator.cs");
        await Assert.That(desc.Members.Length).IsGreaterThan(0);
        await Assert.That(desc.Members.All(m => m.FilePath is null)).IsTrue();
    }

    [Test]
    public async Task DescribeSymbol_FrameworkTypeWithoutProjectName_Resolves()
    {
        var tools = await CreateToolsAsync();

        var desc = await tools.DescribeSymbol("System.Text.StringBuilder", ct: CancellationToken.None);

        await Assert.That(desc.Error).IsNull();
        await Assert.That(desc.Members.Length).IsGreaterThan(0);
    }

    [Test]
    public async Task GetDiagnostics_FilePathsWithoutProjectName_UsesTheirProject()
    {
        var tools = await CreateToolsAsync();

        var result = await tools.GetDiagnostics(filePaths: [CalculatorPath], includeAnalyzers: false, ct: CancellationToken.None);

        await Assert.That(result.Error).IsNull();
        await Assert.That(result.Project).IsEqualTo("App");
        await Assert.That(result.ErrorCount).IsEqualTo(0);
    }

    [Test]
    public async Task GetDiagnostics_NeitherProjectNorFilePaths_ReturnsError()
    {
        var tools = await CreateToolsAsync();

        var result = await tools.GetDiagnostics(ct: CancellationToken.None);

        await Assert.That(result.Error).IsNotNull();
    }

    // --- Paging ----------------------------------------------------------------------------------

    [Test]
    public async Task FindReferences_Limit_PagesWithNextOffset()
    {
        var tools = await CreateToolsAsync();

        var first = await tools.FindReferences("SampleApp.Calculator.Add", limit: 1, ct: CancellationToken.None);
        await Assert.That(first.Total).IsEqualTo(2);
        await Assert.That(first.Truncated).IsTrue();
        await Assert.That(first.NextOffset).IsEqualTo(1);
        await Assert.That(first.Files.Sum(f => f.Hits.Length)).IsEqualTo(1);

        var second = await tools.FindReferences(
            "SampleApp.Calculator.Add", limit: 1, offset: first.NextOffset!.Value, ct: CancellationToken.None);
        await Assert.That(second.Truncated).IsFalse();
        await Assert.That(second.NextOffset).IsNull();
        await Assert.That(second.Files[0].Hits[0].Line).IsNotEqualTo(first.Files[0].Hits[0].Line);
    }

    [Test]
    public async Task FindReferences_GroupsByFile_AndOmitsContextUnlessDetail()
    {
        var tools = await CreateToolsAsync();

        var concise = await tools.FindReferences("SampleApp.Calculator.Add", ct: CancellationToken.None);
        await Assert.That(concise.Files.Length).IsEqualTo(1);
        await Assert.That(Path.GetFileName(concise.Files[0].FilePath)).IsEqualTo("Program.cs");
        await Assert.That(concise.Files[0].Hits.All(h => h.Context is null)).IsTrue();

        var detailed = await tools.FindReferences("SampleApp.Calculator.Add", detail: true, ct: CancellationToken.None);
        await Assert.That(detailed.Files[0].Hits.All(h => h.Context == "SampleApp.Program.Main")).IsTrue();
    }

    [Test]
    public async Task AnalyzeMember_DefaultOmitsSource_AndGroupsCallers()
    {
        var tools = await CreateToolsAsync();

        var analysis = await tools.AnalyzeMember("SampleApp.Calculator", "Add", ct: CancellationToken.None);

        await Assert.That(analysis.Error).IsNull();
        await Assert.That(analysis.Source).IsNull();
        await Assert.That(analysis.LineStart).IsGreaterThan(0);
        await Assert.That(analysis.CallerCount).IsEqualTo(2);
        await Assert.That(analysis.Callers.Length).IsEqualTo(1);
        await Assert.That(analysis.Callers[0].Hits.Length).IsEqualTo(2);
    }

    // --- Disk sync without Refresh ---------------------------------------------------------------

    [Test, NotInParallel]
    public async Task EditOnDisk_IsSeenWithoutRefresh()
    {
        var knownGood = await File.ReadAllTextAsync(ProgramPath, CancellationToken.None);
        try
        {
            var tools = await CreateToolsAsync();
            var before = await tools.GetDiagnostics(filePaths: [ProgramPath], includeAnalyzers: false, ct: CancellationToken.None);
            await Assert.That(before.ErrorCount).IsEqualTo(0);

            await File.WriteAllTextAsync(ProgramPath,
                "namespace SampleApp;\npublic static class Program\n{\n    public static void Main() { BrokenSyntax }\n}\n",
                CancellationToken.None);

            var broken = await tools.GetDiagnostics(filePaths: [ProgramPath], includeAnalyzers: false, ct: CancellationToken.None);
            await Assert.That(broken.ErrorCount).IsGreaterThan(0);

            await File.WriteAllTextAsync(ProgramPath, knownGood, CancellationToken.None);

            var restored = await tools.GetDiagnostics(filePaths: [ProgramPath], includeAnalyzers: false, ct: CancellationToken.None);
            await Assert.That(restored.ErrorCount).IsEqualTo(0);
        }
        finally
        {
            await File.WriteAllTextAsync(ProgramPath, knownGood, CancellationToken.None);
        }
    }

    [Test, NotInParallel]
    public async Task DeletedThenRecreatedFile_IsTrackedWithoutRefresh()
    {
        var path = Path.Combine(SampleAppPath, "SyncProbe.cs");
        const string source = "namespace SampleApp;\npublic class SyncProbe { public int Value { get; set; } }\n";
        try
        {
            await File.WriteAllTextAsync(path, source, CancellationToken.None);
            var tools = await CreateToolsAsync();
            var present = await tools.DescribeSymbol("SampleApp.SyncProbe", ct: CancellationToken.None);
            await Assert.That(present.Error).IsNull();

            File.Delete(path);
            var gone = await tools.DescribeSymbol("SampleApp.SyncProbe", ct: CancellationToken.None);
            await Assert.That(gone.Error).IsNotNull();

            // e.g. dotnet clean deleting obj/*.cs, then a build writing them again.
            await File.WriteAllTextAsync(path, source, CancellationToken.None);
            var back = await tools.DescribeSymbol("SampleApp.SyncProbe", ct: CancellationToken.None);
            await Assert.That(back.Error).IsNull();
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    // --- Dispatch targets of an interface member ---------------------------------------------------

    [Test]
    public async Task GetOverrides_InterfaceMember_ReturnsImplementationsInsteadOfEmpty()
    {
        var tools = await CreateToolsAsync();

        var result = await tools.GetOverrides("SampleApp.IComputable", "Compute", ct: CancellationToken.None);

        await Assert.That(result.Error).IsNull();
        await Assert.That(result.Kind).IsEqualTo("InterfaceMember");
        await Assert.That(result.Total).IsEqualTo(1);
        await Assert.That(result.Overrides[0].Signature).Contains("SampleApp.ComputableImpl.Compute");
    }

    [Test]
    public async Task GetOverrides_InterfaceMember_IncludesExplicitImplementationsAndOverridesOfImplementations()
    {
        var tools = await CreateToolsAsync();

        var result = await tools.GetOverrides("IShape", "Area", ct: CancellationToken.None);

        await Assert.That(result.Error).IsNull();
        await Assert.That(result.Kind).IsEqualTo("InterfaceMember");
        await Assert.That(result.Total).IsEqualTo(3);
        var signatures = result.Overrides.Select(o => o.Signature).ToList();
        await Assert.That(signatures.Any(s => s.Contains("SampleApp.Square.Area"))).IsTrue();
        await Assert.That(signatures.Any(s => s.Contains("SampleApp.LargeSquare.Area"))).IsTrue();
        await Assert.That(signatures.Any(s => s.Contains("SampleApp.Circle."))).IsTrue();
    }

    [Test]
    public async Task GetOverrides_ImplementingVirtualMember_StillReturnsItsOverrides()
    {
        var tools = await CreateToolsAsync();

        var result = await tools.GetOverrides("SampleApp.Square", "Area", ct: CancellationToken.None);

        await Assert.That(result.Kind).IsEqualTo("Virtual");
        await Assert.That(result.Total).IsEqualTo(1);
        await Assert.That(result.Overrides[0].Signature).Contains("SampleApp.LargeSquare.Area");
    }

    [Test]
    public async Task GetOverrides_InheritedMember_NamesTheDeclaringTypeAsCandidate()
    {
        var tools = await CreateToolsAsync();

        var result = await tools.GetOverrides("SampleApp.INamedShape", "Area", ct: CancellationToken.None);

        await Assert.That(result.Kind).IsEqualTo("MemberNotFound");
        await Assert.That(result.Error!).Contains("SampleApp.IShape");
        await Assert.That(result.Candidates!.Select(c => c.Name).ToList()).Contains("SampleApp.IShape.Area");
    }

    // --- PlanRename shaped for text-matching edit tools -------------------------------------------

    [Test]
    public async Task PlanRename_ReturnsWholeLinesBottomUp_AndReplaceAll()
    {
        var tools = await CreateToolsAsync();

        var plan = await tools.PlanRename(
            fullyQualifiedSymbolName: "SampleApp.Calculator.Add", newName: "Sum", ct: CancellationToken.None);

        await Assert.That(plan.Error).IsNull();
        await Assert.That(plan.EditCount).IsEqualTo(3);
        await Assert.That(plan.Truncated).IsFalse();
        await Assert.That(plan.Next!).Contains("GetDiagnostics");

        var program = plan.Files.First(f => f.FilePath.EndsWith("Program.cs"));
        await Assert.That(program.ReplaceAll).IsTrue();
        await Assert.That(program.Edits).IsNull();
        await Assert.That(program.Lines!.Count).IsEqualTo(2);
        await Assert.That(program.Lines[0].Line).IsGreaterThan(program.Lines[1].Line);
        await Assert.That(program.Lines[0].Before).IsEqualTo("var total = Calculator.Add(sum, diff);");
        await Assert.That(program.Lines[0].After).IsEqualTo("var total = Calculator.Sum(sum, diff);");
        await Assert.That(program.Lines[0].Location).IsEqualTo("Reference");
    }

    [Test]
    public async Task PlanRename_TwoEditsOnOneLine_AreMerged_AndReplaceAllIsFalseWhenUnsafe()
    {
        var tools = await CreateToolsAsync();

        // Parameter 'a' of Calculator.Add (line 5, column 31): a plain replace-all of "a" would
        // also hit "namespace", "static", "Calculator", so ReplaceAll must be false.
        var plan = await tools.PlanRename(
            filePath: CalculatorPath, line: 5, column: 31, newName: "x", ct: CancellationToken.None);

        await Assert.That(plan.Error).IsNull();
        var file = plan.Files.Single();
        await Assert.That(file.ReplaceAll).IsFalse();
        await Assert.That(file.EditCount).IsEqualTo(2);
        await Assert.That(file.Lines!.Count).IsEqualTo(1);
        await Assert.That(file.Lines[0].Before).IsEqualTo("public static int Add(int a, int b) => a + b;");
        await Assert.That(file.Lines[0].After).IsEqualTo("public static int Add(int x, int b) => x + b;");
    }

    [Test]
    public async Task PlanRename_Detail_ReturnsEditsInDescendingOffsetOrder()
    {
        var tools = await CreateToolsAsync();

        var plan = await tools.PlanRename(
            fullyQualifiedSymbolName: "SampleApp.Calculator.Add", newName: "Sum", detail: true, ct: CancellationToken.None);

        foreach (var file in plan.Files)
        {
            var offsets = file.Edits!.Select(e => e.StartOffset).ToList();
            await Assert.That(offsets.SequenceEqual(offsets.OrderByDescending(o => o))).IsTrue();
        }
    }

    [Test]
    public async Task PlanRename_FilePaths_LimitsLinesButNotThePlan()
    {
        var tools = await CreateToolsAsync();

        var plan = await tools.PlanRename(
            fullyQualifiedSymbolName: "SampleApp.Calculator.Add", newName: "Sum",
            filePaths: [ProgramPath], ct: CancellationToken.None);

        await Assert.That(plan.FileCount).IsEqualTo(2);
        await Assert.That(plan.Files.First(f => f.FilePath.EndsWith("Program.cs")).Lines).IsNotNull();
        await Assert.That(plan.Files.First(f => f.FilePath.EndsWith("Calculator.cs")).Lines).IsNull();
    }

    [Test]
    public async Task PlanRename_OverloadByFullyQualifiedName_UsesParameterTypes()
    {
        var tools = await CreateToolsAsync();

        var ambiguous = await tools.PlanRename(
            fullyQualifiedSymbolName: "SampleApp.OverloadExample.Calculate", newName: "Compute2", ct: CancellationToken.None);
        await Assert.That(ambiguous.Error).IsNotNull();
        await Assert.That(ambiguous.Candidates!.Count).IsEqualTo(2);

        var plan = await tools.PlanRename(
            fullyQualifiedSymbolName: "SampleApp.OverloadExample.Calculate", parameterTypes: "string",
            newName: "Compute2", ct: CancellationToken.None);
        await Assert.That(plan.Error).IsNull();
        await Assert.That(plan.Symbol).Contains("string");
    }

    // --- Parallel calls ---------------------------------------------------------------------------

    [Test]
    public async Task ParallelCalls_OnOneSession_AllSucceed()
    {
        // Codex and Claude Code run read-only MCP tools concurrently, and the server instructions
        // tell the agent to ask independent questions in one turn.
        var tools = await CreateToolsAsync();

        var calls = Enumerable.Range(0, 4).SelectMany(_ => new Func<Task<string?>>[]
        {
            async () => (await tools.FindReferences("SampleApp.Calculator.Add", ct: CancellationToken.None)).Error,
            async () => (await tools.GetOverrides("SampleApp.PricingService", "CalculateTotal", ct: CancellationToken.None)).Error,
            async () => (await tools.GetImplementations("SampleApp.IComputable", ct: CancellationToken.None)).Error,
            async () => (await tools.DescribeSymbol("SampleApp.Calculator", ct: CancellationToken.None)).Error,
            async () => (await tools.GetDiagnostics(filePaths: [CalculatorPath], includeAnalyzers: false, ct: CancellationToken.None)).Error,
        }).Select(f => Task.Run(f));

        var errors = await Task.WhenAll(calls);

        await Assert.That(errors.All(e => e is null)).IsTrue();
    }

    // --- Positional record properties (declared by a parameter, missed by the exact-name index) ----

    [Test]
    public async Task FindReferences_PositionalRecordProperty_ResolvesWithoutProjectName()
    {
        var tools = await CreateToolsAsync();

        var qualified = await tools.FindReferences("SampleApp.Tag.Key", ct: CancellationToken.None);
        await Assert.That(qualified.Error).IsNull();
        await Assert.That(qualified.Kind).IsEqualTo("Property");
        await Assert.That(qualified.Total).IsEqualTo(1);

        var simple = await tools.FindReferences("Key", ct: CancellationToken.None);
        await Assert.That(simple.Error).IsNull();
        await Assert.That(simple.Kind).IsEqualTo("Property");
    }

    [Test]
    public async Task FindReferences_PositionalRecordProperty_SimpleNameResolvesWithProjectName()
    {
        var tools = await CreateToolsAsync();

        var result = await tools.FindReferences("Key", projectName: "App", ct: CancellationToken.None);

        await Assert.That(result.Error).IsNull();
        await Assert.That(result.Kind).IsEqualTo("Property");
        await Assert.That(result.Total).IsEqualTo(1);
    }

    [Test]
    public async Task PlanRename_PositionalRecordStructProperty_ByName()
    {
        var tools = await CreateToolsAsync();

        var plan = await tools.PlanRename(
            fullyQualifiedSymbolName: "SampleApp.Point.X", newName: "Left", ct: CancellationToken.None);

        await Assert.That(plan.Error).IsNull();
        await Assert.That(plan.SymbolKind).IsEqualTo("Property");
        await Assert.That(plan.EditCount).IsGreaterThanOrEqualTo(1);
    }
}
