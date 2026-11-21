using System.IO;
using System.Runtime.CompilerServices;

namespace PlanetPack
{
    /// <summary>
    /// Minimalny czytnik protobuf wprost po tablicy bajtow.
    ///
    /// Bez biblioteki i bez generowanych klas celowo: PBF planety to ponad
    /// miliard obiektow, a kazda alokacja obiektu posredniego to setki GB
    /// smieci na jeden przebieg. Tu czytamy pola w miejscu i od razu je zuzywamy.
    /// </summary>
    internal static class Proto
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Varint(byte[] b, ref int p)
        {
            ulong v = 0;
            int shift = 0;
            while (true)
            {
                byte x = b[p++];
                v |= (ulong)(x & 0x7F) << shift;
                if (x < 0x80) return v;
                shift += 7;
                if (shift > 63) throw new InvalidDataException("varint za dlugi");
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long ZigZag(ulong v)
        {
            return (long)(v >> 1) ^ -(long)(v & 1);
        }

        public static void Skip(byte[] b, ref int p, int wire)
        {
            switch (wire)
            {
                case 0: Varint(b, ref p); break;
                case 1: p += 8; break;
                case 2: { int len = (int)Varint(b, ref p); p += len; break; }
                case 5: p += 4; break;
                default: throw new InvalidDataException("nieznany wire type " + wire);
            }
        }

        /// <summary>Liczba varintow w spakowanym polu = liczba bajtow bez bitu
        /// kontynuacji. Pozwala zaalokowac bufor raz, zamiast rosnac w petli.</summary>
        public static int CountVarints(byte[] b, int start, int end)
        {
            int n = 0;
            for (int i = start; i < end; i++)
                if (b[i] < 0x80) n++;
            return n;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void PutZigZag(byte[] buf, ref int p, long value)
        {
            ulong z = (ulong)((value << 1) ^ (value >> 63));
            while (z >= 0x80)
            {
                buf[p++] = (byte)(z | 0x80);
                z >>= 7;
            }
            buf[p++] = (byte)z;
        }
    }
}
