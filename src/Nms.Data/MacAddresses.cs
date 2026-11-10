namespace Nms.Data;

/// <summary>
/// Canonical MAC form used for device identity: lowercase, colon separated (<c>aa:bb:cc:dd:ee:ff</c>), the form
/// Linux writes to /proc/net/arp. Accepts colon, dash, dot (Cisco <c>aabb.ccdd.eeff</c>) and bare hex input.
/// </summary>
public static class MacAddresses
{
    /// <summary>The canonical form, or null when <paramref name="value"/> is empty or is not a 48-bit MAC.</summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var hex = new System.Text.StringBuilder(12);
        foreach (var ch in value.Trim())
        {
            if (Uri.IsHexDigit(ch)) hex.Append(char.ToLowerInvariant(ch));
            else if (ch is not (':' or '-' or '.')) return null;
        }
        if (hex.Length != 12) return null;
        var s = hex.ToString();
        if (s is "000000000000" or "ffffffffffff") return null;   // incomplete ARP entry / broadcast
        return string.Join(':', Enumerable.Range(0, 6).Select(i => s.Substring(i * 2, 2)));
    }

    /// <summary>True when the input is empty (no MAC) or normalizes; false for garbage the user should fix.</summary>
    public static bool IsValidOrEmpty(string? value) => string.IsNullOrWhiteSpace(value) || Normalize(value) is not null;
}
