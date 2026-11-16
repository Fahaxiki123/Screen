using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Screen.Core;

namespace Screen.App;

internal interface ICredentialStore
{
    StoredCredential? ReadCredential(string targetName);
    void Write(string targetName, string secret, string? endpointScope = null);
    void Delete(string targetName);
}

internal sealed record StoredCredential(string Secret, string? EndpointScope);

internal sealed class CredentialService : ICredentialStore
{
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    public string? Read(string targetName)
    {
        try
        {
            return ReadCredential(targetName)?.Secret;
        }
        catch (Win32Exception exception)
        {
            SimpleLogger.LogError("Could not read a Screen credential", exception);
            return null;
        }
    }

    public StoredCredential? ReadCredential(string targetName)
    {
        ValidateTarget(targetName);
        if (!NativeMethods.CredRead(targetName, CredTypeGeneric, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            return error == ErrorNotFound
                ? null
                : throw new Win32Exception(error, "Unable to read the key from Windows Credential Manager.");
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeMethods.NativeCredential>(pointer);
            var secret = credential.CredentialBlob == 0 || credential.CredentialBlobSize == 0
                ? string.Empty
                : Marshal.PtrToStringUni(credential.CredentialBlob, checked((int)credential.CredentialBlobSize / 2)) ?? string.Empty;
            return new StoredCredential(secret, Marshal.PtrToStringUni(credential.Comment));
        }
        finally
        {
            NativeMethods.CredFree(pointer);
        }
    }

    public void Write(string targetName, string secret, string? endpointScope = null)
    {
        ValidateTarget(targetName);
        var bytes = Encoding.Unicode.GetBytes(secret);
        var targetPointer = Marshal.StringToCoTaskMemUni(targetName);
        var userPointer = Marshal.StringToCoTaskMemUni("Screen");
        var scopePointer = endpointScope is null ? 0 : Marshal.StringToCoTaskMemUni(endpointScope);
        var secretPointer = Marshal.AllocCoTaskMem(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, secretPointer, bytes.Length);
            var credential = new NativeMethods.NativeCredential
            {
                Type = CredTypeGeneric,
                TargetName = targetPointer,
                UserName = userPointer,
                Comment = scopePointer,
                CredentialBlob = secretPointer,
                CredentialBlobSize = checked((uint)bytes.Length),
                Persist = CredPersistLocalMachine
            };
            if (!NativeMethods.CredWrite(ref credential, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to store the API key in Windows Credential Manager.");
            }
        }
        finally
        {
            Array.Clear(bytes);
            Marshal.Copy(new byte[bytes.Length], 0, secretPointer, bytes.Length);
            Marshal.FreeCoTaskMem(secretPointer);
            Marshal.FreeCoTaskMem(scopePointer);
            Marshal.FreeCoTaskMem(targetPointer);
            Marshal.FreeCoTaskMem(userPointer);
        }
    }

    public void Delete(string targetName)
    {
        ValidateTarget(targetName);
        if (NativeMethods.CredDelete(targetName, CredTypeGeneric, 0))
        {
            return;
        }
        var error = Marshal.GetLastWin32Error();
        if (error != ErrorNotFound)
        {
            throw new Win32Exception(error, "Unable to delete the API key from Windows Credential Manager.");
        }
    }

    private static void ValidateTarget(string targetName)
    {
        if (targetName is not (TranslationProfile.ApiKeyCredentialName
            or TranslationProfile.RacingApiKeyCredentialName
            or TranslationProfile.TencentSecretIdCredentialName
            or TranslationProfile.TencentSecretKeyCredentialName))
        {
            throw new ArgumentException("This credential is outside the Screen credential slots.", nameof(targetName));
        }
    }
}
