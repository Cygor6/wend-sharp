---
name: wendsharp
description: >
  Semantic C# code analysis via Roslyn (WendSharp MCP server). Use when working with C# source:
  finding references, exploring types, planning renames, analysing members, or checking
  diagnostics. Prefer these tools over grep or text search whenever the task involves symbols,
  types, overrides, or cross-file/cross-project accuracy.
compatibility: Any MCP client (Zed, Codex, Claude Code) with the WendSharp MCP server running. Requires a loaded .sln/.slnx file.
---

# WendSharp: semantic oracle for C# refactoring

WendSharp is **read-only**: it never writes to disk or modifies the solution. It returns
compiler-verified information so you can make correct edits yourself.

The server also sends a short version of these rules as MCP server instructions, so clients that
show them (Codex, Claude Code) get them without this skill. This file adds the routing table and
the failure catalogue.

## When to use WendSharp vs standard tools

| Task | Use |
|------|-----|
| Find all callers of a method | `FindReferences` |
| Read a large unfamiliar file | `ExploreCode` (not read_file) |
| Know exact type signatures / usings needed | `DescribeSymbol` |
| Rename a symbol across files | `PlanRename` |
| Understand what a method depends on | `AnalyzeMember` |
| Check compiler/analyzer errors after an edit | `GetDiagnostics` with `filePaths` |
| Find all types that implement an interface | `GetImplementations` |
| Find all overrides of a virtual member, or implementations of an interface member | `GetOverrides` |
| A lookup says NotFound for a symbol you can see | `GetWorkspaceInfo` (`EmptyProjects`) |

Use standard read_file/grep only for non-C# files or when WendSharp is unavailable.

## Decision router: which tool, in what order

**Orienting in unfamiliar code:**
`ExploreCode` → `DescribeSymbol` / `AnalyzeMember`

**Before any rename:**
`PlanRename` → follow `Next` (stop if `HasConflicts`) → apply `Files[].Lines` → `GetDiagnostics`

**Before changing a method/interface signature:**
`FindReferences` + `GetOverrides` (if virtual) + `GetImplementations` (if interface) → edit all sites → `GetDiagnostics`

**Before extracting or splitting a method:**
`ExploreCode` → `AnalyzeMember` → perform extraction → `Refresh` (new file only) → `GetDiagnostics`

**Writing new code against an existing type:**
`DescribeSymbol` → copy `RequiredUsings` and member signatures verbatim → `Refresh` (new file) → `GetDiagnostics`

**After every edit, always:**
`GetDiagnostics(filePaths: the files you touched)` → fix → repeat until `ErrorCount` is 0
(`GetDiagnostics` includes analyzer/spec errors by default: same blocking bar as compiler errors.)

## Gotchas

- **Check `Error` first, then `Candidates`.** `Kind: "Ambiguous"` (or `NotFound` with
  `Candidates`) is an instruction, not a dead end: re-call with one candidate's `Name`, plus its
  `ParameterTypes` for a method. `Name` is a metadata name every tool accepts.
- **`projectName` is optional.** WendSharp finds the declaring project. Pass it only to pick
  between candidates from different projects (each candidate carries `Project`).
- **Lists are paged.** `Truncated: true` means more results exist; re-call with
  `offset = NextOffset`. `Total` (or `ErrorCount`) is always the full count.
- **Edits to existing files are picked up automatically.** Call `Refresh` only for files you
  created, moved, or renamed, so WendSharp can add them to the right project.
- **`HasConflicts: true` means stop.** Do not apply the rename. Report the collision and pick a
  different name.
- **Apply `PlanRename` as text, not offsets.** Each `Files[].Lines[]` entry is a whole changed
  line (`Before` → `After`), ready for a text-matching edit tool. Where `ReplaceAll` is true, a
  plain replace-all of `OldName` with `NewName` in that file is exactly the plan.
- **`EmptyProjects` explains impossible NotFounds.** A project that loaded without sources (missing
  restore) can never resolve anything; `GetWorkspaceInfo` lists it with the MSBuild messages.

## Reference files

Load these only when you need them, not up front:

- [`references/tools.md`](references/tools.md): full parameter and return-value reference for
  every tool. Read when you need exact field names or flag behaviour.
- [`references/workflows.md`](references/workflows.md): step-by-step playbooks for common
  refactoring tasks. Read when you're about to start a multi-step operation.
- [`references/csharp.md`](references/csharp.md): C#-specific patterns where semantic resolution
  beats text search the most (partial classes, records, explicit interface implementations, etc.).
  Read when the task involves these language features.
- [`references/pitfalls.md`](references/pitfalls.md): symptom → cause → fix for the failures
  that actually happen. Read when something goes wrong or the result looks unexpected.
