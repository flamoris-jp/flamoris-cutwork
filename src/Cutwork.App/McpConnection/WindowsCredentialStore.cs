using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Flamoris.Cutwork.App.McpConnection;

public interface IProviderCredentialStore : IProviderCredentialSource
{
    bool Exists();
    void Save(string credential);
    void Delete();
}

/// <summary>Stores the control-plane key in the current user's Windows Credential Manager.</summary>
public sealed class WindowsCredentialStore : IProviderCredentialStore
{
    private const string Target = "FLAMORIS.Cutwork/OpenAiTunnelClient";
    private const uint GenericCredential = 1;
    private const uint PersistLocalMachine = 2;

    public ValueTask<string> GetControlPlaneApiKeyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CredRead(Target, GenericCredential, 0, out nint pointer))
            throw new TunnelClientException("control_plane_credential_unavailable");
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            if (credential.CredentialBlob == 0 || credential.CredentialBlobSize is 0 or > 2048)
                throw new TunnelClientException("control_plane_credential_unavailable");
            byte[] bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            try
            {
                string value = Encoding.Unicode.GetString(bytes);
                if (string.IsNullOrWhiteSpace(value))
                    throw new TunnelClientException("control_plane_credential_unavailable");
                return ValueTask.FromResult(value);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        finally { CredFree(pointer); }
    }

    public bool Exists()
    {
        if (!CredRead(Target, GenericCredential, 0, out nint pointer)) return false;
        CredFree(pointer);
        return true;
    }

    public void Save(string credential)
    {
        if (string.IsNullOrWhiteSpace(credential) || credential.Length > 1024
            || credential.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new ArgumentException("Invalid credential.", nameof(credential));
        byte[] bytes = Encoding.Unicode.GetBytes(credential);
        nint blob = Marshal.AllocCoTaskMem(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var native = new NativeCredential
            {
                Type = GenericCredential,
                TargetName = Target,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = PersistLocalMachine,
                UserName = Environment.UserName,
            };
            if (!CredWrite(ref native, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Credential storage failed.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            Marshal.FreeCoTaskMem(blob);
        }
    }

    public void Delete()
    {
        if (!CredDelete(Target, GenericCredential, 0))
        {
            const int NotFound = 1168;
            int error = Marshal.GetLastWin32Error();
            if (error != NotFound)
                throw new Win32Exception(error, "Credential removal failed.");
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public string? TargetName;
        public string? Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public nint CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public nint Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("Advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out nint credential);

    [DllImport("Advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("Advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("Advapi32.dll")]
    private static extern void CredFree(nint credential);
}
