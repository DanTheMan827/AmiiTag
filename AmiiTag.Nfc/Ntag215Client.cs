using AmiiTag.Tags;

namespace AmiiTag.Nfc;

/// <summary>
/// NTAG21x commands on top of a raw <see cref="INfcTagConnection"/>. Port of the Swift <c>NFCMiFareTag</c> extensions.
/// </summary>
public sealed class Ntag215Client
{
    public const byte CmdGetVersion = 0x60;
    public const byte CmdRead = 0x30;
    public const byte CmdFastRead = 0x3A;
    public const byte CmdWrite = 0xA2;
    public const byte CmdReadSig = 0x3C;
    public const byte CmdPwdAuth = 0x1B;

    /// <summary>
    /// The last user memory/config page of an NTAG215 (PACK/RFUI).
    /// </summary>
    public const int LastPage = 0x86;

    private readonly INfcTagConnection _connection;
    private bool _fastReadUnsupported;

    public Ntag215Client(INfcTagConnection connection)
    {
        _connection = connection;
    }

    public INfcTagConnection Connection => _connection;

    /// <summary>
    /// Returns GET_VERSION, or null if the reader can't send it.
    /// </summary>
    public async Task<NtagVersion?> GetVersionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _connection.TransceiveAsync([CmdGetVersion], cancellationToken);
            return response.Length == 8 ? new NtagVersion(response) : throw new NfcException("Unexpected GET_VERSION response.");
        }
        catch (NfcCommandNotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Returns the 32 byte originality signature, or null if the reader can't send READ_SIG.
    /// </summary>
    public async Task<byte[]?> ReadSignatureAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _connection.TransceiveAsync([CmdReadSig, 0x00], cancellationToken);
            return response.Length == 32 ? response : null;
        }
        catch (NfcCommandNotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads pages <paramref name="startPage"/> through <paramref name="endPage"/> inclusive.
    /// </summary>
    public async Task<byte[]> ReadPagesAsync(int startPage, int endPage, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var total = endPage - startPage + 1;
        var output = new byte[total * TagDump.PageSize];
        var page = startPage;

        while (page <= endPage)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] chunk;
            if (!_fastReadUnsupported && _connection.MaxFastReadPages > 1)
            {
                var batchEnd = Math.Min(page + _connection.MaxFastReadPages - 1, endPage);
                try
                {
                    chunk = await _connection.TransceiveAsync([CmdFastRead, (byte)page, (byte)batchEnd], cancellationToken);
                    if (chunk.Length == 1)
                    {
                        throw new NfcNakException(chunk[0], $"The tag refused FAST_READ {page:X2}-{batchEnd:X2} (NAK {chunk[0]:X}).");
                    }

                    if (chunk.Length != (batchEnd - page + 1) * TagDump.PageSize)
                    {
                        throw new NfcException($"FAST_READ returned {chunk.Length} bytes.");
                    }
                }
                catch (NfcCommandNotSupportedException)
                {
                    _fastReadUnsupported = true;
                    continue;
                }
            }
            else
            {
                // READ always returns 4 pages, wrapping around; only keep what was asked for.
                var read = await _connection.TransceiveAsync([CmdRead, (byte)page], cancellationToken);
                if (read.Length == 1)
                {
                    throw new NfcNakException(read[0], $"The tag refused READ {page:X2} (NAK {read[0]:X}).");
                }

                if (read.Length < 16)
                {
                    throw new NfcException($"READ returned {read.Length} bytes.");
                }

                var pages = Math.Min(4, endPage - page + 1);
                chunk = read.AsSpan(0, pages * TagDump.PageSize).ToArray();
            }

            chunk.CopyTo(output, (page - startPage) * TagDump.PageSize);
            page += chunk.Length / TagDump.PageSize;
            progress?.Report((double)(page - startPage) / total);
        }

        return output;
    }

    public async Task WritePageAsync(int page, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (data.Length != TagDump.PageSize || page is < 0 or > 255)
        {
            throw new ArgumentException("Invalid page write.");
        }

        var command = new byte[6];
        command[0] = CmdWrite;
        command[1] = (byte)page;
        data.Span.CopyTo(command.AsSpan(2));

        var response = await _connection.TransceiveAsync(command, cancellationToken);
        CheckAck(response, page);
    }

    public async Task WritePagesAsync(IReadOnlyList<(int Page, byte[] Data)> writes, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        for (var i = 0; i < writes.Count; i++)
        {
            await WritePageAsync(writes[i].Page, writes[i].Data, cancellationToken);
            progress?.Report((double)(i + 1) / writes.Count);
        }
    }

    /// <summary>
    /// PWD_AUTH. Returns the 2 byte PACK.
    /// </summary>
    public async Task<byte[]> PasswordAuthAsync(ReadOnlyMemory<byte> password, CancellationToken cancellationToken = default)
    {
        var command = new byte[5];
        command[0] = CmdPwdAuth;
        password.Span.CopyTo(command.AsSpan(1));
        var response = await _connection.TransceiveAsync(command, cancellationToken);
        if (response.Length == 1)
        {
            throw new NfcNakException(response[0], "Tag rejected the password.");
        }

        return response;
    }

    private static void CheckAck(byte[] response, int page)
    {
        // Some platforms (Android's NfcA) return nothing for a successful write.
        if (response.Length == 0 || (response.Length == 1 && (response[0] & 0x0F) == 0x0A))
        {
            return;
        }

        var message = response.Length == 1 ? (response[0] & 0x0F) switch
        {
            0x00 => "invalid argument",
            0x01 => "CRC error",
            0x04 => "invalid authentication",
            0x05 => "EEPROM write error",
            _ => $"NAK 0x{response[0]:X2}",
        } : $"unexpected response {Hex.ToSpacedHex(response)}";

        throw new NfcException($"Writing page {page} failed: {message}.");
    }
}

/// <summary>
/// GET_VERSION response.
/// </summary>
public sealed class NtagVersion
{
    private readonly byte[] _data;

    public NtagVersion(byte[] data)
    {
        _data = data;
    }

    public byte VendorId => _data[1];
    public byte ProductType => _data[2];
    public byte ProductSubtype => _data[3];
    public byte StorageSize => _data[6];

    public bool IsNtag215 => ProductType == 0x04 && StorageSize == 0x11;

    /// <summary>
    /// NTAG I²C Plus 2K (NT3H2211), the tag in "v3" amiibo.
    /// </summary>
    public bool IsNtagI2cPlus2k => ProductType == 0x04 && ProductSubtype == 0x05 && StorageSize == 0x15;

    public override string ToString() => Hex.ToSpacedHex(_data);
}
