namespace WendSharp.Tests;

[NotInParallel]
public class WorkspaceSessionResolveTests : IDisposable
{
    readonly List<string> _tempDirs = new();
    string NewTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "WendSharpResolveTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        _tempDirs.Add(path);
        return path;
    }
    static void Touch(string root, params string[] relativePathSegments)
    {
        var full = Path.Combine(new[] { root }.Concat(relativePathSegments).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "");
    }

    public void Dispose()
    {
        foreach (var d in _tempDirs)
        {
            try
            { Directory.Delete(d, recursive: true); }
            catch { }
        }
    }

    [Test]
    public async Task WalkUp_SolutionInStartDir_ReturnsIt()
    {
        var root = NewTempDir();
        var sln = Path.Combine(root, "App.slnx");
        File.WriteAllText(sln, "");

        var resolved = WorkspaceSession.ResolveByWalkingUp(root);

        await Assert.That(resolved).IsEqualTo(sln);
    }

    [Test]
    public async Task WalkUp_SolutionOneLevelUp_ReturnsIt()
    {
        var root = NewTempDir();
        var sln = Path.Combine(root, "App.slnx");
        File.WriteAllText(sln, "");
        var start = Path.Combine(root, "src");
        Directory.CreateDirectory(start);

        var resolved = WorkspaceSession.ResolveByWalkingUp(start);

        await Assert.That(resolved).IsEqualTo(sln);
    }

    [Test]
    public async Task WalkUp_SolutionSeveralLevelsUp_ReturnsIt()
    {
        var root = NewTempDir();
        var sln = Path.Combine(root, "App.sln");
        File.WriteAllText(sln, "");
        var start = Path.Combine(root, "src", "App", "Services");
        Directory.CreateDirectory(start);

        var resolved = WorkspaceSession.ResolveByWalkingUp(start);

        await Assert.That(resolved).IsEqualTo(sln);
    }

    [Test]
    public async Task WalkUp_SingleSlnx_NotDoubleCountedByWindowsGlobQuirk()
    {

        var root = NewTempDir();
        var sln = Path.Combine(root, "Only.slnx");
        File.WriteAllText(sln, "");

        var resolved = WorkspaceSession.ResolveByWalkingUp(root);

        await Assert.That(resolved).IsEqualTo(sln);
    }

    [Test]
    public async Task WalkUp_MultipleInNearestDir_ThrowsListingThoseFiles()
    {

        var parent = NewTempDir();
        var root = Path.Combine(parent, "root");
        Directory.CreateDirectory(root);
        Touch(root, "A.slnx");
        Touch(root, "B.sln");
        Touch(parent, "ancestor.slnx");
        var start = Path.Combine(root, "src", "App");
        Directory.CreateDirectory(start);

        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => WorkspaceSession.ResolveByWalkingUp(start));

        await Assert.That(ex.Message).Contains("Multiple solutions");
        await Assert.That(ex.Message).Contains("A.slnx");
        await Assert.That(ex.Message).Contains("B.sln");
        await Assert.That(ex.Message).DoesNotContain("ancestor.slnx");
    }

    [Test]
    public async Task WalkUp_NoSolution_ThrowsNamingStartDir()
    {

        var root = NewTempDir();
        var start = Path.Combine(root, "a", "b", "c");
        Directory.CreateDirectory(start);

        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => WorkspaceSession.ResolveByWalkingUp(start));

        await Assert.That(ex.Message).Contains("No .slnx/.sln found");
        await Assert.That(ex.Message).Contains("or any parent directory");
        await Assert.That(ex.Message).Contains(start);
    }

    [Test]
    public async Task Explicit_DirectoryWithSingleSlnx_ReturnsIt()
    {
        var root = NewTempDir();
        var sln = Path.Combine(root, "App.slnx");
        File.WriteAllText(sln, "");

        var resolved = WorkspaceSession.ResolveExplicitPath(root);

        await Assert.That(resolved).IsEqualTo(sln);
    }

    [Test]
    public async Task Explicit_DirectoryWithNoSolution_ThrowsDirectoryScopedMessage()
    {
        var root = NewTempDir();

        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => WorkspaceSession.ResolveExplicitPath(root));

        await Assert.That(ex.Message).Contains("No .slnx/.sln found in");
        await Assert.That(ex.Message).Contains("from --solution");
        await Assert.That(ex.Message).DoesNotContain("any parent directory");
    }

    [Test]
    public async Task Explicit_DirectoryWithMultiple_ThrowsListingThem()
    {
        var root = NewTempDir();
        Touch(root, "A.slnx");
        Touch(root, "B.sln");

        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => WorkspaceSession.ResolveExplicitPath(root));

        await Assert.That(ex.Message).Contains("Multiple solutions");
        await Assert.That(ex.Message).Contains("A.slnx");
        await Assert.That(ex.Message).Contains("B.sln");
        await Assert.That(ex.Message).Contains("path-to-specific-file");
    }

    [Test]
    public async Task Explicit_FilePath_ReturnsFullPath()
    {
        var root = NewTempDir();
        var sln = Path.Combine(root, "App.slnx");
        File.WriteAllText(sln, "");

        var resolved = WorkspaceSession.ResolveExplicitPath(sln);

        await Assert.That(resolved).IsEqualTo(sln);
    }

    [Test]
    public async Task Explicit_NonexistentPath_ThrowsDoesNotExist()
    {
        var root = NewTempDir();
        var bogus = Path.Combine(root, "nope.slnx");

        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => WorkspaceSession.ResolveExplicitPath(bogus));

        await Assert.That(ex.Message).Contains("does not exist");
        await Assert.That(ex.Message).Contains(bogus);
    }

    [Test]
    public async Task Explicit_Dot_ResolvesToCurrentDirectorySingleSolution()
    {

        var root = NewTempDir();
        var sln = Path.Combine(root, "App.slnx");
        File.WriteAllText(sln, "");
        var oldCwd = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(root);
        try
        {
            var resolved = WorkspaceSession.ResolveExplicitPath(".");
            await Assert.That(resolved).IsEqualTo(sln);
        }
        finally { Directory.SetCurrentDirectory(oldCwd); }
    }
}
