namespace Mimir.Shn.Crypto;

public interface IShnCrypto
{
    /// <summary>
    /// Encrypt or decrypt a byte array in-place using the SHN XOR cipher.
    /// The algorithm is symmetric: applying it twice restores the original data.
    /// </summary>
    void Crypt(byte[] data, int offset, int length);
}
