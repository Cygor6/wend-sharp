using System.Collections.Immutable;
using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol.Server;

namespace WendSharp;

// Every tool declares the read-only invariant in its annotations; without them clients fall back to
// the MCP defaults, which are destructiveHint/openWorldHint true. destructiveHint and idempotentHint
// are redundant under readOnlyHint per spec, but set anyway so a client that reads them without
// checking readOnlyHint first does not get the permissive default. Refresh counts as read-only: it
// updates only this server's in-memory Solution, never disk.
//
// Response size: Codex cuts any tool result above roughly 10k tokens (about 40 KB) out of the MIDDLE,
// and Claude Code moves text results above 50,000 characters to a file. Every list is therefore
// paged (limit/offset, Truncated, NextOffset) and PlanRename bounds its line output, so a result
// stays well under both limits instead of being cut where the agent cannot see it.
[McpServerToolType]
public sealed class WendSharpTools(WorkspaceSession session)
{
    const int MaxLimit = 1000;
    const int MaxBlueprintChars = 30_000;
    const int MaxPlanChars = 28_000;
    const int MaxPreviewChars = 160;

    // =========================================================================
    // ExploreCode - compact blueprint of a file (signatures, no bodies).
    // =========================================================================
    [McpServerTool(Name = "ExploreCode", Title = "Explore code structure",
                   ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
                   UseStructuredContent = true)]
    [Description("Get a C# file's outline (namespaces, types, member signatures) with bodies stripped, INSTEAD of reading " +
                 "the whole file. Use it first on a large or unfamiliar file, then call DescribeSymbol or AnalyzeMember on the " +
                 "part you care about. Truncated: true means the outline was cut at about 30,000 characters; use DescribeSymbol " +
                 "on the types you still need. On failure Error is set and Blueprint is empty: check Error first.")]
    public async Task<ExploreResult> ExploreCode(
        [Description("Prefer an ABSOLUTE path (e.g. 'C:\\src\\App\\OrderService.cs'). A relative path with a folder " +
                     "(e.g. 'Consumers/OrderConsumer.cs') is resolved against the solution and project folders, and a bare " +
                     "file name (e.g. 'OrderService.cs') matches by name; both can be ambiguous, and the error then lists the matches.")] string fileNameOrPath,
        CancellationToken ct = default)
    {
        try
        {
            await session.InitializeAsync(ct);
            var doc = session.FindDocument(fileNameOrPath)
                      ?? throw new ArgumentException(
                          $"File not found in solution: {fileNameOrPath}. Pass an absolute path; if you just created the file, call Refresh with it first.");

            if (!doc.SupportsSyntaxTree)
                throw new ArgumentException($"File '{fileNameOrPath}' is not a C# source file.");

            var root = await doc.GetSyntaxRootAsync(ct)
                       ?? throw new InvalidOperationException("Document has no syntax root.");

            var stripped = new StripBodyRewriter().Visit(root)!;
            var blueprint = stripped.NormalizeWhitespace().ToFullString();
            var truncated = false;
            if (blueprint.Length > MaxBlueprintChars)
            {
                var cut = blueprint.LastIndexOf('\n', MaxBlueprintChars);
                blueprint = blueprint[..(cut > 0 ? cut : MaxBlueprintChars)];
                truncated = true;
            }
            return new ExploreResult(doc.FilePath ?? fileNameOrPath, blueprint, truncated);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new ExploreResult("", "", Error: ex.Message);
        }
    }

    // =========================================================================
    // FindReferences - semantic references across the whole solution.
    // =========================================================================
    [McpServerTool(Name = "FindReferences", Title = "Find references",
                   ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
                   UseStructuredContent = true)]
    [Description("Find every SEMANTIC reference to a type or member across the solution. Use this INSTEAD of grep/text search: " +
                 "Roslyn resolves the real symbol, so results include references through overrides and interface implementations " +
                 "and exclude comments, strings, and unrelated identifiers with the same name. Use before changing a signature or " +
                 "updating call sites. Files[] groups the hits by file. Results are paged: Total is the full count, limit defaults " +
                 "to 100, and Truncated: true means re-call with offset = NextOffset. Kind \"Ambiguous\" or \"NotFound\": Error " +
                 "explains and Candidates lists the symbols to choose from; re-call with a candidate's Name (and its ParameterTypes " +
                 "for a method). Check Error first.")]
    public async Task<FindReferencesResult> FindReferences(
        [Description("The symbol: a metadata name (e.g. 'SampleApp.Calculator' or 'SampleApp.Calculator.Add') or a simple name (e.g. 'Add').")] string symbolName,
        [Description("Only for an overloaded method: the overload's comma-separated parameter types, e.g. \"int, string\", as " +
                     "listed in Candidates[].ParameterTypes; empty string for the parameterless overload.")] string? parameterTypes = null,
        [Description("Optional project to resolve the name in, e.g. 'App'. Omit it and WendSharp finds the declaring project.")] string? projectName = null,
        [Description("Maximum number of references to return (default 100).")] int limit = 100,
        [Description("Number of references to skip, for paging (default 0). Pass NextOffset from the previous call.")] int offset = 0,
        [Description("Also return each reference's enclosing member as Context (default false).")] bool detail = false,
        CancellationToken ct = default)
    {
        try
        {
            await session.InitializeAsync(ct);
            var (_, symbol) = await ResolveSymbolAsync(projectName, symbolName, typesOnly: false, parameterTypes, ct);

            var hits = await CollectReferenceHitsAsync(symbol, detail, ct);
            var (page, truncated, next) = Page(hits, offset, limit);
            return new FindReferencesResult(
                symbol.ToDisplayString(), symbol.Kind.ToString(), hits.Count, GroupByFile(page), truncated, next);
        }
        catch (OperationCanceledException) { throw; }
        catch (AmbiguousSymbolException ex)
        {
            return new FindReferencesResult(symbolName, "Ambiguous", 0, [], Candidates: [.. ex.Candidates], Error: ex.Message);
        }
        catch (SymbolNotFoundException ex)
        {
            return new FindReferencesResult(symbolName, "NotFound", 0, [], Candidates: OrNull(ex.Suggestions), Error: ex.Message);
        }
        catch (Exception ex)
        {
            return new FindReferencesResult(symbolName, "Error", 0, [], Error: ex.Message);
        }
    }

    // =========================================================================
    // GetImplementations - every type that implements an interface or derives from a class.
    // =========================================================================
    [McpServerTool(Name = "GetImplementations", Title = "Find implementations",
                   ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
                   UseStructuredContent = true)]
    [Description("Find every concrete type in the solution that implements this interface or derives from this class, " +
                 "resolved via Roslyn's symbol graph rather than by grepping for ': IFoo' (which misses multi-level inheritance, " +
                 "generic constraints, and types in other files or projects). Use this BEFORE changing an interface or " +
                 "abstract/base class: every result is a type whose members may need matching changes. Total: 0 with no Error " +
                 "means the type exists but nothing implements it yet. " +
                 "Closed.IsExhaustive tells you whether the list is PROVABLY complete: true only for a C# 15 closed hierarchy or " +
                 "union, where no other assembly can add a derived type. For an ordinary open type it is false and " +
                 "Implementations means 'every implementer in this solution', not 'every implementer possible'. " +
                 "Results are paged: limit defaults to 100; Truncated: true means re-call with offset = NextOffset. " +
                 "Kind \"Ambiguous\" or \"NotFound\": Error explains and Candidates lists the types to choose from. Check Error first.")]
    public async Task<GetImplementationsResult> GetImplementations(
        [Description("Interface or class: a metadata name (e.g. 'SampleApp.IOrderRepository', 'Ns.Repository`2' for a generic) or a simple name (e.g. 'IOrderRepository').")] string typeName,
        [Description("Optional project to resolve the name in, e.g. 'App'. Omit it and WendSharp finds the declaring project.")] string? projectName = null,
        [Description("Maximum number of types to return (default 100).")] int limit = 100,
        [Description("Number of types to skip, for paging (default 0). Pass NextOffset from the previous call.")] int offset = 0,
        CancellationToken ct = default)
    {
        try
        {
            await session.InitializeAsync(ct);
            var (project, symbol) = await ResolveSymbolAsync(projectName, typeName, typesOnly: true, null, ct);
            var typeSymbol = (INamedTypeSymbol)symbol;

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
            var (page, truncated, next) = Page(hitArray, offset, limit);
            return new GetImplementationsResult(
                typeSymbol.ToFullSignature(), typeSymbol.TypeKind.ToString(), hitArray.Length, [.. page],
                DescribeClosedHierarchy(typeSymbol, ct), truncated, next);
        }
        catch (OperationCanceledException) { throw; }
        catch (AmbiguousSymbolException ex)
        {
            return new GetImplementationsResult(typeName, "Ambiguous", 0, [], ClosedHierarchyInfo.Open,
                Candidates: [.. ex.Candidates], Error: ex.Message);
        }
        catch (SymbolNotFoundException ex)
        {
            return new GetImplementationsResult(typeName, "NotFound", 0, [], ClosedHierarchyInfo.Open,
                Candidates: OrNull(ex.Suggestions), Error: ex.Message);
        }
        catch (Exception ex)
        {
            return new GetImplementationsResult(typeName, "Error", 0, [], ClosedHierarchyInfo.Open, Error: ex.Message);
        }
    }

    // SymbolFinder answers "what derives from this in the loaded solution"; for a C# 15 closed type
    // the compiler answers "what CAN derive from this". Only the latter makes an exhaustive edit safe.
    // IsClosed/IsUnion/GetClosedDerivedTypeInfo are [Experimental] (RSEXPERIMENTAL006) and may change
    // or be removed; the suppression is scoped here so a removal breaks the build loudly.
#pragma warning disable RSEXPERIMENTAL006
    internal static ClosedHierarchyInfo DescribeClosedHierarchy(INamedTypeSymbol type, CancellationToken ct)
    {
        try
        {
            if (!type.IsClosed)
                return new ClosedHierarchyInfo(IsClosed: false, type.IsUnion, IsExhaustive: false, []);

            var info = type.GetClosedDerivedTypeInfo(ct);
            return new ClosedHierarchyInfo(
                IsClosed: true,
                type.IsUnion,
                IsExhaustive: info.IsComplete,
                [.. info.ClosedDerivedTypes.Select(t => t.ToDisplayString())]);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            // Optional enrichment: never fail the tool over it.
            return ClosedHierarchyInfo.Open;
        }
    }
#pragma warning restore RSEXPERIMENTAL006

    // =========================================================================
    // GetOverrides - every override of a virtual/abstract member.
    // =========================================================================
    [McpServerTool(Name = "GetOverrides", Title = "Find overrides",
                   ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
                   UseStructuredContent = true)]
    [Description("Find every override of this virtual or abstract member across the whole inheritance chain, not just " +
                 "direct subclasses, resolved via Roslyn's symbol graph. This is the other half of FindReferences: FindReferences " +
                 "finds call sites; GetOverrides finds alternate implementations of the same member. Use this BEFORE changing a " +
                 "virtual/abstract member's signature, return type, or contract: every result must be updated to match. " +
                 "Kind \"NotOverridable\" means the member is not virtual/abstract/override and cannot have overrides. " +
                 "Kind \"Ambiguous\", \"TypeNotFound\" or \"MemberNotFound\": Error explains and Candidates lists what to choose " +
                 "from. Results are paged: limit defaults to 100; Truncated: true means re-call with offset = NextOffset. " +
                 "Check Error first.")]
    public async Task<GetOverridesResult> GetOverrides(
        [Description("Type that declares the virtual/abstract member: a metadata name (e.g. 'SampleApp.PricingService') or a simple name (e.g. 'PricingService').")] string typeName,
        [Description("Member name, e.g. 'CalculateTotal'.")] string memberName,
        [Description("Only for an overloaded method: the overload's comma-separated parameter types, e.g. \"int, string\", as " +
                     "listed in Candidates[].ParameterTypes; empty string for the parameterless overload.")] string? parameterTypes = null,
        [Description("Optional project to resolve the type in, e.g. 'App'. Omit it and WendSharp finds the declaring project.")] string? projectName = null,
        [Description("Maximum number of overrides to return (default 100).")] int limit = 100,
        [Description("Number of overrides to skip, for paging (default 0). Pass NextOffset from the previous call.")] int offset = 0,
        CancellationToken ct = default)
    {
        var label = $"{typeName}.{memberName}";
        try
        {
            await session.InitializeAsync(ct);

            INamedTypeSymbol typeSymbol;
            try
            {
                typeSymbol = (INamedTypeSymbol)(await ResolveSymbolAsync(projectName, typeName, typesOnly: true, null, ct)).Symbol;
            }
            catch (SymbolNotFoundException ex)
            {
                return new GetOverridesResult(label, "TypeNotFound", 0, [], Candidates: OrNull(ex.Suggestions), Error: ex.Message);
            }

            var memberSymbol = ResolveMember(typeSymbol, memberName, parameterTypes);
            if (!IsOverridable(memberSymbol))
                return new GetOverridesResult(memberSymbol.ToFullSignature(), "NotOverridable", 0, []);

            var solution = session.Solution;
            var overrides = await SymbolFinder.FindOverridesAsync(memberSymbol, solution, cancellationToken: ct);

            var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            var hits = new List<ISymbol>();
            foreach (var ov in overrides)
            {
                if (seen.Add(ov))
                    hits.Add(ov);
            }

            var hitArray = await BuildSymbolHitsAsync(hits, ct);
            var (page, truncated, next) = Page(hitArray, offset, limit);
            return new GetOverridesResult(
                memberSymbol.ToFullSignature(), OverridableKind(memberSymbol), hitArray.Length, [.. page], truncated, next);
        }
        catch (OperationCanceledException) { throw; }
        catch (AmbiguousSymbolException ex)
        {
            return new GetOverridesResult(label, "Ambiguous", 0, [], Candidates: [.. ex.Candidates], Error: ex.Message);
        }
        catch (SymbolNotFoundException ex)
        {
            return new GetOverridesResult(label, "MemberNotFound", 0, [], Candidates: OrNull(ex.Suggestions), Error: ex.Message);
        }
        catch (Exception ex)
        {
            return new GetOverridesResult(label, "Error", 0, [], Error: ex.Message);
        }
    }

    // =========================================================================
    // DescribeSymbol - full public surface with resolved types.
    // =========================================================================
    [McpServerTool(Name = "DescribeSymbol", Title = "Describe symbol",
                   ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
                   UseStructuredContent = true)]
    [Description("Get a type's public/protected surface with compiler-resolved types (never `var`), base types, implemented " +
                 "interfaces, and the exact `using` directives needed to write code against it. Use this INSTEAD of reading the " +
                 "file when you need precise signatures, e.g. to author an interface, an adapter, or a DI registration. Works for " +
                 "framework and NuGet types too (pass their metadata name, e.g. 'System.Net.Http.HttpClient'). A member's " +
                 "FilePath is omitted when it is the type's own FilePath. Members are paged: limit defaults to 150; Truncated: " +
                 "true means re-call with offset = NextOffset. On ambiguity or no match, Error explains and Candidates lists " +
                 "the types to choose from. Check Error first.")]
    public async Task<SymbolDescription> DescribeSymbol(
        [Description("Type name: a metadata name (e.g. 'SampleApp.Calculator', 'Ns.Repository`2' for a generic) or a simple name (e.g. 'Calculator').")] string typeName,
        [Description("Optional project to resolve the name in, e.g. 'App'. Omit it and WendSharp finds the declaring project.")] string? projectName = null,
        [Description("Maximum number of members to return (default 150).")] int limit = 150,
        [Description("Number of members to skip, for paging (default 0). Pass NextOffset from the previous call.")] int offset = 0,
        CancellationToken ct = default)
    {
        try
        {
            await session.InitializeAsync(ct);
            var symbol = (INamedTypeSymbol)(await ResolveSymbolAsync(projectName, typeName, typesOnly: true, null, ct)).Symbol;

            var (typeFile, _) = ResolveLocation(symbol.Locations.FirstOrDefault(l => l.IsInSource));
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
                var memberFile = string.Equals(filePath, typeFile, StringComparison.OrdinalIgnoreCase) ? null : filePath;
                members.Add(new MemberSignature(m.Kind.ToString(), sig, memberFile, line));

                CollectUsings(m, usingSet, visitedTypes);
            }

            var baseTypes = BuildBaseTypes(symbol);
            var (page, truncated, next) = Page(members, offset, limit);
            return new SymbolDescription(
                symbol.ToDisplayString(),
                BuildTypeHeader(symbol),
                typeFile,
                [.. baseTypes],
                [.. usingSet.Order()],
                [.. page],
                truncated,
                next);
        }
        catch (OperationCanceledException) { throw; }
        catch (AmbiguousSymbolException ex)
        {
            return new SymbolDescription("", "", "", [], [], [], Candidates: [.. ex.Candidates], Error: ex.Message);
        }
        catch (SymbolNotFoundException ex)
        {
            return new SymbolDescription("", "", "", [], [], [], Candidates: OrNull(ex.Suggestions), Error: ex.Message);
        }
        catch (Exception ex)
        {
            return new SymbolDescription("", "", "", [], [], [], Error: ex.Message);
        }
    }

    // =========================================================================
    // AnalyzeMember - dependencies, data flow, callers.
    // =========================================================================
    [McpServerTool(Name = "AnalyzeMember", Title = "Analyze member",
                   ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
                   UseStructuredContent = true)]
    [Description("Get what a method actually touches, computed by the compiler INSTEAD of traced by hand: internal vs external " +
                 "dependencies, the variables that flow in (candidate parameters) and out (candidate return values) plus any " +
                 "captured by closures, and its callers. Use before extracting or splitting a method: the dependencies tell you " +
                 "what must move or become a parameter, and the callers tell you what will break. FilePath with " +
                 "LineStart/LineEnd locate the member; its Source is only returned with includeSource. Callers are capped at " +
                 "maxCallers (CallerCount is the full count; use FindReferences for the complete list). On ambiguity or no " +
                 "match, Error explains and Candidates lists what to choose from. Check Error first.")]
    public async Task<MemberAnalysis> AnalyzeMember(
        [Description("Type containing the member: a metadata name (e.g. 'SampleApp.OrderService') or a simple name.")] string typeName,
        [Description("Member (method) name, e.g. 'Calculate'.")] string memberName,
        [Description("Only for an overloaded method: the overload's comma-separated parameter types, e.g. \"int, string\", as " +
                     "listed in Candidates[].ParameterTypes; empty string for the parameterless overload.")] string? parameterTypes = null,
        [Description("Optional project to resolve the type in, e.g. 'App'. Omit it and WendSharp finds the declaring project.")] string? projectName = null,
        [Description("Also return the member's source text (default false).")] bool includeSource = false,
        [Description("Maximum number of callers to return (default 50).")] int maxCallers = 50,
        CancellationToken ct = default)
    {
        try
        {
            await session.InitializeAsync(ct);
            var (project, symbol) = await ResolveSymbolAsync(projectName, typeName, typesOnly: true, null, ct);
            var typeSymbol = (INamedTypeSymbol)symbol;
            var memberSymbol = ResolveMember(typeSymbol, memberName, parameterTypes);

            var syntaxRef = memberSymbol.DeclaringSyntaxReferences.FirstOrDefault()
                ?? throw new ArgumentException($"Member '{memberName}' has no syntax reference.");
            var memberNode = await syntaxRef.GetSyntaxAsync(ct);
            var doc = project.Solution.GetDocument(memberNode.SyntaxTree)
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

            var flowsIn = new HashSet<string>(StringComparer.Ordinal);
            var flowsOut = new HashSet<string>(StringComparer.Ordinal);
            var captured = new HashSet<string>(StringComparer.Ordinal);
            foreach (var body in GetExecutableBodies(memberNode))
            {
                var flow = model.AnalyzeDataFlow(body);
                if (!flow.Succeeded)
                    continue;
                foreach (var v in flow.DataFlowsIn)
                    flowsIn.Add(v.Name);
                foreach (var v in flow.DataFlowsOut)
                    flowsOut.Add(v.Name);
                foreach (var v in flow.Captured)
                    captured.Add(v.Name);
            }

            var callers = await CollectReferenceHitsAsync(memberSymbol, withContext: true, ct);
            var (callerPage, callersTruncated, _) = Page(callers, 0, maxCallers);

            return new MemberAnalysis(
                includeSource ? memberNode.ToString() : null,
                doc.FilePath ?? "",
                fullSpan.StartLinePosition.Line + 1,
                fullSpan.EndLinePosition.Line + 1,
                [.. internalDeps.Select(DistinctName).Order()],
                [.. externalDeps.Select(DistinctName).Order()],
                [.. flowsIn.Order()],
                [.. flowsOut.Order()],
                [.. captured.Order()],
                callers.Count,
                GroupByFile(callerPage),
                callersTruncated);
        }
        catch (OperationCanceledException) { throw; }
        catch (AmbiguousSymbolException ex)
        {
            return EmptyAnalysis(ex.Message, [.. ex.Candidates]);
        }
        catch (SymbolNotFoundException ex)
        {
            return EmptyAnalysis(ex.Message, OrNull(ex.Suggestions));
        }
        catch (Exception ex)
        {
            return EmptyAnalysis(ex.Message, null);
        }
    }

    static MemberAnalysis EmptyAnalysis(string error, SymbolCandidate[]? candidates) =>
        new(null, "", 0, 0, [], [], [], [], [], 0, [], Candidates: candidates, Error: error);

    // =========================================================================
    // PlanRename - compiler-verified rename plan WITHOUT applying it.
    // The renamed solution is computed locally and DISCARDED: the session Solution is never
    // mutated and WendSharp never writes to disk. The plan is shaped for the edit tools agents
    // actually have (text match: Claude Code Edit, Codex apply_patch, Zed edit_file), none of
    // which take offsets: whole changed lines per file, plus a ReplaceAll bit when a plain
    // replace-all of the old name is provably the same edit. Offsets stay available with detail.
    // =========================================================================
    [McpServerTool(Name = "PlanRename", Title = "Plan rename",
                   ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
                   UseStructuredContent = true)]
    [Description("Before renaming a symbol by hand, get a compiler-verified rename plan; WendSharp computes it but never writes. " +
                 "Locate the symbol by filePath + line + column, or by fullyQualifiedSymbolName. Returns Files[] with, per file: " +
                 "Lines[] (each changed line as Before and After, whole line trimmed, bottom-up), and ReplaceAll, which is true " +
                 "when replacing every occurrence of OldName with NewName in that file gives exactly the planned result. Also " +
                 "returns Conflicts (name collisions, new compile errors) and Cascades (overrides, interface members, partial " +
                 "declarations) that text search misses. Follow Next: if HasConflicts is true, do NOT apply. Truncated: true " +
                 "means Lines were left out for some files to bound the response; Next says how to fetch them. detail: true " +
                 "adds offset-level Edits (UTF-16 offsets after any BOM, descending per file). On ambiguity Error explains and " +
                 "Candidates lists the symbols to choose from.")]
    public async Task<PlanRenameResult> PlanRename(
        [Description("Optional project, e.g. 'App'. Omit it: it is taken from filePath, or found from fullyQualifiedSymbolName.")] string? projectName = null,
        [Description("Absolute path of the file containing the symbol (locator A, together with line and column).")] string? filePath = null,
        [Description("1-based line number of the symbol's name (with filePath).")] int? line = null,
        [Description("1-based column number inside the symbol's name (with filePath).")] int? column = null,
        [Description("Locator B, instead of filePath + line + column: a metadata name, e.g. 'SampleApp.Calculator.Add' or 'SampleApp.Calculator'.")] string? fullyQualifiedSymbolName = null,
        [Description("The new identifier.")] string newName = "",
        [Description("Rename all overloads of a method too (default false).")] bool renameOverloads = false,
        [Description("Also rename plain-text matches in comments (default false; textual, review those lines).")] bool renameInComments = false,
        [Description("Also rename plain-text matches in string literals (default false; textual, review those lines).")] bool renameInStrings = false,
        [Description("Also rename the file when renaming a type (default false).")] bool renameFile = false,
        [Description("Only with fullyQualifiedSymbolName for an overloaded method: the overload's comma-separated parameter " +
                     "types, e.g. \"int, string\", as listed in Candidates[].ParameterTypes.")] string? parameterTypes = null,
        [Description("Also return offset-level Edits per file (default false).")] bool detail = false,
        [Description("Return Lines/Edits only for these absolute file paths, e.g. the ones a truncated plan left out. " +
                     "The plan itself always covers every file.")] string[]? filePaths = null,
        CancellationToken ct = default)
    {
        var scope = new RenameScope(renameOverloads, renameInComments, renameInStrings, renameFile);
        var locator = fullyQualifiedSymbolName ?? filePath ?? "<no locator>";
        try
        {
            await session.InitializeAsync(ct);
            var solution = session.Solution;
            var symbol = await ResolveRenameTargetAsync(
                projectName, filePath, line, column, fullyQualifiedSymbolName, parameterTypes, ct);

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
                    $"New name '{newName}' equals the current name '{oldName}' - rename is a no-op.",
                    scope);
            }

            var options = new SymbolRenameOptions(
                RenameOverloads: renameOverloads,
                RenameInStrings: renameInStrings,
                RenameInComments: renameInComments,
                RenameFile: renameFile);
            var newSolution = await Renamer.RenameSymbolAsync(solution, symbol, options, newName, ct);

            var changes = await ExtractChangesAsync(solution, newSolution, oldName, ct);
            var conflicts = await DetectConflictsAsync(solution, newSolution, ct);
            var cascades = BuildCascades(symbol, changes.DeclarationSymbols, renameOverloads);
            var files = BuildRenameFiles(changes.Files, oldName, newName, detail, filePaths, out var truncated);

            var editCount = changes.Files.Sum(f => f.Edits.Count);
            var hasConflicts = conflicts.Any(c => c.Severity == "Error");
            var textual = changes.Files.SelectMany(f => f.Edits).Any(e => e.Location is "Comment" or "String");
            var next = BuildNext(hasConflicts, truncated, files, changes.FileRenames.Count > 0, textual, oldName, newName);

            return new PlanRenameResult(
                symbol.ToDisplayString(),
                symbol.Kind.ToString(),
                oldName,
                newName,
                NewNameValid: true,
                hasConflicts,
                editCount,
                changes.Files.Count,
                scope,
                files,
                conflicts,
                cascades,
                changes.FileRenames,
                truncated,
                next,
                Candidates: null,
                Error: null);
        }
        catch (OperationCanceledException) { throw; }
        catch (AmbiguousSymbolException ex)
        {
            return ErrorResult(locator, "", "", newName, false, ex.Message, scope, ex.Candidates);
        }
        catch (SymbolNotFoundException ex)
        {
            return ErrorResult(locator, "", "", newName, false, ex.Message, scope, OrNull(ex.Suggestions));
        }
        catch (Exception ex)
        {
            return ErrorResult("", "", "", newName, false, ex.Message, scope);
        }
    }

    // --- PlanRename helpers -----------------------------------------------------

    async Task<ISymbol> ResolveRenameTargetAsync(
        string? projectName, string? filePath, int? line, int? column,
        string? fullyQualifiedSymbolName, string? parameterTypes, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(filePath) && line is int ln && column is int col)
        {
            var doc = session.FindDocument(filePath);
            if (doc is null && !string.IsNullOrWhiteSpace(projectName))
                doc = session.GetProject(projectName).Documents
                    .FirstOrDefault(d => string.Equals(d.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
            if (doc is null)
                throw new SymbolNotFoundException(
                    $"File '{filePath}' is not in the loaded solution. Pass an absolute path; if you just created the file, call Refresh with it first.", []);

            return await SymbolLookup.FindAtPositionAsync(doc, ln, col, session.Solution.Workspace, ct)
                   ?? throw new SymbolNotFoundException(
                       $"No symbol at {filePath}:{ln}:{col}. Line and column are 1-based and must point inside the symbol's name. " +
                       "Or use fullyQualifiedSymbolName.", []);
        }

        if (!string.IsNullOrWhiteSpace(fullyQualifiedSymbolName))
            return (await ResolveSymbolAsync(projectName, fullyQualifiedSymbolName, typesOnly: false, parameterTypes, ct)).Symbol;

        throw new ArgumentException("Pass filePath + line + column, or fullyQualifiedSymbolName, to locate the symbol to rename.");
    }

    sealed record FileChange(string FilePath, SourceText OldText, SourceText NewText, List<RenameEdit> Edits);

    sealed record RenameChanges(
        List<FileChange> Files, List<(ISymbol Symbol, int Position)> DeclarationSymbols, List<FileRename> FileRenames);

    async Task<RenameChanges> ExtractChangesAsync(
        Solution oldSolution, Solution newSolution, string oldName, CancellationToken ct)
    {
        var files = new List<FileChange>();
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
                var newText = await newDoc.GetTextAsync(ct);
                var root = await oldDoc.GetSyntaxRootAsync(ct);
                var model = await oldDoc.GetSemanticModelAsync(ct);
                var textChanges = await newDoc.GetTextChangesAsync(oldDoc, ct);

                var edits = new List<RenameEdit>();
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

                if (edits.Count > 0)
                    files.Add(new FileChange(filePath, oldText, newText, edits));
            }
        }

        files.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.FilePath, b.FilePath));
        return new RenameChanges(files, declarationSymbols, fileRenames);
    }

    static List<RenameFile> BuildRenameFiles(
        IReadOnlyList<FileChange> changes, string oldName, string newName, bool detail,
        string[]? filePaths, out bool truncated)
    {
        HashSet<string>? only = filePaths is { Length: > 0 }
            ? new HashSet<string>(filePaths.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase)
            : null;
        // Every file is always listed (path, EditCount, ReplaceAll); Lines fill what is left.
        var budget = MaxPlanChars - changes.Sum(c => c.FilePath.Length + 80);
        var anyIncluded = false;
        truncated = false;
        var result = new List<RenameFile>(changes.Count);

        foreach (var change in changes)
        {
            // Exact by construction: the plain replace-all is compared with the renamed text itself.
            var replaceAll = string.Equals(
                change.OldText.ToString().Replace(oldName, newName, StringComparison.Ordinal),
                change.NewText.ToString(),
                StringComparison.Ordinal);

            if (only is not null && (change.FilePath.Length == 0 || !only.Contains(Path.GetFullPath(change.FilePath))))
            {
                result.Add(new RenameFile(change.FilePath, change.Edits.Count, replaceAll, null, null));
                continue;
            }

            var lines = BuildLines(change);
            // Offsets are the fallback whenever line form is impossible (an edit spanning lines).
            var edits = detail || lines is null
                ? change.Edits.OrderByDescending(e => e.StartOffset).ToList()
                : null;

            var cost = (lines?.Sum(l => l.Before.Length + l.After.Length + l.Location.Length + 50) ?? 0)
                       + (edits?.Sum(e => e.FilePath.Length + e.OldText.Length + e.NewText.Length + e.ContainingMember.Length + 220) ?? 0);

            if (cost > budget && anyIncluded)
            {
                truncated = true;
                result.Add(new RenameFile(change.FilePath, change.Edits.Count, replaceAll, null, null));
                continue;
            }

            budget -= cost;
            anyIncluded = true;
            result.Add(new RenameFile(change.FilePath, change.Edits.Count, replaceAll, lines, edits));
        }

        return result;
    }

    /// <summary>Whole changed lines, bottom-up. Null when an edit spans several lines (never the
    /// case for an identifier rename), in which case the file falls back to offset Edits.</summary>
    static List<RenameLine>? BuildLines(FileChange change)
    {
        if (change.Edits.Any(e => e.StartLine != e.EndLine))
            return null;

        var lines = new List<RenameLine>();
        foreach (var group in change.Edits.GroupBy(e => e.StartLine).OrderByDescending(g => g.Key))
        {
            var original = change.OldText.Lines[group.Key - 1].ToString();
            var updated = original;
            foreach (var e in group.OrderByDescending(e => e.StartColumn))
            {
                var start = e.StartColumn - 1;
                updated = updated[..start] + e.NewText + updated[(start + e.Length)..];
            }

            var locations = string.Join(",", group.OrderBy(e => e.StartColumn).Select(e => e.Location).Distinct());
            lines.Add(new RenameLine(group.Key, original.Trim(), updated.Trim(), locations));
        }
        return lines;
    }

    static string BuildNext(
        bool hasConflicts, bool truncated, IReadOnlyList<RenameFile> files, bool hasFileRenames,
        bool hasTextualEdits, string oldName, string newName)
    {
        if (hasConflicts)
            return "Do not apply this plan. The rename creates the compile errors listed in Conflicts: report them and choose another name.";

        if (truncated)
        {
            var missing = files.Count(f => f.Lines is null && f.Edits is null);
            return $"Lines were left out for {missing} of {files.Count} files to keep this response small. Before applying " +
                   "anything, call PlanRename again with the same arguments and filePaths set to the files whose Lines is " +
                   "missing (files with ReplaceAll true can instead be applied as a replace-all).";
        }

        var next = "Apply every file: replace each Lines[].Before with its After (if the same Before text occurs more than " +
                   $"once in a file, use Line to pick the occurrence), or, where ReplaceAll is true, replace every occurrence " +
                   $"of '{oldName}' with '{newName}' in that file.";
        if (hasTextualEdits)
            next += " Review lines whose Location includes Comment or String first: those are plain-text matches.";
        if (hasFileRenames)
            next += " Then rename the files listed in FileRenames on disk and call Refresh with their new paths.";
        return next + " Finally call GetDiagnostics with filePaths set to the changed files and fix until ErrorCount is 0.";
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
                    conflicts.Add(new RenameConflict("Error", e.Id, e.Message, e.FilePath, e.Line, e.Column, e.Preview));
            }
        }

        return conflicts;
    }

    readonly record struct ErrorEntry(string Id, string Message, string FilePath, int Line, int Column, string Preview)
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
                    span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1,
                    PreviewLine(d.Location, ct)));
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
        string error, RenameScope scope, IReadOnlyList<SymbolCandidate>? candidates = null) =>
        new(symbol, symbolKind, oldName, newName, newNameValid,
            HasConflicts: false, EditCount: 0, FileCount: 0, scope,
            Array.Empty<RenameFile>(), Array.Empty<RenameConflict>(), Array.Empty<CascadeTarget>(),
            Array.Empty<FileRename>(), Truncated: false, Next: null, candidates is { Count: > 0 } ? candidates : null, error);

    static readonly SymbolDisplayFormat ContainingMemberFormat = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType,
        parameterOptions: SymbolDisplayParameterOptions.None,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    // =========================================================================
    // Refresh - add files the agent created; changed and deleted files are synced automatically.
    // =========================================================================
    [McpServerTool(Name = "Refresh", Title = "Refresh from disk",
                   ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
                   UseStructuredContent = true)]
    [Description("Tell WendSharp about files you CREATED, moved, or renamed on disk, so it adds them to the right project. " +
                 "Edits to existing files and deletions are picked up automatically on every tool call, so you do not need " +
                 "Refresh for those (passing them is harmless). Pass absolute paths. Unmapped[] lists paths WendSharp could not " +
                 "place in a project (wrong path, or a file outside the solution). On failure Error is set: check Error first.")]
    public async Task<RefreshResult> Refresh(
        [Description("Absolute paths of the files you created, moved, renamed, changed, or deleted.")] string[] changedFilePaths,
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
    // GetDiagnostics - compiler and analyzer errors/warnings.
    // =========================================================================
    [McpServerTool(Name = "GetDiagnostics", Title = "Get diagnostics",
                   ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
                   UseStructuredContent = true)]
    [Description("Report compiler errors (and optionally warnings) PLUS the project's analyzer diagnostics (code style, custom " +
                 "spec analyzers, banned-API rules, with .editorconfig severity): the truth check after you edit. It reflects " +
                 "the files on disk now. Pass filePaths with the files you just edited (their projects are found for you), or " +
                 "projectName for a whole project. ErrorCount and WarningCount cover everything matched; Diagnostics is paged " +
                 "with errors first: limit defaults to 100, and Truncated: true means re-call with offset = NextOffset. A task " +
                 "is done when ErrorCount is 0. Analyzer errors are specifications: fix the code, never .editorconfig, " +
                 "BannedSymbols.txt, or suppressions. On failure Error is set and nothing was computed: check Error first.")]
    public async Task<DiagnosticsResult> GetDiagnostics(
        [Description("Optional project, e.g. 'App'. Omit it when you pass filePaths.")] string? projectName = null,
        [Description("Include warnings in addition to errors (default false).")] bool includeWarnings = false,
        [Description("Also run the project's Roslyn analyzers (default true). Set false only for a quick compiler-only check " +
                     "mid-edit; leave true for the final check.")] bool includeAnalyzers = true,
        [Description("Absolute paths of the files to check, e.g. the files you just edited. Omit (with projectName) for the whole project.")] string[]? filePaths = null,
        [Description("Maximum number of diagnostics to return (default 100).")] int limit = 100,
        [Description("Number of diagnostics to skip, for paging (default 0). Pass NextOffset from the previous call.")] int offset = 0,
        CancellationToken ct = default)
    {
        try
        {
            await session.InitializeAsync(ct);
            var solution = session.Solution;

            HashSet<string>? filter = null;
            if (filePaths is { Length: > 0 } pathArray)
            {
                filter = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in pathArray)
                    filter.Add(Path.GetFullPath(p));
            }

            List<Project> projects;
            if (!string.IsNullOrWhiteSpace(projectName))
            {
                var project = session.GetProject(projectName);
                if (filter is not null)
                {
                    var docPaths = await session.GetSourceTreePathsAsync(project, ct);
                    if (!docPaths.Any(dp => filter.Contains(Path.GetFullPath(dp))))
                        return new DiagnosticsResult(projectName, 0, 0, 0, [],
                            Error: $"None of the {filter.Count} filePaths match a document in project '{projectName}'. " +
                                   "Diagnostics were NOT computed: check the paths (absolute, correct separators) or omit " +
                                   "projectName to let WendSharp find the projects. If you just created the file, call Refresh first.");
                }
                projects = [project];
            }
            else if (filter is not null)
            {
                projects = filter
                    .SelectMany(p => solution.GetDocumentIdsWithFilePath(p))
                    .Select(id => solution.GetProject(id.ProjectId))
                    .OfType<Project>()
                    .DistinctBy(p => p.Id)
                    .ToList();
                if (projects.Count == 0)
                    return new DiagnosticsResult("", 0, 0, 0, [],
                        Error: $"None of the {filter.Count} filePaths is a document in the loaded solution. Diagnostics were " +
                               "NOT computed: check the paths (absolute, correct separators). If you just created the file, " +
                               "call Refresh first.");
            }
            else
            {
                return new DiagnosticsResult("", 0, 0, 0, [],
                    Error: "Pass filePaths (the files you edited) or projectName. Diagnostics were NOT computed.");
            }

            var all = new List<DiagnosticDto>();
            foreach (var project in projects)
                all.AddRange(await CollectDiagnosticsAsync(project, includeWarnings, includeAnalyzers, filter, ct));

            // Errors first, so a page never hides an error behind warnings.
            var ordered = all
                .DistinctBy(d => (d.Code, d.FilePath, d.Line, d.Column, d.Message))
                .OrderBy(d => d.Severity == "Error" ? 0 : 1)
                .ThenBy(d => d.FilePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(d => d.Line)
                .ThenBy(d => d.Column)
                .ToList();
            var errorCount = ordered.Count(d => d.Severity == "Error");
            var (page, truncated, next) = Page(ordered, offset, limit);

            return new DiagnosticsResult(
                string.Join(", ", projects.Select(p => p.Name)),
                errorCount, ordered.Count - errorCount, ordered.Count, [.. page], truncated, next);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new DiagnosticsResult(projectName ?? "", 0, 0, 0, [], Error: ex.Message);
        }
    }

    static async Task<List<DiagnosticDto>> CollectDiagnosticsAsync(
        Project project, bool includeWarnings, bool includeAnalyzers, HashSet<string>? filter, CancellationToken ct)
    {
        var compilation = await project.GetCompilationAsync(ct)
                          ?? throw new InvalidOperationException($"No compilation for project '{project.Name}'.");

        var compilerDiags = compilation.GetDiagnostics(ct);
        var results = new List<DiagnosticDto>(compilerDiags.Length);

        void Append(Diagnostic d)
        {
            if (filter is not null)
            {
                var p = d.Location.SourceTree?.FilePath;
                if (p is null || !filter.Contains(Path.GetFullPath(p)))
                    return;
            }
            var span = d.Location.GetLineSpan();
            results.Add(new DiagnosticDto(
                d.Id,
                d.GetMessage(),
                d.Location.SourceTree?.FilePath ?? "",
                span.StartLinePosition.Line + 1,
                span.StartLinePosition.Character + 1,
                d.Severity.ToString(),
                PreviewLine(d.Location, ct)));
        }

        foreach (var d in compilerDiags)
        {
            if (d.Severity == DiagnosticSeverity.Error
                || (includeWarnings && d.Severity == DiagnosticSeverity.Warning))
                Append(d);
        }

        if (includeAnalyzers)
        {
            var analyzers = project.AnalyzerReferences
                .SelectMany(r => r.GetAnalyzers(LanguageNames.CSharp))
                .ToImmutableArray();

            if (!analyzers.IsEmpty)
            {
                var cwa = compilation.WithAnalyzers(analyzers,
                    new CompilationWithAnalyzersOptions(
                        project.AnalyzerOptions,
                        onAnalyzerException: null,
                        concurrentAnalysis: true,
                        logAnalyzerExecutionTime: false));

                var analyzerDiags = await cwa.GetAnalyzerDiagnosticsAsync(ct);
                foreach (var d in analyzerDiags)
                {
                    if (d.Severity == DiagnosticSeverity.Error
                        || (includeWarnings && d.Severity == DiagnosticSeverity.Warning))
                        Append(d);
                }
            }
        }

        return results;
    }

    // =========================================================================
    // GetWorkspaceInfo - what WendSharp resolved, and the editor's open roots.
    // =========================================================================
    [McpServerTool(Name = "GetWorkspaceInfo", Title = "Get workspace info",
                   ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
                   UseStructuredContent = true)]
    [Description("Report which solution WendSharp loaded and from where: the solution file, its root directory, and the " +
                 "loaded project names. EmptyProjects lists projects that loaded without any source files, which means MSBuild " +
                 "could not evaluate them (usually a missing restore): every lookup in them returns NotFound, and " +
                 "LoadDiagnostics holds the MSBuild messages. When the client exposes workspace roots (MCP roots), also lists " +
                 "every open root folder; ClientExposesRoots is false for clients that cannot report roots (e.g. Zed today). " +
                 "You rarely need this tool: other tools find the project themselves. On failure Error is set: check Error first.")]
    public async Task<WorkspaceInfoResult> GetWorkspaceInfo(McpServer server, CancellationToken ct = default)
    {
        try
        {
            await session.InitializeAsync(server, ct);

            var solution = session.Solution;
            var solutionPath = session.LoadedSolutionPath ?? "";
            var rootDir = solutionPath.Length > 0 ? Path.GetDirectoryName(solutionPath) ?? "" : "";
            var projects = solution.Projects
                .Select(p => p.Name)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var emptyProjects = solution.Projects
                .Where(p => !p.DocumentIds.Any())
                .Select(p => p.Name)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var loadDiagnostics = session.LoadDiagnostics
                .Take(20)
                .Select(m => m.Length > 400 ? m[..400] + "..." : m)
                .ToArray();

            var clientRoots = await session.GetClientRootsAsync(server, ct);
            // Still reported so an agent can see whether the fallback is available. See
            // WorkspaceSession.GetClientRootsAsync for why this deprecated API is kept.
#pragma warning disable MCP9005
            var exposesRoots = server.ClientCapabilities?.Roots is not null;
#pragma warning restore MCP9005

            return new WorkspaceInfoResult(
                solutionPath, rootDir, projects, emptyProjects, loadDiagnostics, [.. clientRoots], exposesRoots);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new WorkspaceInfoResult("", "", [], [], [], [], false, ex.Message);
        }
    }

    // =========================================================================
    // Symbol resolution - one path for every tool, so ambiguity and "not found" always come
    // back the same way: an exception carrying candidates, mapped by each tool to its result.
    // =========================================================================

    /// <summary>Resolves a type or member name. With <paramref name="projectName"/>, the name is
    /// resolved in that project (as before). Without it, source declarations across the whole
    /// solution are searched, and the declaring project is taken from the match.</summary>
    async Task<(Project Project, ISymbol Symbol)> ResolveSymbolAsync(
        string? projectName, string name, bool typesOnly, string? parameterTypes, CancellationToken ct)
    {
        var solution = session.Solution;
        Project? project = null;
        IReadOnlyList<ISymbol> found;

        if (!string.IsNullOrWhiteSpace(projectName))
        {
            project = session.GetProject(projectName);
            var compilation = await project.GetCompilationAsync(ct)
                              ?? throw new InvalidOperationException($"No compilation for project '{project.Name}'.");
            var r = await SymbolLookup.ResolveAsync(
                compilation, project, name, typesOnly ? SymbolKind.NamedType : null,
                await session.GetSourceTreePathsAsync(project, ct), ct);
            if (r.Symbol is { } single)
                return (project, single);
            found = r.Ambiguous;

            if (typesOnly && found.Count > 0 && !found.Any(s => s is INamedTypeSymbol))
                throw new SymbolNotFoundException(
                    $"Type '{name}' not found in project '{project.Name}' (found a non-type symbol of that name: " +
                    $"{found[0].ToDisplayString()}). Pass a type name.", []);
        }
        else
        {
            found = await FindInSolutionAsync(solution, name, typesOnly, ct);
            if (found.Count == 1 && ProjectOf(found[0], solution) is { } declaring)
                return (declaring, found[0]);

            if (found.Count == 0 && (name.Contains('.') || name.Contains('+')))
            {
                // Not declared in source: a type from a referenced assembly (framework or NuGet).
                foreach (var p in solution.Projects)
                {
                    var compilation = await p.GetCompilationAsync(ct);
                    if (compilation?.GetTypeByMetadataName(name) is { } external)
                        return (p, external);
                }
            }
        }

        if (found.Count > 1)
        {
            if (IsOverloadSet(found))
            {
                var chosen = ResolveOverload(found, name, parameterTypes, solution);
                return (project ?? ProjectOf(chosen, solution)
                        ?? throw new InvalidOperationException($"Cannot find the project declaring '{chosen.ToDisplayString()}'."), chosen);
            }

            var candidates = ToCandidates(found, solution);
            var projects = candidates.Select(c => c.Project).Where(p => p.Length > 0).Distinct().ToList();
            var where = projects.Count > 1 ? $" across projects {string.Join(", ", projects)}" : "";
            var names = string.Join("; ", candidates.Take(10).Select(c =>
                c.ParameterTypes is null ? c.Name : $"{c.Name}({c.ParameterTypes})"));
            throw new AmbiguousSymbolException(
                $"'{name}' is ambiguous: it matches {candidates.Count} symbols{where}: {names}. Re-call with the Name " +
                "(a fully-qualified metadata name) of one of the Candidates, plus its ParameterTypes for a method, and its " +
                "Project as projectName if you pass one.",
                candidates);
        }

        throw await NotFoundAsync(name, project, typesOnly, ct);
    }

    static async Task<IReadOnlyList<ISymbol>> FindInSolutionAsync(
        Solution solution, string name, bool typesOnly, CancellationToken ct)
    {
        var simple = SimpleNameOf(name);
        if (simple.Length == 0)
            return [];

        var filter = typesOnly ? SymbolFilter.Type : SymbolFilter.TypeAndMember;
        var decls = await SymbolFinder.FindSourceDeclarationsAsync(solution, simple, ignoreCase: false, filter, ct);

        // One declaration compiled into several projects (multi-targeting, linked files) is one symbol.
        return decls
            .Where(s => NameMatches(s, name))
            .DistinctBy(s => (MetadataNameOf(s), FormatParameterTypes(s), FirstSourcePath(s), FirstSourceLine(s)))
            .OrderBy(s => FirstSourcePath(s), StringComparer.OrdinalIgnoreCase)
            .ThenBy(FirstSourceLine)
            .ToList();
    }

    /// <summary>A member of the type by name; overloads resolve through parameterTypes.</summary>
    static ISymbol ResolveMember(INamedTypeSymbol type, string memberName, string? parameterTypes)
    {
        var candidates = SymbolLookup.FindMembers(type, memberName);
        if (candidates.Count == 1)
            return candidates[0];
        if (candidates.Count > 1)
            return ResolveOverload(candidates, memberName, parameterTypes, solution: null);

        var similar = type.GetMembers()
            .Where(m => !m.IsImplicitlyDeclared && m.CanBeReferencedByName
                        && m.Name.Contains(memberName, StringComparison.OrdinalIgnoreCase))
            .Select(m => m.Name)
            .Distinct()
            .Take(5)
            .ToList();
        var hint = similar.Count > 0 ? $" Members with a similar name: {string.Join(", ", similar)}." : "";
        throw new SymbolNotFoundException(
            $"Member '{memberName}' not found in type '{type.ToDisplayString()}'.{hint}", []);
    }

    static bool IsOverloadSet(IReadOnlyList<ISymbol> symbols) =>
        symbols.All(s => s is IMethodSymbol { ContainingType: not null })
        && symbols.Select(s => MetadataNameOf(s.ContainingType)).Distinct(StringComparer.Ordinal).Count() == 1;

    static ISymbol ResolveOverload(
        IReadOnlyList<ISymbol> candidates, string memberName, string? parameterTypes, Solution? solution)
    {
        var options = string.Join(" | ", candidates.Select(c => $"\"{FormatParameterTypes(c)}\""));
        var list = ToCandidates(candidates, solution);
        if (parameterTypes is null)
            throw new AmbiguousSymbolException(
                $"Member '{memberName}' is overloaded ({candidates.Count}). " +
                $"Re-call with parameterTypes set to one of: {options}.", list);

        var wanted = WithoutWhitespace(parameterTypes);
        var matches = candidates.Where(c => WithoutWhitespace(FormatParameterTypes(c)) == wanted).ToList();
        if (matches.Count == 1)
            return matches[0];
        if (matches.Count > 1)
            throw new AmbiguousSymbolException(
                $"{matches.Count} overloads of '{memberName}' take '{parameterTypes}' (they differ only in generic arity). " +
                "Use PlanRename with filePath + line + column, or FindReferences on the type.", ToCandidates(matches, solution));
        throw new AmbiguousSymbolException(
            $"Member '{memberName}' has no overload with parameter types '{parameterTypes}'. " +
            $"Re-call with parameterTypes set to one of: {options}.", list);
    }

    async Task<SymbolNotFoundException> NotFoundAsync(string name, Project? project, bool typesOnly, CancellationToken ct)
    {
        var solution = session.Solution;
        if (project is not null && !project.DocumentIds.Any())
            return new SymbolNotFoundException(
                $"Project '{project.Name}' has no source documents loaded, so nothing can be found in it. MSBuild most likely " +
                "failed to load it (restore or build it, then restart WendSharp); GetWorkspaceInfo shows EmptyProjects and LoadDiagnostics.", []);

        var suggestions = await SuggestAsync(solution, name, typesOnly, ct);
        var what = typesOnly ? "type" : "symbol";
        var scope = project is null ? "the solution" : $"project '{project.Name}'";
        var hint = suggestions.Count > 0
            ? " Similar names are listed in Candidates; re-call with one of their Name values."
            : project is null ? " Check the spelling." : " Check the spelling, or omit projectName to search the whole solution.";
        var empty = solution.Projects.Count(p => !p.DocumentIds.Any());
        var emptyNote = empty > 0
            ? $" Note: {empty} project(s) loaded without source files, so their symbols cannot be found (see GetWorkspaceInfo)."
            : "";
        return new SymbolNotFoundException($"No {what} named '{name}' in {scope}.{hint}{emptyNote}", suggestions);
    }

    static async Task<IReadOnlyList<SymbolCandidate>> SuggestAsync(
        Solution solution, string name, bool typesOnly, CancellationToken ct)
    {
        var simple = SimpleNameOf(name);
        if (simple.Length < 2)
            return [];
        try
        {
            var found = await SymbolFinder.FindSourceDeclarationsWithPatternAsync(
                solution, simple, typesOnly ? SymbolFilter.Type : SymbolFilter.TypeAndMember, ct);
            var ranked = found
                .OrderBy(s => s.Name.Equals(simple, StringComparison.OrdinalIgnoreCase) ? 0
                    : s.Name.StartsWith(simple, StringComparison.OrdinalIgnoreCase) ? 1 : 2)
                .ThenBy(s => s.Name.Length)
                .ThenBy(s => MetadataNameOf(s), StringComparer.Ordinal)
                .ToList();
            return ToCandidates(ranked, solution).Take(5).ToList();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            return [];   // suggestions are optional enrichment; never fail the tool over them
        }
    }

    static List<SymbolCandidate> ToCandidates(IEnumerable<ISymbol> symbols, Solution? solution) =>
        symbols
            .Select(s => ToCandidate(s, solution))
            .DistinctBy(c => (c.Name, c.ParameterTypes, c.FilePath, c.Line))
            .ToList();

    static SymbolCandidate ToCandidate(ISymbol s, Solution? solution)
    {
        var loc = s.Locations.FirstOrDefault(l => l.IsInSource);
        var (filePath, line) = ResolveLocation(loc);
        var project = solution is not null && ProjectOf(s, solution) is { } p ? p.Name : "";
        return new SymbolCandidate(
            MetadataNameOf(s),
            s.Kind.ToString(),
            s is IMethodSymbol ? FormatParameterTypes(s) : null,
            project,
            filePath,
            line);
    }

    static Project? ProjectOf(ISymbol s, Solution solution)
    {
        var tree = s.Locations.FirstOrDefault(l => l.IsInSource)?.SourceTree;
        return tree is null ? null : solution.GetDocument(tree)?.Project;
    }

    /// <summary>The name every name argument accepts: Namespace.Type, Namespace.Outer+Inner,
    /// Namespace.Generic`2, or that type name followed by .Member.</summary>
    static string MetadataNameOf(ISymbol s) => s switch
    {
        INamedTypeSymbol t => TypeMetadataName(t),
        { ContainingType: { } containing } => TypeMetadataName(containing) + "." + s.Name,
        _ => s.ToDisplayString(),
    };

    static string TypeMetadataName(INamedTypeSymbol t) =>
        t.ContainingType is { } outer
            ? TypeMetadataName(outer) + "+" + t.MetadataName
            : t.ContainingNamespace is { IsGlobalNamespace: false } ns
                ? ns.ToDisplayString() + "." + t.MetadataName
                : t.MetadataName;

    static bool NameMatches(ISymbol s, string requested)
    {
        // A simple name was matched exactly by the declaration search already.
        if (!requested.Contains('.') && !requested.Contains('+') && !requested.Contains('`') && !requested.Contains('<'))
            return true;
        var meta = MetadataNameOf(s);
        return meta == requested
               || meta.EndsWith("." + requested, StringComparison.Ordinal)
               || meta.EndsWith("+" + requested, StringComparison.Ordinal)
               || s.ToDisplayString() == requested;
    }

    /// <summary>The bare identifier of a possibly qualified name: 'Ns.Outer+Repo`2.Add' -> 'Add',
    /// 'Ns.Repo`2' -> 'Repo', 'Repo&lt;T&gt;' -> 'Repo'.</summary>
    static string SimpleNameOf(string name)
    {
        var s = name.Trim();
        var generic = s.IndexOf('<');
        if (generic >= 0)
            s = s[..generic];
        var cut = s.LastIndexOfAny(['.', '+']);
        if (cut >= 0)
            s = s[(cut + 1)..];
        var arity = s.IndexOf('`');
        return arity >= 0 ? s[..arity] : s;
    }

    static string FirstSourcePath(ISymbol s) =>
        s.Locations.FirstOrDefault(l => l.IsInSource)?.SourceTree?.FilePath ?? "";

    static int FirstSourceLine(ISymbol s) =>
        s.Locations.FirstOrDefault(l => l.IsInSource) is { } loc ? loc.GetLineSpan().StartLinePosition.Line + 1 : 0;

    static string WithoutWhitespace(string s) => string.Concat(s.Where(c => !char.IsWhiteSpace(c)));

    static SymbolCandidate[]? OrNull(IReadOnlyList<SymbolCandidate> candidates) =>
        candidates.Count > 0 ? [.. candidates] : null;

    // =========================================================================
    // Paging and shaping
    // =========================================================================

    static (IReadOnlyList<T> Page, bool Truncated, int? NextOffset) Page<T>(IReadOnlyList<T> items, int offset, int limit)
    {
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, MaxLimit);
        var page = items.Skip(offset).Take(limit).ToList();
        var end = offset + page.Count;
        return end < items.Count ? (page, true, end) : (page, false, null);
    }

    static ReferenceFile[] GroupByFile(IReadOnlyList<(string FilePath, ReferenceHit Hit)> hits)
    {
        var files = new List<ReferenceFile>();
        foreach (var group in hits.GroupBy(h => h.FilePath, StringComparer.OrdinalIgnoreCase))
            files.Add(new ReferenceFile(group.Key, [.. group.Select(h => h.Hit)]));
        return [.. files];
    }

    // =========================================================================
    // Shared helpers
    // =========================================================================

    async Task<List<(string FilePath, ReferenceHit Hit)>> CollectReferenceHitsAsync(
        ISymbol symbol, bool withContext, CancellationToken ct)
    {
        var found = await SymbolFinder.FindReferencesAsync(symbol, session.Solution, ct);

        var byDoc = found
            .SelectMany(r => r.Locations)
            .Where(l => l.Location.SourceTree is not null)
            .GroupBy(l => l.Document.Id);

        var hits = new List<(string FilePath, ReferenceHit Hit)>();
        var seen = new HashSet<(string, int, int)>();
        foreach (var group in byDoc)
        {
            var doc = group.First().Document;
            var text = await doc.GetTextAsync(ct);
            var root = withContext ? await doc.GetSyntaxRootAsync(ct) : null;
            var path = doc.FilePath ?? "";

            foreach (var loc in group)
            {
                var span = loc.Location.GetLineSpan();
                var line = span.StartLinePosition.Line + 1;
                var column = span.StartLinePosition.Character + 1;
                if (!seen.Add((path, line, column)))
                    continue;

                var lineIdx = span.StartLinePosition.Line;
                var preview = lineIdx < text.Lines.Count ? Shorten(text.Lines[lineIdx].ToString().Trim()) : "";
                var context = withContext
                    ? ContextWalker.Describe(root?.FindToken(loc.Location.SourceSpan.Start).Parent)
                    : null;

                hits.Add((path, new ReferenceHit(line, column, preview, context)));
            }
        }

        return hits
            .OrderBy(h => h.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(h => h.Hit.Line)
            .ThenBy(h => h.Hit.Column)
            .ToList();
    }

    static string Shorten(string s) => s.Length <= MaxPreviewChars ? s : s[..MaxPreviewChars] + "...";

    static (string FilePath, int Line) ResolveLocation(Location? loc)
    {
        if (loc is null || loc.SourceTree is null)
            return ("", 0);
        var span = loc.GetLineSpan();
        return (loc.SourceTree.FilePath ?? "", span.StartLinePosition.Line + 1);
    }

    static string PreviewLine(Location location, CancellationToken ct)
    {
        var tree = location.SourceTree;
        if (tree is null)
            return "";
        var text = tree.GetText(ct);
        var lineIdx = location.GetLineSpan().StartLinePosition.Line;
        return lineIdx >= 0 && lineIdx < text.Lines.Count
            ? text.Lines[lineIdx].ToString().Trim()
            : "";
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

    static readonly SymbolDisplayFormat TypeHeaderNameFormat = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
        genericsOptions:
            SymbolDisplayGenericsOptions.IncludeTypeParameters |
            SymbolDisplayGenericsOptions.IncludeTypeConstraints |
            SymbolDisplayGenericsOptions.IncludeVariance,
        miscellaneousOptions:
            SymbolDisplayMiscellaneousOptions.UseSpecialTypes |
            SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier |
            SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    static string BuildTypeHeader(INamedTypeSymbol t)
    {
        var parts = new List<string>(6);

        var access = t.DeclaredAccessibility switch
        {
            Accessibility.Public => "public",
            Accessibility.Internal => "internal",
            Accessibility.Protected => "protected",
            Accessibility.Private => "private",
            Accessibility.ProtectedOrInternal => "protected internal",
            Accessibility.ProtectedAndInternal => "private protected",
            _ => null
        };
        if (access is not null)
            parts.Add(access);

        if (t.IsStatic)
        {
            parts.Add("static");
        }
        else
        {
            if (t.IsAbstract && t.TypeKind != TypeKind.Interface)
                parts.Add("abstract");
            if (t.IsSealed && t.TypeKind == TypeKind.Class)
                parts.Add("sealed");
        }

        if (t.TypeKind == TypeKind.Struct)
        {
            if (t.IsReadOnly)
                parts.Add("readonly");
            if (t.IsRefLikeType)
                parts.Add("ref");
        }

        parts.Add(t.TypeKind switch
        {
            TypeKind.Class => t.IsRecord ? "record" : "class",
            TypeKind.Struct => t.IsRecord ? "record struct" : "struct",
            TypeKind.Interface => "interface",
            TypeKind.Enum => "enum",
            TypeKind.Delegate => "delegate",
            _ => t.TypeKind.ToString().ToLowerInvariant()
        });

        parts.Add(t.ToDisplayString(TypeHeaderNameFormat));

        return string.Join(' ', parts);
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
            case IndexerDeclarationSyntax ix:
                if (ix.ExpressionBody?.Expression is { } ixe)
                    yield return ixe;
                else if (ix.AccessorList is { } ixal)
                {
                    foreach (var accessor in ixal.Accessors)
                    {
                        if (accessor.Body is { } iab)
                            yield return iab;
                        else if (accessor.ExpressionBody?.Expression is { } iax)
                            yield return iax;
                    }
                }
                break;
            case OperatorDeclarationSyntax op:
                if (op.Body is { } opb)
                    yield return opb;
                else if (op.ExpressionBody?.Expression is { } ope)
                    yield return ope;
                break;
            case ConversionOperatorDeclarationSyntax cv:
                if (cv.Body is { } cvb)
                    yield return cvb;
                else if (cv.ExpressionBody?.Expression is { } cve)
                    yield return cve;
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

            var signature = symbol.ToFullSignature();
            // Context only adds information when the signature does not already spell it out.
            var usefulContext = context.Length == 0 || signature.Contains(context, StringComparison.Ordinal) ? null : context;
            result.Add((new SymbolHit(signature, filePath, line, column, usefulContext), filePath, line, column));
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
