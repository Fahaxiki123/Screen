using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Screen.App;

internal sealed class CredentialService
{
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;

    public string? Read(string targetName)
    {
        if (!NativeMethods.CredRead(targetName, CredTypeGeneric, 0, out var pointer))
        {
            return null;
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeMethods.NativeCredential>(pointer);
            if (credential.CredentialBlob == 0 || credential.CredentialBlobSize == 0)
            {
                return string.Empty;
            }
            return Marshal.PtrToStringUni(credential.CredentialBlob, checked((int)credential.CredentialBlobSize / 2));
        }
        finally
        {
            NativeMethods.CredFree(pointer);
        }
    }

    public void Write(string targetName, string secret)
    {
        var bytes = Encoding.Unicode.GetBytes(secret);
        var targetPointer = Marshal.StringToCoTaskMemUni(targetName);
        var userPointer = Marshal.StringToCoTaskMemUni("Screen");
        var secretPointer = Marshal.AllocCoTaskMem(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, secretPointer, bytes.Length);
            var credential = new NativeMethods.NativeCredential
            {
                Type = CredTypeGeneric,
                TargetName = targetPointer,
                UserName = userPointer,
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
            Marshal.FreeCoTaskMem(targetPointer);
            Marshal.FreeCoTaskMem(userPointer);
        }
    }

    public void Delete(string targetName)
    {
        if (NativeMethods.CredDelete(targetName, CredTypeGeneric, 0))
        {
            return;
        }
        const int errorNotFound = 1168;
        var error = Marshal.GetLastWin32Error();
        if (error != errorNotFound)
        {
            throw new Win32Exception(error, "Unable to delete the API key from Windows Credential Manager.");
        }
    }
}
