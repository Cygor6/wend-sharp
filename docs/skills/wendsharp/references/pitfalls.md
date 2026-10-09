# WendSharp pitfalls: the failures that actually happen

Format: **Symptom → Cause → Fix.**

---

## A file you created is "not found"

**Symptom:** `ExploreCode`, `GetDiagnostics`, or `PlanRename` says a file you just created is not
in the solution, or a new class is missing from results.
**Cause:** WendSharp picks up edits to existing files and deletions automatically, but it cannot
know which project a **new** file belongs to until you tell it.
**Fix:** Call `Refresh` with the absolute path of every file you created, moved, or renamed.
Check `Unmapped[]`: a path there means WendSharp couldn't map it to a project (typically a wrong
path or a file outside the solution).

---

## Acting on a truncated list

**Symptom:** You update the call sites from `FindReferences` and the build still breaks at
sites you never saw.
**Cause:** The result was paged. `Truncated: true` means `Total` is larger than what came back.
**Fix:** While `Truncated` is true, re-call with `offset = NextOffset` and collect every page
before editing. The same holds for `GetImplementations`, `GetOverrides`, `GetDiagnostics`, and
`DescribeSymbol` members.

---

## Applying a rename plan by offsets

**Symptom:** After applying a `PlanRename`, edits land at wrong positions, the file's encoding or
BOM changes, or the file is corrupted.
**Cause:** Offsets index the **original** file in UTF-16 code units; applying them front-to-back,
as bytes, or through a shell that re-encodes the file breaks them.
**Fix:** Apply `Files[].Lines[]` with your normal text edit tool (`Before` → `After`), or a
replace-all where `ReplaceAll` is true. Use `detail: true` offsets only from code that edits
.NET strings, in descending `StartOffset` order per file.

---

## Applying part of a truncated rename plan

**Symptom:** The rename compiles in some files and not others.
**Cause:** `PlanRename` returned `Truncated: true` and only the files that had `Lines` were
applied.
**Fix:** Before applying anything, call `PlanRename` again with the same arguments and
`filePaths` set to the files whose `Lines` is missing (files with `ReplaceAll: true` can be
applied as a replace-all instead).

---

## Pushing through a conflict

**Symptom:** You apply a rename, then chase a cascade of compile errors.
**Cause:** `HasConflicts` was `true`: the rename introduces a name collision (e.g. CS0102).
**Fix:** If `HasConflicts` is `true`, do **not** apply. Report the collision and choose a
different name.

---

## Treating `Ambiguous` as a dead end

**Symptom:** A tool returns `Kind: "Ambiguous"` and you stop, or you guess a name.
**Cause:** A simple name matched several symbols (types in different namespaces, the same member
name on different types), or a method name matched several overloads.
**Fix:** It's an instruction, not an error. Pick one of `Candidates[]` and re-call with its
`Name` (and its `ParameterTypes` for a method; and `Project` as `projectName` if you pass one).

---

## NotFound for a symbol you can see in the code

**Symptom:** Every lookup in one project returns `NotFound`, even for types you can open.
**Cause:** That project loaded without source documents: MSBuild could not evaluate it (usually
a missing restore, or an SDK/global.json mismatch).
**Fix:** `GetWorkspaceInfo` lists it in `EmptyProjects` with the MSBuild messages in
`LoadDiagnostics`. Restore or build the solution, then restart WendSharp. Do not keep guessing
names.

---

## Guessing namespaces or types

**Symptom:** Build breaks on a missing or wrong `using`, or a type you assumed.
**Cause:** You wrote a `using` or a type from memory instead of asking.
**Fix:** `DescribeSymbol` the type and copy its `RequiredUsings` and member signatures verbatim.
They are compiler-resolved (never `var`) and walk into generic arguments and array element
types.

---

## Renaming a symbol you don't own

**Symptom:** `PlanRename` refuses with a metadata/external message.
**Cause:** The symbol is defined in a NuGet or framework assembly: no source in the workspace.
**Fix:** You can `DescribeSymbol` it to read its surface, but you can't rename what you don't
own. Rename your own wrapper or usage instead, or reconsider the change.

---

## Textual comment/string renames matching unintended text

**Symptom:** A rename with `renameInComments`/`renameInStrings` changed text you didn't intend.
**Cause:** Those flags do plain-text matching, not semantic; their lines carry `Comment` /
`String` in `Location`.
**Fix:** Review every line whose `Location` includes `Comment` or `String` before applying. Leave
the flags off unless you specifically want textual replacement in comments or strings.

---

## Wrong project name

**Symptom:** A tool errors that the project wasn't found, listing available names.
**Cause:** `projectName` was guessed.
**Fix:** Omit `projectName`: WendSharp finds the declaring project. Pass it only when choosing
between candidates from different projects, and then copy the candidate's `Project`.

---

## IndexOutOfRangeException on record synthesized members

**Symptom:** `GetOverrides` throws an `IndexOutOfRangeException` when inspecting a `record`.
**Cause:** Compiler-synthesized record members (e.g. the generated `Equals` override) have a
source `Location` but empty `DeclaringSyntaxReferences`; accessing `[0]` throws.
**Fix:** This is a known guard case in WendSharp. If you encounter it in an older build, update
to the version that falls back to the containing type's display string for these members.

---

## Constructor initializer arguments excluded from data flow

**Symptom:** `AnalyzeMember` on a constructor reports `DataFlowsIn`/`DataFlowsOut`/`Captured`
that omit variables read in a `: base(...)` or `: this(...)` initializer.
**Cause:** Constructor initializer arguments are not part of the constructor `Body`; data-flow
analysis only walks the executable bodies returned by `GetExecutableBodies`. Dependencies
(the descendant-node walk) still capture symbols referenced in the initializer.
**Fix:** Treat the `Dependencies` arrays as authoritative for "what does this member touch";
data flow covers only the block/expression body.

---

## Silencing analyzer/spec errors by editing the spec

**Symptom:** An analyzer diagnostic (banned API, project rule, code-style escalated to error)
blocks the task; the agent "fixes" it by changing `.editorconfig`, `BannedSymbols.txt`, a
ruleset, or an analyzer project, or by `#pragma warning disable` / `SuppressMessage` /
severity downgrades.
**Cause:** Treating a specification as an obstacle instead of a requirement.
**Fix (hard rule):** Analyzer errors are specifications, not obstacles. Never edit
`.editorconfig`, `BannedSymbols.txt`, ruleset files, or analyzer projects to make a diagnostic
go away: that is deleting the spec, not satisfying it. The fix always goes in the code the
diagnostic points at. If a rule seems genuinely wrong for the task, stop and ask the user; do
not suppress it (`#pragma warning disable`, `SuppressMessage`, severity downgrades) on your
own initiative.

---

## Expected analyzer warning missing from GetDiagnostics

**Symptom:** An analyzer warning "isn't showing up" in `GetDiagnostics`.
**Cause:** With default flags only Error-severity diagnostics are returned (`includeWarnings`
defaults to `false`).
**Fix:** Pass `includeWarnings: true`. Do not treat absence under default flags as a tooling
bug.
