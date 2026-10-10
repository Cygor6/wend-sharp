# WendSharp tools: detailed reference

## Shared contract

- **Check `Error` first.** It is set on every failure, with a message written for you to act on.
- **Candidates.** On ambiguity (`Kind: "Ambiguous"`) and on many "not found" results, `Candidates[]`
  lists the symbols you could have meant. Each has `Name`, `Kind`, `ParameterTypes` (methods),
  `Project`, `FilePath`, `Line`. Re-call with `Name` (and `ParameterTypes` for a method). `Name` is
  a metadata name every name argument accepts: `Namespace.Type`, `Namespace.Outer+Inner`,
  `Namespace.Generic`2`, or `Namespace.Type.Member`.
- **`projectName` is optional** on every tool. Without it WendSharp searches source declarations
  across the solution and uses the declaring project; a type from a referenced assembly (framework,
  NuGet) is resolved by its metadata name. Pass `projectName` only to choose between candidates
  from different projects.
- **Paging.** List results take `limit` and `offset`. `Total` is the full count, `Truncated: true`
  means more results exist, and `NextOffset` is the `offset` for the next call. Responses are sized
  to stay well under client limits (Codex cuts tool output above about 10k tokens out of the
  middle; Claude Code moves text over 50,000 characters to a file).
- **Disk sync.** Every call first re-reads documents whose files changed on disk and drops deleted
  ones. Only files you created, moved, or renamed need `Refresh`.
- Optional fields are omitted when empty (`Candidates`, `NextOffset`, `Context`, `Source`, ...).

## ExploreCode: map before you dive

Returns a file's namespaces, types, and member **signatures with bodies stripped**. First move on
any unfamiliar or large file: costs a fraction of the tokens of read_file.

- `fileNameOrPath`: prefer an **absolute** path. A relative path with a directory component
  resolves against solution/project roots; a bare file name matches by name; both can be
  ambiguous when the same name exists in several places (the error lists the matches).
- Returns `FilePath`, `Blueprint`, `Truncated`. `Truncated: true` means the outline was cut at
  about 30,000 characters: use `DescribeSymbol` on the types you still need.

## DescribeSymbol: the contract surface, types resolved by the compiler

A type's public/protected members with **fully-resolved types (never `var`)**, base types,
interfaces, and the **exact `using` directives** needed to write code against it.

- Use when authoring an interface, adapter, DI registration, or test double.
- `typeName`: metadata name (`SampleApp.Calculator`, `Ns.Repository`2`) or simple name
  (`Calculator`). Framework and NuGet types work by metadata name (`System.Text.StringBuilder`).
- `limit` (default 150) / `offset` page `Members`.
- Returns `FullName`, `Declaration`, `FilePath` (the type's file), `BaseTypes`, `RequiredUsings`,
  `Members[]` (`Kind`, `Signature`, `FilePath`, `Line`). A member's `FilePath` is omitted when it
  is the type's own `FilePath` (it is set for members of other partial declarations).
- Surfaces record positional properties and C# 14 extension members; skips synthesized noise
  (`Equals`/`GetHashCode`/`Deconstruct`/`EqualityContract`, accessor methods).

## AnalyzeMember: before you extract or split a method

Reports a method's internal vs external dependencies, data flow (variables read/written), and
its callers.

- Use before extracting/splitting: dependencies tell you what must move or become a parameter;
  callers tell you what breaks.
- **Overloads:** pass `parameterTypes` (comma-separated, e.g. `"int, string"`; empty string for
  the parameterless overload), exactly as `Candidates[].ParameterTypes` lists them.
- `includeSource` (default `false`): also return the member's `Source`. Without it, read
  `FilePath` from `LineStart` to `LineEnd` if you need the text.
- `maxCallers` (default 50): `Callers` is capped; `CallerCount` is the full count and
  `CallersTruncated` says whether the cap applied. Use `FindReferences` for the complete list.
- Returns `FilePath`, `LineStart`/`LineEnd`, `InternalDependencies[]`, `ExternalDependencies[]`,
  `DataFlowsIn[]`, `DataFlowsOut[]`, `Captured[]`, `CallerCount`, `Callers[]` (grouped by file,
  each hit with `Line`, `Column`, `Preview`, `Context`).

## FindReferences: semantic, solution-wide

Roslyn resolves the real symbol, so results include references through overrides and interface
implementations and exclude false hits in comments, strings, and unrelated identifiers.

- Run before changing any call site or signature.
- `symbolName`: metadata name (`SampleApp.Calculator`, `SampleApp.Calculator.Add`) or a simple
  name (`Add`).
- **Overloads:** pass `parameterTypes` as listed in `Candidates[].ParameterTypes`. Methods with
  the same name on *different* types are not overloads: they come back as `Ambiguous` candidates
  with their type in `Name`.
- `limit` (default 100), `offset`, and `detail` (default `false`; adds each hit's enclosing member
  as `Context`).
- `Kind` is the symbol kind on success (`Method`, `NamedType`, ...), or `Ambiguous`, `NotFound`,
  `Error`. `NotFound` often carries similarly named `Candidates`.
- Returns `Symbol`, `Kind`, `Total`, `Files[]` (`FilePath`, `Hits[]` with `Line`, `Column`,
  `Preview`, optional `Context`), `Truncated`, `NextOffset`.

## GetImplementations: the implementation surface

Every concrete type implementing an interface or deriving from a class, across multi-level
inheritance, generic constraints, and other files/projects.

- Run **before** changing an interface or abstract/base class; every result may need matching
  changes.
- `Total: 0` with no `Error` means the type exists and nothing implements it yet.
- `limit` (default 100) / `offset`.
- Returns `Symbol`, `Kind` (type kind, or `Ambiguous`/`NotFound`/`Error`), `Total`,
  `Implementations[]` (`Signature`, `FilePath`, `Line`, `Column`, and `Context` only when the
  signature does not already contain it), `Closed`, `Truncated`, `NextOffset`.
- **`Closed.IsExhaustive` is the exhaustiveness proof.** `Implementations[]` is what exists in the
  loaded solution; it is not a guarantee that nothing else can implement the type, because another
  assembly may add one. For a C# 15 `closed` hierarchy or union the compiler knows the complete set,
  and `Closed.IsExhaustive == true` means an edit covering every implementation is provably total.
  For every ordinary open type it is `false` (with `IsClosed`/`IsUnion` false and
  `DirectDerivedTypes` empty), which is the honest answer rather than a silent assumption.

## GetOverrides: what a call can dispatch to

Every member that can run in place of the given one: for a virtual/abstract member, every
override across the whole chain (not just direct subclasses); for an **interface member**, every
implementation, implicit or explicit, plus the overrides of implementations that are themselves
virtual.

- The other half of `FindReferences`: that finds call sites, this finds alternate
  implementations. Run **before** changing a virtual, abstract, or interface member's signature or
  contract.
- `typeName` must be the type that **declares** the member. Asking a derived type or interface for
  an inherited member returns `MemberNotFound` with the declaring member in `Candidates`.
- **Overloads:** pass `parameterTypes` as listed in `Candidates[].ParameterTypes`.
- `limit` (default 100) / `offset`.
- `Kind`: `Abstract`/`Virtual`/`Override`/`InterfaceMember` on success; `NotOverridable` (not
  virtual/abstract/override and not an implementable interface member); `Ambiguous` (type name or
  overload); `TypeNotFound`; `MemberNotFound`; `Error`.
- Returns `Symbol`, `Kind`, `Total`, `Overrides[]` (same shape as implementations), `Truncated`,
  `NextOffset`.

## PlanRename: never hand-rename across files

Computes a compiler-verified rename plan. It does **not** apply anything itself.

- Locate the target by **either** `filePath` + `line` + `column` (1-based, inside the name; never
  ambiguous) **or** `fullyQualifiedSymbolName` (plus `parameterTypes` for an overloaded method).
- Flags (all default `false`): `renameOverloads`, `renameInComments`, `renameInStrings`,
  `renameFile`. Comment/string renames are **textual** (plain-text match): review those lines.
- Returns:
  - `HasConflicts`: if `true`, do **not** apply; report the collision and choose another name.
  - `Next`: what to do with this plan, in order. Follow it.
  - `EditCount`, `FileCount`.
  - `Files[]`, one per changed file:
    - `Lines[]`: each changed line of the original file as `Line`, `Before`, `After` (whole line,
      trimmed of surrounding whitespace) and `Location` (`Declaration`, `Reference`, `Nameof`,
      `Cref`, `Comment`, `String`; comma-joined when a line has several). Ordered bottom-up. Use
      `Before`/`After` as old/new text in your edit tool; if the same `Before` occurs more than
      once in the file, use `Line` to pick the occurrence.
    - `ReplaceAll`: `true` when replacing every occurrence of `OldName` with `NewName` in this
      file gives exactly the planned text (checked against the renamed file, not guessed).
    - `Edits[]` (only with `detail: true`, or when a line form is impossible): offset-level edits
      in descending `StartOffset` order. `StartOffset`/`Length` are character offsets as in .NET
      string indexing (UTF-16 code units) counted from the first character after any BOM, **not**
      byte offsets. `OldText`/`NewText` are the minimal changed text (often only part of the
      identifier), so they are not usable for text matching.
  - `Conflicts[]`: new compile errors the rename introduces (name collisions and others).
  - `Cascades[]`: related symbols renamed along, tagged `Override`, `InterfaceMember`,
    `ExplicitInterfaceImpl`, `Overload`, or `PartialDeclaration`.
  - `FileRenames[]`: `OldPath` → `NewPath` pairs when `renameFile` is set. Rename those files on
    disk too, then `Refresh` with the new paths.
  - `Truncated`: `Lines` were left out for some files to bound the response. Before applying
    anything, call again with the same arguments and `filePaths` set to those files (files with
    `ReplaceAll: true` can be applied as a replace-all instead).
- `filePaths`: return `Lines`/`Edits` only for these files; the plan itself always covers all.
- Refuses metadata/external symbols (no source in the workspace): you can `DescribeSymbol` them
  but not rename them.

## Refresh: only for files you created

Edits to existing files and deletions are synced automatically on every call. `Refresh` is for
files WendSharp cannot know about yet: files you **created**, moved, or renamed.

- Pass **absolute paths**. Passing changed or deleted files too is harmless.
- Returns `Reloaded[]`, `Added[]`, `Removed[]`, `Unmapped[]`. A path in `Unmapped[]` means
  WendSharp couldn't map it to a project: typically a wrong path or a file outside the solution.

## GetDiagnostics: your truth check

Compiler and analyzer diagnostics for the files as they are on disk now. Analyzers (code-style,
custom spec rules, banned APIs) run by default and respect `.editorconfig` severity.

Parameters:

- `filePaths` (`string[]?`): absolute paths to check, e.g. the files you just edited. Their
  projects are found for you, so `projectName` can be omitted.
- `projectName` (optional): check a whole project (or restrict `filePaths` to it).
  Pass at least one of `filePaths` and `projectName`.
- `includeWarnings` (bool, default `false`): also return warnings.
- `includeAnalyzers` (bool, default `true`): run the project's Roslyn analyzers. Analyzer
  diagnostics use the same severity gate (errors always; warnings only with `includeWarnings`)
  and the same `filePaths` filter as compiler diagnostics. Set `false` only for a fast
  compiler-only check mid-edit; leave `true` for final verification of any task.
- `limit` (default 100) / `offset`: page `Diagnostics`, errors first.

Returns `Project` (comma-joined when several), `ErrorCount` and `WarningCount` (for everything
matched, not just the page), `Total`, `Diagnostics[]` (code, message, file, line, column,
severity, preview), `Truncated`, `NextOffset`.

## GetWorkspaceInfo: what was loaded

Reports the resolved solution file, its root directory, the loaded project names, and (when the
client exposes MCP roots) the open root folders. You rarely need it: other tools find projects
themselves.

- `EmptyProjects[]`: projects that loaded **without any source documents**. MSBuild could not
  evaluate them (usually a missing restore or an SDK mismatch), so every lookup in them comes back
  `NotFound`. `LoadDiagnostics[]` holds the MSBuild messages behind it.
- Returns `SolutionPath`, `RootDirectory`, `Projects[]`, `EmptyProjects[]`, `LoadDiagnostics[]`,
  `ClientRoots[]`, `ClientExposesRoots`.
