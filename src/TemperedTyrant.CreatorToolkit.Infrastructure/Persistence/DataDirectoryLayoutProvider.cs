namespace TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;

public sealed class DataDirectoryLayoutProvider(DataDirectoryLayout layout)
{
    public DataDirectoryLayout Layout { get; } = layout;
}
