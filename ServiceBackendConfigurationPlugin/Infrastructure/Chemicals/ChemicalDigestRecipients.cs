using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mail;

namespace ServiceBackendConfigurationPlugin.Infrastructure.Chemicals;

public static class ChemicalDigestRecipients
{
    /// <summary>
    /// DigestRecipients as stored (comma-separated; ';' tolerated) → trimmed,
    /// lower-case, valid, distinct addresses in their original order. Display
    /// forms like "Anna &lt;anna@example.com&gt;" are rejected rather than guessed at.
    /// </summary>
    public static IReadOnlyList<string> Parse(string raw) =>
        (raw ?? string.Empty)
        .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(address => address.ToLowerInvariant())
        .Where(address => MailAddress.TryCreate(address, out var parsed) && parsed.Address == address)
        .Distinct(StringComparer.Ordinal)
        .ToList();
}
