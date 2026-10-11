using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace WendSharp;

/// <summary>Where a location is for the agent, in a file it can open. <see cref="Exact"/> is false
/// for code generated from a markup file without a source position (Razor emits some code under
/// <c>#line hidden</c>, e.g. the binder behind <c>@bind</c>): <see cref="FilePath"/> is then the
/// markup file and <see cref="Line"/> is 0. <see cref="InGeneratedCode"/> marks locations that live
/// in generated code (a syntax tree carrying <c>#line</c> file mappings).</summary>
internal readonly record struct SourcePosition(
    string FilePath, int Line, int Column, int EndLine, int EndColumn, bool Exact, bool InGeneratedCode);

/// <summary>Razor (.razor/.cshtml) compiles through a source generator into documents whose paths do
/// not exist on disk. Their <c>#line</c> directives map almost every C# expression back to the markup
/// with character precision. Every location a tool reports goes through <see cref="Map"/>, so the
/// agent never sees a generated path it cannot open or edit.</summary>
internal static class SourceMap
{
    static readonly ConditionalWeakTable<SyntaxTree, StrongBox<string?>> Owners = new();

    public static SourcePosition Map(Location? location)
    {
        if (location is null || !location.IsInSource || location.SourceTree is not { } tree)
            return new("", 0, 0, 0, 0, Exact: false, InGeneratedCode: false);

        var owner = OwningFile(tree);
        var mapped = location.GetMappedLineSpan();
        if (mapped.IsValid && mapped.HasMappedPath)
            return new(Normalize(mapped.Path),
                mapped.StartLinePosition.Line + 1, mapped.StartLinePosition.Character + 1,
                mapped.EndLinePosition.Line + 1, mapped.EndLinePosition.Character + 1,
                Exact: true, InGeneratedCode: owner is not null);

        if (owner is not null)
            return new(owner, 0, 0, 0, 0, Exact: false, InGeneratedCode: true);

        var raw = location.GetLineSpan();
        return new(raw.Path ?? tree.FilePath,
            raw.StartLinePosition.Line + 1, raw.StartLinePosition.Character + 1,
            raw.EndLinePosition.Line + 1, raw.EndLinePosition.Character + 1,
            Exact: true, InGeneratedCode: false);
    }

    /// <summary>The markup file a generated tree was produced from: the most frequent mapped file,
    /// ignoring shared imports files (_Imports.razor, _ViewImports.cshtml) unless nothing else is
    /// mapped. Null for an ordinary C# file (no file mappings).</summary>
    public static string? OwningFile(SyntaxTree tree) =>
        Owners.GetValue(tree, static t => new StrongBox<string?>(ComputeOwner(t))).Value;

    static string? ComputeOwner(SyntaxTree tree)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var m in tree.GetLineMappings())
            {
                if (m.IsHidden || !m.MappedSpan.HasMappedPath)
                    continue;
                var path = Normalize(m.MappedSpan.Path);
                counts[path] = counts.TryGetValue(path, out var c) ? c + 1 : 1;
            }
        }
        catch (Exception)
        {
            return null;    // optional enrichment: never fail a tool over it
        }
        if (counts.Count == 0)
            return null;

        static bool IsSharedImports(string p) =>
            Path.GetFileName(p) is var n
            && (n.Equals("_Imports.razor", StringComparison.OrdinalIgnoreCase)
                || n.Equals("_ViewImports.cshtml", StringComparison.OrdinalIgnoreCase));

        var preferred = counts.Where(kv => !IsSharedImports(kv.Key)).ToList();
        var pool = preferred.Count > 0 ? preferred : counts.ToList();
        return pool.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).First().Key;
    }

    public static bool IsMarkupFile(string path) =>
        path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase);

    public static string Normalize(string path)
    {
        try
        { return Path.GetFullPath(path); }
        catch (Exception)
        { return path; }
    }
}

/// <summary>Text of files by path for one tool call: documents and additional documents (markup)
/// from the in-memory solution, which the session keeps in sync with disk.</summary>
internal sealed class SolutionTexts(Solution solution)
{
    readonly Dictionary<string, SourceText?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<SourceText?> GetAsync(string path, CancellationToken ct)
    {
        if (_cache.TryGetValue(path, out var cached))
            return cached;

        SourceText? text = null;
        foreach (var id in solution.GetDocumentIdsWithFilePath(path))
        {
            if (solution.GetDocument(id) is { } doc)
            { text = await doc.GetTextAsync(ct); break; }
            if (solution.GetAdditionalDocument(id) is { } additional)
            { text = await additional.GetTextAsync(ct); break; }
        }
        _cache[path] = text;
        return text;
    }

    /// <summary>The trimmed text of a 1-based line, or "" when unknown.</summary>
    public async Task<string> LineAsync(string path, int line, CancellationToken ct)
    {
        if (line <= 0 || await GetAsync(path, ct) is not { } text || line > text.Lines.Count)
            return "";
        return text.Lines[line - 1].ToString().Trim();
    }
}
