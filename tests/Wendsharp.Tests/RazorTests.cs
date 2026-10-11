using Microsoft.Extensions.Logging.Abstractions;

namespace WendSharp.Tests;

/// <summary>C# used from Razor markup compiles through a source generator into documents that do not
/// exist on disk. Every tool must report and edit the .razor file instead, and never claim success
/// for a plan or a check that left the markup out. Fixture: sample/RazorSample.slnx (restore or
/// build it once, like sample/Sample.slnx).</summary>
[NotInParallel("RazorSample")]
public class RazorTests
{
    static readonly string RepoRoot = FindRepoRoot();
    static readonly string SolutionPath = Path.Combine(RepoRoot, "sample", "RazorSample.slnx");
    static readonly string AppPath = Path.Combine(RepoRoot, "sample", "RazorApp");
    static readonly string CardPath = Path.Combine(AppPath, "PersonCard.razor");
    static readonly string ViewModelPath = Path.Combine(AppPath, "PersonViewModel.cs");

    static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "sample", "RazorSample.slnx")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException("Could not locate repo root with sample/RazorSample.slnx");
    }

    static async Task<WendSharpTools> CreateToolsAsync()
    {
        var session = new WorkspaceSession(NullLogger<WorkspaceSession>.Instance, SolutionPath);
        await session.InitializeAsync(CancellationToken.None);
        return new WendSharpTools(session);
    }

    static bool IsGeneratedPath(string path) =>
        path.Contains("razor.g.cs", StringComparison.OrdinalIgnoreCase)
        || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

    [Test]
    public async Task FindReferences_UsesInMarkup_AreReportedInTheRazorFile()
    {
        var tools = await CreateToolsAsync();

        var result = await tools.FindReferences("RazorApp.PersonViewModel.DisplayName", ct: CancellationToken.None);

        await Assert.That(result.Error).IsNull();
        await Assert.That(result.Files.Length).IsEqualTo(1);
        await Assert.That(result.Files[0].FilePath).IsEqualTo(CardPath);
        // Line 3 (@bind) also produces generated getter/setter code with no source line; those
        // duplicates must not show up as extra hits.
        await Assert.That(result.Files[0].Hits.Select(h => h.Line).SequenceEqual([2, 3, 13])).IsTrue();
        await Assert.That(result.Files[0].Hits[0].Preview).IsEqualTo("<h3>@Model.DisplayName</h3>");
        await Assert.That(result.Total).IsEqualTo(3);
    }

    [Test]
    public async Task PlanRename_PropertyUsedInMarkup_PlansTheMarkupEdits()
    {
        var tools = await CreateToolsAsync();

        var plan = await tools.PlanRename(
            fullyQualifiedSymbolName: "RazorApp.PersonViewModel.DisplayName", newName: "FullName", ct: CancellationToken.None);

        await Assert.That(plan.Error).IsNull();
        await Assert.That(plan.HasConflicts).IsFalse();
        await Assert.That(plan.FileCount).IsEqualTo(2);
        var card = plan.Files.Single(f => f.FilePath == CardPath);
        await Assert.That(card.EditCount).IsEqualTo(3);
        await Assert.That(card.ReplaceAll).IsTrue();
        await Assert.That(card.Lines!.Any(l => l.After == "<h3>@Model.FullName</h3>")).IsTrue();
        await Assert.That(card.Lines!.Any(l => l.After == "<input @bind=\"Model.FullName\" />")).IsTrue();
        await Assert.That(plan.Files.Any(f => IsGeneratedPath(f.FilePath))).IsFalse();
    }

    [Test]
    public async Task PlanRename_SymbolDeclaredInRazorCode_IsPlannedInTheMarkup()
    {
        var tools = await CreateToolsAsync();

        // Roslyn's Renamer returns no change at all for a symbol declared in a .razor @code block.
        var plan = await tools.PlanRename(
            fullyQualifiedSymbolName: "RazorApp.PersonCard.Caption", newName: "Title", ct: CancellationToken.None);

        await Assert.That(plan.Error).IsNull();
        await Assert.That(plan.HasConflicts).IsFalse();
        await Assert.That(plan.EditCount).IsEqualTo(2);
        var card = plan.Files.Single();
        await Assert.That(card.FilePath).IsEqualTo(CardPath);
        await Assert.That(card.Lines!.Select(l => l.Line).SequenceEqual([13, 6])).IsTrue();
        await Assert.That(card.Lines![0].Location).IsEqualTo("Declaration");
    }

    [Test]
    public async Task PlanRename_PositionInRazorFile_SaysToUseTheName()
    {
        var tools = await CreateToolsAsync();

        var plan = await tools.PlanRename(filePath: CardPath, line: 2, column: 20, newName: "FullName", ct: CancellationToken.None);

        await Assert.That(plan.Error!).Contains("fullyQualifiedSymbolName");
        await Assert.That(plan.EditCount).IsEqualTo(0);
    }

    [Test]
    public async Task PlanRename_IntoAnExistingName_ReportsTheConflictFromTheRazorCode()
    {
        var tools = await CreateToolsAsync();

        // Renaming IsActive to DisplayName collides in PersonViewModel.cs (CS0102), and the markup
        // then reads a bool as a string; at least the declaration conflict must surface.
        var plan = await tools.PlanRename(
            fullyQualifiedSymbolName: "RazorApp.PersonViewModel.IsActive", newName: "DisplayName", ct: CancellationToken.None);

        await Assert.That(plan.HasConflicts).IsTrue();
        await Assert.That(plan.Conflicts.Any(c => IsGeneratedPath(c.FilePath))).IsFalse();
    }

    [Test]
    public async Task GetDiagnostics_ErrorsInCodeGeneratedFromMarkup_AreReportedOnTheRazorFile()
    {
        var original = await File.ReadAllBytesAsync(ViewModelPath, CancellationToken.None);
        try
        {
            var tools = await CreateToolsAsync();
            // The old failure: only the C# declaration renamed, the markup left behind.
            var renamed = (await File.ReadAllTextAsync(ViewModelPath, CancellationToken.None))
                .Replace("DisplayName", "FullName", StringComparison.Ordinal);
            await File.WriteAllTextAsync(ViewModelPath, renamed, CancellationToken.None);

            var result = await tools.GetDiagnostics(
                filePaths: [CardPath], includeAnalyzers: false, ct: CancellationToken.None);

            await Assert.That(result.Error).IsNull();
            await Assert.That(result.ErrorCount).IsGreaterThanOrEqualTo(3);
            await Assert.That(result.Diagnostics.All(d => d.FilePath == CardPath)).IsTrue();
            var lines = result.Diagnostics.Where(d => d.Line > 0).Select(d => d.Line).Distinct().Order().ToList();
            await Assert.That(lines.SequenceEqual([2, 3, 13])).IsTrue();
        }
        finally
        {
            await File.WriteAllBytesAsync(ViewModelPath, original, CancellationToken.None);
        }
    }

    [Test]
    public async Task EditToRazorFile_IsSeenWithoutRefresh()
    {
        var original = await File.ReadAllBytesAsync(CardPath, CancellationToken.None);
        try
        {
            var tools = await CreateToolsAsync();
            var before = await tools.GetDiagnostics(filePaths: [CardPath], includeAnalyzers: false, ct: CancellationToken.None);
            await Assert.That(before.ErrorCount).IsEqualTo(0);

            var broken = (await File.ReadAllTextAsync(CardPath, CancellationToken.None))
                .Replace("<h3>@Model.DisplayName</h3>", "<h3>@Model.Missing</h3>", StringComparison.Ordinal);
            await File.WriteAllTextAsync(CardPath, broken, CancellationToken.None);

            var after = await tools.GetDiagnostics(filePaths: [CardPath], includeAnalyzers: false, ct: CancellationToken.None);
            await Assert.That(after.ErrorCount).IsGreaterThan(0);
            await Assert.That(after.Diagnostics.Any(d => d.Line == 2 && d.Code == "CS1061")).IsTrue();
        }
        finally
        {
            await File.WriteAllBytesAsync(CardPath, original, CancellationToken.None);
        }
    }

    [Test]
    public async Task Refresh_NewRazorFile_IsCompiledAsAComponent()
    {
        var path = Path.Combine(AppPath, "ExtraCard.razor");
        try
        {
            var tools = await CreateToolsAsync();
            await File.WriteAllTextAsync(path,
                "<p>@Model.DisplayName</p>\n\n@code {\n    [Parameter] public PersonViewModel Model { get; set; } = new();\n}\n",
                CancellationToken.None);

            var refresh = await tools.Refresh([path], CancellationToken.None);
            await Assert.That(refresh.Added).Contains(path);

            var desc = await tools.DescribeSymbol("RazorApp.ExtraCard", ct: CancellationToken.None);
            await Assert.That(desc.Error).IsNull();
            await Assert.That(desc.FilePath).IsEqualTo(path);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public async Task ExploreCode_RazorFile_ReturnsTheComponentOutline()
    {
        var tools = await CreateToolsAsync();

        var result = await tools.ExploreCode(CardPath, CancellationToken.None);

        await Assert.That(result.Error).IsNull();
        await Assert.That(result.Blueprint).Contains("class PersonCard");
        await Assert.That(result.Blueprint).Contains("Caption");
        await Assert.That(result.Blueprint).Contains("PersonViewModel Model");
        await Assert.That(result.Blueprint).DoesNotContain("#line");
        await Assert.That(result.Blueprint).DoesNotContain("#pragma");
    }

    [Test]
    public async Task DescribeSymbol_ComponentBySimpleName_PointsAtTheRazorFile()
    {
        var tools = await CreateToolsAsync();

        var desc = await tools.DescribeSymbol("PersonCard", ct: CancellationToken.None);

        await Assert.That(desc.Error).IsNull();
        await Assert.That(desc.FullName).IsEqualTo("RazorApp.PersonCard");
        await Assert.That(desc.FilePath).IsEqualTo(CardPath);
        await Assert.That(desc.Members.Any(m => m.FilePath is { } p && IsGeneratedPath(p))).IsFalse();
    }

    [Test]
    public async Task AnalyzeMember_MemberDeclaredInRazorCode_ReportsTheRazorFile()
    {
        var tools = await CreateToolsAsync();

        var analysis = await tools.AnalyzeMember("RazorApp.PersonCard", "Caption", ct: CancellationToken.None);

        await Assert.That(analysis.Error).IsNull();
        await Assert.That(analysis.FilePath).IsEqualTo(CardPath);
        await Assert.That(analysis.LineStart).IsEqualTo(13);
        await Assert.That(analysis.ExternalDependencies.Any(d => d.Contains("DisplayName"))).IsTrue();
    }
}
