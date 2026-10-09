namespace WendSharp;

/// <summary>MCP server instructions, sent once in the initialize result. Codex shows them with the
/// server's tools and Claude Code puts them in the system prompt, so they reach the model even when
/// no skill is installed and before any tool description is loaded. Zed's built-in agent ignores
/// them; there, install docs/skills/wendsharp. Keep this short and limited to cross-tool rules:
/// anything one tool's description already says does not belong here.</summary>
internal static class ServerInstructions
{
    public const string Text = """
        WendSharp is a read-only Roslyn oracle for the loaded C# solution. It never edits files: you edit, the compiler judges.

        Use it instead of grep or reading whole files whenever the question is about C# symbols: references (FindReferences), implementations and overrides (GetImplementations, GetOverrides), exact signatures and usings (DescribeSymbol), a file's outline (ExploreCode), what a method depends on (AnalyzeMember), a safe rename (PlanRename), and compiler plus analyzer errors (GetDiagnostics, which also enforces the project's analyzer and banned-API rules).

        Rules:
        - Check Error first. Kind "Ambiguous" or a non-empty Candidates list is an instruction: re-call with one candidate's Name (plus its ParameterTypes for a method). Never guess a name.
        - projectName is optional everywhere; WendSharp finds the declaring project. Pass it only to pick between candidates from different projects.
        - Lists are paged. Truncated: true means more results exist; re-call with offset = NextOffset.
        - Your edits to existing files are picked up automatically. Call Refresh only for files you created, moved, or renamed.
        - After editing, call GetDiagnostics with filePaths set to the files you touched and repeat until ErrorCount is 0. Analyzer errors are specifications: fix the code, never .editorconfig, BannedSymbols.txt, or suppressions.
        - PlanRename: if HasConflicts is true, do not apply. Otherwise follow its Next field.
        """;
}
