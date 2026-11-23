using System.Buffers.Binary;
using ExtendSpace.Core.Protocol;

namespace ExtendSpace.Core.Input;

/// <summary>How the PC turns tablet touches into Windows input (D82). Off is the default.</summary>
public enum InputMode
{
    Off,

    /// <summary>Real touch: Windows' synthetic touch device, up to 10 contacts.</summary>
    Touch,

    /// <summary>Mouse input interpreted from gestures; never described as touch.</summary>
    Mouse,
}

public enum TouchPhase : byte
{
    Down = 0,
    Move = 1,
    Up = 2,
    Cancel = 3,
}

/// <param name="X">0 is the picture's left edge, 65535 its right edge.</param>
public readonly record struct TouchContact(byte Id, TouchPhase Phase, ushort X, ushort Y);

/// <summary>
/// One <c>touch</c> message on the input channel (D81, input architecture section 2): every contact
/// that is down, plus those lifting in this frame.
/// </summary>
public sealed record TouchFrame(uint Sequence, IReadOnlyList<TouchContact> Contacts)
{
    public const byte MessageType = 1;
    public const int MaxContacts = 10;
    public const int MaxContactId = 31;
    private const int HeaderLength = 8;
    private const int ContactLength = 8;

    /// <exception cref="ProtocolException">Not a valid touch frame: a fatal <c>malformed_message</c>.</exception>
    public static TouchFrame Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < HeaderLength)
        {
            throw Malformed($"A touch frame of {payload.Length} bytes is shorter than its header.");
        }

        uint sequence = BinaryPrimitives.ReadUInt32BigEndian(payload);
        int count = payload[4];
        if (count is < 1 or > MaxContacts)
        {
            throw Malformed($"A touch frame lists {count} contacts; 1 to {MaxContacts} are allowed.");
        }

        if (payload.Length != HeaderLength + (count * ContactLength))
        {
            throw Malformed($"A touch frame of {count} contacts is {payload.Length} bytes; it must be {HeaderLength + (count * ContactLength)}.");
        }

        var contacts = new TouchContact[count];
        Span<bool> seen = stackalloc bool[MaxContactId + 1];
        for (int index = 0; index < count; index++)
        {
            ReadOnlySpan<byte> contact = payload.Slice(HeaderLength + (index * ContactLength), ContactLength);
            byte id = contact[0];
            byte phase = contact[1];
            if (id > MaxContactId || seen[id])
            {
                throw Malformed($"Contact id {id} is out of range or repeated.");
            }

            if (phase > (byte)TouchPhase.Cancel)
            {
                throw Malformed($"Contact phase {phase} is not defined.");
            }

            seen[id] = true;
            contacts[index] = new TouchContact(id, (TouchPhase)phase, BinaryPrimitives.ReadUInt16BigEndian(contact[2..]), BinaryPrimitives.ReadUInt16BigEndian(contact[4..]));
        }

        return new TouchFrame(sequence, contacts);
    }

    /// <summary>The payload, as the tablet writes it. For tests and tools.</summary>
    public byte[] Encode()
    {
        var payload = new byte[HeaderLength + (Contacts.Count * ContactLength)];
        BinaryPrimitives.WriteUInt32BigEndian(payload, Sequence);
        payload[4] = (byte)Contacts.Count;
        for (int index = 0; index < Contacts.Count; index++)
        {
            Span<byte> contact = payload.AsSpan(HeaderLength + (index * ContactLength), ContactLength);
            contact[0] = Contacts[index].Id;
            contact[1] = (byte)Contacts[index].Phase;
            BinaryPrimitives.WriteUInt16BigEndian(contact[2..], Contacts[index].X);
            BinaryPrimitives.WriteUInt16BigEndian(contact[4..], Contacts[index].Y);
        }

        return payload;
    }

    private static ProtocolException Malformed(string message) => new(WireErrorCode.MalformedMessage, message);
}

/// <summary>A rectangle in virtual-screen pixels (physical, per-monitor DPI aware).</summary>
public readonly record struct ScreenRect(int Left, int Top, int Width, int Height);

public readonly record struct ScreenPoint(int X, int Y);

/// <summary>
/// From a point on the tablet's picture to a point on the mirrored monitor (D83, input architecture
/// section 3), through the encoder's own letterbox.
/// </summary>
public static class InputGeometry
{
    /// <summary>
    /// Where the monitor's picture lies inside the encoded frame: the same arithmetic as
    /// <c>ExtendSpace.Media.Graphics.OutputGeometry.Create</c> with a requested size (a test there
    /// checks the two agree): scaled to fit, shape kept, even sizes, centred on even offsets.
    /// </summary>
    public static (int X, int Y, int Width, int Height) Picture(int sourceWidth, int sourceHeight, int frameWidth, int frameHeight)
    {
        double scale = Math.Min((double)frameWidth / sourceWidth, (double)frameHeight / sourceHeight);
        int width = Math.Clamp(RoundToEven(sourceWidth * scale), 2, frameWidth);
        int height = Math.Clamp(RoundToEven(sourceHeight * scale), 2, frameHeight);
        return (((frameWidth - width) / 2) & ~1, ((frameHeight - height) / 2) & ~1, width, height);
    }

    /// <summary>
    /// The screen point for a picture point. Null when the point lies in the encoder's bars and
    /// <paramref name="clamp"/> is false (a touch that starts there is not the user's desktop);
    /// with <paramref name="clamp"/> it is moved to the nearest edge of the picture instead.
    /// </summary>
    public static ScreenPoint? Map(ushort x, ushort y, int frameWidth, int frameHeight, ScreenRect monitor, bool clamp)
    {
        if (frameWidth <= 0 || frameHeight <= 0 || monitor.Width <= 0 || monitor.Height <= 0)
        {
            return null;
        }

        (int px, int py, int pw, int ph) = Picture(monitor.Width, monitor.Height, frameWidth, frameHeight);
        double u = ((x / 65535.0 * frameWidth) - px) / pw;
        double v = ((y / 65535.0 * frameHeight) - py) / ph;
        const double Slack = 1e-9;
        if (!clamp && (u < -Slack || u > 1 + Slack || v < -Slack || v > 1 + Slack))
        {
            return null;
        }

        u = Math.Clamp(u, 0, 1);
        v = Math.Clamp(v, 0, 1);
        return new ScreenPoint(monitor.Left + (int)Math.Round(u * (monitor.Width - 1)), monitor.Top + (int)Math.Round(v * (monitor.Height - 1)));
    }

    private static int RoundToEven(double value) => (int)Math.Round(value / 2, MidpointRounding.AwayFromZero) * 2;
}
