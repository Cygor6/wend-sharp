# C#- and .NET-specific patterns wendsharp handles

These are the cases where semantic resolution beats text search by the widest margin. When any of
these is involved, prefer wendsharp — grep is most wrong exactly here.

## Partial classes and partial methods
A type split across files, or a partial method's defining/implementing halves, are **one logical
symbol**. `FindReferences` and `PlanRename` treat them as one; grep sees disconnected fragments.
`PlanRename` reports these as `PartialDeclaration` cascades so you rename every part together.

## Explicit interface implementations
`void IFoo.Bar()` doesn't read like an ordinary `Bar(` to a text search. `GetImplementations` and
`PlanRename` resolve them via the symbol graph; `PlanRename` tags them `ExplicitInterfaceImpl` in
cascades. Renaming the interface member must carry the explicit implementation along — wendsharp shows
you both.

## Records and positional members
Record positional properties are compiler-synthesized but are real contract surface, so
`DescribeSymbol` surfaces them. It deliberately skips the synthesized noise —
`Equals`/`GetHashCode`/`ToString`/`Deconstruct`/`PrintMembers`/`EqualityContract` and the operators —
so you see the usable contract, not the generated plumbing.

## C# 14 extension members
A `extension(T receiver) { ... }` block lives in a synthesized container type whose own members are
the real extension methods. `DescribeSymbol` flattens these so they appear as part of the type's
usable surface. Don't be surprised that the extension methods show up under the static class — that's
intentional and correct.

## Generic type arguments and arrays in `using` resolution
`DescribeSymbol`'s `RequiredUsings` walks **into** `Task<Customer>`, `Dictionary<K,V>`, `Customer[]`,
`Nullable<T>`, tuples, etc., collecting the namespace of every type argument and element type — not
just the outer type. So the `using` set you copy is complete, not just the surface type's namespace.

## Method overloads
Within a type, the same name with different parameters means you must disambiguate. Tools that can't
auto-pick (`AnalyzeMember`, `GetOverrides`) return the exact `parameterTypes` strings to choose from —
copy one back verbatim (comma-separated types, e.g. `"int, string"`; empty string for the
parameterless overload). `PlanRename` has `renameOverloads` to rename all overloads as a set.

## Virtual / abstract / override chains
Changing one link without the others is a classic break that compiles to a confusing error later.
`GetOverrides` gives you the entire chain (not just direct subclasses) before you touch the
signature, so you update every override together. For interface members, `GetImplementations` is the
parallel tool.

## Cross-project references
Roslyn resolves symbols across the whole loaded solution, so `FindReferences`,
`GetImplementations`, and `PlanRename` see uses in **other projects**, which a per-file or per-project
text search misses. This is why `PlanRename`'s edit list can span projects — apply all of them, then
`Refresh` every touched file.

## Nullable reference types and resolved signatures
`DescribeSymbol`/`AnalyzeMember` use a display format that includes nullable annotations and fully
qualified types (never `var`). When you author code against a type, the signatures you copy carry the
correct nullability — don't strip the `?`/non-`?` distinction.
