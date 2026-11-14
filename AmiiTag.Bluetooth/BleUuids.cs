namespace AmiiTag.Bluetooth;

public static class BleUuids
{
    /// <summary>
    /// Nordic UART service, used by the puck firmware, AmiiboLink and AmiLoop.
    /// </summary>
    public static readonly Guid NordicUartService = Guid.Parse("6e400001-b5a3-f393-e0a9-e50e24dcca9e");

    /// <summary>
    /// UART TX from the central's point of view (write).
    /// </summary>
    public static readonly Guid NordicUartWrite = Guid.Parse("6e400002-b5a3-f393-e0a9-e50e24dcca9e");

    /// <summary>
    /// UART RX from the central's point of view (notify).
    /// </summary>
    public static readonly Guid NordicUartNotify = Guid.Parse("6e400003-b5a3-f393-e0a9-e50e24dcca9e");

    /// <summary>
    /// Advertised (not served) by V2 AmiiboLinks: the UART UUID with 0000180F as its first group.
    /// </summary>
    public static readonly Guid AmiiboLinkV2AdvertisedService = Guid.Parse("0000180f-b5a3-f393-e0a9-e50e24dcca9e");

    public static readonly Guid LegacyPuckService = Guid.Parse("78290001-d52e-473f-a9f4-f03da7c67dd1");
    public static readonly Guid LegacyPuckCommand = Guid.Parse("78290002-d52e-473f-a9f4-f03da7c67dd1");
    public static readonly Guid LegacyPuckResponse = Guid.Parse("78290003-d52e-473f-a9f4-f03da7c67dd1");
    public static readonly Guid LegacyPuckName = Guid.Parse("78290004-d52e-473f-a9f4-f03da7c67dd1");

    /// <summary>
    /// What to look for when scanning or showing the browser's device picker.
    /// </summary>
    public static BleScanFilter TagDeviceFilter { get; } = new()
    {
        Services = [NordicUartService, LegacyPuckService, AmiiboLinkV2AdvertisedService],
        Names = ["amiibolink", "Pixl.js"],
        NamePrefixes = ["AmiLoop", "amiibolink", "Puck.js", "Pixl"],
        OptionalServices = [NordicUartService, LegacyPuckService],
    };

    /// <summary>
    /// Parses 128 bit service UUIDs from raw advertisement record data (little endian, as transmitted).
    /// </summary>
    public static IEnumerable<Guid> ParseUuid128List(ReadOnlySpan<byte> data)
    {
        var result = new List<Guid>();
        for (var offset = 0; offset + 16 <= data.Length; offset += 16)
        {
            var reversed = data.Slice(offset, 16).ToArray();
            Array.Reverse(reversed);
            result.Add(Guid.Parse(Convert.ToHexString(reversed).Insert(8, "-").Insert(13, "-").Insert(18, "-").Insert(23, "-")));
        }

        return result;
    }
}
