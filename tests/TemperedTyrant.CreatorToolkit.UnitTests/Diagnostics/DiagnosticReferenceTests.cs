using TemperedTyrant.CreatorToolkit.Core.Diagnostics;

namespace TemperedTyrant.CreatorToolkit.UnitTests.Diagnostics;

public sealed class DiagnosticReferenceTests
{
    [Fact]
    public void RandomReferencesUseOpaqueOneHundredTwentyEightBitValues()
    {
        DiagnosticReference[] references = Enumerable.Range(0, 256)
            .Select(_ => DiagnosticReference.CreateRandom())
            .ToArray();

        Assert.Equal(256, references.Select(reference => reference.Value).Distinct().Count());
        Assert.All(
            references,
            reference => Assert.Matches(@"\ACTK-[A-F0-9]{32}\z", reference.Value));
    }
}
