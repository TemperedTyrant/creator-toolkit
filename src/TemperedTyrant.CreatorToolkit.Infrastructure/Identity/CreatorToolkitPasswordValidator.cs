using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Identity;

namespace TemperedTyrant.CreatorToolkit.Infrastructure.Identity;

public sealed class CreatorToolkitPasswordValidator : IPasswordValidator<ApplicationUser>
{
    public const int MinimumScalarCount = 15;
    public const int MaximumScalarCount = 128;
    public const int MaximumPreNormalizationCodeUnitCount =
        MaximumScalarCount * 8;

    private static readonly Lazy<HashSet<string>> CommonPasswords =
        new(LoadCommonPasswords, LazyThreadSafetyMode.ExecutionAndPublication);

    public Task<IdentityResult> ValidateAsync(
        UserManager<ApplicationUser> manager,
        ApplicationUser user,
        string? password)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(user);

        if (password is null)
        {
            return Task.FromResult(Failed(
                "PasswordRequired",
                "A password is required."));
        }

        if (password.Length > MaximumPreNormalizationCodeUnitCount)
        {
            return Task.FromResult(Failed(
                "PasswordTooLong",
                $"Passwords must contain no more than {MaximumScalarCount} Unicode scalar values."));
        }

        string normalized;
        try
        {
            normalized = password.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            return Task.FromResult(Failed(
                "PasswordInvalidUnicode",
                "The password contains invalid Unicode."));
        }

        int scalarCount = normalized.EnumerateRunes().Count();
        if (scalarCount < MinimumScalarCount)
        {
            return Task.FromResult(Failed(
                "PasswordTooShort",
                $"Passwords must contain at least {MinimumScalarCount} Unicode scalar values."));
        }

        if (scalarCount > MaximumScalarCount)
        {
            return Task.FromResult(Failed(
                "PasswordTooLong",
                $"Passwords must contain no more than {MaximumScalarCount} Unicode scalar values."));
        }

        if (CommonPasswords.Value.Contains(normalized)
            || IsContextPassword(normalized, user))
        {
            return Task.FromResult(Failed(
                "PasswordCommon",
                "Choose a password that is not a common or account-related password."));
        }

        return Task.FromResult(IdentityResult.Success);
    }

    private static bool IsContextPassword(string candidate, ApplicationUser user)
    {
        return EqualsWhole(candidate, "TemperedTyrant")
            || EqualsWhole(candidate, "Creator Toolkit")
            || EqualsWhole(candidate, "Creator-Toolkit")
            || EqualsWhole(candidate, user.UserName)
            || EqualsWhole(candidate, user.NormalizedUserName)
            || EqualsWhole(candidate, user.DisplayName);
    }

    private static bool EqualsWhole(string candidate, string? contextValue)
    {
        return !string.IsNullOrEmpty(contextValue)
            && string.Equals(
                candidate,
                contextValue.Normalize(NormalizationForm.FormC),
                StringComparison.OrdinalIgnoreCase);
    }

    private static HashSet<string> LoadCommonPasswords()
    {
        const string resourceName =
            "TemperedTyrant.CreatorToolkit.Infrastructure.Identity.CommonPasswords."
            + "seclists-2026.1-10k-most-common.txt";
        using Stream stream = Assembly
            .GetExecutingAssembly()
            .GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                "The pinned common-password snapshot is unavailable.");
        using StreamReader reader = new(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true);
        HashSet<string> passwords = new(StringComparer.OrdinalIgnoreCase);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length > 0)
            {
                passwords.Add(line.Normalize(NormalizationForm.FormC));
            }
        }

        return passwords;
    }

    private static IdentityResult Failed(string code, string description)
    {
        return IdentityResult.Failed(
            new IdentityError
            {
                Code = code,
                Description = description,
            });
    }
}
