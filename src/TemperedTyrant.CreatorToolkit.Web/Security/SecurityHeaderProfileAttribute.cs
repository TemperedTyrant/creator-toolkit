namespace TemperedTyrant.CreatorToolkit.Web.Security;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class SensitiveSecurityHeaderProfileAttribute : Attribute;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class SetupSecurityHeaderProfileAttribute : Attribute;
