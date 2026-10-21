namespace Unictrl;

/// The Mac owns the wheel settings because it processes wheel events before forwarding them.
internal sealed record ScrollPreferences(bool Reverse, bool Smooth)
{
    public static ScrollPreferences? Parse(ReadOnlySpan<byte> payload) => payload.Length == 3 && payload[0] == 1 && payload[1] <= 1 && payload[2] <= 1 ? new(payload[1] == 1, payload[2] == 1) : null;
    public byte[] Message(byte type) => [type, 1, Reverse ? (byte)1 : (byte)0, Smooth ? (byte)1 : (byte)0];
}
