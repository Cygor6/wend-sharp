namespace WendSharp;

// Nullable fields are omitted from the wire when null (the MCP SDK serializes with
// JsonIgnoreCondition.WhenWritingNull), so optional detail costs nothing when it is not requested.

/// <summary>A symbol the request could have meant. To pick it, re-call with the ambiguous argument
/// set to <see cref="Name"/>, plus <see cref="ParameterTypes"/> (methods) and <see cref="Project"/>.
/// <see cref="Name"/> is a metadata name every name argument accepts: <c>Namespace.Type</c>,
/// <c>Namespace.Outer+Inner</c>, <c>Namespace.Generic`2</c>, or <c>Namespace.Type.Member</c>.</summary>
public readonly record struct SymbolCandidate(
    string Name,
    string Kind,
    string? ParameterTypes,
    string Project,
    string FilePath,
    int Line);

public readonly record struct ExploreResult(
    string FilePath, string Blueprint, bool Truncated = false, string? Error = null);

/// <summary>One reference inside a file. <see cref="Context"/> (the enclosing member) is only
/// returned when the caller asks for detail.</summary>
public readonly record struct ReferenceHit(int Line, int Column, string Preview, string? Context = null);

public readonly record struct ReferenceFile(string FilePath, ReferenceHit[] Hits);

public readonly record struct FindReferencesResult(
    string Symbol, string Kind, int Total, ReferenceFile[] Files,
    bool Truncated = false, int? NextOffset = null,
    SymbolCandidate[]? Candidates = null, string? Error = null);

public readonly record struct DiagnosticDto(
    string Code, string Message, string FilePath, int Line, int Column, string Severity, string Preview);

public readonly record struct DiagnosticsResult(
    string Project, int ErrorCount, int WarningCount, int Total, DiagnosticDto[] Diagnostics,
    bool Truncated = false, int? NextOffset = null, string? Error = null);

public readonly record struct RefreshResult(
    string[] Reloaded, string[] Added, string[] Removed, string[] Unmapped, string? Error = null);

// --- GetWorkspaceInfo: what WendSharp resolved, and which roots the client exposes ---

/// <summary>One workspace root folder. <see cref="Name"/> is the client-supplied label (or the
/// folder name when the client gives none); <see cref="Path"/> is the absolute directory.</summary>
public readonly record struct WorkspaceRoot(string Name, string Path);

/// <summary><see cref="EmptyProjects"/> lists loaded projects with no source documents, which
/// almost always means MSBuild failed to evaluate them (missing restore, SDK mismatch). Every
/// lookup in such a project comes back NotFound. <see cref="LoadDiagnostics"/> carries the
/// MSBuild messages behind it.</summary>
public readonly record struct WorkspaceInfoResult(
    string SolutionPath,
    string RootDirectory,
    string[] Projects,
    string[] EmptyProjects,
    string[] LoadDiagnostics,
    WorkspaceRoot[] ClientRoots,
    bool ClientExposesRoots,
    string? Error = null);

/// <summary><see cref="FilePath"/> is null when the member is declared in the type's own
/// file (<see cref="SymbolDescription.FilePath"/>).</summary>
public readonly record struct MemberSignature(string Kind, string Signature, string? FilePath, int Line);

public readonly record struct SymbolDescription(
    string FullName, string Declaration, string FilePath,
    string[] BaseTypes, string[] RequiredUsings, MemberSignature[] Members,
    bool Truncated = false, int? NextOffset = null,
    SymbolCandidate[]? Candidates = null, string? Error = null);

public readonly record struct MemberAnalysis(
    string? Source, string FilePath, int LineStart, int LineEnd,
    string[] InternalDependencies, string[] ExternalDependencies,
    string[] DataFlowsIn, string[] DataFlowsOut, string[] Captured,
    int CallerCount, ReferenceFile[] Callers, bool CallersTruncated = false,
    SymbolCandidate[]? Candidates = null, string? Error = null);

public readonly record struct RenameScope(
    bool RenameOverloads,
    bool RenameInComments,
    bool RenameInStrings,
    bool RenameFile);

public readonly record struct RenameEdit(
    string FilePath,
    int StartLine, int StartColumn,   // 1-based, positions in the ORIGINAL file
    int EndLine, int EndColumn,       // 1-based, exclusive end
    int StartOffset, int Length,      // absolute char offset + length in ORIGINAL file (UTF-16 code units, after any BOM, NOT byte offsets)
    string OldText,                   // the minimal changed text, often only part of the identifier
    string NewText,
    string Location,                  // Declaration | Reference | Nameof | Cref | Comment | String
    string ContainingMember);         // namespace.type.member enclosing the edit

/// <summary>One changed line of the ORIGINAL file. <see cref="Before"/> and <see cref="After"/>
/// are the whole line, trimmed of leading and trailing whitespace, so a text-matching edit tool
/// can use them directly as old and new text. <see cref="Location"/> lists the kinds of the edits
/// on the line (Declaration, Reference, Nameof, Cref, Comment, String).</summary>
public readonly record struct RenameLine(int Line, string Before, string After, string Location);

/// <summary>All changes for one file. <see cref="ReplaceAll"/> is true when replacing every
/// occurrence of the old name with the new name in this file produces exactly the planned text.
/// <see cref="Lines"/> are ordered bottom-up; null when left out for size (see
/// <see cref="PlanRenameResult.Truncated"/>) or when the file was filtered out.
/// <see cref="Edits"/> (offset-level, descending StartOffset) is only returned with detail.</summary>
public sealed record RenameFile(
    string FilePath,
    int EditCount,
    bool ReplaceAll,
    IReadOnlyList<RenameLine>? Lines,
    IReadOnlyList<RenameEdit>? Edits);

public readonly record struct RenameConflict(
    string Severity,                  // "Error" | "Warning"
    string Id,                        // e.g. "CS0102"
    string Message,
    string FilePath,
    int Line, int Column,
    string Preview);                  // trimmed source line at the location

public readonly record struct CascadeTarget(
    string Symbol,
    string SymbolKind,
    string Reason,                    // Override | InterfaceMember | ExplicitInterfaceImpl | Overload | PartialDeclaration
    string FilePath, int Line);

public readonly record struct FileRename(string OldPath, string NewPath);

/// <summary><see cref="Context"/> is null when it would only repeat <see cref="Signature"/>.</summary>
public readonly record struct SymbolHit(
    string Signature, string FilePath, int Line, int Column, string? Context = null);

/// <summary>Whether the compiler can prove a type's derived set is complete (C# 15 <c>closed</c>
/// hierarchies and unions). For an ordinary open type everything is false/empty: any assembly may
/// add a derived type, so the implementation list is what exists in this solution, not what can
/// exist. <c>IsExhaustive</c> is also false for a closed generic with an unspeakable derived type.
/// <c>DirectDerivedTypes</c> is direct, not transitive.</summary>
public readonly record struct ClosedHierarchyInfo(
    bool IsClosed,
    bool IsUnion,
    bool IsExhaustive,
    string[] DirectDerivedTypes)
{
    public static ClosedHierarchyInfo Open => new(false, false, false, []);
}

public readonly record struct GetImplementationsResult(
    string Symbol, string Kind, int Total, SymbolHit[] Implementations,
    ClosedHierarchyInfo Closed, bool Truncated = false, int? NextOffset = null,
    SymbolCandidate[]? Candidates = null, string? Error = null);

public readonly record struct GetOverridesResult(
    string Symbol, string Kind, int Total, SymbolHit[] Overrides,
    bool Truncated = false, int? NextOffset = null,
    SymbolCandidate[]? Candidates = null, string? Error = null);

public sealed record PlanRenameResult(
    string Symbol,            // fully-qualified display string of the target
    string SymbolKind,        // "Method", "Property", "Field", "NamedType", ...
    string OldName,
    string NewName,
    bool NewNameValid,        // valid C# identifier and not a bare keyword
    bool HasConflicts,        // true if any Conflict has Severity == "Error"
    int EditCount,
    int FileCount,
    RenameScope Scope,
    IReadOnlyList<RenameFile> Files,
    IReadOnlyList<RenameConflict> Conflicts,
    IReadOnlyList<CascadeTarget> Cascades,
    IReadOnlyList<FileRename> FileRenames,
    bool Truncated,           // Lines/Edits were left out for some files to bound the response size
    string? Next,             // what to do with this plan, in order
    IReadOnlyList<SymbolCandidate>? Candidates,
    string? Error             // non-null only when the plan could not be computed
);

/// <summary>The request named more than one symbol. Tools map this to Kind "Ambiguous" (where
/// the result has a Kind), Error set to the message, and Candidates.</summary>
public sealed class AmbiguousSymbolException(string message, IReadOnlyList<SymbolCandidate> candidates)
    : ArgumentException(message)
{
    public IReadOnlyList<SymbolCandidate> Candidates { get; } = candidates;
}

/// <summary>Nothing matched. <see cref="Suggestions"/> holds similarly named declarations, which
/// tools return as Candidates.</summary>
public sealed class SymbolNotFoundException(string message, IReadOnlyList<SymbolCandidate> suggestions)
    : ArgumentException(message)
{
    public IReadOnlyList<SymbolCandidate> Suggestions { get; } = suggestions;
}
