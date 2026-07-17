using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using WendSharp.Analyzers;

namespace WendSharp.Analyzers.Tests;

public class Ws0001AnalyzerTests
{
    // A minimal stub attribute — name-based matching in the analyzer makes this sufficient,
    // so the tests need no reference to the real MCP package. Appended AFTER the body (global
    // namespace, fully-qualified base) so each body keeps its using directives at the top.
    const string AttributeStub = """

        public sealed class McpServerToolAttribute : System.Attribute { }
        """;

    static Task RunAsync(string body) =>
        new CSharpAnalyzerTest<Ws0001Analyzer, DefaultVerifier>
        {
            TestCode = body + "\n" + AttributeStub,
        }.RunAsync();

    [Test]
    public async Task CompliantToolMethod_NoDiagnostic()
    {
        await RunAsync("""
            using System;

            public class Tools
            {
                [McpServerTool]
                public string Good()
                {
                    try
                    {
                        return "ok";
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { return ex.Message; }
                }
            }
            """);
    }

    [Test]
    public async Task ToolMethod_NoTryAtAll_ReportsWs0001()
    {
        await RunAsync("""

            public class Tools
            {
                [McpServerTool]
                public string {|WS0001:Bad|}()
                {
                    return "no try";
                }
            }
            """);
    }

    [Test]
    public async Task ToolMethod_TryWithoutOceCatch_ReportsWs0001()
    {
        await RunAsync("""
            using System;

            public class Tools
            {
                [McpServerTool]
                public string {|WS0001:Bad|}()
                {
                    try
                    {
                        return "ok";
                    }
                    catch (Exception ex) { return ex.Message; }
                }
            }
            """);
    }

    [Test]
    public async Task ToolMethod_OceCatchSwallows_ReportsWs0001()
    {
        await RunAsync("""
            using System;

            public class Tools
            {
                [McpServerTool]
                public string {|WS0001:Bad|}()
                {
                    try
                    {
                        return "ok";
                    }
                    catch (OperationCanceledException) { return "swallowed"; }
                    catch (Exception ex) { return ex.Message; }
                }
            }
            """);
    }

    [Test]
    public async Task NonToolMethod_NoTry_NoDiagnostic()
    {
        await RunAsync("""
            public class Tools
            {
                public string Plain()
                {
                    return "not a tool";
                }
            }
            """);
    }
}
