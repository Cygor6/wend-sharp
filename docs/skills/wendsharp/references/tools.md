# WendSharp tools — detailed reference

Every tool except `ExploreCode` takes `projectName`. Call `GetWorkspaceInfo` first if you
don't know it. On failure, each tool sets `Error` (and `Kind` is `"Error"` where applicable)
— check `Error` before using other fields.

## ExploreCode — map before you dive

Returns a file's namespaces, types, and member **signatures with bodies stripped**. First
move on any unfamiliar or large file — costs a fraction of the tokens of read_file.

- `fileNameOrPath`: prefer an **absolute** path. A relative path with a directory component
  resolves against solution/project roots; a bare file name matches by name — both can be
  ambiguous when the same name exists in multiple places.
- On success `Blueprint` holds the stripped structure. On failure `Blueprint` is empty and
  `Error` is set.

## DescribeSymbol — the contract surface, types resolved by the compiler

A type's public/protected members with **fully-resolved types (never `var`)**, base types,
interfaces, and the **exact `using` directives** needed to write code against it.

- Use when authoring an interface, adapter, DI registration, or test double.
- `typeName`: metadata name (`SampleApp.Calculator`) or simple name (`Calculator`). A simple
  name that matches several types returns an ambiguity error — re-call with the metadata name.
- Returns `FullName`, `BaseTypes`, `RequiredUsings`, `Members[]` (kind, signature, file, line).
- Surfaces record positional properties and C# 14 extension members; skips synthesized noise
  (`Equals`/`GetHashCode`/`Deconstruct`/`EqualityContract`, accessor methods).

## AnalyzeMember — before you extract or split a method

Reports a method's internal vs external dependencies, data flow (variables read/written), and
its callers.

- Use before extracting/splitting: dependencies tell you what must move or become a parameter;
  callers tell you what breaks.
- **Overloads:** pass `parameterTypes` (comma-separated, e.g. `"int, string"`; empty string for
  the parameterless overload) exactly as the ambiguity error lists them. Omit only for
  non-overloaded members.
- Returns `Source`, `FilePath`, `LineStart`/`LineEnd`, `InternalDependencies[]`,
  `ExternalDependencies[]`, `DataFlowsIn[]`, `DataFlowsOut[]`, `Captured[]`, `Callers[]`.

## FindReferences — semantic, solution-wide

Roslyn resolves the real symbol, so results include overrides and interface implementations
and exclude false hits in comments, strings, and unrelated identifiers.

- Run before changing any call site or signature.
- `symbolName`: metadata type name or simple member name.
- **Overloads:** pass `parameterTypes` (comma-separated, e.g. `"int, string"`; empty
  string for the parameterless overload) exactly as the guidance lists them. Omit only
  for non-overloaded members.
- `Kind == "Ambiguous"`: re-call with the fully-qualified metadata name; for method overloads
  pass `parameterTypes`.
- `Kind == "NotFound"`: nothing matched — check spelling or use `ExploreCode` to confirm the
  member exists.
- `Kind == "Error"`: check `Error` — for an overloaded member it carries the valid
  `parameterTypes` options.
- Returns `Total` and `References[]` (file, line, column, preview, context).

## GetImplementations — the implementation surface

Every concrete type implementing an interface or deriving from a class — across multi-level
inheritance, generic constraints, and other files/projects.

- Run **before** changing an interface or abstract/base class; every result may need matching
  changes.
- `Total: 0` unexpectedly? Confirm with `DescribeSymbol` that the type is actually an
  interface or has abstract members.
- Returns `Kind` (type kind), `Total`, `Implementations[]` (signature, file, line, column,
  context), and `Closed`.
- **`Closed.IsExhaustive` is the exhaustiveness proof.** `Implementations[]` is what exists in the
  loaded solution; it is not a guarantee that nothing else can implement the type, because another
  assembly may add one. For a C# 15 `closed` hierarchy or union the compiler knows the complete set,
  and `Closed.IsExhaustive == true` means an edit covering every implementation is provably total.
  For every ordinary open type it is `false` (with `IsClosed`/`IsUnion` false and
  `DirectDerivedTypes` empty), which is the honest answer rather than a silent assumption.

## GetOverrides — the override chain

Every override of a virtual/abstract member across the whole chain, not just direct
subclasses.

- The other half of `FindReferences`: that finds call sites, this finds alternate
  implementations. Run **before** changing a virtual/abstract member's signature or contract.
- **Overloads:** pass `parameterTypes` (comma-separated, e.g. `"int, string"`; empty string
  for the parameterless overload) exactly as the ambiguity error lists them. Omit only for
  non-overloaded members.
- `Kind == "NotOverridable"`: the member isn't virtual/abstract/override — recheck the name.
- `Kind == "Ambiguous"`: overloaded member — pass `parameterTypes` with one of the listed options.
- Returns `Kind`, `Total`, `Overrides[]` (signature, file, line, column, context).

## PlanRename — never hand-rename across files

Computes a compiler-verified rename plan and returns apply-ready edits, conflicts, and
cascades. It does **not** apply anything itself.

- Locate the target by **either** `filePath` + `line` + `column` (1-based; never ambiguous)
  **or** `fullyQualifiedSymbolName`.
- Flags: `renameOverloads`, `renameInComments`, `renameInStrings`, `renameFile`.
  - Comment/string renames are **textual** (plain-text match) — review those edits before
    applying.
- Returns:
  - `HasConflicts` — if `true`, do **not** apply; report the collision and choose a different
    name.
  - `EditCount`, `FileCount`
  - `Edits[]` — each entry has: file, 1-based line/col, absolute `StartOffset`/`Length`,
    `OldText`/`NewText`, `Location`, `ContainingMember`.
  - `Conflicts[]` — name collisions that block the rename.
  - `Cascades[]` — related symbols that will also be renamed, tagged as `Override`,
    `InterfaceMember`, `ExplicitInterfaceImpl`, `Overload`, or `PartialDeclaration`.
  - `FileRenames[]` — `OldPath` → `NewPath` pairs when `renameFile` is set.
- **Apply by absolute offset/length, last-offset-first per file.** Line/col are also provided
  for line-based editors, but offsets are unambiguous. `StartOffset`/`Length` are character
  offsets as in .NET string indexing (UTF-16 code units), counted from the first character
  after any BOM — **not** byte offsets. Apply edits with text/string APIs, or convert offsets
  if writing bytes (e.g. on UTF-8 files with non-ASCII content).
- If `FileRenames` is non-empty, rename those files on disk too, then `Refresh` with the new
  paths.
- Refuses metadata/external symbols (no source in the workspace) — you can `DescribeSymbol`
  them but not rename them.

## Refresh — mandatory after every edit

Re-reads files from disk into WendSharp's model. WendSharp is blind to your changes until
you call this.

- Pass **absolute paths** of every file you changed, created, or deleted.
- Always call before any `GetDiagnostics`, `FindReferences`, or follow-up plan after an edit.
- Returns `Reloaded[]`, `Added[]`, `Removed[]`, `Unmapped[]`. A path in `Unmapped[]` means
  WendSharp couldn't map it to a project — typically a wrong path or a file outside the
  solution.

## GetDiagnostics — your truth check

Compiler and analyzer diagnostics for the project's current in-memory state. Only current if
you `Refresh`ed first. Analyzers (code-style, custom spec rules, banned APIs) run by default
and respect `.editorconfig` severity.

Parameters (after `projectName`):

- `includeWarnings` (bool, default `false`): also return warnings.
- `includeAnalyzers` (bool, default `true`): run the project's Roslyn analyzers. Analyzer
  diagnostics use the same severity gate (errors always; warnings only with `includeWarnings`)
  and the same `filePaths` filter as compiler diagnostics. Set `false` only for a fast
  compiler-only check mid-edit when the analyzer pass is measurably slow; leave `true` for
  final verification of any task.
- `filePaths` (`string[]?`, omit for whole project): scope to specific absolute paths
  (e.g. the files you just edited).

Returns `ErrorCount` (for the filtered set) and `Diagnostics[]` (code, message, file, line,
column, severity).

## GetWorkspaceInfo — orient

Reports the resolved solution file, its root directory, the loaded project names, and (when
the client exposes MCP roots) the open root folders.

- Call when unsure of `projectName`, or to confirm which solution is loaded.
- Returns `SolutionPath`, `RootDirectory`, `Projects[]`, `Roots[]`.
