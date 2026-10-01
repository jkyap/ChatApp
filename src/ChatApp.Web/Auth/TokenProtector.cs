using System.Security.Cryptography;

namespace ChatApp.Web.Auth;

/// <summary>
/// Login tickets: "userId.expiry.signature". The server keeps no session
/// list - the ticket carries everything, and the HMAC signature proves it
/// wasn't tampered with. Delete the key file to invalidate all tickets.
/// Trade-off: one user's ticket can't be revoked early (7-day expiry).
/// </summary>
public sealed class TokenProtector
{
    private const int TokenLifetimeDays = 7;
    private readonly byte[] _key;

    public TokenProtector(string keyFilePath)
    {
        if (File.Exists(keyFilePath))
        {
            _key = Convert.FromBase64String(File.ReadAllText(keyFilePath).Trim());
        }
        else
        {
            _key = RandomNumberGenerator.GetBytes(32);
            File.WriteAllText(keyFilePath, Convert.ToBase64String(_key));
        }
    }

    public string Issue(int userId)
    {
        var payload = $"{userId}.{DateTimeOffset.UtcNow.AddDays(TokenLifetimeDays).ToUnixTimeSeconds()}";
        return $"{payload}.{Sign(payload)}";
    }

    /// <summary>Returns the user id when the ticket is well-formed, unexpired and correctly signed.</summary>
    public int? Validate(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;

        var parts = token.Split('.');
        if (parts.Length != 3) return null;
        if (!int.TryParse(parts[0], out var userId)) return null;
        if (!long.TryParse(parts[1], out var expiryUnix)) return null;
        if (DateTimeOffset.FromUnixTimeSeconds(expiryUnix) < DateTimeOffset.UtcNow) return null;

        var expected = Sign($"{parts[0]}.{parts[1]}");
        // constant-time compare: don't leak how close a forged signature is
        var expectedBytes = Convert.FromHexString(expected);
        byte[] providedBytes;
        try { providedBytes = Convert.FromHexString(parts[2]); }
        catch (FormatException) { return null; }

        return CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes) ? userId : null;
    }

    private string Sign(string payload) =>
        Convert.ToHexString(HMACSHA256.HashData(_key, System.Text.Encoding.UTF8.GetBytes(payload)));
}
