using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TemperedTyrant.CreatorToolkit.Infrastructure.Identity;
using TemperedTyrant.CreatorToolkit.IntegrationTests.TestSupport;

namespace TemperedTyrant.CreatorToolkit.IntegrationTests.Identity;

public sealed class PasswordPolicyTests
{
    [Fact]
    public async Task ScalarLimitsAcceptUnicodeSpacesAndPasswordManagerPaste()
    {
        using TestDataDirectory data = new();
        await using ServiceProvider provider = TestServices.Create(data.Path);
        await TestServices.InitializeAsync(provider);
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        CreatorToolkitPasswordValidator validator = scope.ServiceProvider
            .GetRequiredService<CreatorToolkitPasswordValidator>();
        UserManager<ApplicationUser> manager = scope.ServiceProvider
            .GetRequiredService<UserManager<ApplicationUser>>();
        ApplicationUser user = ApplicationUser.CreateInitialOwner(
            "local-owner",
            null,
            DateTimeOffset.UnixEpoch);

        Assert.True((await validator.ValidateAsync(
            manager,
            user,
            string.Concat(Enumerable.Repeat("😀", 15)))).Succeeded);
        Assert.True((await validator.ValidateAsync(
            manager,
            user,
            " correct horse battery staple ")).Succeeded);
        Assert.True((await validator.ValidateAsync(
            manager,
            user,
            string.Concat(Enumerable.Repeat("🌱", 128)))).Succeeded);

        IdentityResult tooShort = await validator.ValidateAsync(
            manager,
            user,
            string.Concat(Enumerable.Repeat("😀", 14)));
        IdentityResult tooLong = await validator.ValidateAsync(
            manager,
            user,
            string.Concat(Enumerable.Repeat("🌱", 129)));
        IdentityResult abusiveInput = await validator.ValidateAsync(
            manager,
            user,
            new string(
                'a',
                CreatorToolkitPasswordValidator.MaximumPreNormalizationCodeUnitCount + 1));

        Assert.Contains(tooShort.Errors, error => error.Code == "PasswordTooShort");
        Assert.Contains(tooLong.Errors, error => error.Code == "PasswordTooLong");
        Assert.Contains(abusiveInput.Errors, error => error.Code == "PasswordTooLong");
    }

    [Fact]
    public async Task BlocklistAndContextChecksCompareOnlyWholeNormalizedPasswords()
    {
        using TestDataDirectory data = new();
        await using ServiceProvider provider = TestServices.Create(data.Path);
        await TestServices.InitializeAsync(provider);
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        CreatorToolkitPasswordValidator validator = scope.ServiceProvider
            .GetRequiredService<CreatorToolkitPasswordValidator>();
        UserManager<ApplicationUser> manager = scope.ServiceProvider
            .GetRequiredService<UserManager<ApplicationUser>>();
        ApplicationUser user = ApplicationUser.CreateInitialOwner(
            "LongAccountContextValue",
            "Long Displ\u00e1y Context",
            DateTimeOffset.UnixEpoch);

        IdentityResult common = await validator.ValidateAsync(
            manager,
            user,
            "FILMS+PIC+GALERIES");
        IdentityResult accountContext = await validator.ValidateAsync(
            manager,
            user,
            "longaccountcontextvalue");
        IdentityResult containingCommon = await validator.ValidateAsync(
            manager,
            user,
            "prefix-films+pic+galeries-suffix");
        IdentityResult containingContext = await validator.ValidateAsync(
            manager,
            user,
            "prefix-LongAccountContextValue-suffix");
        IdentityResult normalizedContext = await validator.ValidateAsync(
            manager,
            user,
            "Long Displa\u0301y Context");

        Assert.Contains(common.Errors, error => error.Code == "PasswordCommon");
        Assert.Contains(accountContext.Errors, error => error.Code == "PasswordCommon");
        Assert.Contains(normalizedContext.Errors, error => error.Code == "PasswordCommon");
        Assert.True(containingCommon.Succeeded);
        Assert.True(containingContext.Succeeded);
    }

    [Fact]
    public async Task DelegatingHasherUsesNfcWithoutChangingStandardHashFormat()
    {
        using TestDataDirectory data = new();
        await using ServiceProvider provider = TestServices.Create(data.Path);
        await TestServices.InitializeAsync(provider);
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        IPasswordHasher<ApplicationUser> hasher = scope.ServiceProvider
            .GetRequiredService<IPasswordHasher<ApplicationUser>>();
        ApplicationUser user = ApplicationUser.CreateInitialOwner(
            "local-owner",
            null,
            DateTimeOffset.UnixEpoch);
        string decomposed = string.Concat(Enumerable.Repeat("e\u0301", 15));
        string composed = string.Concat(Enumerable.Repeat("\u00e9", 15));

        string hash = hasher.HashPassword(user, decomposed);
        PasswordHasher<ApplicationUser> legacyHasher = new(
            Options.Create(
                new PasswordHasherOptions
                {
                    CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV2,
                }));
        string legacyHash = legacyHasher.HashPassword(user, composed);

        Assert.StartsWith("AQAAAA", hash, StringComparison.Ordinal);
        Assert.NotEqual(
            PasswordVerificationResult.Failed,
            hasher.VerifyHashedPassword(user, hash, composed));
        Assert.Equal(
            PasswordVerificationResult.Failed,
            hasher.VerifyHashedPassword(user, hash, composed + " "));
        Assert.Equal(
            PasswordVerificationResult.SuccessRehashNeeded,
            hasher.VerifyHashedPassword(user, legacyHash, composed));
        Assert.Equal(
            PasswordVerificationResult.Failed,
            hasher.VerifyHashedPassword(
                user,
                hash,
                new string(
                    'a',
                    CreatorToolkitPasswordValidator.MaximumPreNormalizationCodeUnitCount + 1)));
    }
}
