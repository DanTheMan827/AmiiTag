using AmiiTag.Simulation;

namespace AmiiTag.Nfc.Flipper;

/// <summary>
/// Answers relayed reader frames with a <see cref="TagSimulator"/>.
/// </summary>
/// <remarks>
/// The Flipper handles REQA/WUPA, anticollision and SELECT in hardware (and HLTA with
/// <see cref="BridgeListenFlags.HandleHlta"/>) and only relays frames while the tag is selected, so a frame arriving
/// while the model is halted means the reader selected it again.
/// </remarks>
public sealed class TagSimulatorResponder(TagSimulator tag)
{
    public TagSimulator Tag { get; } = tag;

    public BridgeAnswer Respond(BridgeFrame frame)
    {
        if (Tag.IsHalted)
        {
            Tag.Activate();
        }

        if (frame.IsStandard)
        {
            return ToAnswer(Tag.Transceive(frame.Data), raw: false);
        }

        // A whole-byte frame whose CRC failed: let the model judge it (an NTAG answers a CRC error with a NAK).
        if (frame.BitCount % 8 == 0 && frame.Data.Length >= 3)
        {
            return ToAnswer(Tag.TransceiveFrame(frame.Data), raw: true);
        }

        return BridgeAnswer.Silence;
    }

    /// <summary>
    /// The reader's field went away: the tag loses power and its session state.
    /// </summary>
    public void FieldOff() => Tag.PowerCycle();

    public void Halted() => Tag.Halt();

    /// <summary>
    /// The reader selected the tag (again).
    /// </summary>
    public void Activated() => Tag.Activate();

    private static BridgeAnswer ToAnswer(TagResponse response, bool raw) => response.Kind switch
    {
        TagResponseKind.Data => raw ? BridgeAnswer.RawFrame(response.Data) : BridgeAnswer.Frame(response.Data),
        TagResponseKind.Ack => BridgeAnswer.Nibble(TagResponse.AckValue),
        TagResponseKind.Nak => BridgeAnswer.NibbleThenIdle(response.Nibble),
        _ => BridgeAnswer.Silence,
    };
}
