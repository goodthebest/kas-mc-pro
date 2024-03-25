using Miningcore.Contracts;
using Miningcore.Native;

namespace Miningcore.Crypto.Hashing.Algorithms;

[Identifier("fishhashkarlsen")]
public unsafe class FishHashKarlsen : IHashAlgorithm
{
    private IntPtr handle = IntPtr.Zero;
    private readonly object genLock = new();

    public FishHashKarlsen(bool fullContext = false, uint threads = 4)
    {
        lock(genLock)
        {
            this.handle = Multihash.fishhashGetContext(fullContext);
            if(fullContext)
                Multihash.fishhashPrebuildDataset(this.handle, threads);
        }
    }

    public void Digest(ReadOnlySpan<byte> data, Span<byte> result, params object[] extra)
    {
        Contract.Requires<ArgumentException>(this.handle != IntPtr.Zero);
        Contract.Requires<ArgumentException>(result.Length >= 32);

        // concat data in byte[64]
        Span<byte> seedBytes = stackalloc byte[64];
        data.CopyTo(seedBytes);

        var mixHash = new Multihash.Fishhash_hash256();

        var seed = new Multihash.Fishhash_hash512();
        seed.bytes = seedBytes.ToArray();

        mixHash = Multihash.fishhashKernel(this.handle, ref seed);

        fixed(byte* input = mixHash.bytes)
        {
            fixed (byte* output = result)
            {
                Multihash.blake3(input, output, (uint) mixHash.bytes.Length, null, 0);
            }
        }
    }
}
