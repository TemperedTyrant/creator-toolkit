using System.Reflection;
using System.Xml.Linq;
using TemperedTyrant.CreatorToolkit.Core;

namespace TemperedTyrant.CreatorToolkit.UnitTests.Architecture;

public sealed class ProjectDependencyTests
{
    private static readonly string[] ApprovedSolutionProjects =
    [
        "src/TemperedTyrant.CreatorToolkit.Core/TemperedTyrant.CreatorToolkit.Core.csproj",
        "src/TemperedTyrant.CreatorToolkit.Infrastructure/TemperedTyrant.CreatorToolkit.Infrastructure.csproj",
        "src/TemperedTyrant.CreatorToolkit.Web/TemperedTyrant.CreatorToolkit.Web.csproj",
        "tests/TemperedTyrant.CreatorToolkit.IntegrationTests/TemperedTyrant.CreatorToolkit.IntegrationTests.csproj",
        "tests/TemperedTyrant.CreatorToolkit.UnitTests/TemperedTyrant.CreatorToolkit.UnitTests.csproj",
    ];

    private static readonly string[] ForbiddenPackagePrefixes =
    [
        "Discord",
        "Kafka",
        "MassTransit",
        "Npgsql",
        "RabbitMQ",
        "StackExchange.Redis",
        "Temporal",
    ];

    [Fact]
    public void CoreAssemblyHasNoForbiddenFrameworkOrProjectDependencies()
    {
        string[] references = typeof(CoreAssemblyMarker)
            .Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain(references, name => name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
        Assert.DoesNotContain(references, name => name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
        Assert.DoesNotContain(references, name => name.StartsWith("Microsoft.Data.Sqlite", StringComparison.Ordinal));
        Assert.DoesNotContain("TemperedTyrant.CreatorToolkit.Infrastructure", references);
        Assert.DoesNotContain("creator-toolkit", references);
    }

    [Fact]
    public void ProjectReferencesFollowTheApprovedDependencyDirection()
    {
        DirectoryInfo repository = FindRepositoryRoot();

        AssertProjectReferences(repository, ApprovedSolutionProjects[0]);
        AssertProjectReferences(repository, ApprovedSolutionProjects[1], ApprovedSolutionProjects[0]);
        AssertProjectReferences(
            repository,
            ApprovedSolutionProjects[2],
            ApprovedSolutionProjects[0],
            ApprovedSolutionProjects[1]);
        AssertProjectReferences(repository, ApprovedSolutionProjects[3], ApprovedSolutionProjects[1], ApprovedSolutionProjects[2]);
        AssertProjectReferences(repository, ApprovedSolutionProjects[4], ApprovedSolutionProjects[0]);
    }

    [Fact]
    public void SolutionContainsOnlyTheApprovedProjects()
    {
        DirectoryInfo repository = FindRepositoryRoot();
        XDocument solution = XDocument.Load(Path.Combine(repository.FullName, "TemperedTyrant.CreatorToolkit.slnx"));

        string[] projects = solution
            .Descendants("Project")
            .Select(element => element.Attribute("Path")?.Value)
            .OfType<string>()
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ApprovedSolutionProjects.Order(StringComparer.Ordinal), projects);
    }

    [Fact]
    public void PublicTechnicalNamesMatchTheApprovedIdentity()
    {
        Assembly coreAssembly = typeof(CoreAssemblyMarker).Assembly;

        Assert.Equal("TemperedTyrant.CreatorToolkit.Core", coreAssembly.GetName().Name);
        Assert.Equal("TemperedTyrant.CreatorToolkit.Core", typeof(CoreAssemblyMarker).Namespace);

        DirectoryInfo repository = FindRepositoryRoot();
        XDocument webProject = XDocument.Load(Path.Combine(repository.FullName, ApprovedSolutionProjects[2]));
        Assert.Equal("creator-toolkit", webProject.Descendants("AssemblyName").Single().Value);
        Assert.Equal(
            "TemperedTyrant.CreatorToolkit.Web",
            webProject.Descendants("RootNamespace").Single().Value);
    }

    [Fact]
    public void ProjectsDoNotReferenceForbiddenInfrastructurePackages()
    {
        DirectoryInfo repository = FindRepositoryRoot();
        IEnumerable<string> packageNames = ApprovedSolutionProjects
            .Select(path => XDocument.Load(Path.Combine(repository.FullName, path)))
            .SelectMany(project => project.Descendants("PackageReference"))
            .Select(element => element.Attribute("Include")?.Value)
            .OfType<string>();

        Assert.DoesNotContain(
            packageNames,
            package => ForbiddenPackagePrefixes.Any(
                prefix => package.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
    }

    private static void AssertProjectReferences(
        DirectoryInfo repository,
        string projectPath,
        params string[] expectedReferences)
    {
        string fullProjectPath = Path.Combine(repository.FullName, projectPath);
        string projectDirectory = Path.GetDirectoryName(fullProjectPath)
            ?? throw new InvalidOperationException($"Project path has no directory: {projectPath}");
        XDocument project = XDocument.Load(fullProjectPath);

        string[] actualReferences = project
            .Descendants("ProjectReference")
            .Select(element => element.Attribute("Include")?.Value)
            .OfType<string>()
            .Select(reference => Path.GetFullPath(reference, projectDirectory))
            .Select(reference => Path.GetRelativePath(repository.FullName, reference).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expectedReferences.Order(StringComparer.Ordinal), actualReferences);
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TemperedTyrant.CreatorToolkit.slnx")))
            {
                return directory;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root from the test output directory.");
    }
}
