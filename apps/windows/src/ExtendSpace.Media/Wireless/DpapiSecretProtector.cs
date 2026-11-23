using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using ExtendSpace.Core.Wireless;

namespace ExtendSpace.Media.Wireless;

/// <summary>
/// DPAPI for the current user (D79): only this Windows account on this PC can read what it protects.
/// Called directly through crypt32 because the managed wrapper is a separate package.
/// </summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    // Optional entropy: keeps another program's DPAPI blobs from being mistaken for ours.
    private static readonly byte[] Entropy = "ExtendSpace identity v1"u8.ToArray();

    private const int CryptProtectUiForbidden = 0x1;

    public byte[] Protect(byte[] secret) => Run(secret, protect: true);

    public byte[] Unprotect(byte[] protectedData) => Run(protectedData, protect: false);

    private static unsafe byte[] Run(byte[] input, bool protect)
    {
        fixed (byte* data = input)
        fixed (byte* entropy = Entropy)
        {
            var inBlob = new DataBlob { Size = input.Length, Data = (nint)data };
            var entropyBlob = new DataBlob { Size = Entropy.Length, Data = (nint)entropy };
            var outBlob = default(DataBlob);
            bool ok = protect
                ? CryptProtectData(ref inBlob, null, ref entropyBlob, 0, 0, CryptProtectUiForbidden, ref outBlob)
                : CryptUnprotectData(ref inBlob, 0, ref entropyBlob, 0, 0, CryptProtectUiForbidden, ref outBlob);
            if (!ok)
            {
                throw new CryptographicException($"DPAPI could not {(protect ? "protect" : "unprotect")} the data.", new Win32Exception(Marshal.GetLastPInvokeError()));
            }

            try
            {
                var output = new byte[outBlob.Size];
                Marshal.Copy(outBlob.Data, output, 0, outBlob.Size);
                return output;
            }
            finally
            {
                LocalFree(outBlob.Data);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public nint Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, ref DataBlob entropy, nint reserved, nint prompt, int flags, ref DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, nint description, ref DataBlob entropy, nint reserved, nint prompt, int flags, ref DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);
}
