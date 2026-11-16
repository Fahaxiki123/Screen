using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using Screen.Core;

namespace Screen.App;

internal static class TranslationCredentialStore
{
    private const string UnboundScope = "Screen.Endpoint.Unbound";
    private static readonly object Gate = new();

    public static string? EndpointScope(string? address)
    {
        if (TranslationEndpointRules.ValidateBaseAddress(address) is not null)
        {
            return null;
        }
        // Canonicalize the host and default port but keep the path and query.
        var uri = new Uri(address!.Trim().TrimEnd('/'), UriKind.Absolute);
        return "Screen.Endpoint." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri)));
    }

    public static TranslationCredentials ReadSaved(TranslationProfile profile, ICredentialStore store) => new(
        ReadSavedKey(store, TranslationProfile.ApiKeyCredentialName, profile.BaseAddress, profile.CredentialName),
        ReadSecret(store, TranslationProfile.TencentSecretIdCredentialName),
        ReadSecret(store, TranslationProfile.TencentSecretKeyCredentialName),
        ReadSavedKey(store, TranslationProfile.RacingApiKeyCredentialName, profile.RacingBaseAddress, profile.RacingCredentialName));

    public static void BindLegacyKeys(ICredentialStore store, TranslationProfile savedProfile)
    {
        lock (Gate)
        {
            BindLegacyKey(store, TranslationProfile.ApiKeyCredentialName, savedProfile.BaseAddress, savedProfile.CredentialName);
            BindLegacyKey(store, TranslationProfile.RacingApiKeyCredentialName, savedProfile.RacingBaseAddress, savedProfile.RacingCredentialName);
        }
    }

    public static string? ReadKey(ICredentialStore store, string slot, string? address)
    {
        lock (Gate)
        {
            try
            {
                var scope = EndpointScope(address);
                var credential = store.ReadCredential(slot);
                return scope is not null && credential?.EndpointScope == scope ? credential.Secret : null;
            }
            catch (Win32Exception exception)
            {
                SimpleLogger.LogError("Could not read an AI translation credential", exception);
                return null;
            }
        }
    }

    private static string? ReadSavedKey(ICredentialStore store, string slot, string? address, string? savedLegacyName)
    {
        lock (Gate)
        {
            try
            {
                var credential = BindLegacyKey(store, slot, address, savedLegacyName);
                var scope = EndpointScope(address);
                return scope is not null && credential?.EndpointScope == scope ? credential.Secret : null;
            }
            catch (Win32Exception exception)
            {
                SimpleLogger.LogError("Could not bind an existing AI translation credential", exception);
                return null;
            }
        }
    }

    private static StoredCredential? BindLegacyKey(ICredentialStore store, string slot, string? address, string? savedLegacyName)
    {
        var credential = store.ReadCredential(slot);
        if (credential is not null && string.IsNullOrEmpty(credential.EndpointScope))
        {
            var scope = string.Equals(savedLegacyName, slot, StringComparison.Ordinal) ? EndpointScope(address) : null;
            credential = credential with { EndpointScope = scope ?? UnboundScope };
            store.Write(slot, credential.Secret, credential.EndpointScope);
        }
        return credential;
    }

    private static string? ReadSecret(ICredentialStore store, string slot)
    {
        try
        {
            return store.ReadCredential(slot)?.Secret;
        }
        catch (Win32Exception exception)
        {
            SimpleLogger.LogError("Could not read a Tencent translation credential", exception);
            return null;
        }
    }

    public static void SaveKey(ICredentialStore store, string slot, string secret, string scope)
    {
        lock (Gate)
        {
            if (!string.IsNullOrWhiteSpace(secret))
            {
                store.Write(slot, secret, scope);
            }
            else if (store.ReadCredential(slot)?.EndpointScope == scope)
            {
                store.Delete(slot);
            }
        }
    }
}
