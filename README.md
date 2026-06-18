<div align="center">

# wend-sharp

**Help AI assistants refactor your C# codebase**

A read-only MCP server that gives an AI agent compiler-verified facts about your C# code via Roslyn — references, structure, signatures, dependencies, and diagnostics — so it refactors from the compiler's truth instead of guessing with text search.

[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![C# 14](https://img.shields.io/badge/C%23-14-239120?logo=csharp&logoColor=white)](https://learn.microsoft.com/dotnet/csharp/)
[![MCP](https://img.shields.io/badge/protocol-MCP-orange)](https://modelcontextprotocol.io/)
[![License](https://img.shields.io/badge/license-Apache--2.0%20%2B%20Commons%20Clause-blue)](#license)

</div>

A **read-only** MCP server for the Zed editor that gives an AI agent compiler-verified information about a C# codebase via Roslyn. wend-sharp never edits code — the agent makes the edits; wend-sharp supplies the truth (references, structure, signatures, dependencies, diagnostics) and a **validation loop**.

## What wend-sharp does

wend-sharp is a **tool server, not an agent**. It has no LLM, no chat loop, and no file/text search
(Zed already does that well). Every tool is **semantic** — it uses Roslyn's `SemanticModel` and
`SymbolFinder`, so references are compiler-verified (no false text/comment matches) and types are
resolved (never `var`).

### Tools

| Tool | What it does |
|---|---|
| `ExploreCode` | Compact blueprint of a file (signatures, no bodies). |
| `DescribeSymbol` | A type's full public/protected surface with resolved types, base types, interfaces, and the `using` directives needed to author an interface against it. |
| `FindReferences` | Semantic references to a type/member across the solution (no false text matches). |
| `GetImplementations` | Every concrete type implementing an interface or deriving from a class, across multi-level inheritance and other files/projects (resolved via Roslyn's symbol graph). |
| `GetOverrides` | Every override of a virtual/abstract member across the whole inheritance chain, not just direct subclasses. |
| `AnalyzeMember` | A method's internal/external dependencies, data flow (read/written variables), and callers. Powers "extract logic". |
| `PlanRename` | Computes a compiler-verified rename plan **without modifying code**: apply-ready edits (file + 1-based line/col + absolute offset/length + old/new text), conflicts (name collisions & new compile errors), and cascade targets (overrides, interface members, partial declarations) that text search misses. wend-sharp does NOT apply it — apply the edits yourself, then `Refresh` + `GetDiagnostics`. |
| `Refresh` | Re-read files from disk after the agent edits them (changed / new / deleted). |
| `GetDiagnostics` | Roslyn compiler errors/warnings for the current state. |
| `GetWorkspaceInfo` | Reports the resolved solution file, its root directory, the loaded project names, and (when the client exposes MCP roots) the open root folders. |

### Hard constraints (non-negotiable)

1. **wend-sharp NEVER writes source code to disk.** `PlanRename` returns an apply-ready edit *plan* (precise spans + conflict detection + cascade visibility); it does not apply it. The renamed solution is computed locally and discarded.
2. **`stdout` is the JSON-RPC channel.** All logging goes to **stderr**.
3. **Disk is the source of truth.** The agent edits files outside wend-sharp; wend-sharp's `Solution` is only
   mutated by re-reading from disk (`Refresh`).
4. **No file/text search tools.** Deliberately out of scope.
5. **No own LLM / chat loop.** wend-sharp is a tool server, not an agent.

## Folder structure wend-sharp operates in

Zed launches this server inside your project's worktree. wend-sharp locates the **solution file**
(`.slnx`/`.sln`) and lets MSBuild/Roslyn load the real project graph — that loaded `Solution` *is*
wend-sharp's understanding of the structure. (Only solution discovery walks the directory tree; the
tools themselves never browse the filesystem.)

Solution path resolution order:

1. `--solution <path-or-dir>` argument (recommended — deterministic). Accepts a `.slnx`/`.sln`
   file *or* a directory containing exactly one (use `.` for the cwd).
2. `WendSharp_SOLUTION` environment variable (same path-or-directory rules).
3. Automatic discovery from an anchor directory — `ZED_WORKTREE_ROOT` when Zed sets it, otherwise
   the current working directory:
   - first walk **up** to the nearest ancestor containing exactly one `.slnx`/`.sln`
     (the OmniSharp/DotRush convention);
   - if nothing is found walking up, scan **down** up to 6 levels (skipping `bin`, `obj`,
     `node_modules`, `.git`, and similar folders, plus symlinks) for exactly one solution.

   Finding more than one solution at the chosen level is an error rather than a guess — pass
   `--solution` to disambiguate.
4. If the anchor resolves nothing and the client exposes MCP roots, wend-sharp retries discovery
   across the open root folders.

If Zed launches the server with a cwd outside the worktree, pass `--solution` explicitly.

## Build

> Requires the **.NET 10 SDK**.

```bash
dotnet build src/WendSharp/WendSharp.csproj -c Release
```

## Try it against the bundled sample

The `sample/` folder is a tiny solution where `Calculator.Add` is called twice from `Program.cs`
(plus a decoy mention in a comment).

```bash
# Build the sample once so Roslyn has a clean compilation:
dotnet build sample/Sample.slnx

# Run wend-sharp pointed at the sample (it speaks MCP over stdio):
dotnet run --project src/WendSharp/WendSharp.csproj -- --solution "$(pwd)/sample/Sample.slnx"
```

A `FindReferences` call for project `App`, symbol `Add` returns **2 references** (the two real
calls) and **not** the comment.

## Wire into Zed

Edit your Zed `settings.json` (or commit a project-local `.zed/settings.json`). Use the **official
flat custom-server format**:

```json
{
  "context_servers": {
    "wendsharp": {
      "enabled": true,
      "command": "C:\\ABS\\PATH\\WendSharp\\WendSharp.exe",
      "args": ["--solution", "C:\\ABS\\PATH\\solution.slnx"],
      "env": {}
    }
  }
}
```

`--solution .` resolves to the worktree directory Zed launches the process in. If that isn't
where your solution lives, point at it explicitly instead:
`"--solution", "/ABS/PATH/to/YourSolution.slnx"`. You can also drop `--solution` entirely and let
automatic discovery find the solution from the cwd.

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

wend-sharp never writes; the session `Solution` is byte-for-byte unchanged after every `PlanRename` call (the renamed solution is local and discarded).

## Acceptance scenarios

wend-sharp **only supplies information and validation** — the agent performs the edits:

- **Extract logic**: `ExploreCode` → `AnalyzeMember` (deps + callers) → *(agent creates the new class
  & edits the original)* → `Refresh` → `GetDiagnostics` (0 errors) → `FindReferences` (no dangling calls).
- **Safe rename**: `PlanRename` (review conflicts + cascades) → apply the apply-ready edits → `Refresh` → `GetDiagnostics` (0 errors) → `FindReferences` (no dangling calls).
- **Create interface**: `DescribeSymbol` (resolved surface + RequiredUsings) → `FindReferences` on the
  type (find DI registration + ctor-injection sites) → *(agent writes the interface & updates DI)* →
  `Refresh` → `GetDiagnostics`.

## License

Apache-2.0 with the Commons Clause. See [`LICENSE`](LICENSE).
