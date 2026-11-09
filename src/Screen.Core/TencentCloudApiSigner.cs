using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Screen.Core;

public static class TencentCloudApiSigner
{
    public static string CreateAuthorization(
        string secretId,
        string secretKey,
        string payload,
        long timestamp,
        string host,
        string service,
        string contentType = "application/json; charset=utf-8")
    {
        const string algorithm = "TC3-HMAC-SHA256";
        const string signedHeaders = "content-type;host";
        var canonicalHeaders = $"content-type:{contentType}\nhost:{host}\n";
        var canonicalRequest = $"POST\n/\n\n{canonicalHeaders}\n{signedHeaders}\n{Sha256Hex(payload)}";
        var date = DateTimeOffset.FromUnixTimeSeconds(timestamp)
            .UtcDateTime
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var credentialScope = $"{date}/{service}/tc3_request";
        var stringToSign = $"{algorithm}\n{timestamp}\n{credentialScope}\n{Sha256Hex(canonicalRequest)}";

        var secretDate = HmacSha256(Encoding.UTF8.GetBytes("TC3" + secretKey), date);
        var secretService = HmacSha256(secretDate, service);
        var secretSigning = HmacSha256(secretService, "tc3_request");
        var signature = Convert.ToHexString(HmacSha256(secretSigning, stringToSign)).ToLowerInvariant();
        return $"{algorithm} Credential={secretId}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signature}";
    }

    private static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static byte[] HmacSha256(byte[] key, string value) =>
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(value));
}
