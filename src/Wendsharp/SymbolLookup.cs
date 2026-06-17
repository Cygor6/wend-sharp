using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;

namespace WendSharp;

public static class SymbolLookup
{
    public static async Task<ISymbol?> FindByNameAsync(
        Compilation compilation, Project project, string name, HashSet<string>? sourceTreePaths, CancellationToken ct)
    {
        var type = compilation.GetTypeByMetadataName(name);
        if (type is not null)
            return type;

        var candidates = await FindCandidatesAsync(project, name, sourceTreePaths, ct);
        return candidates.Count switch
        {
            0 => null,
            1 => candidates[0],
            // Ambiguous: prefer the first type declaration (types are the common case for
            // DescribeSymbol/PlanRename). Callers that need full ambiguity detection should
            // use FindCandidatesAsync directly.
            _ => candidates.FirstOrDefault(s => s.Kind == SymbolKind.NamedType) ?? candidates[0]
        };
    }

    public static async Task<IReadOnlyList<ISymbol>?> FindAmbiguousAsync(
        Compilation compilation, Project project, string name, HashSet<string>? sourceTreePaths, CancellationToken ct)
    {
        if (compilation.GetTypeByMetadataName(name) is not null)
            return null;

        var candidates = await FindCandidatesAsync(project, name, sourceTreePaths, ct);
        return candidates.Count > 1 ? candidates : null;
    }

    public readonly record struct SymbolResolution(ISymbol? Symbol, IReadOnlyList<ISymbol> Ambiguous);


    public static async Task<SymbolResolution> ResolveAsync(
        Compilation compilation, Project project, string name, SymbolKind? preferKind,
        HashSet<string>? sourceTreePaths, CancellationToken ct)
    {
        if (compilation.GetTypeByMetadataName(name) is { } meta &&
            (preferKind is null || meta.Kind == preferKind))
            return new SymbolResolution(meta, []);

        var candidates = await FindCandidatesAsync(project, name, sourceTreePaths, ct);
        var filtered = preferKind is null
            ? candidates
            : candidates.Where(s => s.Kind == preferKind).ToList();

        return filtered.Count switch
        {
            1 => new SymbolResolution(filtered[0], []),
            > 1 => new SymbolResolution(null, filtered),              // ambiguous
            _ => new SymbolResolution(null, candidates)               // none of the preferred kind (may still have other-kind matches)
        };
    }

    public static async Task<IReadOnlyList<ISymbol>> FindCandidatesAsync(
        Project project, string name, HashSet<string>? sourceTreePaths, CancellationToken ct)
    {
        sourceTreePaths ??= new HashSet<string>(
            (await Task.WhenAll(project.Documents.Select(d => d.GetSyntaxTreeAsync(ct))))
                .Where(t => t is not null).Select(t => t!.FilePath),
            StringComparer.OrdinalIgnoreCase);

        var decls = await SymbolFinder.FindDeclarationsAsync(project, name, ignoreCase: false, ct);

        return decls
            .Where(s => s.Locations.Any(loc => loc.IsInSource
                && sourceTreePaths.Contains(loc.SourceTree?.FilePath ?? "")))
            .Distinct(SymbolEqualityComparer.Default)
            .ToList();
    }

    public static async Task<ISymbol?> FindAtPositionAsync(
        Document doc, int line, int column, Workspace workspace, CancellationToken ct)
    {
        var model = await doc.GetSemanticModelAsync(ct)
                    ?? throw new InvalidOperationException("No semantic model for document.");
        var sourceText = await doc.GetTextAsync(ct);
        var linePosition = new LinePosition(line - 1, column - 1);  // convert to 0-based
        var position = sourceText.Lines.GetPosition(linePosition);
        return await SymbolFinder.FindSymbolAtPositionAsync(model, position, workspace, ct);
    }

    public static ISymbol? FindMember(INamedTypeSymbol type, string memberName) =>
        type.GetMembers(memberName).FirstOrDefault();

    public static IReadOnlyList<ISymbol> FindMembers(INamedTypeSymbol type, string memberName) =>
        type.GetMembers(memberName).ToList();
}
