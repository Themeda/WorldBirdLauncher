namespace WorldBirdLauncher.Core.Protocol;

/// <summary>Minecraft 协议使用的 VarInt 编解码。</summary>
internal static class VarInt
{
    private const int MaxBytes = 5;

    public static void Write(Stream stream, int value)
    {
        var current = unchecked((uint)value);
        for (var i = 0; i < MaxBytes; i++)
        {
            if ((current & ~0x7Fu) == 0)
            {
                stream.WriteByte((byte)current);
                return;
            }

            stream.WriteByte((byte)((current & 0x7F) | 0x80));
            current >>= 7;
        }
    }

    public static int WriteTo(Span<byte> buffer, int value)
    {
        var index = 0;
        var current = unchecked((uint)value);
        while (index < MaxBytes)
        {
            if ((current & ~0x7Fu) == 0)
            {
                buffer[index++] = (byte)current;
                return index;
            }

            buffer[index++] = (byte)((current & 0x7F) | 0x80);
            current >>= 7;
        }

        throw new ArgumentOutOfRangeException(nameof(value), value, "VarInt 超长。");
    }

    /// <summary>读取一个 VarInt；流已结束时返回 -1。</summary>
    public static async Task<int> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var result = 0;
        var shift = 0;
        var buffer = new byte[1];

        while (shift < 32)
        {
            var read = await stream.ReadAsync(buffer, 0, 1, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return -1;
            }

            var value = buffer[0];
            result |= (value & 0x7F) << shift;
            if ((value & 0x80) == 0)
            {
                return result;
            }

            shift += 7;
        }

        throw new InvalidDataException("VarInt 超长。");
    }

    public static int EncodeSize(int value)
    {
        var size = 1;
        var current = unchecked((uint)value);
        while ((current & ~0x7Fu) != 0)
        {
            size++;
            current >>= 7;
        }

        return size;
    }
}
