using System.Text;
using AmiiTag.Tags;
using AmiiTag.Tests.Simulation;

namespace AmiiTag.Tests;

/// <summary>
/// Deterministic test fixtures. The retail keys can't be included, so these keys have the real file layout
/// ("unfixed infos" data key followed by the "locked secret" static key) with made-up secrets. Encryption round trips
/// and every tag operation behave the same way; only interoperability with real consoles needs the real keys.
/// </summary>
public static class TestData
{
    public const string MarioId = "0000000000340102";
    public const string LinkId = "01000000000e0002";

    private static readonly Lazy<AmiiboKeySet> LazyKeys = new(() =>
    {
        var random = new Random(215);
        var file = new byte[AmiiboKeySet.FileLength];
        random.NextBytes(file);

        WriteKey(file.AsSpan(0, 80), "unfixed infos", magicSize: 14);
        WriteKey(file.AsSpan(80, 80), "locked secret", magicSize: 16);
        return AmiiboKeySet.FromKeyFile(file, requireKnownHash: false);
    });

    public static AmiiboKeySet Keys => LazyKeys.Value;

    private static void WriteKey(Span<byte> key, string type, byte magicSize)
    {
        // HMAC key 0-15, type string 16-29 (null terminated), RFU 30, magic size 31, magic 32-47, xor pad 48-79.
        var typeBytes = new byte[14];
        Encoding.ASCII.GetBytes(type).CopyTo(typeBytes, 0);
        typeBytes.CopyTo(key[16..30]);
        key[30] = 0;
        key[31] = magicSize;
    }

    public static byte[] Uid7(byte seed) => [0x04, seed, (byte)(seed + 1), (byte)(seed + 2), (byte)(seed + 3), (byte)(seed + 4), (byte)(seed + 5)];

    public static byte[] Uid9(byte seed) => NtagUid.FromSevenByteUid(Uid7(seed));

    /// <summary>
    /// A complete, encrypted amiibo dump on the given UID, with a stand-in signature.
    /// </summary>
    public static TagDump Amiibo(string id = MarioId, byte uidSeed = 0x10)
    {
        var signature = Enumerable.Range(0, 32).Select(i => (byte)(0xA0 + i)).ToArray();
        return TagDump.FromAmiiboId(id, Keys).WithUid(Uid9(uidSeed), signature, Keys);
    }

    /// <summary>
    /// A genuine-looking, locked amiibo tag.
    /// </summary>
    public static Ntag215Simulator LockedAmiiboTag(string id = MarioId, byte uidSeed = 0x10) =>
        Ntag215Simulator.FromAmiiboDump(Amiibo(id, uidSeed));

    public static byte[] Page(params byte[] bytes) => bytes;
}
