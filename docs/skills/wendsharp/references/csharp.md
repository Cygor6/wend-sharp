# C#-specific patterns — where WendSharp beats text search the most

When any of these features is involved, prefer WendSharp tools over grep. Semantic resolution
is most accurate exactly here because the surface and the text diverge.

---

## Partial classes and partial methods

A type split across files, or a partial method's defining and implementing halves, are **one
logical symbol**. `FindReferences` and `PlanRename` treat them as one; grep sees disconnected
fragments. `PlanRename` reports these as `PartialDeclaration` cascades so you rename every
part together.

## Explicit interface implementations

`void IFoo.Bar()` doesn't read like an ordinary `Bar(` in a text search. `GetImplementations`
and `PlanRename` resolve them via the symbol graph. `PlanRename` tags them `ExplicitInterfaceImpl`
in cascades — renaming the interface member must carry the explicit implementation along, and
WendSharp shows you both.

## Records and positional members

Record positional properties are compiler-synthesized but are real contract surface.
`DescribeSymbol` surfaces them and deliberately skips synthesized noise —
`Equals`/`GetHashCode`/`ToString`/`Deconstruct`/`PrintMembers`/`EqualityContract` and the
operators — so you see the usable contract, not the generated plumbing.

`GetOverrides` requires a guard for record compiler-synthesized members: they have a source
`Location` but empty `DeclaringSyntaxReferences`, which would cause an `IndexOutOfRangeException`
at `[0]`. The tool falls back to the containing type's display string in this case.

## C# 14 extension members

An `extension(T receiver) { ... }` block lives in a synthesized container type. `DescribeSymbol`
flattens these so extension methods appear as part of the type's usable surface. Extension
methods showing up under the static class is intentional and correct.

## Generic type arguments and arrays in `using` resolution

`DescribeSymbol`'s `RequiredUsings` walks **into** `Task<Customer>`, `Dictionary<K,V>`,
`Customer[]`, `Nullable<T>`, tuples, etc., collecting the namespace of every type argument and
element type — not just the outer type. The `using` set you copy is complete.

## Method overloads

Within a type, the same name with different parameters requires disambiguation. Tools that
can't auto-pick (`AnalyzeMember`, `GetOverrides`) return the exact `parameterTypes` strings to
choose from — copy one back verbatim (comma-separated types, e.g. `"int, string"`; empty string
for the parameterless overload). `PlanRename` has `renameOverloads` to rename all overloads as
a set.

Both `GetImplementations` and `GetOverrides` use type-qualified resolution (the same approach as
`DescribeSymbol` and `AnalyzeMember`) rather than a simple name lookup, because the latter
reports ambiguity on every override chain — exactly the case these tools inspect.

## Virtual / abstract / override chains

Changing one link without the others compiles to a confusing error later. `GetOverrides` gives
you the entire chain (not just direct subclasses) before you touch the signature. For interface
members, `GetImplementations` is the parallel tool.

## Cross-project references

Roslyn resolves symbols across the whole loaded solution, so `FindReferences`,
`GetImplementations`, and `PlanRename` see uses in **other projects** that a per-file or
per-project text search misses. `PlanRename`'s edit list can span projects — apply all of them,
then `Refresh` every touched file.

## Nullable reference types and resolved signatures

`DescribeSymbol` and `AnalyzeMember` use a display format that includes nullable annotations
and fully qualified types (never `var`). The signatures you copy carry the correct nullability —
don't strip the `?`/non-`?` distinction.

## Data flow in `AnalyzeMember`

`AnalyzeMember` returns semantically precise data-flow sets, not coarse variable lists:

- `DataFlowsIn[]` — variables that flow into the analysed region (read inside, assigned outside
  or as parameters).
- `DataFlowsOut[]` — variables assigned inside the region and read outside it.
- `Captured[]` — variables captured by lambdas or local functions inside the region.

These sets map directly to what must become parameters (`DataFlowsIn`), return values or
out-parameters (`DataFlowsOut`), and closure state (`Captured`) when extracting a method.
