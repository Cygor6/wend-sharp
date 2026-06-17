# wendsharp tools — detailed reference

Every tool takes `projectName`. Call `GetWorkspaceInfo` first if you don't know it. On failure,
each tool sets `Error` (and `Kind` is `"Error"` where applicable) — check `Error` before using
other fields.

## ExploreCode — map before you dive
Returns a file's namespaces, types, and member **signatures with bodies stripped**. First move on
any unfamiliar or large file, instead of reading hundreds of lines — costs a fraction of the tokens.
- `fileNameOrPath`: prefer an **absolute** path. A relative path with a directory component resolves
  against the solution/project roots; a bare file name matches by name — both can be ambiguous.
- On success `Blueprint` holds the stripped structure; on failure `Blueprint` is empty and `Error`
  is set.

## DescribeSymbol — the contract surface, types resolved by the compiler
A type's public/protected members with **fully-resolved types (never `var`)**, base types,
interfaces, and the **exact `using` directives** needed to write code against it.
- Use when authoring an interface, adapter, DI registration, or test double — you get compiler truth,
  not a guessed namespace.
- `typeName`: metadata name (`SampleApp.Calculator`) or simple name (`Calculator`). A simple name
  that matches several types throws an ambiguity error — re-call with the metadata name.
- Returns `FullName`, `BaseTypes`, `RequiredUsings`, `Members[]` (kind, signature, file, line).
- Surfaces record positional properties and C# 14 extension members as part of the contract; skips
  synthesized noise (`Equals`/`GetHashCode`/`Deconstruct`/`EqualityContract`, accessor methods).

## AnalyzeMember — before you extract or split a method
Reports a method's internal vs external dependencies, the variables it reads/writes (data flow), and
its callers.
- Use before extracting/splitting: dependencies tell you what must move or become a parameter;
  callers tell you what breaks.
- **Overloads:** pass `parameterTypes` (comma-separated, e.g. `"int, string"`; empty string for the
  parameterless overload) exactly as the ambiguity error lists them. Omit only for non-overloaded
  members.
- Returns `Source`, `FilePath`, `LineStart/End`, `InternalDependencies[]`, `ExternalDependencies[]`,
  `ReadVariables[]`, `WrittenVariables[]`, `Callers[]`.

## FindReferences — semantic, solution-wide
Roslyn resolves the real symbol, so results include overrides and interface implementations and
exclude false hits in comments/strings/unrelated identifiers. Run before changing any call site or
signature.
- `symbolName`: metadata type name or simple member name.
- **`Kind == "Ambiguous"`**: a simple name matched several symbols — re-call with the fully-qualified
  metadata name. **`Kind == "NotFound"`**: nothing matched. **`Kind == "Error"`**: check `Error`.
- Returns `Total` and `References[]` (file, line, column, preview, context).

## GetImplementations — the implementation surface
Every concrete type implementing an interface or deriving from a class — across multi-level
inheritance, generic constraints, and other files/projects that grep misses. Run **before** changing
an interface or abstract/base class; each result may need matching changes.
- `Total: 0` unexpectedly? Confirm with `DescribeSymbol` that the type really is an interface / has
  abstract members.

## GetOverrides — the override chain
Every override of a virtual/abstract member across the whole chain, not just direct subclasses. The
other half of `FindReferences`: that finds call sites, this finds alternate implementations. Run
**before** changing a virtual/abstract member's signature or contract.
- `Kind == "NotOverridable"`: the member isn't virtual/abstract/override — recheck the name.
- `Kind == "Ambiguous"`: overloaded member — disambiguate via the containing type's full name.

## PlanRename — never hand-rename across files
Computes a compiler-verified rename plan and returns apply-ready edits, conflicts, and cascades. It
does **not** apply anything.
- Locate the target by **either** `filePath` + `line` + `column` (1-based; never ambiguous) **or**
  `fullyQualifiedSymbolName`.
- Flags: `renameOverloads`, `renameInComments`, `renameInStrings`, `renameFile`. Comment/string
  renames are **textual** (plain-text match) — review those edits before applying.
- Returns: `HasConflicts`, `EditCount`, `FileCount`, `Edits[]` (file + 1-based line/col + absolute
  `StartOffset`/`Length` + `OldText`/`NewText` + `Location` + `ContainingMember`), `Conflicts[]`,
  `Cascades[]` (Override / InterfaceMember / ExplicitInterfaceImpl / Overload / PartialDeclaration),
  `FileRenames[]` (`OldPath` → `NewPath`).
- **Apply by absolute offset/length, last-offset-first per file.** If `FileRenames` is non-empty,
  rename those files on disk too, then `Refresh`.
- Refuses metadata/external symbols (no source in the workspace) — you can `DescribeSymbol` them but
  not rename them.

## Refresh — mandatory after every edit
Re-reads files from disk into wendsharp's model. wendsharp is blind to your edits until you do this.
- Pass **absolute paths** of every file you changed, created, or deleted.
- Always call before any `GetDiagnostics`, `FindReferences`, or follow-up plan after an edit.
- Returns `Reloaded[]`, `Added[]`, `Removed[]`, `Unmapped[]` (paths it couldn't map to a project).

## GetDiagnostics — your truth check
Compiler errors (and warnings if asked) for the project's current in-memory state — current only if
you `Refresh`ed first.
- `includeWarnings`: add warnings.`filePaths`: scope to specific files (e.g. the ones you
  just edited).
- Returns `ErrorCount` (for the filtered set) and `Diagnostics[]` (code, message, file, line, column,
  severity).

## GetWorkspaceInfo — orient
Reports the resolved solution file, its root directory, the loaded project names, and (when the
client exposes MCP roots) the open root folders. Call when unsure of a `projectName`, or to confirm
which solution loaded.
