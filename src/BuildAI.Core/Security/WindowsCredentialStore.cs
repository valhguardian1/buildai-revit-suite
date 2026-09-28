using System;
using System.Runtime.InteropServices;
using System.Text;

namespace BuildAI.Core.Security
{
    /// <summary>
    /// Windows Credential Manager-backed token store (CredRead/CredWrite/CredDelete).
    /// Generic credential, target "BuildAI:ApiToken". Works on .NET Framework 4.8
    /// and .NET 8 (Windows). No external dependencies.
    /// </summary>
    public sealed class WindowsCredentialStore : ICredentialStore
    {
        private const string Target = "BuildAI:ApiToken";

        public string GetApiToken()
        {
            if (!CredRead(Target, CRED_TYPE_GENERIC, 0, out var handle))
                return null;
            try
            {
                var cred = Marshal.PtrToStructure<CREDENTIAL>(handle);
                if (cred.CredentialBlob == IntPtr.Zero || cred.CredentialBlobSize == 0)
                    return null;
                var bytes = new byte[cred.CredentialBlobSize];
                Marshal.Copy(cred.CredentialBlob, bytes, 0, (int)cred.CredentialBlobSize);
                return Encoding.Unicode.GetString(bytes);
            }
            finally { CredFree(handle); }
        }

        public void SetApiToken(string token)
        {
            var blob = Encoding.Unicode.GetBytes(token ?? string.Empty);
            var blobPtr = Marshal.AllocHGlobal(blob.Length);
            try
            {
                Marshal.Copy(blob, 0, blobPtr, blob.Length);
                var cred = new CREDENTIAL
                {
                    Type = CRED_TYPE_GENERIC,
                    TargetName = Target,
                    CredentialBlob = blobPtr,
                    CredentialBlobSize = (uint)blob.Length,
                    Persist = CRED_PERSIST_LOCAL_MACHINE,
                    UserName = Environment.UserName
                };
                if (!CredWrite(ref cred, 0))
                    throw new InvalidOperationException(
                        "CredWrite failed: " + Marshal.GetLastWin32Error());
            }
            finally { Marshal.FreeHGlobal(blobPtr); }
        }

        public void Clear() => CredDelete(Target, CRED_TYPE_GENERIC, 0);

        // ---- P/Invoke ------------------------------------------------------
        private const int CRED_TYPE_GENERIC = 1;
        private const int CRED_PERSIST_LOCAL_MACHINE = 2;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CREDENTIAL
        {
            public uint Flags;
            public uint Type;
            public string TargetName;
            public string Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public string TargetAlias;
            public string UserName;
        }

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CredReadW")]
        private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPtr);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CredWriteW")]
        private static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CredDeleteW")]
        private static extern bool CredDelete(string target, int type, int flags);

        [DllImport("advapi32.dll")]
        private static extern void CredFree(IntPtr cred);
    }
}
