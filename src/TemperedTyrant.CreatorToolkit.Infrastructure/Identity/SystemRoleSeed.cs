using Microsoft.AspNetCore.Identity;

namespace TemperedTyrant.CreatorToolkit.Infrastructure.Identity;

internal static class SystemRoleSeed
{
    internal static readonly Guid OwnerId = new("0197fd3c-5a8d-7a20-b52b-5bd4a7dd7a01");
    internal static readonly Guid AdminId = new("0197fd3c-5a8d-7a20-b52b-5bd4a7dd7a02");
    internal static readonly Guid EditorId = new("0197fd3c-5a8d-7a20-b52b-5bd4a7dd7a03");
    internal static readonly Guid ViewerId = new("0197fd3c-5a8d-7a20-b52b-5bd4a7dd7a04");

    internal static IEnumerable<IdentityRole<Guid>> All
    {
        get
        {
            yield return Create(OwnerId, "Owner", "owner");
            yield return Create(AdminId, "Admin", "admin");
            yield return Create(EditorId, "Editor", "editor");
            yield return Create(ViewerId, "Viewer", "viewer");
        }
    }

    private static IdentityRole<Guid> Create(Guid id, string name, string concurrencyStamp)
    {
        return new IdentityRole<Guid>
        {
            Id = id,
            Name = name,
            NormalizedName = name.ToUpperInvariant(),
            ConcurrencyStamp = concurrencyStamp,
        };
    }
}
