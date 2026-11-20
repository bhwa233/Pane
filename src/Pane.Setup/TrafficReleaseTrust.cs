using System.Runtime.InteropServices;
using System.Security.Cryptography;

internal static unsafe class TrafficReleaseTrust
{
    const uint VerifyState = 1, CloseState = 2;
    const uint NoUi = 2, FileChoice = 1;
    const uint CheckChainExceptRoot = 0x80, DisableMd2Md4 = 0x2000;
    static readonly Guid AuthenticodePolicy = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    internal static bool Verify(string path, string? signerSha256)
    {
        if (signerSha256 is not { Length: 64 } || !signerSha256.All(Uri.IsHexDigit)) return false;
        var expected = Convert.FromHexString(signerSha256);
        using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        fixed (char* filePath = path)
        {
            var target = new TrustFile { Size = (uint)sizeof(TrustFile), Path = filePath, Handle = file.SafeFileHandle.DangerousGetHandle() };
            var data = new TrustData
            {
                Size = (uint)sizeof(TrustData), Ui = NoUi, Choice = FileChoice, File = &target, Action = VerifyState,
                Flags = CheckChainExceptRoot | DisableMd2Md4
            };
            var policy = AuthenticodePolicy;
            try
            {
                if (WinVerifyTrust(-1, &policy, &data) != 0 || data.State == 0) return false;
                var provider = WTHelperProvDataFromStateData(data.State);
                if (provider == 0) return false;
                var signer = WTHelperGetProvSignerFromChain(provider, 0, 0, 0);
                if (signer == 0) return false;
                var certificate = WTHelperGetProvCertFromChain(signer, 0);
                if (certificate == null || certificate->Context == null) return false;
                var context = certificate->Context;
                if (context->Encoded == null || context->Length is 0 or > 65536) return false;
                var sha256 = SHA256.HashData(new ReadOnlySpan<byte>(context->Encoded, (int)context->Length));
                return CryptographicOperations.FixedTimeEquals(sha256, expected);
            }
            finally
            {
                if (data.State != 0)
                {
                    data.Action = CloseState;
                    _ = WinVerifyTrust(-1, &policy, &data);
                }
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct TrustFile { public uint Size; public char* Path; public nint Handle, KnownSubject; }

    [StructLayout(LayoutKind.Sequential)]
    struct TrustData
    {
        public uint Size;
        public nint PolicyData, SipData;
        public uint Ui, Revocation, Choice;
        public TrustFile* File;
        public uint Action;
        public nint State, Url;
        public uint Flags, UiContext;
        public nint SignatureSettings;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ProviderCertificate { public uint Size; public CertificateContext* Context; }

    [StructLayout(LayoutKind.Sequential)]
    struct CertificateContext { public uint Encoding; public byte* Encoded; public uint Length; }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern int WinVerifyTrust(nint owner, Guid* policy, TrustData* data);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern nint WTHelperProvDataFromStateData(nint state);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern nint WTHelperGetProvSignerFromChain(nint provider, uint signer, int counterSigner, uint counterSignerIndex);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern ProviderCertificate* WTHelperGetProvCertFromChain(nint signer, uint index);
}
