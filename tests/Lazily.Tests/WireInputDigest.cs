using System.Globalization;

namespace Lazily.Tests;

internal static class WireInputDigest
{
    internal static string Fnv1a64Hex(ReadOnlySpan<byte> bytes)
    {
        const ulong offset = 0xcbf29ce484222325;
        const ulong prime = 0x100000001b3;
        var hash = offset;
        unchecked
        {
            foreach (var value in bytes)
            {
                hash ^= value;
                hash *= prime;
            }
        }

        return hash.ToString("x16", CultureInfo.InvariantCulture);
    }
}
