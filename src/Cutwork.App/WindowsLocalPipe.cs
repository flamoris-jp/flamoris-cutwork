using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Flamoris.Cutwork.App;

internal static class WindowsLocalPipe
{
    // .NET 10 CurrentUserOnly uses WindowsIdentity.Owner as the sole allowed SID.
    // Retain that exact owner/DACL policy, with native PIPE_REJECT_REMOTE_CLIENTS
    // in dwPipeMode (NOT PipeOptions/openMode). Apply it atomically at creation.
    internal const uint RejectRemoteClients = 0x00000008;
    public static NamedPipeServerStream Create(string name)
    {
        using var identity = WindowsIdentity.GetCurrent();
        string owner = identity.Owner!.Value;
        var descriptor = new RawSecurityDescriptor($"O:{owner}D:P(A;;GA;;;{owner})");
        byte[] bytes = new byte[descriptor.BinaryLength]; descriptor.GetBinaryForm(bytes, 0);
        var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = pin.AddrOfPinnedObject() };
            var handle = CreateNamedPipeW(@"\\.\pipe\" + name, 0x40000000 | 0x00080000 | 3,
                RejectRemoteClients, 1, 65536, 65536, 0, ref attributes);
            if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
            try { return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle); }
            catch { handle.Dispose(); throw; }
        }
        finally { pin.Free(); }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { public int Length; public IntPtr Descriptor; public int InheritHandle; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateNamedPipeW(string name, uint openMode, uint pipeMode,
        uint maxInstances, uint outBufferSize, uint inBufferSize, uint defaultTimeout, ref SecurityAttributes attributes);
}
