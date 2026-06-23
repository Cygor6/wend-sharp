---
name: wendsharp
description: >
  Semantic C# code analysis via Roslyn for the Zed editor. Use when working with C# source —
  finding references, exploring types, planning renames, analysing members, or checking
  diagnostics. Prefer these tools over grep or text search whenever the task involves symbols,
  types, overrides, or cross-file/cross-project accuracy.
compatibility: Zed editor with WendSharp MCP server running. Requires a loaded .sln file.
---

# WendSharp — semantic oracle for C# refactoring

WendSharp is **read-only**: it never writes to disk or modifies the solution. It returns
compiler-verified information so you can make correct edits yourself.

## When to use WendSharp vs standard tools

| Task | Use |
|------|-----|
| Find all callers of a method | `FindReferences` |
| Read a large unfamiliar file | `ExploreCode` (not read_file) |
| Know exact type signatures / usings needed | `DescribeSymbol` |
| Rename a symbol across files | `PlanRename` |
| Understand what a method depends on | `AnalyzeMember` |
| Check build errors after an edit | `Refresh` → `GetDiagnostics` |
| Find all types that implement an interface | `GetImplementations` |
| Find all overrides of a virtual member | `GetOverrides` |
| Don't know the project name | `GetWorkspaceInfo` first |

Use standard read_file/grep only for non-C# files or when WendSharp is unavailable.

## Decision router — which tool, in what order

**Orienting in unfamiliar code:**
`GetWorkspaceInfo` → `ExploreCode` → `DescribeSymbol` / `AnalyzeMember`

**Before any rename:**
`PlanRename` → inspect `HasConflicts` → apply edits last-offset-first → `Refresh` → `GetDiagnostics`

**Before changing a method/interface signature:**
`FindReferences` + `GetOverrides` (if virtual) + `GetImplementations` (if interface) → edit all sites → `Refresh` → `GetDiagnostics`

**Before extracting or splitting a method:**
`ExploreCode` → `AnalyzeMember` → perform extraction → `Refresh` → `GetDiagnostics`

**Writing new code against an existing type:**
`DescribeSymbol` → copy `RequiredUsings` and member signatures verbatim → `Refresh` → `GetDiagnostics`

**After every edit, always:**
`Refresh(all changed/created/deleted paths)` → `GetDiagnostics` → fix → repeat until clean

## Gotchas

- **`Refresh` before anything else after an edit.** WendSharp is blind to disk changes until you
  refresh. Stale results are the most common source of wrong answers.
- **Apply `PlanRename` edits last-offset-first per file.** Each edit's `StartOffset` indexes the
  original file. Applying front-to-back shifts all later offsets and corrupts the file.
- **`HasConflicts: true` means stop.** Do not apply the rename. Report the collision and pick a
  different name.
- **`Ambiguous` is an instruction, not an error.** Re-call with the fully-qualified metadata name
  (e.g. `MyApp.Services.OrderService.Calculate`).
- **Pass every affected path to `Refresh`.** New files, deleted files, and modified files all need
  to be listed. Check `Unmapped[]` in the response — a path there means the file wasn't found in
  the solution.
- **Never guess `projectName`.** Call `GetWorkspaceInfo` to get the exact names.
- **`ExploreCode` needs an absolute path when possible.** Bare file names can be ambiguous if the
  same name exists in multiple projects.

## Reference files

Load these only when you need them — not up front:

- [`references/tools.md`](references/tools.md) — full parameter and return-value reference for
  every tool. Read when you need exact field names or flag behaviour.
- [`references/workflows.md`](references/workflows.md) — step-by-step playbooks for common
  refactoring tasks. Read when you're about to start a multi-step operation.
- [`references/csharp.md`](references/csharp.md) — C#-specific patterns where semantic resolution
  beats text search the most (partial classes, records, explicit interface implementations, etc.).
  Read when the task involves these language features.
- [`references/pitfalls.md`](references/pitfalls.md) — symptom → cause → fix for the failures
  that actually happen. Read when something goes wrong or the result looks unexpected.
