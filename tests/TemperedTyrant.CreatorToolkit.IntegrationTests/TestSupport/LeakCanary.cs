using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;

namespace TemperedTyrant.CreatorToolkit.IntegrationTests.TestSupport;

internal sealed class LeakCanary
{
    private LeakCanary(string category, string value)
    {
        Category = category;
        Value = value;
    }

    internal string Category { get; }

    internal string Value { get; }

    internal static LeakCanary Create(string category)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        return new LeakCanary(
            category,
            $"ctk-canary-{WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32))}");
    }

    internal void AssertAbsent(string destination, string? content)
    {
        if (content?.Contains(Value, StringComparison.Ordinal) == true)
        {
            Assert.Fail(
                $"Leak canary category '{Category}' appeared in {destination}.");
        }
    }

    internal void AssertAbsent(string destination, IEnumerable<string> content)
    {
        if (content.Any(value => value.Contains(Value, StringComparison.Ordinal)))
        {
            Assert.Fail(
                $"Leak canary category '{Category}' appeared in {destination}.");
        }
    }

    internal void AssertAbsent(HttpResponseMessage response)
    {
        AssertAbsent("HTTP response headers", SerializeHeaders(response.Headers));
        AssertAbsent("HTTP content headers", SerializeHeaders(response.Content.Headers));
        AssertAbsent("redirect location", response.Headers.Location?.OriginalString);
    }

    private static IEnumerable<string> SerializeHeaders(
        IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers)
    {
        return headers.SelectMany(
            header => header.Value.Select(value => $"{header.Key}:{value}"));
    }
}
