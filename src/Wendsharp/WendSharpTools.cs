using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Rename;
using ModelContextProtocol.Server;

namespace WendSharp;

[McpServerToolType]
public sealed class WendSharpTools(WorkspaceSession session)
{
    // =========================================================================
    // ExploreCode — compact blueprint of a file (signatures, no bodies).
    // =========================================================================
    [McpServerTool(Name = "ExploreCode")]
    [Description("Get a C# file's structure (namespaces, types, member signatures) with method bodies stripped, " +
                 "INSTEAD of reading the whole file. Use this first to map a large or unfamiliar file cheaply, " +
                 "then call DescribeSymbol or AnalyzeMember on the specific member you care about. " +
                 "On failure, Error is set and Blueprint is empty — check Error first.")]
    public async Task<ExploreResult> ExploreCode(
        [Description("Prefer an ABSOLUTE path (e.g. 'C:\\src\\App\\OrderService.cs') — it is unambiguous and always correct. " +
                     "A relative path with a directory component (e.g. 'Consumers/OrderConsumer.cs') is resolved against the solution and project directories, " +
                     "and a bare file name (e.g. 'OrderService.cs') matches by name, but both can be ambiguous when the same name exists in several places; " +
                     "use an absolute path whenever you have one.")] string fileNameOrPath,
        CancellationToken ct = default)
    {
        try
        {
            await session.InitializeAsync(ct);
            var doc = session.FindDocument(fileNameOrPath)
                      ?? throw new ArgumentException($"File not found in solution: {fileNameOrPath}");

            if (!doc.SupportsSyntaxTree)
                throw new ArgumentException($"File '{fileNameOrPath}' is not a C# source file.");

            var root = await doc.GetSyntaxRootAsync(ct)
                       ?? throw new InvalidOperationException("Document has no syntax root.");

            var stripped = new StripBodyRewriter().Visit(root)!;
            return new ExploreResult(doc.FilePath ?? fileNameOrPath, stripped.NormalizeWhitespace().ToFullString());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new ExploreResult("", "", ex.Message);
        }
    }

    // =========================================================================
    // FindReferences — semantic references across the whole solution.
    // =========================================================================
    [McpServerTool(Name = "FindReferences")]
    [Description("Find every SEMANTIC reference to a type or member across the solution — use this INSTEAD of grep/text search. " +
                 "Roslyn resolves the real symbol, so results include overrides and interface implementations and exclude " +
                 "false hits in comments, strings, and unrelated same-named identifiers. Use before any rename or call-site update. " +
                 "If the result is marked AMBIGUOUS, re-call with the fully-qualified metadata name. " +
                 "On an unexpected failure (e.g. unknown project), Kind is \"Error\" and Error is set — check Error first.")]
    public async Task<FindReferencesResult> FindReferences(
        [Description("Project name that declares or can see the symbol, e.g. 'App'.")] string projectName,
        [Description("Symbol name: a metadata type name (e.g. 'SampleApp.Calculator') or a simple member name (e.g. 'Add').")] string symbolName,
        CancellationToken ct = default)
    {
        try
        {
            await session.InitializeAsync(ct);
            var project = session.GetProject(projectName);
            var compilation = await project.GetCompilationAsync(ct)
                              ?? throw new InvalidOperationException($"No compilation for project '{projectName}'.");

            var sourceTreePaths = await session.GetSourceTreePathsAsync(project, ct);
            var symbol = await SymbolLookup.FindByNameAsync(compilation, project, symbolName, sourceTreePaths, ct);
            if (symbol is null)
                return new FindReferencesResult(symbolName, "NotFound", 0, []);

            var ambiguous = await SymbolLookup.FindAmbiguousAsync(compilation, project, symbolName, sourceTreePaths, ct);
            if (ambiguous is not null)
            {
                var candidates = string.Join("; ", ambiguous.Select(s => s.ToDisplayString()));
                return new FindReferencesResult(
                    $"{symbolName} (AMBIGUOUS — matched {ambiguous.Count} symbols: {candidates}. " +
                    "Use the full metadata name to disambiguate.)",
                    "Ambiguous", 0, []);
            }

            var hits = await CollectReferenceHitsAsync(symbol, ct);
            return new FindReferencesResult(symbol.ToDisplayString(), symbol.Kind.ToString(), hits.Count, [.. hits]);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new FindReferencesResult(symbolName, "Error", 0, [], ex.Message);
        }
    }

    // =========================================================================
    // GetImplementations — every type that implements an interface or derives from a class.
    // =========================================================================
    [McpServerTool(Name = "GetImplementations")]
    [Description("Find every concrete type in the solution that implements this interface or derives from this " +
                 "class — the real implementation surface, resolved via Roslyn's symbol graph, not by grepping " +
                 "for ': IFoo' (which misses multi-level inheritance, generic constraints, and types declared in " +
                 "other files or projects). Use this BEFORE changing an interface or abstract/base class: every " +
                 "result is a type whose members may need matching changes. Total: 0 means no implementers " +
                 "exist yet — if that's unexpected, confirm with DescribeSymbol that the type is actually an " +
                 "interface or has abstract/virtual members. " +
                 "On an unexpected failure, Kind is \"Error\" and Error is set — check Error first.")]
    public async Task<GetImplementationsResult> GetImplementations(
        [Description("Project name that declares the type, e.g. 'App'.")] string projectName,
        [Description("Interface or class name to inspect, e.g. 'IOrderRepository' or 'SampleApp.Calculator'.")] string typeName,
        CancellationToken ct = default)
    {
        try
        {
            await session.InitializeAsync(ct);
            var project = session.GetProject(projectName);
            var compilation = await session.GetCompilationAsync(projectName, ct);

            INamedTypeSymbol typeSymbol;
            try
            { typeSymbol = await ResolveTypeAsync(project, compilation, typeName, projectName, ct); }
            catch (ArgumentException) { return new GetImplementationsResult(typeName, "NotFound", 0, []); }

            var solution = project.Solution;
            var impls = await SymbolFinder.FindImplementationsAsync(typeSymbol, solution, transitive: true, cancellationToken: ct);

            var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            var hits = new List<ISymbol>();
            foreach (var impl in impls)
            {
                if (impl.Locations.Any(l => l.IsInSource) && seen.Add(impl))
                    hits.Add(impl);
            }

            var hitArray = await BuildSymbolHitsAsync(hits, ct);
            return new GetImplementationsResult(typeSymbol.ToFullSignature(), typeSymbol.TypeKind.ToString(), hitArray.Length, hitArray);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new GetImplementationsResult(typeName, "Error", 0, [], ex.Message);
        }
    }

    // =========================================================================
    // GetOverrides — every override of a virtual/abstract member.
    // =========================================================================
    [McpServerTool(Name = "GetOverrides")]
    [Description("Find every override of this virtual or abstract member, across the whole inheritance chain — " +
                 "not just direct subclasses — resolved via Roslyn's symbol graph. This is the other half of " +
                 "FindReferences: FindReferences finds call sites; GetOverrides finds alternate implementations " +
                 "of the same member. Use this BEFORE changing a virtual/abstract member's signature, return " +
                 "type, or contract: every result must be updated to match, or the build breaks with a much " +
                 "less helpful error later. Kind: \"NotOverridable\" means the member is not virtual/abstract/" +
                 "override and cannot have overrides — check the member name and AnalyzeMember if unexpected. " +
                 "On an unexpected failure, Kind is \"Error\" and Error is set — check Error first.")]
    public async Task<GetOverridesResult> GetOverrides(
        [Description("Project name that declares the type, e.g. 'App'.")] string projectName,
        [Description("Type that declares the virtual/abstract member, e.g. 'PricingService'.")] string typeName,
        [Description("Member name, e.g. 'CalculateTotal'.")] string memberName,
        CancellationToken ct = default)
    {
        try
        {
            await session.InitializeAsync(ct);
            var project = session.GetProject(projectName);
            var compilation = await session.GetCompilationAsync(projectName, ct);

            INamedTypeSymbol typeSymbol;
            try
            { typeSymbol = await ResolveTypeAsync(project, compilation, typeName, projectName, ct); }
            catch (ArgumentException) { return new GetOverridesResult($"{typeName}.{memberName}", "TypeNotFound", 0, []); }

            var candidates = SymbolLookup.FindMembers(typeSymbol, memberName);
            if (candidates.Count == 0)
                return new GetOverridesResult($"{typeName}.{memberName}", "MemberNotFound", 0, []);

            ISymbol memberSymbol;
            if (candidates.Count == 1)
            {
                memberSymbol = candidates[0];
            }
            else
            {
                var sigs = string.Join("; ", candidates.Select(c => c.ToFullSignature()));
                return new GetOverridesResult($"{typeName}.{memberName}", "Ambiguous", 0, [],
                    $"Member '{memberName}' in '{typeName}' has {candidates.Count} overloads: {sigs}. " +
                    "Use the containing type's fully-qualified name to disambiguate.");
            }

            if (!IsOverridable(memberSymbol))
                return new GetOverridesResult(memberSymbol.ToFullSignature(), "NotOverridable", 0, []);

            var solution = project.Solution;
            var overrides = await SymbolFinder.FindOverridesAsync(memberSymbol, solution, cancellationToken: ct);

            var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            var hits = new List<ISymbol>();
            foreach (var ov in overrides)
            {
                if (seen.Add(ov))
                    hits.Add(ov);
            }

            var hitArray = await BuildSymbolHitsAsync(hits, ct);
            var kind = OverridableKind(memberSymbol);
            return new GetOverridesResult(memberSymbol.ToFullSignature(), kind, hitArray.Length, hitArray);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new GetOverridesResult($"{typeName}.{memberName}", "Error", 0, [], ex.Message);
        }
    }

    // =========================================================================
    // DescribeSymbol — full public surface with resolved types (Task 1).
    // =========================================================================
    [McpServerTool(Name = "DescribeSymbol")]
    [Description("Get a type's full public/protected surface with compiler-resolved types (never `var`), base types, " +
                 "implemented interfaces, and the exact `using` directives needed to write code against it. " +
                 "Use this INSTEAD of reading the file when you need precise signatures — e.g. to author an interface, " +
                 "an adapter, or a DI registration for the type. " +
                 "On failure, Error is set and other fields are empty/default — check Error first.")]
    public async Task<SymbolDescription> DescribeSymbol(
        [Description("Project name, e.g. 'App'.")] string projectName,
        [Description("Type name: a metadata name (e.g. 'SampleApp.Calculator') or a simple name (e.g. 'Calculator').")] string typeName,
        CancellationToken ct = default)
    {
        try
        {
            await session.InitializeAsync(ct);
            var project = session.GetProject(projectName);
            var compilation = await project.GetCompilationAsync(ct)
                              ?? throw new InvalidOperationException($"No compilation for project '{projectName}'.");

            var symbol = await ResolveTypeAsync(project, compilation, typeName, projectName, ct);
            var members = new List<MemberSignature>();
            var usingSet = new HashSet<string>(StringComparer.Ordinal);
            var visitedTypes = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);

            IEnumerable<ISymbol> SurfaceMembers()
            {
                foreach (var m in symbol.GetMembers())
                {
                    if (m is INamedTypeSymbol { Name.Length: 0 } extContainer)
                    {
                        foreach (var inner in extContainer.GetMembers())
                            yield return inner;
                        continue;
                    }
                    yield return m;
                }
            }

            foreach (var m in SurfaceMembers())
            {
                if (m.IsImplicitlyDeclared && m.Kind is not SymbolKind.Property)
                    continue;

                if (m is IMethodSymbol { AssociatedSymbol: not null })
                    continue;

                if (m.Name == "EqualityContract")
                    continue;

                if (m.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Protected
                    or Accessibility.ProtectedOrInternal))
                    continue;

                var sig = m.ToFullSignature();
                var loc = m.Locations.FirstOrDefault();
                var (filePath, line) = ResolveLocation(loc);
                members.Add(new MemberSignature(m.Kind.ToString(), sig, filePath, line));

                CollectUsings(m, usingSet, visitedTypes);
            }

            var baseTypes = BuildBaseTypes(symbol);
            return new SymbolDescription(
                symbol.ToDisplayString(),
                [.. baseTypes],
                [.. usingSet.Order()],
                [.. members]);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new SymbolDescription("", [], [], [], ex.Message);
        }
    }

    // =========================================================================
    // AnalyzeMember — dependencies, data flow, callers (Task 2).
    // =========================================================================
    [McpServerTool(Name = "AnalyzeMember")]
    [Description("Get what a method actually touches — internal vs external dependencies, the variables it reads/writes " +
                 "(data flow), and its callers — computed by the compiler INSTEAD of traced by hand. Use before extracting " +
                 "or splitting a method: the dependencies tell you what must move or become a parameter, and the callers " +
                 "tell you what will break. " +
                 "On failure, Error is set and other fields are empty/default — check Error first.")]
    public async Task<MemberAnalysis> AnalyzeMember(
        [Description("Project name, e.g. 'App'.")] string projectName,
        [Description("Type name containing the member (metadata or simple name).")] string typeName,
        [Description("Member (method) name, e.g. 'Calculate'.")] string memberName,
        [Description("For an overloaded method, the comma-separated parameter types of the overload to analyze, " +
                     "exactly as listed in the ambiguity error (e.g. \"int, string\"); empty string for the parameterless overload.")] string? parameterTypes = null,
        CancellationToken ct = default)
    {
        try
        {
            await session.InitializeAsync(ct);
            var project = session.GetProject(projectName);
            var compilation = await project.GetCompilationAsync(ct)
                              ?? throw new InvalidOperationException($"No compilation for project '{projectName}'.");

            var typeSymbol = await ResolveTypeAsync(project, compilation, typeName, projectName, ct);


            var candidates = SymbolLookup.FindMembers(typeSymbol, memberName);
            if (candidates.Count == 0)
                throw new ArgumentException($"Member '{memberName}' not found in type '{typeName}'.");
            var memberSymbol = candidates.Count == 1
                ? candidates[0]
                : ResolveOverload(candidates, memberName, parameterTypes);

            var syntaxRef = memberSymbol.DeclaringSyntaxReferences.FirstOrDefault()
                ?? throw new ArgumentException($"Member '{memberName}' has no syntax reference.");
            var memberNode = await syntaxRef.GetSyntaxAsync(ct);
            var doc = project.GetDocument(memberNode.SyntaxTree)
                ?? throw new ArgumentException($"Cannot resolve document for member '{memberName}'.");
            var model = await doc.GetSemanticModelAsync(ct)
                ?? throw new InvalidOperationException("No semantic model available.");

            var fullSpan = memberNode.GetLocation().GetLineSpan();

            var internalDeps = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            var externalDeps = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

            foreach (var node in memberNode.DescendantNodes())
            {
                var info = model.GetSymbolInfo(node);
                var sym = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
                if (sym is null)
                    continue;

                if (SymbolEqualityComparer.Default.Equals(sym, memberSymbol))
                    continue;

                if (sym.Kind is not (SymbolKind.Field or SymbolKind.Property
                    or SymbolKind.Method or SymbolKind.Event or SymbolKind.NamedType))
                    continue;

                if (sym is IMethodSymbol { MethodKind: not MethodKind.Ordinary and not MethodKind.Constructor })
                    continue;

                if (sym.IsContainedBy(typeSymbol))
                    internalDeps.Add(sym);
                else
                    externalDeps.Add(sym);
            }

            var readVars = new HashSet<string>(StringComparer.Ordinal);
            var writtenVars = new HashSet<string>(StringComparer.Ordinal);
            foreach (var body in GetExecutableBodies(memberNode))
            {
                var flow = model.AnalyzeDataFlow(body);
                if (!flow.Succeeded)
                    continue;
                foreach (var v in flow.ReadInside)
                    readVars.Add(v.Name);
                foreach (var v in flow.WrittenInside)
                    writtenVars.Add(v.Name);
            }

            var callers = await CollectReferenceHitsAsync(memberSymbol, ct);

            var sourceStr = memberNode.ToString();
            return new MemberAnalysis(
                sourceStr,
                doc.FilePath ?? "",
                fullSpan.StartLinePosition.Line + 1,
                fullSpan.EndLinePosition.Line + 1,
                [.. internalDeps.Select(DistinctName).Order()],
                [.. externalDeps.Select(DistinctName).Order()],
                [.. readVars.Order()],
                [.. writtenVars.Order()],
                [.. callers]);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new MemberAnalysis("", "", 0, 0, [], [], [], [], [], ex.Message);
        }
    }

    // =========================================================================
    // PlanRename — compiler-verified rename plan WITHOUT applying it (Task 3 upgrade).
    // Returns apply-ready edits (file + 1-based line/col + absolute offset/length +
    // old/new text), conflicts detected via a diagnostics diff, and cascade targets
    // (overrides, interface members, partial declarations) that text search misses.
    // The renamed solution is computed locally and DISCARDED — the session Solution
    // is never mutated; WendSharp never writes to disk.
    // =========================================================================
    [McpServerTool(Name = "PlanRename")]
    [Description("Before renaming a symbol by hand, get a compiler-verified rename plan — WendSharp computes it but never writes. " +
                 "Returns apply-ready edits (file + 1-based line/col + absolute offset/length + old/new text), plus conflicts " +
                 "(name collisions, new compile errors) and cascades (overrides, interface members, partial declarations) that " +
                 "text search misses. If HasConflicts is true, do NOT apply — report the collision. Otherwise apply each edit by " +
                 "its absolute offset/length, then call Refresh and GetDiagnostics to confirm. " +
                 "If FileRenames is non-empty, rename those files on disk (OldPath to NewPath) in addition to applying Edits, " +
                 "before calling Refresh.")]
    public async Task<PlanRenameResult> PlanRename(
        [Description("Project name, e.g. 'App'.")] string projectName,
        [Description("Absolute path of the file containing the symbol (locator path A, with line/column). Prefer the absolute path — it is unambiguous; a relative path is resolved against the solution/project directories and a bare file name by name, but both can be ambiguous.")] string? filePath = null,
        [Description("1-based line number of the symbol (with filePath).")] int? line = null,
        [Description("1-based column number of the symbol (with filePath).")] int? column = null,
        [Description("Fully-qualified symbol name, e.g. 'SampleApp.Calculator.Add' (locator path B, alternative to file+position).")] string? fullyQualifiedSymbolName = null,
        [Description("The proposed new identifier.")] string newName = "",
        [Description("Rename all overloads of a method (maps to SymbolRenameOptions.RenameOverloads).")] bool renameOverloads = false,
        [Description("Rename plain text matches inside comments (textual, not semantic — review these).")] bool renameInComments = false,
        [Description("Rename plain text matches inside strings (textual, not semantic — review these).")] bool renameInStrings = false,
        [Description("Also rename the file when renaming a type.")] bool renameFile = false,
        CancellationToken ct = default)
    {
        var scope = new RenameScope(renameOverloads, renameInComments, renameInStrings, renameFile);
        try
        {
            await session.InitializeAsync(ct);
            var project = session.GetProject(projectName);
            var compilation = await project.GetCompilationAsync(ct)
                              ?? throw new InvalidOperationException($"No compilation for project '{projectName}'.");

            var resolution = await ResolveRenameTargetAsync(
                project, compilation, filePath, line, column, fullyQualifiedSymbolName, ct);

            if (resolution.Ambiguous.Count > 1)
            {
                var list = string.Join("; ", resolution.Ambiguous.Select(s => s.ToDisplayString()));
                var locator = fullyQualifiedSymbolName ?? filePath ?? "<no locator>";
                return ErrorResult(locator, "", "", newName, false,
                    $"Symbol name '{locator}' is ambiguous (matched {resolution.Ambiguous.Count} symbols: {list}). " +
                    "Re-call with the fully-qualified name or filePath+line+column to disambiguate.",
                    scope);
            }

            var symbol = resolution.Symbol;
            if (symbol is null)
            {
                var locator = fullyQualifiedSymbolName ?? filePath ?? "<no locator>";
                return ErrorResult(locator, "", "", newName, false,
                    $"Symbol not found for '{locator}' in project '{projectName}'. Use fullyQualifiedSymbolName or filePath+line+column.",
                    scope);
            }

            if (symbol.DeclaringSyntaxReferences.Length == 0)
            {
                return ErrorResult(symbol.ToDisplayString(), symbol.Kind.ToString(), symbol.Name, newName, true,
                    $"Symbol '{symbol.ToDisplayString()}' is defined outside the loaded source (metadata/external assembly) and cannot be renamed by WendSharp.",
                    scope);
            }

            var oldName = symbol.Name;

            var newNameValid = IsValidNewName(newName);
            if (!newNameValid)
            {
                return ErrorResult(symbol.ToDisplayString(), symbol.Kind.ToString(), oldName, newName, false,
                    $"'{newName}' is not a valid C# identifier or is a reserved keyword. Prefix with '@' to use a verbatim identifier.",
                    scope);
            }

            var effectiveNew = newName.StartsWith("@", StringComparison.Ordinal) ? newName[1..] : newName;
            if (effectiveNew == oldName)
            {
                return ErrorResult(symbol.ToDisplayString(), symbol.Kind.ToString(), oldName, newName, true,
                    $"New name '{newName}' equals the current name '{oldName}' — rename is a no-op.",
                    scope);
            }

            var options = new SymbolRenameOptions(
                RenameOverloads: renameOverloads,
                RenameInStrings: renameInStrings,
                RenameInComments: renameInComments,
                RenameFile: renameFile);
            var newSolution = await Renamer.RenameSymbolAsync(session.Solution, symbol, options, newName, ct);

            var edits = await ExtractEditsAsync(session.Solution, newSolution, oldName, ct);
            var conflicts = await DetectConflictsAsync(session.Solution, newSolution, ct);
            var cascades = BuildCascades(symbol, edits.DeclarationSymbols, renameOverloads);

            var sortedEdits = edits.Edits
                .OrderBy(e => e.FilePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.StartLine).ThenBy(e => e.StartColumn).ToList();

            var fileCount = 0;
            string? prevFile = null;
            foreach (var e in sortedEdits)
            {
                if (!string.Equals(prevFile, e.FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    fileCount++;
                    prevFile = e.FilePath;
                }
            }

            return new PlanRenameResult(
                symbol.ToDisplayString(),
                symbol.Kind.ToString(),
                oldName,
                newName,
                NewNameValid: true,
                conflicts.Any(c => c.Severity == "Error"),
                sortedEdits.Count,
                fileCount,
                scope,
                sortedEdits,
                conflicts,
                cascades,
                edits.FileRenames,
                Error: null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return ErrorResult("", "", "", newName, false, ex.Message, scope);
        }
    }

    // --- PlanRename helpers -----------------------------------------------------

    async Task<SymbolLookup.SymbolResolution> ResolveRenameTargetAsync(
        Project project, Compilation compilation,
        string? filePath, int? line, int? column, string? fullyQualifiedSymbolName, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(filePath) && line is int ln && column is int col)
        {
            var doc = session.FindDocument(filePath)
                      ?? project.Documents.FirstOrDefault(d => string.Equals(d.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
            if (doc is null)
                return new SymbolLookup.SymbolResolution(null, []);
            var sym = await SymbolLookup.FindAtPositionAsync(doc, ln, col, session.Solution.Workspace, ct);
            return new SymbolLookup.SymbolResolution(sym, []);
        }

        if (!string.IsNullOrWhiteSpace(fullyQualifiedSymbolName))
            return await SymbolLookup.ResolveAsync(
                compilation, project, fullyQualifiedSymbolName, preferKind: null,
                await session.GetSourceTreePathsAsync(project, ct), ct);
        return new SymbolLookup.SymbolResolution(null, []);
    }

    async Task<(List<RenameEdit> Edits, List<(ISymbol Symbol, int Position)> DeclarationSymbols, List<FileRename> FileRenames)> ExtractEditsAsync(
        Solution oldSolution, Solution newSolution, string oldName, CancellationToken ct)
    {
        var edits = new List<RenameEdit>();
        var declarationSymbols = new List<(ISymbol, int)>();
        var fileRenames = new List<FileRename>();

        foreach (var projectChange in newSolution.GetChanges(oldSolution).GetProjectChanges())
        {
            foreach (var docId in projectChange.GetChangedDocuments())
            {
                var oldDoc = oldSolution.GetDocument(docId)!;
                var newDoc = newSolution.GetDocument(docId)!;

                var filePath = oldDoc.FilePath ?? newDoc.FilePath ?? "";

                if (!string.IsNullOrEmpty(oldDoc.Name)
                    && !string.Equals(oldDoc.Name, newDoc.Name, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrEmpty(oldDoc.FilePath))
                {
                    var dir = Path.GetDirectoryName(oldDoc.FilePath);
                    var newPath = dir is not null && newDoc.Name.Length > 0
                        ? Path.Combine(dir, newDoc.Name)
                        : newDoc.Name;
                    fileRenames.Add(new FileRename(oldDoc.FilePath, newPath));
                }

                var oldText = await oldDoc.GetTextAsync(ct);
                var root = await oldDoc.GetSyntaxRootAsync(ct);
                var model = await oldDoc.GetSemanticModelAsync(ct);
                var textChanges = await newDoc.GetTextChangesAsync(oldDoc, ct);

                foreach (var tc in textChanges)
                {
                    var linePos = oldText.Lines.GetLinePositionSpan(tc.Span);
                    var location = ClassifyLocation(root, model, tc.Span.Start, oldName);
                    edits.Add(new RenameEdit(
                        filePath,
                        linePos.Start.Line + 1, linePos.Start.Character + 1,
                        linePos.End.Line + 1, linePos.End.Character + 1,   // 1-based, exclusive end
                        tc.Span.Start, tc.Span.Length,                      // absolute offset + length in ORIGINAL file
                        oldText.ToString(tc.Span),
                        tc.NewText ?? "",
                        location,
                        GetContainingMember(model, tc.Span.Start, ct)));

                    if (location == "Declaration" && root is not null && model is not null)
                    {
                        var token = root.FindToken(tc.Span.Start);
                        var declSym = token.Parent is not null ? model.GetDeclaredSymbol(token.Parent) : null;
                        if (declSym is not null)
                            declarationSymbols.Add((declSym, tc.Span.Start));
                    }
                }
            }
        }

        return (edits, declarationSymbols, fileRenames);
    }

    static string ClassifyLocation(SyntaxNode? root, SemanticModel? model, int position, string oldName)
    {
        if (root is null)
            return "Reference";

        var token = root.FindToken(position, findInsideTrivia: true);
        var parent = token.Parent;

        if (parent is not null && IsInsideNameof(parent))
            return "Nameof";

        if (parent is not null && IsInsideCref(parent))
            return "Cref";

        if (model is not null && parent is not null)
        {
            var declSym = model.GetDeclaredSymbol(parent);
            if (declSym is not null && declSym.Name == oldName)
                return "Declaration";
        }


        var trivia = root.FindTrivia(position);
        if (trivia.Kind() is SyntaxKind.SingleLineCommentTrivia or SyntaxKind.MultiLineCommentTrivia)
            return "Comment";

        if (token.Kind() is SyntaxKind.StringLiteralToken or SyntaxKind.InterpolatedStringTextToken)
            return "String";

        return "Reference";
    }

    static bool IsInsideNameof(SyntaxNode node) =>
        node.AncestorsAndSelf().OfType<InvocationExpressionSyntax>()
            .Any(i => i.Expression is IdentifierNameSyntax id && id.Identifier.ValueText == "nameof");

    static bool IsInsideCref(SyntaxNode node) =>
        node.AncestorsAndSelf().Any(n => n is XmlCrefAttributeSyntax or CrefSyntax);

    static string GetContainingMember(SemanticModel? model, int position, CancellationToken ct)
    {
        if (model is null)
            return "";
        var enclosing = model.GetEnclosingSymbol(position, ct);
        return enclosing is null ? "" : enclosing.ToDisplayString(ContainingMemberFormat);
    }

    static async Task<List<RenameConflict>> DetectConflictsAsync(
        Solution oldSolution, Solution newSolution, CancellationToken ct)
    {
        var conflicts = new List<RenameConflict>();

        foreach (var projectChange in newSolution.GetChanges(oldSolution).GetProjectChanges())
        {
            var oldComp = await projectChange.OldProject.GetCompilationAsync(ct);
            var newComp = await projectChange.NewProject.GetCompilationAsync(ct);
            if (oldComp is null || newComp is null)
                continue;

            var oldTrees = new List<SyntaxTree>();
            var newTrees = new List<SyntaxTree>();
            foreach (var docId in projectChange.GetChangedDocuments())
            {
                var oldDoc = oldSolution.GetDocument(docId);
                var newDoc = newSolution.GetDocument(docId);
                var ot = oldDoc is not null ? await oldDoc.GetSyntaxTreeAsync(ct) : null;
                var nt = newDoc is not null ? await newDoc.GetSyntaxTreeAsync(ct) : null;
                if (ot is not null)
                    oldTrees.Add(ot);
                if (nt is not null)
                    newTrees.Add(nt);
            }

            var oldErrors = CollectErrors(oldComp, oldTrees, ct);
            var newErrors = CollectErrors(newComp, newTrees, ct);

            var remaining = new Dictionary<(string Id, string Message), int>();
            foreach (var e in oldErrors)
                remaining[e.Key] = (remaining.TryGetValue(e.Key, out var c) ? c : 0) + 1;

            foreach (var e in newErrors)
            {
                if (remaining.TryGetValue(e.Key, out var left) && left > 0)
                    remaining[e.Key] = left - 1;
                else
                    conflicts.Add(new RenameConflict("Error", e.Id, e.Message, e.FilePath, e.Line, e.Column));
            }
        }

        return conflicts;
    }

    readonly record struct ErrorEntry(string Id, string Message, string FilePath, int Line, int Column)
    {
        public (string Id, string Message) Key => (Id, Message);
    }

    static List<ErrorEntry> CollectErrors(
        Compilation compilation, IReadOnlyCollection<SyntaxTree> changedTrees, CancellationToken ct)
    {
        var result = new List<ErrorEntry>();
        foreach (var tree in changedTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var d in model.GetDiagnostics(null, ct))
            {
                if (d.Severity != DiagnosticSeverity.Error)
                    continue;
                var span = d.Location.GetLineSpan();
                result.Add(new ErrorEntry(
                    d.Id, d.GetMessage(),
                    d.Location.SourceTree?.FilePath ?? "",
                    span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1));
            }
        }
        return result;
    }

    static List<CascadeTarget> BuildCascades(
        ISymbol primary, List<(ISymbol Symbol, int Position)> declarationSymbols, bool renameOverloads)
    {
        var cascades = new List<CascadeTarget>();
        var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

        var partialMethodKeys = new HashSet<string>(StringComparer.Ordinal);

        if (IsMultiDeclaration(primary))
            TryAdd(primary, "PartialDeclaration");

        foreach (var (sym, _) in declarationSymbols)
        {
            if (SymbolEqualityComparer.Default.Equals(sym, primary))
                continue;
            if (!seen.Add(sym))
                continue;
            var reason = ClassifyCascadeReason(sym, primary, renameOverloads);
            if (reason is not null)
                TryAdd(sym, reason);
        }

        return cascades;

        void TryAdd(ISymbol s, string reason)
        {
            if (reason == "PartialDeclaration" && s is IMethodSymbol)
            {
                var key = $"{s.ContainingType?.ToDisplayString()}|{s.Name}";
                if (!partialMethodKeys.Add(key))
                    return;
            }

            var loc = s.Locations.FirstOrDefault(l => l.IsInSource);
            var (fp, line) = ResolveLocation(loc);
            cascades.Add(new CascadeTarget(s.ToDisplayString(), s.Kind.ToString(), reason, fp, line));
        }
    }

    static string? ClassifyCascadeReason(ISymbol sym, ISymbol primary, bool renameOverloads)
    {
        if (sym is IMethodSymbol { ExplicitInterfaceImplementations.Length: > 0 })
            return "ExplicitInterfaceImpl";
        if (sym is IMethodSymbol { IsOverride: true })
            return "Override";
        if (sym.ContainingType?.TypeKind == TypeKind.Interface)
            return "InterfaceMember";
        if (ImplementsInterfaceMember(sym))
            return "InterfaceMember";
        if (IsMultiDeclaration(sym))
            return "PartialDeclaration";
        if (renameOverloads && IsOverloadOf(sym, primary))
            return "Overload";
        return null;
    }

    static bool IsMultiDeclaration(ISymbol sym)
    {
        if (sym is IMethodSymbol m && (m.PartialDefinitionPart is not null || m.PartialImplementationPart is not null))
            return true;
        return IsDeclaredAcrossMultipleFiles(sym);
    }

    static bool IsDeclaredAcrossMultipleFiles(ISymbol sym)
    {
        var count = 0;
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in sym.DeclaringSyntaxReferences)
        {
            var p = r.SyntaxTree?.FilePath;
            if (!string.IsNullOrEmpty(p) && files.Add(p))
                count++;
        }
        return count > 1;
    }

    static bool ImplementsInterfaceMember(ISymbol sym)
    {
        var type = sym.ContainingType;
        if (type is null || type.TypeKind == TypeKind.Interface)
            return false;

        foreach (var iface in type.AllInterfaces)
        {
            foreach (var im in iface.GetMembers(sym.Name))
            {
                if (SymbolEqualityComparer.Default.Equals(
                        sym, type.FindImplementationForInterfaceMember(im)))
                    return true;
            }
        }
        return false;
    }

    static bool IsOverloadOf(ISymbol sym, ISymbol primary)
    {
        if (sym.Kind != SymbolKind.Method || primary.Kind != SymbolKind.Method)
            return false;
        if (!SymbolEqualityComparer.Default.Equals(sym.ContainingType, primary.ContainingType))
            return false;
        return sym.Name == primary.Name;
    }

    static bool IsValidNewName(string newName)
    {
        if (string.IsNullOrEmpty(newName))
            return false;
        var verbatim = newName.StartsWith("@", StringComparison.Ordinal);
        var inner = verbatim ? newName[1..] : newName;
        if (inner.Length == 0 || !SyntaxFacts.IsValidIdentifier(inner))
            return false;

        return verbatim || !SyntaxFacts.IsReservedKeyword(SyntaxFacts.GetKeywordKind(inner));
    }

    static PlanRenameResult ErrorResult(
        string symbol, string symbolKind, string oldName, string newName, bool newNameValid,
        string error, RenameScope scope) =>
        new(symbol, symbolKind, oldName, newName, newNameValid,
            HasConflicts: false, EditCount: 0, FileCount: 0, scope,
            Array.Empty<RenameEdit>(), Array.Empty<RenameConflict>(), Array.Empty<CascadeTarget>(),
            Array.Empty<FileRename>(), error);

    static readonly SymbolDisplayFormat ContainingMemberFormat = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType,
        parameterOptions: SymbolDisplayParameterOptions.None,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    // =========================================================================
    // Refresh — re-read files from disk after the agent edits them.
    // =========================================================================
    [McpServerTool(Name = "Refresh")]
    [Description("Re-read files from disk into WendSharp's model after YOU edit them — WendSharp does NOT see your changes until " +
                 "you do this. Pass every changed, created, and deleted file (absolute paths). Always call this before " +
                 "GetDiagnostics or FindReferences after an edit, or those tools report stale results. " +
                 "On failure, Error is set and the path lists are empty — check Error first.")]
    public async Task<RefreshResult> Refresh(
        [Description("Absolute paths of files you changed, created, or deleted.")] string[] changedFilePaths,
        CancellationToken ct = default)
    {
        try
        {
            await session.InitializeAsync(ct);
            return await session.RefreshAsync(changedFilePaths, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new RefreshResult([], [], [], [], ex.Message);
        }
    }

    // =========================================================================
    // GetDiagnostics — compiler errors/warnings.
    // =========================================================================
    [McpServerTool(Name = "GetDiagnostics")]
    [Description("Report Roslyn compiler errors (and optionally warnings) for a project's current in-memory state — the truth " +
                 "check after you edit. WendSharp only sees edits you have Refreshed, so call Refresh first or the results are stale. " +
                 "Pass filePaths to limit results to specific files (e.g. the ones you just edited); omit for the whole project. " +
                 "On failure, Error is set and Diagnostics is empty — check Error first.")]
    public async Task<DiagnosticsResult> GetDiagnostics(
        [Description("Project name, e.g. 'App'.")] string projectName,
        [Description("Include warnings in addition to errors.")] bool includeWarnings = false,
        [Description("Limit diagnostics to these absolute file paths (e.g. the files you just edited). Omit for the whole project.")] string[]? filePaths = null,
        CancellationToken ct = default)
    {
        try
        {
            await session.InitializeAsync(ct);
            var project = session.GetProject(projectName);
            var compilation = await project.GetCompilationAsync(ct)
                              ?? throw new InvalidOperationException($"No compilation for project '{projectName}'.");

            HashSet<string>? filter = filePaths is { Length: > 0 } pathArray
                ? new HashSet<string>(pathArray, StringComparer.OrdinalIgnoreCase)
                : null;

            var compilerDiags = compilation.GetDiagnostics(ct);
            var results = new List<DiagnosticDto>(compilerDiags.Length);
            var errorCount = 0;

            void Append(Diagnostic d)
            {
                if (filter is not null)
                {
                    var p = d.Location.SourceTree?.FilePath;
                    if (p is null || !filter.Contains(p))
                        return;
                }
                if (d.Severity == DiagnosticSeverity.Error)
                    errorCount++;
                var span = d.Location.GetLineSpan();
                results.Add(new DiagnosticDto(
                    d.Id,
                    d.GetMessage(),
                    d.Location.SourceTree?.FilePath ?? "",
                    span.StartLinePosition.Line + 1,
                    span.StartLinePosition.Character + 1,
                    d.Severity.ToString()));
            }

            foreach (var d in compilerDiags)
            {
                if (d.Severity == DiagnosticSeverity.Error
                    || (includeWarnings && d.Severity == DiagnosticSeverity.Warning))
                    Append(d);
            }

            return new DiagnosticsResult(projectName, errorCount, [.. results]);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new DiagnosticsResult(projectName, 0, [], ex.Message);
        }
    }

    // =========================================================================
    // GetWorkspaceInfo — what WendSharp resolved, and the editor's open roots.
    // =========================================================================
    [McpServerTool(Name = "GetWorkspaceInfo")]
    [Description("Report which solution WendSharp resolved and loaded, and from where: the solution file, its root " +
                 "directory, and the loaded project names. When the client exposes workspace roots (MCP roots), also " +
                 "lists every open root folder — use this to see all loaded projects when several are open. " +
                 "ClientExposesRoots is false for clients that cannot report roots at all (e.g. Zed today); in that " +
                 "case ClientRoots is empty and RootDirectory is the single resolved root. " +
                 "On failure, Error is set — check Error first.")]
    public async Task<WorkspaceInfoResult> GetWorkspaceInfo(McpServer server, CancellationToken ct = default)
    {
        try
        {
            await session.InitializeAsync(server, ct);

            var solutionPath = session.LoadedSolutionPath ?? "";
            var rootDir = solutionPath.Length > 0 ? Path.GetDirectoryName(solutionPath) ?? "" : "";
            var projects = session.Solution.Projects
                .Select(p => p.Name)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var clientRoots = await session.GetClientRootsAsync(server, ct);
            var exposesRoots = server.ClientCapabilities?.Roots is not null;

            return new WorkspaceInfoResult(solutionPath, rootDir, projects, [.. clientRoots], exposesRoots);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new WorkspaceInfoResult("", "", [], [], false, ex.Message);
        }
    }

    // =========================================================================
    // Shared helpers
    // =========================================================================

    async Task<List<ReferenceHit>> CollectReferenceHitsAsync(ISymbol symbol, CancellationToken ct)
    {
        var found = await SymbolFinder.FindReferencesAsync(symbol, session.Solution, ct);

        var byDoc = found
            .SelectMany(r => r.Locations)
            .Where(l => l.Location.SourceTree is not null)
            .GroupBy(l => l.Document.Id);

        var hits = new List<ReferenceHit>();
        foreach (var group in byDoc)
        {
            var doc = group.First().Document;
            var text = await doc.GetTextAsync(ct);
            var root = await doc.GetSyntaxRootAsync(ct);

            foreach (var loc in group)
            {
                var span = loc.Location.GetLineSpan();
                var lineIdx = span.StartLinePosition.Line;
                var preview = lineIdx < text.Lines.Count
                    ? text.Lines[lineIdx].ToString().Trim()
                    : "";
                var context = ContextWalker.Describe(
                    root?.FindToken(loc.Location.SourceSpan.Start).Parent);

                hits.Add(new ReferenceHit(
                    loc.Document.FilePath ?? "",
                    span.StartLinePosition.Line + 1,
                    span.StartLinePosition.Character + 1,
                    preview,
                    context));
            }
        }

        return hits;
    }

    static (string FilePath, int Line) ResolveLocation(Location? loc)
    {
        if (loc is null || loc.SourceTree is null)
            return ("", 0);
        var span = loc.GetLineSpan();
        return (loc.SourceTree.FilePath ?? "", span.StartLinePosition.Line + 1);
    }

    static List<string> BuildBaseTypes(INamedTypeSymbol type)
    {
        var result = new List<string>();
        if (type.BaseType is not null && type.BaseType.SpecialType != SpecialType.System_Object)
            result.Add(type.BaseType.ToDisplayString());
        foreach (var iface in type.AllInterfaces)
            result.Add(iface.ToDisplayString());
        return result;
    }

    async Task<INamedTypeSymbol> ResolveTypeAsync(
        Project project, Compilation compilation, string typeName, string projectName, CancellationToken ct)
    {
        var r = await SymbolLookup.ResolveAsync(
            compilation, project, typeName, SymbolKind.NamedType, await session.GetSourceTreePathsAsync(project, ct), ct);
        if (r.Ambiguous.Count > 1)
        {
            var list = string.Join("; ", r.Ambiguous.Select(s => s.ToDisplayString()));
            throw new ArgumentException(
                $"Type name '{typeName}' is ambiguous (matched {r.Ambiguous.Count} types: {list}). " +
                "Re-call with the fully-qualified metadata name (e.g. 'Namespace.Type') to disambiguate.");
        }
        if (r.Symbol is INamedTypeSymbol named)
            return named;
        if (r.Ambiguous.Count == 1)
            throw new ArgumentException(
                $"Type '{typeName}' not found in project '{projectName}' " +
                $"(found a non-type symbol of that name: {r.Ambiguous[0].ToDisplayString()}). Pass a type name.");
        throw new ArgumentException($"Type '{typeName}' not found in project '{projectName}'.");
    }

    static ISymbol ResolveOverload(IReadOnlyList<ISymbol> candidates, string memberName, string? parameterTypes)
    {
        var options = string.Join(" | ", candidates.Select(c => $"\"{FormatParameterTypes(c)}\""));
        if (parameterTypes is null)
            throw new ArgumentException(
                $"Member '{memberName}' is overloaded ({candidates.Count}). " +
                $"Re-call with parameterTypes set to one of: {options}.");
        var wanted = parameterTypes.Trim();
        var match = candidates.FirstOrDefault(c => FormatParameterTypes(c) == wanted);
        if (match is null)
            throw new ArgumentException(
                $"Member '{memberName}' has no overload with parameter types '{parameterTypes}'. " +
                $"Re-call with parameterTypes set to one of: {options}.");
        return match;
    }

    static string FormatParameterTypes(ISymbol symbol) =>
        symbol is IMethodSymbol m
            ? string.Join(", ", m.Parameters.Select(p => p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)))
            : "";

    static void CollectUsings(ISymbol member, HashSet<string> usingSet, HashSet<ITypeSymbol> visited)
    {
        void AddType(ITypeSymbol? t)
        {
            if (t is null || !visited.Add(t))
                return;

            switch (t)
            {
                case ITypeParameterSymbol or IErrorTypeSymbol or IDynamicTypeSymbol:
                    return;                                   // no namespace to import
                case IArrayTypeSymbol arr:
                    AddType(arr.ElementType);
                    return;
                case IPointerTypeSymbol ptr:
                    AddType(ptr.PointedAtType);
                    return;
            }

            if (t.ContainingNamespace is { IsGlobalNamespace: false } ns)
                usingSet.Add(ns.ToDisplayString());

            if (t is INamedTypeSymbol { IsGenericType: true } named)
                foreach (var arg in named.TypeArguments)
                    AddType(arg);                             // covers Task<Customer>, Dictionary<K,V>, Nullable<T>, tuples
        }

        switch (member)
        {
            case IMethodSymbol method:
                AddType(method.ReturnType);
                foreach (var p in method.Parameters)
                    AddType(p.Type);
                break;
            case IPropertySymbol prop:
                AddType(prop.Type);
                break;
            case IFieldSymbol field:
                AddType(field.Type);
                break;
            case IEventSymbol ev:
                AddType(ev.Type);
                break;
        }
    }

    static IEnumerable<SyntaxNode> GetExecutableBodies(SyntaxNode memberNode)
    {
        switch (memberNode)
        {
            case MethodDeclarationSyntax m:
                if (m.Body is { } mb)
                    yield return mb;
                else if (m.ExpressionBody?.Expression is { } me)
                    yield return me;
                break;
            case ConstructorDeclarationSyntax c:
                if (c.Body is { } cb)
                    yield return cb;
                else if (c.ExpressionBody?.Expression is { } ce)
                    yield return ce;
                break;
            case PropertyDeclarationSyntax p:
                if (p.ExpressionBody?.Expression is { } pe)
                    yield return pe;
                else if (p.AccessorList is { } al)
                {
                    foreach (var accessor in al.Accessors)
                    {
                        if (accessor.Body is { } ab)
                            yield return ab;
                        else if (accessor.ExpressionBody?.Expression is { } ax)
                            yield return ax;
                    }
                }
                break;
            case EventDeclarationSyntax e:
                if (e.AccessorList is { } eal)
                {
                    foreach (var accessor in eal.Accessors)
                    {
                        if (accessor.Body is { } eab)
                            yield return eab;
                        else if (accessor.ExpressionBody?.Expression is { } eax)
                            yield return eax;
                    }
                }
                break;
        }
    }

    static readonly SymbolDisplayFormat ShortDepFormat = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType,
        parameterOptions: SymbolDisplayParameterOptions.None,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
    );

    static string DistinctName(ISymbol s) => s.ToDisplayString(ShortDepFormat);

    static async Task<SymbolHit[]> BuildSymbolHitsAsync(IEnumerable<ISymbol> symbols, CancellationToken ct)
    {
        var result = new List<(SymbolHit Hit, string FilePath, int Line, int Column)>();
        foreach (var symbol in symbols)
        {
            var loc = symbol.Locations.FirstOrDefault(l => l.IsInSource);
            if (loc is null)
                continue;

            var span = loc.GetLineSpan();
            var filePath = loc.SourceTree?.FilePath ?? "";
            var line = span.StartLinePosition.Line + 1;
            var column = span.StartLinePosition.Character + 1;

            string context;
            if (symbol.DeclaringSyntaxReferences is { Length: > 0 } refs)
            {
                var node = await refs[0].GetSyntaxAsync(ct);
                context = ContextWalker.Describe(node);
            }
            else
            {
                context = symbol.ContainingType?.ToDisplayString() ?? "";
            }

            result.Add((new SymbolHit(symbol.ToFullSignature(), filePath, line, column, context), filePath, line, column));
        }

        return result
            .OrderBy(x => x.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Line)
            .ThenBy(x => x.Column)
            .Select(x => x.Hit)
            .ToArray();
    }

    static bool IsOverridable(ISymbol symbol) => symbol switch
    {
        IMethodSymbol m => m.IsVirtual || m.IsAbstract || m.IsOverride,
        IPropertySymbol p => p.IsVirtual || p.IsAbstract || p.IsOverride,
        IEventSymbol e => e.IsVirtual || e.IsAbstract || e.IsOverride,
        _ => false
    };

    static string OverridableKind(ISymbol symbol) => symbol switch
    {
        IMethodSymbol { IsAbstract: true } => "Abstract",
        IMethodSymbol { IsOverride: true } => "Override",
        IPropertySymbol { IsAbstract: true } => "Abstract",
        IPropertySymbol { IsOverride: true } => "Override",
        IEventSymbol { IsAbstract: true } => "Abstract",
        IEventSymbol { IsOverride: true } => "Override",
        _ => "Virtual"
    };
}
