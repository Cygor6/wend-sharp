---
name: wendsharp
title: wendsharp
description: Semantic C# editing workflows powered by the wendsharp MCP server (Roslyn-based). Use when working in a C# or .NET repository where wendsharp is connected and the task involves renaming symbols, changing signatures, extracting or splitting methods, finding usages, inspecting types, or verifying that edits still compile. Replaces grep and text-based find-and-replace with compiler-verified symbol operations. Trigger on requests like "rename this method", "where is X used", "change this signature", "extract this method", "what calls this", "find implementations of", or any C# refactor. Do NOT use for non-C# languages or when wendsharp is not connected.
metadata:
  author: wendsharp
  version: 1.0.0
  mcp-server: wendsharp
  tags: [csharp, dotnet, refactoring, roslyn, semantic-analysis]
compatibility: Requires the wendsharp MCP server connected to a loaded C#/.NET solution. Read-only; wendsharp never edits files — the agent performs all edits.
---

# wendsharp: semantic C# editing

This repository has the **wendsharp** MCP server connected. It gives you Roslyn-based semantic
intelligence about the C# code. wendsharp is **read-only**: it tells you what is true before and
after an edit, but *you* make every edit. Use it instead of grep and text-based find-and-replace.

## CRITICAL: the one rule

**Do not reason about C# structure from raw text or grep when wendsharp can answer semantically.**
Text search finds strings; wendsharp finds *symbols*. A grep for `CalculateTotal` matches comments,
unrelated same-named methods, and strings — and misses overrides, interface implementations, and
references in other files. Before any rename, signature change, extraction, or "where is this used"
question, ask wendsharp.

## CRITICAL: the edit loop

Every change follows this loop. **Skipping `Refresh` is the #1 failure** — wendsharp sees disk only
through `Refresh`, so every answer after an un-refreshed edit is stale.

```
plan with wendsharp  →  you edit on disk  →  Refresh(changed+new+deleted files)  →  GetDiagnostics  →  repeat until clean
```

## Tool selection (use the left, NOT the right)

| You want to… | Use | NOT |
|---|---|---|
| See a file's shape (types, signatures) | `ExploreCode` | reading the whole file |
| Get exact signatures + required `using`s | `DescribeSymbol` | reading + guessing types |
| Know what a method touches / who calls it | `AnalyzeMember` | tracing by hand |
| Find every real use of a symbol | `FindReferences` | grep / text search |
| Find every implementer of an interface/base | `GetImplementations` | grepping `: IFoo` |
| Find every override of a virtual/abstract member | `GetOverrides` | grepping `override` |
| Plan a rename safely | `PlanRename` | find-and-replace |
| Re-sync wendsharp after you edit | `Refresh` | (nothing — mandatory) |
| Check it compiles after an edit | `GetDiagnostics` | assuming it's fine |
| See loaded solution/projects | `GetWorkspaceInfo` | guessing the project name |

Every tool takes a `projectName`. If you don't know it, call `GetWorkspaceInfo` first — passing a
wrong project name is the most common avoidable error.

For full per-tool guidance (parameters, return shapes, ambiguity handling), read
`references/tools.md`.

## Core workflows

Read `references/workflows.md` for the full step-by-step playbooks. The essentials:

**Rename a symbol**
```
PlanRename(project, locator, newName)     # locate by file+line+column OR fully-qualified name
  → HasConflicts == true?  STOP, report the collision, do not apply.
  → else: apply edits LAST-OFFSET-FIRST per file; if FileRenames set, rename those files too.
Refresh(every changed/renamed file) → GetDiagnostics → fix until clean.
```

**Change a method/interface signature**
```
DescribeSymbol / AnalyzeMember            # confirm current signature + callers
FindReferences                            # every call site
GetOverrides / GetImplementations         # every alternate implementation that must match
  → edit the declaration AND every site surfaced above
Refresh → GetDiagnostics → repeat until clean.
```

**Extract or split a method**
```
ExploreCode(file)                         # see the shape
AnalyzeMember(project, type, member)      # dependencies (what must move) + callers (what breaks)
  → perform the extraction
Refresh(changed + new files) → GetDiagnostics → fix until clean.
```

**Author code against an existing type (adapter, test, DI registration)**
```
DescribeSymbol(project, type)             # resolved signatures + the using directives to copy
  → write the new code using exactly those usings and signatures
Refresh(new file) → GetDiagnostics.
```

## Top pitfalls

Full list with fixes in `references/pitfalls.md`. The ones that actually bite:

- **Skipping `Refresh`.** wendsharp sees disk only through `Refresh`. Weird answers after an edit =
  you forgot to `Refresh`. It handles changed, created, AND deleted files — pass all three.
- **Applying rename edits front-to-back.** Edit offsets index the *original* file. Apply them
  **last-offset-first within each file**, or each edit shifts the ones after it.
- **Ignoring `HasConflicts`.** If `true`, the rename introduces a compile error. Don't apply and
  "fix after" — report it and pick another name.
- **Treating `Ambiguous` as a dead end.** It's an instruction: re-call with the fully-qualified
  metadata name (e.g. `SampleApp.Orders.OrderService`, not `OrderService`).
- **Guessing types instead of asking.** About to write a `using` from memory? `DescribeSymbol` the
  type and copy its `RequiredUsings` instead.

## C#-specific patterns wendsharp handles (and text search mishandles)

These are the cases where the semantic advantage is largest. Details in `references/csharp.md`:
partial classes/methods, explicit interface implementations, records with synthesized members,
C# 14 extension members, generic type arguments in `using` resolution, method overloads, and
virtual/abstract/override chains. When any of these are involved, **always** prefer wendsharp —
grep is most wrong exactly here.

## TL;DR

1. Don't grep C# structure. Ask wendsharp.
2. `GetWorkspaceInfo` if unsure of the project name.
3. Map with `ExploreCode`; drill with `DescribeSymbol` / `AnalyzeMember`.
4. Before any rename/signature change: `FindReferences` + `GetOverrides`/`GetImplementations`, or
   `PlanRename`.
5. After every edit: `Refresh` (changed + new + deleted) → `GetDiagnostics`. Repeat until clean.
6. Conflicts or `Ambiguous`? Stop and disambiguate — don't push through.
