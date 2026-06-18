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

        // Try "Namespace.Type.Member" dotted lookup before falling back to FindDeclarationsAsync,
        // which only matches simple (unqualified) names and would find nothing for dotted input.
        var dotted = TryFindMembersByDottedName(compilation, name, sourceTreePaths);
        if (dotted is not null)
            return dotted.Count switch
            {
                0 => null,
                1 => dotted[0],
                _ => dotted.FirstOrDefault(s => s.Kind == SymbolKind.NamedType) ?? dotted[0]
            };

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

        var dotted = TryFindMembersByDottedName(compilation, name, sourceTreePaths);
        if (dotted is not null)
            return dotted.Count > 1 ? dotted : null;

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

        // Try "Namespace.Type.Member" dotted lookup before falling back to FindDeclarationsAsync,
        // which only matches simple (unqualified) names and would find nothing for dotted input.
        var dotted = TryFindMembersByDottedName(compilation, name, sourceTreePaths);
        if (dotted is not null)
        {
            var dFiltered = preferKind is null ? dotted : dotted.Where(s => s.Kind == preferKind).ToList();
            return dFiltered.Count switch
            {
                1 => new SymbolResolution(dFiltered[0], []),
                > 1 => new SymbolResolution(null, dFiltered),          // overloads — ambiguous
                _ => new SymbolResolution(null, dotted)                // none of the preferred kind
            };
        }

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

    // Returns the members of the type named by the dotted prefix of `name` (e.g. "Ns.Type.Member").
    // Returns null when `name` has no dot or the type prefix does not resolve via GetTypeByMetadataName,
    // so callers can fall through to FindDeclarationsAsync for simple names.
    private static IReadOnlyList<ISymbol>? TryFindMembersByDottedName(
        Compilation compilation, string name, HashSet<string>? sourceTreePaths)
    {
        var lastDot = name.LastIndexOf('.');
        if (lastDot < 0)
            return null;

        var typePart = name[..lastDot];
        var memberPart = name[(lastDot + 1)..];

        var containingType = compilation.GetTypeByMetadataName(typePart);
        if (containingType is null)
            return null;

        return containingType.GetMembers(memberPart)
            .Where(m => sourceTreePaths is null || m.Locations.Any(l =>
                l.IsInSource && sourceTreePaths.Contains(l.SourceTree?.FilePath ?? "")))
            .ToList();
    }

    public static ISymbol? FindMember(INamedTypeSymbol type, string memberName) =>
        type.GetMembers(memberName).FirstOrDefault();

    public static IReadOnlyList<ISymbol> FindMembers(INamedTypeSymbol type, string memberName) =>
        type.GetMembers(memberName).ToList();
}
