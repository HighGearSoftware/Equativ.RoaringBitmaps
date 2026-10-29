#if NETSTANDARD2_0
using System.Runtime.CompilerServices;

// Minimal stand-ins for APIs that only exist on .NET Core, so that shared code compiles unchanged on .NET Framework
namespace System.Numerics
{
    internal static class BitOperations
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int PopCount(ulong value)
        {
            // SWAR popcount, same as the software fallback of System.Numerics.BitOperations
            const ulong c1 = 0x_55555555_55555555ul;
            const ulong c2 = 0x_33333333_33333333ul;
            const ulong c3 = 0x_0F0F0F0F_0F0F0F0Ful;
            const ulong c4 = 0x_01010101_01010101ul;

            value -= (value >> 1) & c1;
            value = (value & c2) + ((value >> 2) & c2);
            value = (((value + (value >> 4)) & c3) * c4) >> 56;

            return (int)value;
        }
    }
}
#endif
