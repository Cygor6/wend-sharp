# wendsharp pitfalls — the failures that actually happen

Format: **Symptom → Cause → Fix.**

## Stale results after editing
**Symptom:** A tool gives wrong or surprising answers right after you edited a file.
**Cause:** wendsharp sees disk only through `Refresh`. Until you refresh, it reasons about the old code.
**Fix:** After every edit, `Refresh` with the absolute paths of every changed, created, and deleted
file — before any `GetDiagnostics`, `FindReferences`, or follow-up plan.

## Rename edits applied front-to-back
**Symptom:** After applying a `PlanRename`, edits land at the wrong positions / the file is corrupted.
**Cause:** Each edit's `StartOffset`/`Length` indexes the **original** file. Applying an earlier edit
shifts the offsets of every later edit.
**Fix:** Group edits by file and apply them in **descending `StartOffset` order** (last edit first)
within each file. (Line/column are also provided if your edit tool is line-based, but offsets are the
unambiguous path.)

## Pushing through a conflict
**Symptom:** You apply a rename, then chase a cascade of compile errors.
**Cause:** `HasConflicts` was `true` — the rename introduces an error (e.g. a name collision, CS0102).
**Fix:** If `HasConflicts` is `true`, do **not** apply. Report the collision and choose a different
name.

## Treating `Ambiguous` as a dead end
**Symptom:** `FindReferences`/`GetOverrides`/`PlanRename` returns `Ambiguous` and you stop.
**Cause:** A simple name matched several symbols.
**Fix:** It's an instruction, not an error. Re-call with the **fully-qualified metadata name**
(`Namespace.Type` or `Namespace.Type.Member`). For overloaded members in `AnalyzeMember`, pass the
exact `parameterTypes` string the error lists.

## Forgetting new or deleted files in Refresh
**Symptom:** An extracted class or deleted file isn't reflected; references look wrong.
**Cause:** `Refresh` was called with only the modified file, not the new/deleted ones.
**Fix:** `Refresh` handles all three — changed, created, deleted. Pass every affected path. Check the
returned `Unmapped[]` — a path there means wendsharp couldn't map it to a project (often a wrong path
or a file outside the solution).

## Guessing namespaces/types
**Symptom:** Build breaks on a missing or wrong `using`, or a type you assumed.
**Cause:** You wrote a `using` or a type from memory instead of asking.
**Fix:** `DescribeSymbol` the type and copy its `RequiredUsings` and member signatures verbatim. They
are compiler-resolved (never `var`) and walk into generic arguments and array element types, so you
get every namespace you actually need.

## Renaming a symbol you don't own
**Symptom:** `PlanRename` refuses with a metadata/external message.
**Cause:** The symbol is defined in a NuGet/framework assembly — there's no source in the workspace.
**Fix:** You can `DescribeSymbol` it to read its surface, but you can't rename what you don't own.
Rename your own wrapper/usage instead, or reconsider the change.

## Textual comment/string renames matching unintended text
**Symptom:** A rename with `renameInComments`/`renameInStrings` changed text you didn't mean.
**Cause:** Those flags do plain-text matching, not semantic — edits are tagged `Comment` / `String`.
**Fix:** Review every `Comment`/`String` edit in the plan before applying. Leave the flags off unless
you specifically want textual replacement.

## Wrong project name
**Symptom:** A tool errors that the project wasn't found, listing available names.
**Cause:** Guessed `projectName`.
**Fix:** `GetWorkspaceInfo` lists the loaded projects. Copy the exact name.
