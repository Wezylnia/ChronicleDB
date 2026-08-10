using System.Xml.Linq;

namespace ChronicleDB.ArchitectureTests;

public sealed class ArchitectureRulesTests
{
    private static readonly IReadOnlyDictionary<string, string[]> AllowedSourceDependencies =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["ChronicleDB.Core"] = [],
            ["ChronicleDB.Diagnostics"] = ["ChronicleDB.Core"],
            ["ChronicleDB.Mvcc"] = ["ChronicleDB.Core"],
            ["ChronicleDB.History"] = ["ChronicleDB.Core", "ChronicleDB.Mvcc"],
            ["ChronicleDB.Storage"] = ["ChronicleDB.Core", "ChronicleDB.Diagnostics"],
            ["ChronicleDB.Wal"] = ["ChronicleDB.Core", "ChronicleDB.Diagnostics"],
            ["ChronicleDB.Indexing.Abstractions"] = ["ChronicleDB.Core"],
            ["ChronicleDB.Indexing.Baseline"] =
            [
                "ChronicleDB.Core",
                "ChronicleDB.Diagnostics",
                "ChronicleDB.Indexing.Abstractions"
            ],
            ["ChronicleDB.Transactions"] =
            [
                "ChronicleDB.Core",
                "ChronicleDB.Diagnostics",
                "ChronicleDB.History",
                "ChronicleDB.Indexing.Abstractions",
                "ChronicleDB.Mvcc",
                "ChronicleDB.Storage",
                "ChronicleDB.Wal"
            ],
            ["ChronicleDB.Recovery"] =
            [
                "ChronicleDB.Core",
                "ChronicleDB.Diagnostics",
                "ChronicleDB.History",
                "ChronicleDB.Indexing.Abstractions",
                "ChronicleDB.Mvcc",
                "ChronicleDB.Storage",
                "ChronicleDB.Wal"
            ],
            ["ChronicleDB.Maintenance"] =
            [
                "ChronicleDB.Core",
                "ChronicleDB.Diagnostics",
                "ChronicleDB.History",
                "ChronicleDB.Indexing.Abstractions",
                "ChronicleDB.Mvcc",
                "ChronicleDB.Storage"
            ],
            ["ChronicleDB"] =
            [
                "ChronicleDB.Core",
                "ChronicleDB.Diagnostics",
                "ChronicleDB.History",
                "ChronicleDB.Indexing.Abstractions",
                "ChronicleDB.Indexing.Baseline",
                "ChronicleDB.Maintenance",
                "ChronicleDB.Mvcc",
                "ChronicleDB.Recovery",
                "ChronicleDB.Storage",
                "ChronicleDB.Transactions",
                "ChronicleDB.Wal"
            ]
        };

    [Fact]
    public void SourceProjectDependenciesMatchArchitectureContract()
    {
        var graph = LoadSourceProjectGraph();

        Assert.Equal(
            AllowedSourceDependencies.Keys.Order(StringComparer.Ordinal),
            graph.Keys.Order(StringComparer.Ordinal));

        foreach (var (project, expectedDependencies) in AllowedSourceDependencies)
        {
            Assert.Equal(
                expectedDependencies.Order(StringComparer.Ordinal),
                graph[project].Order(StringComparer.Ordinal));
        }
    }

    [Fact]
    public void SourceProjectReferencesDoNotContainCycles()
    {
        var graph = LoadSourceProjectGraph();
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);

        foreach (var project in graph.Keys)
        {
            Visit(project, graph, visiting, visited, []);
        }
    }

    [Fact]
    public void OnlyPublicFacadeReferencesBaselineIndexImplementation()
    {
        var graph = LoadSourceProjectGraph();
        var consumers = graph
            .Where(pair => pair.Value.Contains("ChronicleDB.Indexing.Baseline", StringComparer.Ordinal))
            .Select(pair => pair.Key)
            .ToArray();

        Assert.Equal(["ChronicleDB"], consumers);
    }

    [Fact]
    public void SourceProjectsHaveNoUnreviewedExternalPackages()
    {
        foreach (var projectFile in SourceProjectFiles())
        {
            var packages = LoadProject(projectFile)
                .Descendants("PackageReference")
                .Select(element => element.Attribute("Include")?.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray();

            Assert.True(
                packages.Length == 0,
                $"{RelativePath(projectFile)} introduces external packages: {string.Join(", ", packages)}. " +
                "Record the dependency decision and update this boundary test deliberately.");
        }
    }

    [Fact]
    public void UnsafeCodeIsDisabledOutsideReservedNativeMemoryModule()
    {
        var globalProperties = XDocument.Load(Path.Combine(RepositoryRoot(), "Directory.Build.props"));
        Assert.Equal("false", PropertyValues(globalProperties, "AllowUnsafeBlocks").Single());

        foreach (var projectFile in SourceProjectFiles())
        {
            var projectName = Path.GetFileNameWithoutExtension(projectFile);
            var unsafeValues = PropertyValues(LoadProject(projectFile), "AllowUnsafeBlocks");

            if (projectName == "ChronicleDB.Memory.Native")
            {
                Assert.Equal(["true"], unsafeValues);
            }
            else
            {
                Assert.DoesNotContain("true", unsafeValues, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void PackageVersionsAreCentralized()
    {
        foreach (var projectFile in AllProjectFiles())
        {
            var versionedReferences = LoadProject(projectFile)
                .Descendants("PackageReference")
                .Where(element => element.Attribute("Version") is not null)
                .Select(element => element.Attribute("Include")?.Value ?? "<unknown>")
                .ToArray();

            Assert.True(
                versionedReferences.Length == 0,
                $"{RelativePath(projectFile)} contains local package versions: " +
                string.Join(", ", versionedReferences));
        }
    }

    [Fact]
    public void AllProjectsUseNet10Baseline()
    {
        var properties = XDocument.Load(Path.Combine(RepositoryRoot(), "Directory.Build.props"));
        Assert.Equal("net10.0", PropertyValues(properties, "TargetFramework").Single());
        Assert.Equal("14.0", PropertyValues(properties, "LangVersion").Single());

        foreach (var projectFile in AllProjectFiles())
        {
            var localTargets = PropertyValues(LoadProject(projectFile), "TargetFramework");
            Assert.All(localTargets, target => Assert.Equal("net10.0", target));
        }
    }

    [Fact]
    public void TemplatePlaceholderTypesAreNotCommitted()
    {
        var placeholders = Directory
            .EnumerateFiles(RepositoryRoot(), "Class1.cs", SearchOption.AllDirectories)
            .Where(path => !IsGeneratedPath(path))
            .Select(RelativePath)
            .ToArray();

        Assert.Empty(placeholders);
    }

    private static Dictionary<string, string[]> LoadSourceProjectGraph()
    {
        var projectFiles = SourceProjectFiles();

        return projectFiles.ToDictionary(
            projectFile => Path.GetFileNameWithoutExtension(projectFile)!,
            projectFile => LoadProject(projectFile)
                .Descendants("ProjectReference")
                .Select(element => element.Attribute("Include")?.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => NormalizeProjectReference(projectFile, value!))
                .Where(path => IsUnder(path, Path.Combine(RepositoryRoot(), "src")))
                .Select(path => Path.GetFileNameWithoutExtension(path)!)
                .Order(StringComparer.Ordinal)
                .ToArray(),
            StringComparer.Ordinal);
    }

    private static string NormalizeProjectReference(string projectFile, string reference)
    {
        var normalized = reference
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(projectFile)!, normalized));
    }

    private static void Visit(
        string project,
        IReadOnlyDictionary<string, string[]> graph,
        ISet<string> visiting,
        ISet<string> visited,
        IReadOnlyList<string> path)
    {
        if (visited.Contains(project))
        {
            return;
        }

        if (!visiting.Add(project))
        {
            throw new InvalidOperationException(
                $"Project reference cycle detected: {string.Join(" -> ", path.Append(project))}");
        }

        foreach (var dependency in graph[project])
        {
            Visit(dependency, graph, visiting, visited, path.Append(project).ToArray());
        }

        visiting.Remove(project);
        visited.Add(project);
    }

    private static string[] SourceProjectFiles()
        => Directory
            .EnumerateFiles(Path.Combine(RepositoryRoot(), "src"), "*.csproj", SearchOption.AllDirectories)
            .Where(path => !IsGeneratedPath(path))
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string[] AllProjectFiles()
        => Directory
            .EnumerateFiles(RepositoryRoot(), "*.csproj", SearchOption.AllDirectories)
            .Where(path => !IsGeneratedPath(path))
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static XDocument LoadProject(string projectFile) => XDocument.Load(projectFile);

    private static string[] PropertyValues(XDocument document, string propertyName)
        => document
            .Descendants(propertyName)
            .Select(element => element.Value.Trim())
            .Where(value => value.Length > 0)
            .ToArray();

    private static bool IsGeneratedPath(string path)
        => path.Contains($"{Path.DirectorySeparatorChar}.artifacts{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
           || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
           || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

    private static bool IsUnder(string path, string directory)
    {
        var relative = Path.GetRelativePath(directory, path);
        return relative != ".."
               && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
               && !Path.IsPathRooted(relative);
    }

    private static string RelativePath(string path)
        => Path.GetRelativePath(RepositoryRoot(), path).Replace(Path.DirectorySeparatorChar, '/');

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ChronicleDB.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
               ?? throw new DirectoryNotFoundException("Could not locate the ChronicleDB repository root.");
    }
}
