using System.Text;
using Microsoft.AspNetCore.Identity;

namespace TemperedTyrant.CreatorToolkit.Infrastructure.Identity;

public sealed class NfcPasswordHasher(
    PasswordHasher<ApplicationUser> standardHasher)
    : IPasswordHasher<ApplicationUser>
{
    public string HashPassword(ApplicationUser user, string password)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(password);
        if (password.Length > CreatorToolkitPasswordValidator.MaximumPreNormalizationCodeUnitCount)
        {
            throw new ArgumentException("The password input is too large.", nameof(password));
        }

        return standardHasher.HashPassword(user, password.Normalize(NormalizationForm.FormC));
    }

    public PasswordVerificationResult VerifyHashedPassword(
        ApplicationUser user,
        string hashedPassword,
        string providedPassword)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(hashedPassword);
        ArgumentNullException.ThrowIfNull(providedPassword);
        if (providedPassword.Length
            > CreatorToolkitPasswordValidator.MaximumPreNormalizationCodeUnitCount)
        {
            return PasswordVerificationResult.Failed;
        }

        return standardHasher.VerifyHashedPassword(
            user,
            hashedPassword,
            providedPassword.Normalize(NormalizationForm.FormC));
    }
}
