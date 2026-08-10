namespace ChronicleDB.Storage.Formats;

internal static class Crc32C
{
    private const uint Polynomial = 0x82F63B78;
    private static readonly uint[] Table = CreateTable();

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = uint.MaxValue;

        foreach (var value in data)
        {
            crc = Table[(crc ^ value) & byte.MaxValue] ^ (crc >> 8);
        }

        return ~crc;
    }

    public static uint ComputeWithZeroedRange(
        ReadOnlySpan<byte> data,
        int zeroStart,
        int zeroLength)
    {
        if (zeroStart < 0 || zeroLength < 0 || zeroStart > data.Length - zeroLength)
        {
            throw new ArgumentOutOfRangeException(nameof(zeroStart));
        }

        var crc = uint.MaxValue;
        var zeroEnd = zeroStart + zeroLength;

        for (var index = 0; index < data.Length; index++)
        {
            var value = index >= zeroStart && index < zeroEnd ? (byte)0 : data[index];
            crc = Table[(crc ^ value) & byte.MaxValue] ^ (crc >> 8);
        }

        return ~crc;
    }

    private static uint[] CreateTable()
    {
        var table = new uint[256];

        for (uint index = 0; index < table.Length; index++)
        {
            var value = index;

            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) == 0
                    ? value >> 1
                    : (value >> 1) ^ Polynomial;
            }

            table[index] = value;
        }

        return table;
    }
}
