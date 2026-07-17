using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace WendSharp.Analyzers;

/// <summary>
/// WS0001 — every MCP tool method must follow WendSharp's error contract: wrap its body in
/// a try statement that rethrows <see cref="System.OperationCanceledException"/> and returns an
/// Error-carrying result on any other exception. Forgetting this reproduces the silent-false-result
/// bug class the project has been bitten by.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class Ws0001Analyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "WS0001";

    static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: "MCP tool method must follow the error contract",
        messageFormat: "Tool method '{0}' must wrap its body in try/catch, rethrow OperationCanceledException, and return a result with Error set on failure.",
        category: "WendSharp.Contract",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "MCP tool methods must catch exceptions and surface them via an Error-carrying result, " +
                     "while rethrowing OperationCanceledException so cancellation is not swallowed.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeMethod, SyntaxKind.MethodDeclaration);
    }

    static void AnalyzeMethod(SyntaxNodeAnalysisContext context)
    {
        var method = (MethodDeclarationSyntax)context.Node;

        // Name-based attribute matching is a deliberate, pragmatic choice: it keeps the analyzer
        // (and its tests) free of a reference to the MCP package. A method is a tool if it carries
        // an attribute named McpServerTool / McpServerToolAttribute.
        if (!HasMcpServerToolAttribute(method))
            return;

        if (!FollowsErrorContract(method))
            context.ReportDiagnostic(Diagnostic.Create(Rule, method.Identifier.GetLocation(), method.Identifier.ValueText));
    }

    static bool HasMcpServerToolAttribute(MethodDeclarationSyntax method)
    {
        foreach (var list in method.AttributeLists)
        {
            foreach (var attr in list.Attributes)
            {
                var name = attr.Name switch
                {
                    QualifiedNameSyntax q => q.Right.Identifier.ValueText,
                    IdentifierNameSyntax id => id.Identifier.ValueText,
                    _ => attr.Name.ToString()
                };
                if (name == "McpServerTool" || name == "McpServerToolAttribute")
                    return true;
            }
        }
        return false;
    }

    static bool FollowsErrorContract(MethodDeclarationSyntax method)
    {
        // v1 is intentionally syntactic: require a top-level try in the body with (a) an
        // OperationCanceledException catch that rethrows, and (b) a subsequent general catch.
        // Verifying the general catch actually returns an Error-populated result is out of scope.
        if (method.Body is null)
            return false;

        foreach (var tryStmt in method.Body.Statements.OfType<TryStatementSyntax>())
        {
            if (HasRethrowingOceCatch(tryStmt, out var oceIndex) && HasGeneralCatchAfter(tryStmt, oceIndex))
                return true;
        }
        return false;
    }

    static bool HasRethrowingOceCatch(TryStatementSyntax tryStmt, out int oceIndex)
    {
        for (var i = 0; i < tryStmt.Catches.Count; i++)
        {
            var catchClause = tryStmt.Catches[i];
            if (IsOperationCanceledCatch(catchClause) && Rethrows(catchClause))
            {
                oceIndex = i;
                return true;
            }
        }
        oceIndex = -1;
        return false;
    }

    static bool IsOperationCanceledCatch(CatchClauseSyntax catchClause)
    {
        var typeName = catchClause.Declaration?.Type?.ToString();
        return typeName is not null
            && (typeName == "OperationCanceledException"
                || typeName.EndsWith(".OperationCanceledException")
                || typeName == "TaskCanceledException"
                || typeName.EndsWith(".TaskCanceledException"));
    }

    static bool Rethrows(CatchClauseSyntax catchClause) =>
        catchClause.Block.Statements.OfType<ThrowStatementSyntax>().Any(t => t.Expression is null);

    static bool HasGeneralCatchAfter(TryStatementSyntax tryStmt, int oceIndex)
    {
        for (var i = oceIndex + 1; i < tryStmt.Catches.Count; i++)
        {
            var catchClause = tryStmt.Catches[i];
            // A catch is "general" if it has no declaration (catch { }) or catches System.Exception
            // with no exception filter.
            if (catchClause.Filter is not null)
                continue;
            if (catchClause.Declaration is null)
                return true;
            var typeName = catchClause.Declaration.Type?.ToString();
            if (typeName == "Exception" || typeName is not null && typeName.EndsWith(".Exception"))
                return true;
        }
        return false;
    }
}
