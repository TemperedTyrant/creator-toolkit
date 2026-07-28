using TemperedTyrant.CreatorToolkit.Core.Diagnostics;

namespace TemperedTyrant.CreatorToolkit.Infrastructure.Diagnostics;

internal interface IDiagnosticReferenceGenerator
{
    DiagnosticReference Create();
}

internal sealed class CryptographicDiagnosticReferenceGenerator
    : IDiagnosticReferenceGenerator
{
    public DiagnosticReference Create()
    {
        return DiagnosticReference.CreateRandom();
    }
}
