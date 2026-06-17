using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace WendSharp;

public sealed class StripBodyRewriter : CSharpSyntaxRewriter
{
    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node) =>
        node.WithBody(null).WithExpressionBody(null).WithSemicolonToken(Semi());

    public override SyntaxNode? VisitConstructorDeclaration(ConstructorDeclarationSyntax node) =>
        node.WithBody(null).WithExpressionBody(null).WithInitializer(null).WithSemicolonToken(Semi());

    public override SyntaxNode? VisitAccessorDeclaration(AccessorDeclarationSyntax node) =>
        node.WithBody(null).WithExpressionBody(null).WithSemicolonToken(Semi());

    public override SyntaxNode? VisitFieldDeclaration(FieldDeclarationSyntax node) =>
        node.WithDeclaration(node.Declaration.WithVariables(
            SyntaxFactory.SeparatedList(node.Declaration.Variables.Select(v =>
                v.WithInitializer(null)))));

    public override SyntaxNode? VisitPropertyDeclaration(PropertyDeclarationSyntax node) =>
        node.WithInitializer(null);

    public override SyntaxNode? VisitLocalFunctionStatement(LocalFunctionStatementSyntax node) =>
        node.WithBody(null).WithExpressionBody(null).WithSemicolonToken(Semi());

    static SyntaxToken Semi() => SyntaxFactory.Token(SyntaxKind.SemicolonToken);
}

public static class ContextWalker
{
    public static string Describe(SyntaxNode? node)
    {
        string? ns = null, type = null, member = null;
        for (var c = node; c is not null; c = c.Parent)
        {
            switch (c)
            {
                case BaseNamespaceDeclarationSyntax n:
                    ns ??= n.Name.ToString();
                    break;
                case TypeDeclarationSyntax t:
                    type ??= t.Identifier.ValueText;
                    break;
                case MethodDeclarationSyntax m:
                    member ??= m.Identifier.ValueText;
                    break;
                case PropertyDeclarationSyntax p:
                    member ??= p.Identifier.ValueText;
                    break;
                case ConstructorDeclarationSyntax:
                    member ??= ".ctor";
                    break;
                case LocalFunctionStatementSyntax lf:
                    member ??= lf.Identifier.ValueText;
                    break;
            }
        }

        return JoinNonEmpty('.', ns, type, member) is { Length: > 0 } joined ? joined : "Global";
    }

    static string JoinNonEmpty(char separator, params ReadOnlySpan<string?> parts)
    {
        var total = 0;
        var count = 0;
        foreach (var p in parts)
        {
            if (p is { Length: > 0 })
            {
                total += p.Length;
                count++;
            }
        }
        if (count == 0)
            return "";

        var sb = new System.Text.StringBuilder(total + count - 1);
        var first = true;
        foreach (var p in parts)
        {
            if (p is not { Length: > 0 })
                continue;
            if (!first)
                sb.Append(separator);
            sb.Append(p);
            first = false;
        }
        return sb.ToString();
    }
}

// C# 14: extension members — new syntax that allows extending types without instance methods.
// Declared in a top-level static class; the extension block targets ISymbol.
// These appear as extension methods but are defined in an extension block.
public static class SymbolExtensions
{
    extension(ISymbol symbol)
    {
        /// <summary>True if the symbol is declared inside the given containing type.</summary>
        public bool IsContainedBy(INamedTypeSymbol type) =>
            SymbolEqualityComparer.Default.Equals(symbol.ContainingType, type);

        /// <summary>Short display string using the FullSignature format.</summary>
        public string ToFullSignature() => symbol.ToDisplayString(RoslynFormats.FullSignature);
    }
}

/// <summary>
/// Shared Roslyn display formats and helpers.
/// </summary>
public static class RoslynFormats
{
    /// <summary>
    /// Full signature format: fully-qualified types, nullable annotations, accessibility,
    /// modifiers, parameter names/defaults. Used by DescribeSymbol and AnalyzeMember so the
    /// agent gets compiler-resolved types (never `var`).
    /// </summary>
    public static readonly SymbolDisplayFormat FullSignature = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        memberOptions:
            SymbolDisplayMemberOptions.IncludeAccessibility |
            SymbolDisplayMemberOptions.IncludeModifiers |
            SymbolDisplayMemberOptions.IncludeType |
            SymbolDisplayMemberOptions.IncludeParameters |
            SymbolDisplayMemberOptions.IncludeContainingType,
        parameterOptions:
            SymbolDisplayParameterOptions.IncludeName |
            SymbolDisplayParameterOptions.IncludeType |
            SymbolDisplayParameterOptions.IncludeDefaultValue |
            SymbolDisplayParameterOptions.IncludeExtensionThis,
        miscellaneousOptions:
            SymbolDisplayMiscellaneousOptions.UseSpecialTypes |
            SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier |
            SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers,
        localOptions: SymbolDisplayLocalOptions.IncludeType,
        kindOptions: SymbolDisplayKindOptions.IncludeMemberKeyword
    );
}
