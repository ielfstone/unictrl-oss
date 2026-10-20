using System.Security.Cryptography;
using System.Text.Json;
namespace Unictrl;
internal sealed record UpdateAsset(string Url, string Sha256, long Size);
internal sealed record UpdateRelease(string Version, UpdateAsset Macos, UpdateAsset Windows, UpdateAsset? WindowsInstaller = null);
internal static class UpdateProtocol
{
    public const string Feed = "https://github.com/ielfstone/unictrl-oss/releases/latest/download/update-v1.json";
    public const string PublicKey = "MIIBigKCAYEA0ZI9WH2fvSbKi7pdq0XLYS7eztsiXcLa7TnshlhkANyfAE3MtHBk9Ryvnx1eckzZqCtrlVYH64M3sMQvXwme+f7I3NSvdEBs2aukSp+k5QzU8VDj5iIu8t+juop3X9zLtlQm8QykHpyYytnhk5WsvsX+UZ4zAaLBAey/2g1gpYeHR3dFQG2YNbXvQTMEFrm7ou3THnoUhPYG13wYC0T1YZs9EYluo+YVoBoQh6Oyn41MVeP53SZxIR54WVvahxt02byFtY/P506fRvH8gBD5e0w+OhinxulZJ9uMXiHxSOdpsYq5Xs1CjOEAAahNEwpwXMXkez/0uSxuBQx/fvMg2+dmr4rbp2EG+h6g6P3kmLv9JU6BQgI0s5kFEQKV/s4T23SMnbBytuH+qehotAdeptNA8aL1m2qhc1LC/SRCmj/lX/hyr3sShGH7kzyEZW3aKwUVlSyVaTh+m0AbRiZNGMt2i/mGqkrBVz+gwK5UDFLe/tjCHtRH91Y9+6GWQZNpAgMBAAE=";
    private sealed record Envelope(string Payload, string Signature);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static Version? VersionOf(string value)
    {
        var parts = value.Split('.');
        if (parts.Length != 3 || parts.Any(p => p.Length is < 1 or > 5 || p.Any(c => c is < '0' or > '9'))) return null;
        return new Version(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]));
    }
    public static UpdateRelease Parse(byte[] bytes, string key = PublicKey)
    {
        if (bytes.Length > 65536) throw new InvalidDataException("Update manifest too large");
        var envelope = JsonSerializer.Deserialize<Envelope>(bytes, Json) ?? throw new InvalidDataException("Update envelope");
        var payload = Convert.FromBase64String(envelope.Payload); var signature = Convert.FromBase64String(envelope.Signature);
        using var rsa = RSA.Create(); rsa.ImportRSAPublicKey(Convert.FromBase64String(key), out _);
        if (!rsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) throw new InvalidDataException("Update signature invalid");
        var release = JsonSerializer.Deserialize<UpdateRelease>(payload, Json) ?? throw new InvalidDataException("Update release");
        if (VersionOf(release.Version) is null) throw new InvalidDataException("Update version invalid");
        foreach (var asset in new[] {release.Macos, release.Windows, release.WindowsInstaller}.OfType<UpdateAsset>())
        {
            if (!Uri.TryCreate(asset.Url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "github.com" || uri.UserInfo != "" ||
                !uri.AbsolutePath.StartsWith($"/ielfstone/unictrl-oss/releases/download/v{release.Version}/", StringComparison.Ordinal) || uri.Query != "" || uri.Fragment != "" ||
                asset.Sha256.Length != 64 || !asset.Sha256.All(c => char.IsAsciiHexDigit(c)) || asset.Size is <= 0 or > 1_073_741_824)
                throw new InvalidDataException("Update asset invalid");
        }
        return release;
    }
    public static void Verify(string path, UpdateAsset asset)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length != asset.Size || Convert.ToHexStringLower(SHA256.HashData(stream)) != asset.Sha256.ToLowerInvariant())
            throw new InvalidDataException("Update size or checksum invalid");
    }
}
