namespace WendSharp;

public readonly record struct ExploreResult(string FilePath, string Blueprint, string? Error = null);

public readonly record struct ReferenceHit(
    string FilePath, int Line, int Column, string Preview, string Context);

public readonly record struct FindReferencesResult(
    string Symbol, string Kind, int Total, ReferenceHit[] References, string? Error = null);

public readonly record struct DiagnosticDto(
    string Code, string Message, string FilePath, int Line, int Column, string Severity);

public readonly record struct DiagnosticsResult(
    string Project, int ErrorCount, DiagnosticDto[] Diagnostics, string? Error = null);

public readonly record struct RefreshResult(
    string[] Reloaded, string[] Added, string[] Removed, string[] Unmapped, string? Error = null);

// --- GetWorkspaceInfo: what WendSharp resolved, and which roots the client exposes ---

/// <summary>One workspace root folder. <see cref="Name"/> is the client-supplied label (or the
/// folder name when the client gives none); <see cref="Path"/> is the absolute directory.</summary>
public readonly record struct WorkspaceRoot(string Name, string Path);

public readonly record struct WorkspaceInfoResult(
    string SolutionPath,
    string RootDirectory,
    string[] Projects,
    WorkspaceRoot[] ClientRoots,
    bool ClientExposesRoots,
    string? Error = null);

public readonly record struct MemberSignature(string Kind, string Signature, string FilePath, int Line);

public readonly record struct SymbolDescription(
    string FullName, string[] BaseTypes, string[] RequiredUsings, MemberSignature[] Members, string? Error = null);

public readonly record struct MemberAnalysis(
    string Source, string FilePath, int LineStart, int LineEnd,
    string[] InternalDependencies, string[] ExternalDependencies,
    string[] ReadVariables, string[] WrittenVariables, ReferenceHit[] Callers, string? Error = null);

public readonly record struct RenameScope(
    bool RenameOverloads,
    bool RenameInComments,
    bool RenameInStrings,
    bool RenameFile);

public readonly record struct RenameEdit(
    string FilePath,
    int StartLine, int StartColumn,   // 1-based, positions in the ORIGINAL file
    int EndLine, int EndColumn,       // 1-based, exclusive end
    int StartOffset, int Length,      // absolute char offset + length in ORIGINAL file
    string OldText,
    string NewText,
    string Location,                  // Declaration | Reference | Nameof | Cref | Comment | String
    string ContainingMember);         // namespace.type.member enclosing the edit

public readonly record struct RenameConflict(
    string Severity,                  // "Error" | "Warning"
    string Id,                        // e.g. "CS0102"
    string Message,
    string FilePath,
    int Line, int Column);

public readonly record struct CascadeTarget(
    string Symbol,
    string SymbolKind,
    string Reason,                    // Override | InterfaceMember | ExplicitInterfaceImpl | Overload | PartialDeclaration
    string FilePath, int Line);

public readonly record struct FileRename(string OldPath, string NewPath);


public readonly record struct SymbolHit(
    string Signature, string FilePath, int Line, int Column, string Context);

public readonly record struct GetImplementationsResult(
    string Symbol, string Kind, int Total, SymbolHit[] Implementations, string? Error = null);

public readonly record struct GetOverridesResult(
    string Symbol, string Kind, int Total, SymbolHit[] Overrides, string? Error = null);

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
    IReadOnlyList<RenameEdit> Edits,
    IReadOnlyList<RenameConflict> Conflicts,
    IReadOnlyList<CascadeTarget> Cascades,
    IReadOnlyList<FileRename> FileRenames,
    string? Error             // non-null only when the plan could not be computed
);
