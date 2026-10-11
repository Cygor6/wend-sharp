# WendSharp workflows: step-by-step playbooks

Every workflow ends in the same verification loop:
`GetDiagnostics(filePaths = the files you touched) → fix → repeat until ErrorCount is 0`

Edits to existing files are picked up automatically. If the workflow **created**, moved, or
renamed a file, call `Refresh` with its absolute path first.

`GetDiagnostics` returns compiler errors **and** analyzer/spec diagnostics (banned APIs,
project-specific rules, etc.). Both are blocking: a task is not done until `ErrorCount == 0`.

Projects may encode specifications as Roslyn analyzers escalated to Error via `.editorconfig`.
The diagnostic message states the rule and usually the fix (banned-symbol messages carry the
replacement API): follow it in the code being edited.

`projectName` is optional in every step below. If a call answers `Ambiguous`, pick one of its
`Candidates` and re-call with that `Name` (plus `ParameterTypes` for a method).

---

## Rename a symbol across the solution

```
1. PlanRename(locator, newName)
   locator = filePath + line + column  OR  fullyQualifiedSymbolName (+ parameterTypes for an overload)
2. Follow Next:
   - HasConflicts == true?  STOP. Report the collision. Do not apply.
   - Truncated == true?     Call PlanRename again with the same arguments and filePaths = the
                            files whose Lines is missing. Do this BEFORE applying anything.
   - else: proceed.
3. For each file in Files[]:
   - ReplaceAll == true  → you may replace every OldName with NewName in that file, or
   - apply each Lines[] entry: replace Before with After (use Line when the same Before
     occurs more than once).
   Review lines whose Location includes Comment or String: those are plain-text matches.
4. If FileRenames[] is non-empty, rename each file on disk OldPath → NewPath,
   then Refresh(the new paths).
5. GetDiagnostics(filePaths = the files you touched).
   - clean? done.
   - errors? fix → GetDiagnostics again.
```

---

## Change a method or interface signature

```
1. DescribeSymbol(type)  OR  AnalyzeMember(type, member)
   → confirm the exact current signature.
2. FindReferences(symbol)
   → every call site that passes or uses the old signature.
   (Truncated? page with offset = NextOffset until you have Total.)
3. If the member is virtual/abstract/override, or an interface member:
   GetOverrides(declaringType, member) → every override or implementation that must match.
4. If you change a whole interface or base class (not one member):
   GetImplementations(type)        → every implementer that must match.
5. Edit the declaration AND every site surfaced in steps 2–4.
6. GetDiagnostics(filePaths = all changed files) → repeat until clean.
```

Skipping steps 3–4 is the classic break: changing one link of a virtual/interface chain
without the others compiles to a confusing error later.

---

## Extract or split a method

```
1. ExploreCode(file)                          # see the shape cheaply.
2. AnalyzeMember(type, member)                # includeSource: true if you need the body text
   → InternalDependencies: what must move or become a parameter.
   → ExternalDependencies: what the extracted code still needs in scope.
   → DataFlowsIn / DataFlowsOut: the data flow across the new boundary.
   → Callers: what will break if the signature changes.
3. Perform the extraction (new method, or new class in a new file).
4. Refresh(any NEW file you created).
5. GetDiagnostics(filePaths = changed + new files) → fix until clean.
```

---

## Author new code against an existing type

For an adapter, test double, DI registration, or anything that must implement a contract:

```
1. DescribeSymbol(type)
   → copy RequiredUsings verbatim into the new file.
   → match Members[] signatures exactly (compiler-resolved, never `var`).
2. Write the new code.
3. Refresh(new file) → GetDiagnostics(filePaths = [new file]) → fix until clean.
```

---

## Orient in an unfamiliar codebase

```
1. ExploreCode(entry file or suspected file)  # map the structure cheaply.
2. DescribeSymbol / AnalyzeMember on parts that matter.
3. GetWorkspaceInfo only if a lookup returns NotFound for a symbol you can see
   (EmptyProjects / LoadDiagnostics explain a project that failed to load).
```

---

## Find everything affected before a breaking change

When you're not sure how far a change reaches:

```
FindReferences(symbol)          → call sites
GetImplementations(type)        → implementers of an interface/base
GetOverrides(type, member)      → overrides of a virtual/abstract member, implementations of an interface member
```

Together these are the full affected surface. Text search gives you a subset, with false
positives. Page each with `offset = NextOffset` while `Truncated` is true.
