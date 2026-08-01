using System.Text.RegularExpressions;

namespace TemperedTyrant.CreatorToolkit.UnitTests.Architecture;

public sealed partial class DeploymentSecurityTests
{
    [Fact]
    public void DockerfileKeepsPinnedShelllessNonRootRuntimeBoundary()
    {
        string dockerfile = ReadRepositoryFile("Dockerfile");
        Assert.Matches(
            PinnedSdkImagePattern(),
            dockerfile);
        Assert.Matches(
            PinnedRuntimeImagePattern(),
            dockerfile);
        Assert.Contains("USER 1654:1654", dockerfile, StringComparison.Ordinal);
        Assert.Contains("VOLUME [\"/app/data\"]", dockerfile, StringComparison.Ordinal);
        Assert.Contains(
            "COPY --from=build --chown=1654:1654 /app/data/ ./data/",
            dockerfile,
            StringComparison.Ordinal);
        Assert.Contains(
            "chmod 0444 /app/notices/LICENSE /app/notices/THIRD_PARTY_NOTICES.md",
            dockerfile,
            StringComparison.Ordinal);
        Assert.Contains(
            "ENTRYPOINT [\"/app/creator-toolkit\"]",
            dockerfile,
            StringComparison.Ordinal);

        string runtime = dockerfile[(dockerfile.IndexOf(
            " AS runtime",
            StringComparison.Ordinal) + " AS runtime".Length)..];
        string[] forbiddenRuntimeInstructions =
        [
            "RUN ",
            " apt ",
            "apk ",
            "curl ",
            "wget ",
            "COPY tests/",
            "COPY .git",
            "COPY .env",
        ];
        foreach (string forbidden in forbiddenRuntimeInstructions)
        {
            Assert.DoesNotContain(forbidden, runtime, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ComposeKeepsOneLoopbackOnlyHardenedServiceAndOneVolume()
    {
        string compose = ReadRepositoryFile("compose.yaml");
        string normalized = compose.Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Single(ServiceDeclarationPattern().Matches(normalized).Cast<Match>());
        Assert.Single(VolumeDeclarationPattern().Matches(normalized).Cast<Match>());
        Assert.Contains(
            "${CREATOR_TOOLKIT_HOST_BIND:-127.0.0.1}:${CREATOR_TOOLKIT_HOST_PORT:-8080}:8080",
            compose,
            StringComparison.Ordinal);
        Assert.Contains("restart: unless-stopped", compose, StringComparison.Ordinal);
        Assert.Contains("stop_grace_period: 30s", compose, StringComparison.Ordinal);
        Assert.Contains("cap_drop:\n      - ALL", normalized, StringComparison.Ordinal);
        Assert.Contains("no-new-privileges:true", compose, StringComparison.Ordinal);
        Assert.Contains(
            "test: [\"CMD\", \"creator-toolkit\", \"healthcheck\"]",
            compose,
            StringComparison.Ordinal);
        Assert.Contains("creator-toolkit-data:/app/data", compose, StringComparison.Ordinal);

        string[] forbidden =
        [
            "privileged:",
            "network_mode:",
            "docker.sock",
            "devices:",
            "pid: host",
            "ipc: host",
        ];
        foreach (string value in forbidden)
        {
            Assert.DoesNotContain(value, compose, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void DockerBuildContextExcludesTestsRuntimeStateAndLocalConfiguration()
    {
        string dockerignore = ReadRepositoryFile(".dockerignore");
        string[] lines = dockerignore
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("**", lines[0]);
        Assert.Contains("!src/**", lines);
        Assert.DoesNotContain(lines, line => line.StartsWith("!tests", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains(".env", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains(".git", StringComparison.Ordinal));

        string dockerfile = ReadRepositoryFile("Dockerfile");
        Assert.DoesNotContain("COPY tests", dockerfile, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("COPY . ", dockerfile, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("creator-toolkit.db", dockerfile, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DataProtection-Keys", dockerfile, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WorkflowActionsRemainImmutableAndReadOnlyByDefault()
    {
        DirectoryInfo repository = FindRepositoryRoot();
        string[] workflows = Directory.GetFiles(
            Path.Combine(repository.FullName, ".github", "workflows"),
            "*.yml",
            SearchOption.TopDirectoryOnly);
        Assert.NotEmpty(workflows);

        foreach (string workflow in workflows)
        {
            string source = File.ReadAllText(workflow);
            Assert.Contains("permissions:\n  contents: read", source, StringComparison.Ordinal);
            MatchCollection actions = ActionReferencePattern().Matches(source);
            Assert.NotEmpty(actions);
            Assert.All(
                actions.Cast<Match>(),
                action => Assert.Matches(FullCommitShaPattern(), action.Groups[1].Value));
            Assert.DoesNotContain("pull_request_target:", source, StringComparison.Ordinal);
            Assert.DoesNotContain("continue-on-error:", source, StringComparison.Ordinal);
        }
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        return File.ReadAllText(Path.Combine(FindRepositoryRoot().FullName, relativePath));
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

        throw new InvalidOperationException("Could not locate the repository root.");
    }

    [GeneratedRegex(
        "^FROM mcr\\.microsoft\\.com/dotnet/sdk:10\\.0\\.302-noble@sha256:[0-9a-f]{64} AS build$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex PinnedSdkImagePattern();

    [GeneratedRegex(
        "^FROM mcr\\.microsoft\\.com/dotnet/aspnet:10\\.0\\.10-noble-chiseled-extra@sha256:[0-9a-f]{64} AS runtime$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex PinnedRuntimeImagePattern();

    [GeneratedRegex(
        "(?m)^  (?!#)([a-zA-Z0-9_-]+):\\n    image:",
        RegexOptions.CultureInvariant)]
    private static partial Regex ServiceDeclarationPattern();

    [GeneratedRegex(
        "(?m)^  creator-toolkit-data:$",
        RegexOptions.CultureInvariant)]
    private static partial Regex VolumeDeclarationPattern();

    [GeneratedRegex(
        "(?m)^\\s*-?\\s*uses:\\s*[^@\\s]+@([^\\s]+)\\s*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ActionReferencePattern();

    [GeneratedRegex("^[0-9a-f]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex FullCommitShaPattern();
}
