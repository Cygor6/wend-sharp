using System.Collections.Concurrent;
using System.Collections.Frozen;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace WendSharp;

public sealed class WorkspaceSession(ILogger<WorkspaceSession> logger, string? solutionPath)
{
    MSBuildWorkspace? _workspace;

    Solution? _solution;
    string? _loadedSolutionPath;
    readonly SemaphoreSlim _gate = new(1, 1);

    readonly ConcurrentDictionary<ProjectId, HashSet<string>> _sourcePathCache = new();

    public Solution Solution =>
        Volatile.Read(ref _solution)
        ?? throw new InvalidOperationException(
            "Solution not initialized. Call InitializeAsync before accessing Solution.");

    public string? LoadedSolutionPath => _loadedSolutionPath;

    public ValueTask InitializeAsync(CancellationToken ct) => InitializeAsync(null, ct);

    public async ValueTask InitializeAsync(McpServer? server, CancellationToken ct)
    {
        if (Volatile.Read(ref _solution) is not null)
            return;
        await _gate.WaitAsync(ct);
        try
        {
            if (Volatile.Read(ref _solution) is not null)
                return;
            var path = await ResolveSolutionPathAsync(server, ct);
            logger.LogInformation("Opening solution: {Path}", path);

            _workspace?.Dispose();
            _workspace = MSBuildWorkspace.Create();
            _workspace.RegisterWorkspaceFailedHandler(e => logger.LogWarning("MSBuild: {Message}", e.Diagnostic.Message));
            var loaded = await _workspace.OpenSolutionAsync(path, cancellationToken: ct);

            _loadedSolutionPath = path;

            Volatile.Write(ref _solution, loaded);
            logger.LogInformation("Loaded {Count} project(s) from {Path}.", loaded.Projects.Count(), path);
        }
        finally { _gate.Release(); }
    }

    async ValueTask<string> ResolveSolutionPathAsync(McpServer? server, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(solutionPath))
            return ResolveExplicitPath(solutionPath);

        var anchor = GetAnchorDirectory();
        try
        {
            return ResolveFromAnchor(anchor);
        }
        catch (InvalidOperationException anchorEx)
        {
            var rootDirs = (await GetClientRootsAsync(server, ct)).Select(r => r.Path).ToList();
            if (rootDirs.Count == 0)
                throw;

            var resolved = ResolveAmongRoots(rootDirs);
            if (resolved is not null)
            {
                logger.LogInformation(
                    "Working directory '{Anchor}' did not resolve a solution; recovered via client roots.", anchor);
                return resolved;
            }

            throw new InvalidOperationException(
                $"{anchorEx.Message} Additionally, none of the {rootDirs.Count} open client root(s) " +
                $"contained exactly one solution: {string.Join(", ", rootDirs)}.", anchorEx);
        }
    }

    static string GetAnchorDirectory()
    {
        var zedRoot = Environment.GetEnvironmentVariable("ZED_WORKTREE_ROOT");
        return !string.IsNullOrWhiteSpace(zedRoot) && Directory.Exists(zedRoot)
            ? Path.GetFullPath(zedRoot)
            : Directory.GetCurrentDirectory();
    }

    public async ValueTask<IReadOnlyList<WorkspaceRoot>> GetClientRootsAsync(McpServer? server, CancellationToken ct)
    {
        if (server?.ClientCapabilities?.Roots is null)
            return [];
        try
        {
            var result = await server.RequestRootsAsync(new ListRootsRequestParams(), ct);
            var roots = new List<WorkspaceRoot>();
            foreach (var r in result.Roots)
            {
                var dir = RootUriToDirectory(r.Uri);
                if (dir is not null)
                    roots.Add(new WorkspaceRoot(
                        string.IsNullOrWhiteSpace(r.Name) ? Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar)) : r.Name!,
                        dir));
            }
            return roots;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // -32601 / transport hiccup / malformed root: degrade to "no roots".
            logger.LogDebug(ex, "roots/list query failed; treating as no client roots.");
            return [];
        }
    }

    static string? RootUriToDirectory(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri) || !Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || !parsed.IsFile)
            return null;
        var path = parsed.LocalPath;
        if (File.Exists(path))
            return Path.GetDirectoryName(Path.GetFullPath(path));
        return Directory.Exists(path) ? Path.GetFullPath(path) : null;
    }

    internal static string? ResolveAmongRoots(IReadOnlyList<string> roots)
    {
        var found = new List<string>();
        foreach (var r in roots)
        {
            try
            { found.Add(ResolveFromAnchor(r)); }
            catch (InvalidOperationException) { /* 0 or ambiguous in this root — skip */ }
        }
        var distinct = found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return distinct.Count == 1 ? distinct[0] : null;
    }

    const int DownScanMaxDepth = 6;
    const int DownScanMaxDirs = 5000;

    static readonly FrozenSet<string> PrunedDirNames =
        FrozenSet.Create(
            StringComparer.OrdinalIgnoreCase,
            ["bin", "obj", "node_modules", ".git", ".vs", ".idea", "packages", "TestResults", ".cache"]);

    internal static string ResolveFromAnchor(string anchor)
    {
        if (TryWalkUpForSolution(anchor, out var up))
            return up!;

        var down = ScanDownForSolutions(anchor, DownScanMaxDepth);
        return down.Length switch
        {
            1 => down[0],
            0 => throw new InvalidOperationException(
                $"No .slnx/.sln found at '{anchor}', any parent directory, or within {DownScanMaxDepth} level(s) below it. " +
                "Pass --solution <path>."),
            _ => throw new InvalidOperationException(
                $"Multiple solutions found within '{anchor}': {string.Join(", ", down.Select(Path.GetFileName))}. " +
                "Pass --solution <path> to disambiguate."),
        };
    }

    internal static string ResolveByWalkingUp(string start) =>
        TryWalkUpForSolution(start, out var solution)
            ? solution!
            : throw new InvalidOperationException(
                $"No .slnx/.sln found in '{start}' or any parent directory. Pass --solution <path>.");

    static bool TryWalkUpForSolution(string start, out string? solution)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            var found = GetSolutionsIn(dir.FullName);
            if (found.Length == 1)
            {
                solution = found[0];
                return true;
            }
            if (found.Length > 1)
                throw new InvalidOperationException(
                    $"Multiple solutions in '{dir.FullName}': {string.Join(", ", found.Select(Path.GetFileName))}. " +
                    "Pass --solution <path> to disambiguate.");
        }
        solution = null;
        return false;
    }

    internal static string[] ScanDownForSolutions(string root, int maxDepth)
    {
        var rootInfo = new DirectoryInfo(root);
        if (!rootInfo.Exists)
            return [];

        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(DirectoryInfo Dir, int Depth)>();
        queue.Enqueue((rootInfo, 0));
        var visited = 0;

        while (queue.Count > 0 && visited < DownScanMaxDirs)
        {
            var (dir, depth) = queue.Dequeue();
            visited++;
            foreach (var s in GetSolutionsIn(dir.FullName))
                results.Add(s);

            if (depth >= maxDepth)
                continue;

            IEnumerable<DirectoryInfo> subdirs;
            try
            { subdirs = dir.EnumerateDirectories(); }
            catch (UnauthorizedAccessException) { continue; }
            catch (DirectoryNotFoundException) { continue; }

            foreach (var sub in subdirs)
            {
                if ((sub.Attributes & FileAttributes.ReparsePoint) != 0)
                    continue;
                if (PrunedDirNames.Contains(sub.Name))
                    continue;
                queue.Enqueue((sub, depth + 1));
            }
        }

        return [.. results];
    }

    internal static string ResolveExplicitPath(string raw)
    {
        var full = Path.GetFullPath(raw);

        if (File.Exists(full))
            return full;

        if (Directory.Exists(full))
        {
            var found = GetSolutionsIn(full);
            return found.Length switch
            {
                1 => found[0],
                0 => throw new InvalidOperationException(
                    $"No .slnx/.sln found in '{full}' (from --solution '{raw}')."),
                _ => throw new InvalidOperationException(
                    $"Multiple solutions in '{full}' (from --solution '{raw}'): " +
                    $"{string.Join(", ", found.Select(Path.GetFileName))}. " +
                    "Pass --solution <path-to-specific-file> instead."),
            };
        }

        throw new InvalidOperationException(
            $"--solution path '{raw}' (resolved to '{full}') does not exist.");
    }

    static string[] GetSolutionsIn(string directory) =>
        Directory.EnumerateFiles(directory)
            .Where(f => f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            .ToArray();

    FrozenDictionary<string, Project>? _projectIndex;
    Solution? _projectIndexFor;

    public Project GetProject(string name)
    {
        var solution = Solution;
        var index = _projectIndex;
        if (index is null || !ReferenceEquals(_projectIndexFor, solution))
        {
            index = solution.Projects
                .GroupBy(p => p.Name, StringComparer.Ordinal)
                .ToFrozenDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            _projectIndexFor = solution;
            _projectIndex = index;
        }

        return index.TryGetValue(name, out var project)
            ? project
            : throw new ArgumentException(
                $"Project '{name}' not found. Available: " +
                $"{string.Join(", ", solution.Projects.Select(p => p.Name))}");
    }

    public async ValueTask<Compilation> GetCompilationAsync(string projectName, CancellationToken ct) =>
        await GetProject(projectName).GetCompilationAsync(ct)
        ?? throw new InvalidOperationException($"No compilation available for project '{projectName}'.");


    public Document? FindDocument(string fileNameOrPath)
    {
        var solution = Solution;   // single consistent snapshot for the whole lookup
        var relativeTargets = ResolveRelativeTargets(fileNameOrPath);
        return solution.Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d => MatchesName(d.FilePath, fileNameOrPath, relativeTargets));
    }

    public async ValueTask<HashSet<string>> GetSourceTreePathsAsync(Project project, CancellationToken ct)
    {
        if (_sourcePathCache.TryGetValue(project.Id, out var cached))
            return cached;

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in project.Documents)
        {
            var tree = await d.GetSyntaxTreeAsync(ct);
            if (tree is not null && tree.FilePath is { Length: > 0 } fp)
                paths.Add(fp);
        }
        _sourcePathCache[project.Id] = paths;
        return paths;
    }

    public async ValueTask<RefreshResult> RefreshAsync(IReadOnlyList<string> paths, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var working = Volatile.Read(ref _solution)
                ?? throw new InvalidOperationException("RefreshAsync called before initialization.");
            var reloaded = new List<string>();
            var added = new List<string>();
            var removed = new List<string>();
            var unmapped = new List<string>();

            foreach (var raw in paths)
            {
                var path = Path.GetFullPath(raw);
                var docId = working.GetDocumentIdsWithFilePath(path).FirstOrDefault();

                if (docId is not null && File.Exists(path))        // changed
                {
                    working = working.WithDocumentText(docId, await ReadAsync(path, ct));
                    reloaded.Add(path);
                }
                else if (docId is not null)
                {
                    working = working.RemoveDocument(docId);
                    removed.Add(path);
                }
                else if (File.Exists(path))
                {
                    var project = InferProject(working, path);
                    if (project is null)
                    { unmapped.Add(path); continue; }
                    var doc = project.AddDocument(Path.GetFileName(path), await ReadAsync(path, ct), filePath: path);
                    working = doc.Project.Solution;
                    added.Add(path);
                }
                else
                    unmapped.Add(path);
            }

            if (reloaded.Count + added.Count + removed.Count > 0)
                _sourcePathCache.Clear();

            Volatile.Write(ref _solution, working);
            return new RefreshResult(reloaded.ToArray(), added.ToArray(), removed.ToArray(), unmapped.ToArray());
        }
        finally { _gate.Release(); }
    }

    Project? InferProject(Solution solution, string filePath) =>
        solution.Projects
            .Where(p => p.FilePath is not null)
            .Select(p => (Project: p, Dir: Path.GetDirectoryName(Path.GetFullPath(p.FilePath!))!))
            .Where(x => filePath.StartsWith(x.Dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.Dir.Length)               // most specific project wins
            .Select(x => x.Project)
            .FirstOrDefault();

    static async ValueTask<SourceText> ReadAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return SourceText.From(stream);                        // auto-detects encoding/BOM
    }

    static bool MatchesName(string? filePath, string name, IReadOnlyList<string> relativeTargets)
    {
        if (string.IsNullOrEmpty(filePath))
            return false;

        if (filePath.Equals(name, StringComparison.OrdinalIgnoreCase))
            return true;

        if (Path.GetFileName(filePath).Equals(name, StringComparison.OrdinalIgnoreCase)
            || Path.GetFileNameWithoutExtension(filePath).Equals(name, StringComparison.OrdinalIgnoreCase))
            return true;

        if (relativeTargets.Count > 0)
        {
            try
            {
                var full = Path.GetFullPath(filePath);
                for (var i = 0; i < relativeTargets.Count; i++)
                {
                    if (full.Equals(relativeTargets[i], StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch { /* filePath not normalizable — skip relative matching */ }
        }

        return false;
    }

    List<string> ResolveRelativeTargets(string fileNameOrPath)
    {
        var targets = new List<string>();

        if (Path.IsPathRooted(fileNameOrPath)
            || fileNameOrPath.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) < 0)
            return targets;

        foreach (var root in GetDocumentRoots())
        {
            try
            { targets.Add(Path.GetFullPath(Path.Combine(root, fileNameOrPath))); }
            catch (ArgumentException) { /* path invalid under this root — skip */ }
        }
        return targets;
    }

    IEnumerable<string> GetDocumentRoots()
    {
        var solution = Solution;
        var sln = _loadedSolutionPath ?? solutionPath;
        if (!string.IsNullOrEmpty(sln))
        {
            var dir = Path.GetDirectoryName(sln);
            if (!string.IsNullOrEmpty(dir))
                yield return dir;
        }

        foreach (var p in solution.Projects)
        {
            if (p.FilePath is null)
                continue;

            string? dir = null;
            try
            { dir = Path.GetDirectoryName(Path.GetFullPath(p.FilePath)); }
            catch { /* skip projects whose path can't be normalized */ }
            if (!string.IsNullOrEmpty(dir))
                yield return dir;
        }
    }
}
