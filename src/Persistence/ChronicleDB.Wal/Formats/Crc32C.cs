namespace ChronicleDB.Wal.Formats;

internal static class Crc32C
{
    private const uint Polynomial = 0x82F63B78u;
    private static readonly uint[] Table = CreateTable();

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = uint.MaxValue;
        foreach (var value in data)
        {
            crc = Table[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return ~crc;
    }

    public static uint ComputeWithZeroedRange(ReadOnlySpan<byte> data, int offset, int length)
    {
        if ((uint)offset > (uint)data.Length || length < 0 || length > data.Length - offset)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        var crc = uint.MaxValue;
        for (var index = 0; index < data.Length; index++)
        {
            var value = index >= offset && index < offset + length ? (byte)0 : data[index];
            crc = Table[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return ~crc;
    }

    private static uint[] CreateTable()
    {
        var table = new uint[256];
        for (var index = 0; index < table.Length; index++)
        {
            var value = (uint)index;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) == 0 ? value >> 1 : (value >> 1) ^ Polynomial;
            }

            table[index] = value;
        }

        return table;
    }
}
