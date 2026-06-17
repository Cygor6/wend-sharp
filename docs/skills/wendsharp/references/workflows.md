# wendsharp workflows — step-by-step playbooks

Every workflow ends in the same verification loop:
`Refresh(changed + new + deleted) → GetDiagnostics → fix → repeat until clean`.

## Rename a symbol across the solution
```
1. GetWorkspaceInfo                          # if unsure of the project name
2. PlanRename(project, locator, newName)     # locator = file+line+column OR fullyQualifiedSymbolName
3. Inspect the result:
   - HasConflicts == true?  STOP. Report the collision (e.g. CS0102 name clash). Do not apply.
   - Ambiguous error?       Re-call with the fully-qualified metadata name.
   - else proceed.
4. Apply Edits:
   - Group edits by file.
   - Within each file, apply in DESCENDING StartOffset order (last edit first) so earlier
     edits don't shift later offsets. Use StartOffset + Length + NewText.
5. If FileRenames is non-empty, rename each file on disk OldPath -> NewPath.
6. Refresh(every changed file + every renamed file's new path).
7. GetDiagnostics(project, filePaths = the files you touched).
   - clean? done.
   - errors? fix, Refresh, GetDiagnostics again.
```

## Change a method or interface signature
```
1. DescribeSymbol(project, type)  OR  AnalyzeMember(project, type, member)
   → confirm the exact current signature.
2. FindReferences(project, symbol)
   → every call site that passes/uses the old signature.
   (Ambiguous? re-call with the metadata name.)
3. GetOverrides(project, type, member)        # if virtual/abstract/override
   AND/OR GetImplementations(project, type)   # if it's an interface/base member
   → every alternate implementation that must change to match.
4. Edit the declaration AND every site surfaced in steps 2–3.
5. Refresh(all changed files) → GetDiagnostics → repeat until clean.
```
Skipping step 3 is the classic break: changing one link of a virtual/interface chain without the
others compiles to a worse error later.

## Extract or split a method
```
1. ExploreCode(file)                          # see the shape cheaply.
2. AnalyzeMember(project, type, member)
   → InternalDependencies: what must move or become a parameter.
   → ExternalDependencies: what the extracted code still needs in scope.
   → ReadVariables / WrittenVariables: the data flow across the new boundary.
   → Callers: what will break if the signature changes.
3. Perform the extraction (new method, or new class in a new file).
4. Refresh(changed files + any NEW file you created).
5. GetDiagnostics → fix until clean.
```

## Author new code against an existing type
For an adapter, a test double, a DI registration, or anything that must match an existing contract.
```
1. DescribeSymbol(project, type)
   → copy RequiredUsings verbatim into the new file.
   → match Members signatures exactly (they're compiler-resolved, never `var`).
2. Write the new code.
3. Refresh(new file) → GetDiagnostics.
```

## Orient in an unfamiliar area
```
1. GetWorkspaceInfo                           # solution, projects, roots.
2. ExploreCode(entry or suspected file)       # map the structure.
3. DescribeSymbol / AnalyzeMember on the parts that matter.
```

## Find everything affected before a breaking change
When you're not sure how far a change reaches:
```
- FindReferences          → call sites.
- GetImplementations      → implementers of an interface/base.
- GetOverrides            → overrides of a virtual/abstract member.
Together these are the full affected surface. Text search gives you a subset, with false positives.
```
