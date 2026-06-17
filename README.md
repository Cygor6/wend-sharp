<div align="center">

# wend-sharp

**Help AI assistants refactor your C# codebase**

Give your agents a compounding knowledge layer — semantically searchable, temporally tracked, and synthesized across sessions — instead of starting cold every time.

[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![C# 14](https://img.shields.io/badge/C%23-14-239120?logo=csharp&logoColor=white)](https://learn.microsoft.com/dotnet/csharp/)
[![MCP](https://img.shields.io/badge/protocol-MCP-orange)](https://modelcontextprotocol.io/)
[![License](https://img.shields.io/badge/license-Apache--2.0%20%2B%20Commons%20Clause-blue)](#license)

</div>

A **read-only** MCP server for the Zed editor that gives an AI agent compiler-verified information about a C# codebase via Roslyn. wendsharp never edits code — the agent makes the edits; wendsharp supplies the truth (references, structure, signatures, dependencies, diagnostics) and a **validation loop**.

## What wend-sharp does

wendsharp is a **tool server, not an agent**. It has no LLM, no chat loop, and no file/text search
(Zed already does that well). Every tool is **semantic** — it uses Roslyn's `SemanticModel` and
`SymbolFinder`, so references are compiler-verified (no false text/comment matches) and types are
resolved (never `var`).

### Tools

| Tool | What it does |
|---|---|
| `ExploreCode` | Compact blueprint of a file (signatures, no bodies). |
| `DescribeSymbol` | A type's full public surface with resolved types, base types, interfaces, and the `using` directives needed to author an interface against it. |
| `FindReferences` | Semantic references to a type/member across the solution (no false text matches). |
| `GetImplementations` | Every concrete type implementing an interface or deriving from a class, across multi-level inheritance and other files/projects (resolved via Roslyn's symbol graph). |
| `GetOverrides` | Every override of a virtual/abstract member across the whole inheritance chain, not just direct subclasses. |
| `AnalyzeMember` | A method's internal/external dependencies, data-flow (read/written variables), and callers. Powers "extract logic". |
| `PlanRename` | Computes a compiler-verified rename plan **without modifying code**: apply-ready edits (file + 1-based line/col + absolute offset/length + old/new text), conflicts (name collisions & new compile errors), and cascade targets (overrides, interface members, partial declarations) that text search misses. wendsharp does NOT apply it — apply the edits yourself, then `Refresh` + `GetDiagnostics`. |
| `Refresh` | Re-read files from disk after the agent edits them (changed / new / deleted). |
| `GetDiagnostics` | Roslyn compiler errors/warnings for the current state. |
| `GetWorkspaceInfo` | Reports the resolved solution file, its root directory, the loaded project names, and (when the client exposes MCP roots) the open root folders. |

### Hard constraints (non-negotiable)

1. **wendsharp NEVER writes source code to disk.** `PlanRename` returns an apply-ready edit *plan* (precise spans + conflict detection + cascade visibility); it does not apply it. The renamed solution is computed locally and discarded.
2. **`stdout` is the JSON-RPC channel.** All logging goes to **stderr**.
3. **Disk is the source of truth.** The agent edits files outside wendsharp; wendsharp's `Solution` is only
   mutated by re-reading from disk (`Refresh`).
4. **No file/text search tools.** Deliberately out of scope.
5. **No own LLM / chat loop.** wendsharp is a tool server, not an agent.

## Folder structure wendsharp operates in

Zed launches this server inside your project's worktree. wendsharp locates the **solution file**
(`.slnx`/`.sln`) and lets MSBuild/Roslyn load the real project graph — that loaded `Solution` *is*
wendsharp's understanding of the structure. (Only solution discovery walks the directory tree; the
tools themselves never browse the filesystem.)

Solution path resolution order:
1. `--solution <path-or-dir>` argument (recommended — deterministic). Accepts a
   `.slnx`/`.sln` file *or* a directory containing exactly one (use `.` for the cwd).
2. `wendsharp_SOLUTION` environment variable (same path-or-directory rules)
3. a **bottom-up scan**: the nearest directory from cwd (inclusive) that contains exactly
   one `.slnx`/`.sln` (the OmniSharp/DotRush convention). If the nearest directory with any
   solution holds more than one, it errors rather than walking further. This makes wendsharp
   robust to cwd being anywhere *inside* the worktree. If Zed launches it with a cwd that
   isn't in the worktree at all, pass `--solution` explicitly.

## Build

> Requires the **.NET 10 SDK**.

```bash
dotnet build src/wendsharp/wendsharp.csproj -c Release
```

## Try it against the bundled sample

The `sample/` folder is a tiny solution where `Calculator.Add` is called twice from `Program.cs`
(plus a decoy mention in a comment).

```bash
# Build the sample once so Roslyn has a clean compilation:
dotnet build sample/Sample.slnx

# Run wendsharp pointed at the sample (it speaks MCP over stdio):
dotnet run --project src/wendsharp/wendsharp.csproj -- --solution "$(pwd)/sample/Sample.slnx"
```

A `FindReferences` call for project `App`, symbol `Add` returns **2 references** (the two real
calls) and **not** the comment.

## Tests

```bash
dotnet run --project tests/wendsharp.Tests/wendsharp.Tests.csproj -c Release
```

The TUnit test suite verifies: FindReferences (2 refs, 0 comment hits), DescribeSymbol (resolved types),
AnalyzeMember on a block-bodied method (internal dependency), AnalyzeMember on an expression-bodied
method (data-flow regression), PlanRename edits across both files (with 1-based positions AND
absolute offsets + matching OldText/NewText, files on disk unchanged), PlanRename edit content
(contains the new name), PlanRename conflict detection (in-scope collision → Error conflict),
PlanRename override cascade, PlanRename partial-declaration cascade, PlanRename nameof/cref
location classification, PlanRename no-comment-edits when the flag is off, PlanRename invalid
name → error, PlanRename symbol-not-found → error, PlanRename position locator (path A) and
metadata-symbol → error, PlanRename leaves files unchanged on the cascade path, ExploreCode
(bodies and field initializers stripped), Refresh (diagnostics reflect new content),
GetDiagnostics with warnings (no errors), and WorkspaceSession solution resolution — the
bottom-up scan (solution in cwd, in a parent 1–3 levels up, the Windows `*.sln` glob double-count
fix, multiple-in-nearest-dir → ambiguous error, none-found → error) plus `--solution` accepting a
directory/file/`.`.

## Wire into Zed

Edit your Zed `settings.json` (or commit a project-local `.zed/settings.json`). Use the **official
flat custom-server format**:

```json
{
    "context_servers": {
      "wendsharp": {
        "enabled": true,
        "command": "C:\\ABS\\PATH\\wendsharp\\wendsharp.exe",
        "args": ["--solution", "C:\\ABS\\PATH\\solution.slnx"],
        "env": {},
      },
    },     
}
```

`--solution .` resolves to the worktree directory Zed launches the process in. If that isn't
where your solution lives, point at it explicitly instead:
`"--solution", "/ABS/PATH/to/YourSolution.slnx"`. You can also drop `--solution` entirely and let
the bottom-up scan find the solution from cwd.

Then open the Agent Panel → Settings and confirm the green "Server is active" dot. Mention "wendsharp"
in your prompt so the model picks its tools. Because these tools are read-only, you can safely
auto-approve them per-tool (key form `mcp:wendsharp:<tool_name>`) or via
`agent.tool_permissions.default`.

### Agent workflow: safe rename via `PlanRename`

`PlanRename` surfaces the three things Roslyn provides that text search cannot:

1. **Apply-ready edits** — every `RenameEdit` carries both 1-based line/column **and** absolute offset/length in the *original* file, plus `OldText`/`NewText`, so you apply verbatim spans with zero re-interpretation. `Location` classifies each edit: `Declaration`, `Reference`, `Nameof` (semantic — always safe), `Cref` (semantic — always safe), or `Comment`/`String` (textual matches, present only when `renameInComments`/`renameInStrings` is on — review these).
2. **Conflicts** — a best-effort diagnostics diff (`Compilation.GetDiagnostics`, filtered to errors, compared before/after as an `(Id, Message)` multiset) reports name collisions and new compile errors (`CS0102`, `CS0111`, `CS0229`, …). `HasConflicts == true` means **stop and reconsider** instead of corrupting code. This is a heuristic, not a proof.
3. **Cascade targets** — declaration sites touched by the rename that text search would miss: `Override`, `InterfaceMember`, `ExplicitInterfaceImpl`, `PartialDeclaration`, `Overload`.

The loop:

1. Call `PlanRename`.
2. If `HasConflicts`, report the collision — do **not** apply.
3. Otherwise apply each `RenameEdit` by absolute offset/length (verbatim spans).
4. Call `Refresh`, then `GetDiagnostics` — confirms the result and catches any application slip.

wendsharp never writes; the session `Solution` is byte-for-byte unchanged after every `PlanRename` call (the renamed solution is local and discarded).

## Acceptance scenarios

The MVP is done when an agent driving wendsharp can carry out all three, where **wendsharp only supplies
information and validation** and the agent performs the edits:

- **Extract logic**: `ExploreCode` → `AnalyzeMember` (deps + callers) → *(agent creates the new class
  & edits the original)* → `Refresh` → `GetDiagnostics` (0 errors) → `FindReferences` (no dangling calls).
- **Safe rename**: `PlanRename` (review conflicts + cascades) → apply the apply-ready edits → `Refresh` → `GetDiagnostics` (0 errors) → `FindReferences` (no dangling calls).
- **Create interface**: `DescribeSymbol` (resolved surface + RequiredUsings) → `FindReferences` on the
  type (find DI registration + ctor-injection sites) → *(agent writes the interface & updates DI)* →
  `Refresh` → `GetDiagnostics`.

---
