/* MPA regression check compiled against the exact source/configuration used by the probe.
 * Include the implementation to exercise its internal recursive maximum-depth path.
 * FFmpeg's original license notices remain in that unmodified-inclusion source. */
#include "libavcodec/vlc.c"

int main(void)
{
    for (int wide = 0; wide <= 1; wide++) {
        const int symbols = wide ? 3 : 6;
        const int entries = 1 << symbols;
        VLC_MULTI_ELEM output[64] = { 0 };
        VLC_MULTI_ELEM info = { 0 };
        VLCcode codes[2] = { 0 };
        unsigned counts[VLC_MULTI_MAX_SYMBOLS - 1] = { 0 };
        codes[0].bits = codes[1].bits = 1;
        codes[0].code = 0;
        codes[1].code = 1U << 31;
        codes[0].symbol = wide ? 1000 : 17;
        codes[1].symbol = wide ? 2000 : 23;
        add_level(output, wide, 2, symbols, codes, 0, 0, symbols + 1, 0, 1, 1, counts, info);
        for (int index = 0; index < entries; index++) {
            av_assert0(output[index].num == symbols && output[index].len == symbols);
            for (int symbol = 0; symbol < symbols; symbol++) {
                const int bit = (index >> (symbols - symbol - 1)) & 1;
                av_assert0((wide ? output[index].val16[symbol] : output[index].val8[symbol]) == codes[bit].symbol);
            }
        }
    }
    puts("VLC 8-bit six-symbol and 16-bit three-symbol maximum recursion tables match exact independent bit patterns.");
    return 0;
}
