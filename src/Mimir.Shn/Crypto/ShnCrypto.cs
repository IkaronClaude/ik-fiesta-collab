namespace Mimir.Shn.Crypto;

/// <summary>
/// SHN XOR cipher. Processes data backwards with a rolling key
/// derived from position, constants 0x55 and 0xAA, and multiplier 11.
/// </summary>
public sealed class ShnCrypto : IShnCrypto
{
    public void Crypt(byte[] data, int offset, int length)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 1);

        if (offset + length > data.Length)
            throw new ArgumentOutOfRangeException(nameof(length), "offset + length exceeds buffer size");

        byte key = (byte)length;

        for (int i = offset + length - 1; i >= offset; i--)
        {
            data[i] ^= key;

            byte nextKey = (byte)(i - offset);
            nextKey = (byte)(nextKey & 0x0F);
            nextKey = (byte)(nextKey + 0x55);
            nextKey = (byte)(nextKey ^ (byte)((byte)(i - offset) * 11));
            nextKey = (byte)(nextKey ^ key);
            nextKey = (byte)(nextKey ^ 0xAA);
            key = nextKey;
        }
    }
}
