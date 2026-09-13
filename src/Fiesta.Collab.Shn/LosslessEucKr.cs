using System.Text;

namespace Fiesta.Collab.Shn;

/// <summary>
/// EUC-KR (code page 949) text that round-trips byte for byte. Bytes the code page cannot decode
/// (the 2026 US client ships cp1252 text such as a curly quote 0x94) become U+F700 + byte, a private
/// use range cp949 never maps to, and are turned back into the same byte on encode. Valid EUC-KR text
/// is unaffected.
/// </summary>
public static class LosslessEucKr
{
    private const int EscapeBase = 0xF700;
    private static readonly Encoding Strict;
    private static readonly Encoding Decoding;

    static LosslessEucKr()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Strict = Encoding.GetEncoding(949, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        Decoding = Encoding.GetEncoding(949, EncoderFallback.ExceptionFallback, new EscapeDecoderFallback());
    }

    public static string GetString(byte[] bytes, int index, int count) => Decoding.GetString(bytes, index, count);

    public static string GetString(byte[] bytes) => Decoding.GetString(bytes);

    public static byte[] GetBytes(string value)
    {
        var result = new List<byte>(value.Length * 2);
        int runStart = 0;
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c >= EscapeBase && c <= EscapeBase + 0xFF)
            {
                if (i > runStart)
                    result.AddRange(Strict.GetBytes(value, runStart, i - runStart));
                result.Add((byte)(c - EscapeBase));
                runStart = i + 1;
            }
        }
        if (runStart < value.Length)
            result.AddRange(Strict.GetBytes(value, runStart, value.Length - runStart));
        return result.ToArray();
    }

    private sealed class EscapeDecoderFallback : DecoderFallback
    {
        public override int MaxCharCount => 2;
        public override DecoderFallbackBuffer CreateFallbackBuffer() => new Buffer();

        private sealed class Buffer : DecoderFallbackBuffer
        {
            private char[] _chars = [];
            private int _pos;

            public override bool Fallback(byte[] bytesUnknown, int index)
            {
                _chars = new char[bytesUnknown.Length];
                for (int i = 0; i < bytesUnknown.Length; i++)
                    _chars[i] = (char)(EscapeBase + bytesUnknown[i]);
                _pos = 0;
                return true;
            }

            public override char GetNextChar() => _pos < _chars.Length ? _chars[_pos++] : '\0';

            public override bool MovePrevious()
            {
                if (_pos == 0) return false;
                _pos--;
                return true;
            }

            public override int Remaining => _chars.Length - _pos;

            public override void Reset()
            {
                _chars = [];
                _pos = 0;
            }
        }
    }
}
