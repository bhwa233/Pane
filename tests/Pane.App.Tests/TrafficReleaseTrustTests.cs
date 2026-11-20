using System.Reflection.PortableExecutable;

internal static class TrafficReleaseTrustTests
{
    internal static void Run(string signedFixture, string signerPin, string unsignedFixture)
    {
        Require(TrafficReleaseTrust.Verify(signedFixture, signerPin), "Expected a trusted embedded signature and matching pin.");
        var differentPin = (signerPin[0] == '0' ? "1" : "0") + signerPin[1..];
        Require(!TrafficReleaseTrust.Verify(signedFixture, differentPin), "A trusted signature from a different signer was accepted.");
        Require(!TrafficReleaseTrust.Verify(unsignedFixture, signerPin), "The unsigned fixture was accepted.");

        var directory = TestResults.NewDirectory("traffic-trust-");
        Directory.CreateDirectory(directory);
        var changed = Path.Combine(directory, Path.GetFileName(signedFixture));
        File.Copy(signedFixture, changed);
        Require(TrafficReleaseTrust.Verify(changed, signerPin), "The unmodified copy failed signature verification.");
        using (var file = File.Open(changed, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            using var executable = new PEReader(file, PEStreamOptions.LeaveOpen);
            var section = executable.PEHeaders.SectionHeaders.First(item => item.Name == ".text" && item.SizeOfRawData > 0);
            var offset = section.PointerToRawData + section.SizeOfRawData / 2;
            file.Position = offset;
            var original = file.ReadByte();
            Require(original >= 0, "The fixture's code section is incomplete.");
            file.Position = offset;
            file.WriteByte((byte)(original ^ 1));
        }

        Require(!TrafficReleaseTrust.Verify(changed, signerPin), "A changed code section passed the original signature.");
        Console.WriteLine("PASS traffic release trust: matching signer accepted; different signer, unsigned file, and altered signed file rejected.");
    }

    static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
