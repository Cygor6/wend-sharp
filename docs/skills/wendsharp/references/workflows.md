# WendSharp workflows — step-by-step playbooks

Every workflow ends in the same verification loop:
`Refresh(changed + new + deleted) → GetDiagnostics → fix → repeat until clean`

If you don't know the project name, call `GetWorkspaceInfo` before starting any workflow.

---

## Rename a symbol across the solution

```
1. GetWorkspaceInfo                           # confirm project name
2. PlanRename(project, locator, newName)
   locator = filePath+line+column  OR  fullyQualifiedSymbolName
3. Inspect the result:
   - HasConflicts == true?  STOP. Report the collision. Do not apply.
   - Ambiguous error?       Re-call with the fully-qualified metadata name.
   - else: proceed.
4. Group Edits[] by file.
   Within each file, apply in DESCENDING StartOffset order (last edit first).
   Use StartOffset + Length + NewText — offsets index the original file.
5. If FileRenames[] is non-empty, rename each file on disk OldPath → NewPath.
6. Refresh(every changed file + every renamed file at its new path).
7. GetDiagnostics(project, filePaths = the files you touched).
   - clean? done.
   - errors? fix → Refresh → GetDiagnostics again.
```

---

## Change a method or interface signature

```
1. DescribeSymbol(project, type)  OR  AnalyzeMember(project, type, member)
   → confirm the exact current signature.
2. FindReferences(project, symbol)
   → every call site that passes or uses the old signature.
   (Ambiguous? re-call with the metadata name.)
3. If the member is virtual/abstract/override:
   GetOverrides(project, type, member)      → every override that must match.
4. If the symbol is an interface or base-class member:
   GetImplementations(project, type)        → every implementer that must match.
5. Edit the declaration AND every site surfaced in steps 2–4.
6. Refresh(all changed files) → GetDiagnostics → repeat until clean.
```

Skipping steps 3–4 is the classic break: changing one link of a virtual/interface chain
without the others compiles to a confusing error later.

---

## Extract or split a method

```
1. ExploreCode(file)                          # see the shape cheaply.
2. AnalyzeMember(project, type, member)
   → InternalDependencies: what must move or become a parameter.
   → ExternalDependencies: what the extracted code still needs in scope.
   → DataFlowsIn / DataFlowsOut: the data flow across the new boundary.
   → Callers: what will break if the signature changes.
3. Perform the extraction (new method, or new class in a new file).
4. Refresh(all changed files + any new file you created).
5. GetDiagnostics → fix until clean.
```

---

## Author new code against an existing type

For an adapter, test double, DI registration, or anything that must implement a contract:

```
1. DescribeSymbol(project, type)
   → copy RequiredUsings verbatim into the new file.
   → match Members[] signatures exactly (compiler-resolved, never `var`).
2. Write the new code.
3. Refresh(new file) → GetDiagnostics → fix until clean.
```

---

## Orient in an unfamiliar codebase

```
1. GetWorkspaceInfo                           # solution, projects, roots.
2. ExploreCode(entry file or suspected file)  # map the structure cheaply.
3. DescribeSymbol / AnalyzeMember on parts that matter.
```

---

## Find everything affected before a breaking change

When you're not sure how far a change reaches:

```
FindReferences(project, symbol)          → call sites
GetImplementations(project, type)        → implementers of an interface/base
GetOverrides(project, type, member)      → overrides of a virtual/abstract member
```

Together these are the full affected surface. Text search gives you a subset, with false
positives.
