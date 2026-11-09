namespace Screen.Core;

/// <summary>
/// Validation for user-editable AI translation endpoints.  Lives in Core so
/// the rule is unit-testable without the WPF settings dialog.
/// </summary>
public static class TranslationEndpointRules
{
    /// <summary>
    /// Returns null when the address is a usable endpoint, otherwise the
    /// user-facing validation error.  HTTPS is mandatory: the request carries
    /// the API key as a Bearer token, and a plain-HTTP base address would put
    /// that key on the wire unencrypted.
    /// </summary>
    public static string? ValidateBaseAddress(string? baseAddress) =>
        Uri.TryCreate(baseAddress, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
            ? null
            : "AI 基础地址必须是有效的 HTTPS 地址（HTTP 会以明文发送 API 密钥）。";
}
