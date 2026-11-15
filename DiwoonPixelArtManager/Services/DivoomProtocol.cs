namespace DiwoonPixelArtManager.Services;

/// <summary>
/// Builds the Bluetooth packets understood by Divoom displays (Pixoo, Pixoo-Max, Timebox Evo, Ditoo).
/// Packet layout: 01 LLLL PAYLOAD CRCR 02, numbers little-endian, CRC = byte sum of LLLL + PAYLOAD.
/// Based on iebb/Diwoom (Apache-2.0) and the protocol notes of RomRider/node-divoom-timebox-evo.
/// </summary>
public static class DivoomProtocol
{
    public const byte CmdDrawImage = 0x44;
    public const byte CmdSetChannel = 0x45;
    public const byte CmdAnimation = 0x49;
    public const byte CmdBrightness = 0x74;

    // Every frame uses a palette padded to 255 entries, so each pixel is exactly one byte.
    private const int PaletteSize = 255;
    private const int AnimationChunkSize = 200;

    public static byte[] BuildPacket(byte command, ReadOnlySpan<byte> args)
    {
        var body = new List<byte>(args.Length + 3);
        AddUInt16(body, args.Length + 3);
        body.Add(command);
        body.AddRange(args.ToArray());

        int checksum = body.Sum(b => b);
        var packet = new List<byte>(body.Count + 4) { 0x01 };
        packet.AddRange(body);
        AddUInt16(packet, checksum);
        packet.Add(0x02);
        return packet.ToArray();
    }

    public static byte[] Brightness(int percent) =>
        BuildPacket(CmdBrightness, [(byte)Math.Clamp(percent, 0, 100)]);

    public static byte[] Clock(ClockStyle style, RgbColor color) =>
        BuildPacket(CmdSetChannel, [0x00, 0x01, (byte)style, 1, 0, 0, 0, color.R, color.G, color.B]);

    public static byte[] Light(RgbColor color, int brightness, LightEffect effect, bool on) =>
        BuildPacket(CmdSetChannel, [0x01, color.R, color.G, color.B, (byte)Math.Clamp(brightness, 0, 100), (byte)effect, (byte)(on ? 1 : 0), 0, 0, 0]);

    public static byte[] CloudChannel() => BuildPacket(CmdSetChannel, [0x02]);

    public static byte[] VjEffect(int index) => BuildPacket(CmdSetChannel, [0x03, (byte)index]);

    public static byte[] Visualizer(int index) => BuildPacket(CmdSetChannel, [0x04, (byte)index]);

    public static byte[] CustomAnimationChannel() => BuildPacket(CmdSetChannel, [0x05]);

    public static byte[] Scoreboard(int red, int blue)
    {
        red = Math.Clamp(red, 0, 999);
        blue = Math.Clamp(blue, 0, 999);
        return BuildPacket(CmdSetChannel, [0x06, 0x00, (byte)(red & 0xFF), (byte)(red >> 8), (byte)(blue & 0xFF), (byte)(blue >> 8)]);
    }

    /// <summary>Static image from one RGB frame already resized to the display size.</summary>
    public static byte[] Image(byte[] rgbFrame, bool isLargeDisplay)
    {
        var (palette, pixels) = Quantize(rgbFrame);

        int headerSize = isLargeDisplay ? 8 : 7;
        int frameSize = headerSize + PaletteSize * 3 + pixels.Length;

        var args = new List<byte> { 0x00, 0x0A, 0x0A, 0x04, 0xAA };
        AddUInt16(args, frameSize);
        args.AddRange([0x00, 0x00, (byte)(isLargeDisplay ? 0x03 : 0x00)]);
        if (isLargeDisplay)
            AddUInt16(args, PaletteSize);
        else
            args.Add(PaletteSize);
        args.AddRange(palette);
        args.AddRange(pixels);
        return BuildPacket(CmdDrawImage, args.ToArray());
    }

    /// <summary>
    /// Looping animation for 16×16 displays. Returns one packet per 200-byte chunk; send them in order.
    /// </summary>
    public static IReadOnlyList<byte[]> Animation(DisplayArt animation)
    {
        var data = new List<byte>();
        for (int i = 0; i < animation.Frames.Count; i++)
        {
            var (palette, pixels) = Quantize(animation.Frames[i]);
            int frameSize = 7 + palette.Length + pixels.Length;

            data.Add(0xAA);
            AddUInt16(data, frameSize);
            AddUInt16(data, Math.Clamp(animation.DelaysMs[i], 1, ushort.MaxValue));
            data.Add(0x00); // reset the palette on every frame
            data.Add(PaletteSize);
            data.AddRange(palette);
            data.AddRange(pixels);
        }

        var packets = new List<byte[]>();
        for (int offset = 0, index = 0; offset < data.Count; offset += AnimationChunkSize, index++)
        {
            int length = Math.Min(AnimationChunkSize, data.Count - offset);
            var args = new List<byte>(length + 3);
            AddUInt16(args, data.Count);
            args.Add((byte)index);
            args.AddRange(data.GetRange(offset, length));
            packets.Add(BuildPacket(CmdAnimation, args.ToArray()));
        }
        return packets;
    }

    private static (byte[] Palette, byte[] Pixels) Quantize(byte[] rgbFrame)
    {
        var (colors, indices) = ColorQuantizer.Quantize(rgbFrame, PaletteSize);
        var palette = new byte[PaletteSize * 3];
        for (int i = 0; i < colors.Length; i++)
        {
            palette[i * 3] = (byte)(colors[i] >> 16);
            palette[i * 3 + 1] = (byte)(colors[i] >> 8);
            palette[i * 3 + 2] = (byte)colors[i];
        }
        return (palette, indices);
    }

    private static void AddUInt16(List<byte> buffer, int value)
    {
        buffer.Add((byte)(value & 0xFF));
        buffer.Add((byte)((value >> 8) & 0xFF));
    }
}

public readonly record struct RgbColor(byte R, byte G, byte B);

public enum ClockStyle : byte
{
    FullScreen = 0,
    Rainbow = 1,
    WithBox = 2,
    AnalogSquare = 3,
    FullScreenNegative = 4,
    AnalogRound = 5
}

public enum LightEffect : byte
{
    PlainColor = 0,
    Love = 1,
    Plants = 2,
    NoMosquito = 3,
    Sleeping = 4
}
