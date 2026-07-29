using System.Reflection;
using System.Security.Cryptography;
using TemperedTyrant.CreatorToolkit.Infrastructure;

namespace TemperedTyrant.CreatorToolkit.IntegrationTests.Identity;

public sealed class CommonPasswordSnapshotTests
{
    [Fact]
    public void EmbeddedSnapshotMatchesReviewedSecListsArtifact()
    {
        const string resourceName =
            "TemperedTyrant.CreatorToolkit.Infrastructure.Identity.CommonPasswords."
            + "seclists-2026.1-10k-most-common.txt";
        using Stream stream = typeof(InfrastructureAssemblyMarker)
            .Assembly
            .GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("The password snapshot is missing.");
        using MemoryStream copy = new();
        stream.CopyTo(copy);
        byte[] content = copy.ToArray();

        Assert.Equal(
            "4adb3f0afb4a10cf19ebe48d8c69a46f934bbc8d77c694c210564f9583e7f4ba",
            Convert.ToHexStringLower(SHA256.HashData(content)));
        Assert.Equal(10_000, content.Count(value => value == (byte)'\n'));
    }
}
