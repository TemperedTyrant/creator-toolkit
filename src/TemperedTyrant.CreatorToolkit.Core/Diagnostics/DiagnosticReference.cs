using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace TemperedTyrant.CreatorToolkit.Core.Diagnostics;

public sealed partial record DiagnosticReference
{
    public DiagnosticReference(string value)
    {
        if (!DiagnosticReferencePattern().IsMatch(value))
        {
            throw new ArgumentException(
                "The diagnostic reference is not in the supported opaque format.",
                nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public static DiagnosticReference CreateRandom()
    {
        return new DiagnosticReference(
            $"CTK-{Convert.ToHexString(RandomNumberGenerator.GetBytes(16))}");
    }

    public override string ToString() => Value;

    [GeneratedRegex(@"\ACTK-[A-F0-9]{32}\z", RegexOptions.CultureInvariant)]
    private static partial Regex DiagnosticReferencePattern();
}
