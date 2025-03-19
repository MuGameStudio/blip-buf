using fixed_t = System.UInt64;
using Unity.Collections.LowLevel.Unsafe;

/* Library Copyright (C) 2003-2009 Shay Green. This library is free software;
you can redistribute it and/or modify it under the terms of the GNU Lesser
General Public License as published by the Free Software Foundation; either
version 2.1 of the License, or (at your option) any later version. This
library is distributed in the hope that it will be useful, but WITHOUT ANY
WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR
A PARTICULAR PURPOSE.  See the GNU Lesser General Public License for more
details. You should have received a copy of the GNU Lesser General Public
License along with this module; if not, write to the Free Software Foundation,
Inc., 51 Franklin Street, Fifth Floor, Boston, MA 02110-1301 USA */

/* We could eliminate avail and encode whole samples in offset, but that would
limit the total buffered samples to blip_max_frame. That could only be
increased by decreasing time_bits, which would reduce resample ratio accuracy.
*/

/** Sample buffer that resamples to output rate and accumulates samples until they're read out */
/// <summary>
/// Sample buffer that resamples from input clock rate to output sample rate 
/// </summary>
public unsafe struct blip
{
    #region
    static short[] _step = new short[(phase_count + 1) * half_width]
    {
    43, -115,  350, -488, 1136, -914, 5861,21022,
    44, -118,  348, -473, 1076, -799, 5274,21001,
    45, -121,  344, -454, 1011, -677, 4706,20936,
    46, -122,  336, -431,  942, -549, 4156,20829,
    47, -123,  327, -404,  868, -418, 3629,20679,
    47, -122,  316, -375,  792, -285, 3124,20488,
    47, -120,  303, -344,  714, -151, 2644,20256,
    46, -117,  289, -310,  634,  -17, 2188,19985,
    46, -114,  273, -275,  553,  117, 1758,19675,
    44, -108,  255, -237,  471,  247, 1356,19327,
    43, -103,  237, -199,  390,  373,  981,18944,
    42,  -98,  218, -160,  310,  495,  633,18527,
    40,  -91,  198, -121,  231,  611,  314,18078,
    38,  -84,  178,  -81,  153,  722,   22,17599,
    36,  -76,  157,  -43,   80,  824, -241,17092,
    34,  -68,  135,   -3,    8,  919, -476,16558,
    32,  -61,  115,   34,  -60, 1006, -683,16001,
    29,  -52,   94,   70, -123, 1083, -862,15422,
    27,  -44,   73,  106, -184, 1152,-1015,14824,
    25,  -36,   53,  139, -239, 1211,-1142,14210,
    22,  -27,   34,  170, -290, 1261,-1244,13582,
    20,  -20,   16,  199, -335, 1301,-1322,12942,
    18,  -12,   -3,  226, -375, 1331,-1376,12293,
    15,   -4,  -19,  250, -410, 1351,-1408,11638,
    13,    3,  -35,  272, -439, 1361,-1419,10979,
    11,    9,  -49,  292, -464, 1362,-1410,10319,
     9,   16,  -63,  309, -483, 1354,-1383, 9660,
     7,   22,  -75,  322, -496, 1337,-1339, 9005,
     6,   26,  -85,  333, -504, 1312,-1280, 8355,
     4,   31,  -94,  341, -507, 1278,-1205, 7713,
     3,   35, -102,  347, -506, 1238,-1119, 7082,
     1,   40, -110,  350, -499, 1190,-1021, 6464,
     0,   43, -115,  350, -488, 1136, -914, 5861
    };

    /** Maximum number of samples that can be generated from one time frame. */
    private const int blip_max_frame = 4000;

    /** Maximum clock_rate/sample_rate ratio. For a given sample_rate,clock_rate must not be greater than sample_rate*blip_max_ratio. */
    private const int blip_max_ratio = 1 << 20;

    private const int pre_shift = 32;
    // pre_shift = 0

    private const int time_bits = pre_shift + 20;

    private const fixed_t time_unit = (fixed_t)1 << time_bits;

    private const int bass_shift = 9; /* affects high-pass filter breakpoint frequency */
    private const int end_frame_extra = 2; /* allows deltas slightly after frame length */

    private const int half_width = 8;
    private const int buf_extra = half_width * 2 + end_frame_extra;
    private const int phase_bits = 5;
    private const int phase_count = 1 << phase_bits;
    private const int delta_bits = 15;
    private const int delta_unit = 1 << delta_bits;
    private const int frac_bits = time_bits - pre_shift;

    #endregion

    public fixed_t factor;
    public fixed_t offset;
    /** Number of buffered samples available for reading. */
    public int avail;
    public int size;
    public int integrator;

    public int* sanmples;

    //#define SAMPLES(blip* buf ) ((buf*) ((buf) + 1))
     
    public void SetRates(float clock_rate, float sample_rate)
    {
        float factor = time_unit * sample_rate / clock_rate;
        this.factor = (fixed_t)factor;

        /* Fails if clock_rate exceeds maximum, relative to sample_rate */
        //Debug.Assert(0 <= factor - m.factor && factor - m.factor < 1);

        /* Avoid requiring math.h. Equivalent to
        m.factor = (int) ceil( factor ) */
        if (this.factor < factor)
            this.factor++;

        /* At this point, factor is most likely rounded up, but could still
        have been rounded down in the floating-point calculation. */
    }


    /** Clears entire buffer. Afterwards, blip_samples_avail() == 0. */
    public void Clear()
    {
        /* We could set offset to 0, factor/2, or factor-1. 0 is suitable if
        factor is rounded up. factor-1 is suitable if factor is rounded down.
        Since we don't know rounding direction, factor/2 accommodates either,
        with the slight loss of showing an error in half the time. Since for
        a 64-bit factor this is years, the halving isn't a problem. */

        this.offset = this.factor / 2;
        this.avail = 0;
        this.integrator = 0;
        UnsafeUtility.MemClear(this.sanmples, (size + buf_extra) * sizeof(int));
    }

    /** Adds positive/negative delta into buffer at specified clock time. */
    /* Shifting by pre_shift allows calculation using unsigned int rather than possibly-wider fixed_t. On 32-bit platforms, this is likely more efficient.
    And by having pre_shift 32, a 32-bit platform can easily do the shift by simply ignoring the low half. */
    public void AddDelta(uint time, int delta)
    {
        uint _fixed = (uint)((time * this.factor + this.offset) >> pre_shift);
        var offset = this.avail + (_fixed >> frac_bits);

        int* sout = sanmples + offset;

        int phase_shift = frac_bits - phase_bits;
        int phase = (int)(_fixed >> phase_shift & (phase_count - 1));

        {

            var _inIndex = phase * half_width;
            var _revIndex = (phase_count - phase) * half_width;

            int interp = (int)(_fixed >> (phase_shift - delta_bits) & (delta_unit - 1));
            int delta2 = (delta * interp) >> delta_bits;
            delta -= delta2;

            /* Fails if buffer size was exceeded */
            //Debug.Assert(_out <= &SAMPLES(m)[m.size + end_frame_extra]);


            sout[0] += _step[_inIndex + 0] * delta + _step[_inIndex + half_width + 0] * delta2;
            sout[1] += _step[_inIndex + 1] * delta + _step[_inIndex + half_width + 1] * delta2;
            sout[2] += _step[_inIndex + 2] * delta + _step[_inIndex + half_width + 2] * delta2;
            sout[3] += _step[_inIndex + 3] * delta + _step[_inIndex + half_width + 3] * delta2;
            sout[4] += _step[_inIndex + 4] * delta + _step[_inIndex + half_width + 4] * delta2;
            sout[5] += _step[_inIndex + 5] * delta + _step[_inIndex + half_width + 5] * delta2;
            sout[6] += _step[_inIndex + 6] * delta + _step[_inIndex + half_width + 6] * delta2;
            sout[7] += _step[_inIndex + 7] * delta + _step[_inIndex + half_width + 7] * delta2;

            sout[8] += _step[_revIndex + 7] * delta + _step[_revIndex + 7 - half_width] * delta2;
            sout[9] += _step[_revIndex + 6] * delta + _step[_revIndex + 6 - half_width] * delta2;
            sout[10] += _step[_revIndex + 5] * delta + _step[_revIndex + 5 - half_width] * delta2;
            sout[11] += _step[_revIndex + 4] * delta + _step[_revIndex + 4 - half_width] * delta2;
            sout[12] += _step[_revIndex + 3] * delta + _step[_revIndex + 3 - half_width] * delta2;
            sout[13] += _step[_revIndex + 2] * delta + _step[_revIndex + 2 - half_width] * delta2;
            sout[14] += _step[_revIndex + 1] * delta + _step[_revIndex + 1 - half_width] * delta2;
            sout[15] += _step[_revIndex + 0] * delta + _step[_revIndex + 0 - half_width] * delta2;
        }
    }

    /** Same as blip_add_delta(), but uses faster, lower-quality synthesis. */
    public void AddDeltaFast(uint time, int delta)
    {
        uint _fixed = (uint)((time * this.factor + this.offset) >> pre_shift);
        var offset = this.avail + (_fixed >> frac_bits);
        int* sout = sanmples + offset;
        int interp = (int)(_fixed >> (frac_bits - delta_bits) & (delta_unit - 1));
        int delta2 = delta * interp;

        /* Fails if buffer size was exceeded */
        //Debug.Assert(@out <= &SAMPLES(m)[m.size + end_frame_extra]);

        sout[7] += delta * delta_unit - delta2;
        sout[8] += delta2;
    }

    /** Length of time frame, in clocks, needed to make sample_count additional samples available. */
    public int ClocksNeeded(int samples)
    {
        /* Fails if buffer can't hold that many more samples */
        //Debug.Assert(samples >= 0 && m.avail + samples <= m.size);

        fixed_t needed = (fixed_t)samples * time_unit;
        if (needed < this.offset)
            return 0;

        return (int)((needed - this.offset + this.factor - 1) / this.factor);
    }

    /** Makes input clocks before clock_duration available for reading as output samples. 
    Also begins new time frame at clock_duration, so that clock time 0 in the new time frame specifies the same clock as clock_duration in the old time frame specified. 
    Deltas can have been added slightly past clock_duration (up to however many clocks there are in two output samples). */
    public void EndFrame(uint t)
    {
        fixed_t off = t * this.factor + this.offset;
        this.avail += (int)(off >> time_bits);
        this.offset = off & (time_unit - 1);

        /* Fails if buffer size was exceeded */
        //Debug.Assert(avail <= size);
    }


    /* Reads and removes at most 'count' samples and writes them to 'out'. 
     * If 'stereo' is true, writes output to every other element of 'out', 
     * allowing easy interleaving of two buffers into a stereo sample stream.  
     * Outputs 16-bit signed samples. Returns number of samples actually read.  */
    public int ReadSamples(short* data, int count, int stereo)
    {
        //Debug.Assert(count >= 0);

        if (count > this.avail)
            count = this.avail;

        if (count != 0)
        {
            int step = stereo != 0 ? 2 : 1;

            int sum = this.integrator;
            for (int i = 0; i < count; i++)
            {
                /* Eliminate fraction */
                int s = sum >> delta_bits;

                sum += sanmples[i];

                if ((short)s != s)
                    s = (s >> 16) ^ short.MaxValue;

                *data = (short)s;
                data += step;

                /* High-pass filter */
                sum -= s << (delta_bits - bass_shift);
            }
            this.integrator = sum;

            RemoveSamples(count);
        }

        return count;
    }

    public void RemoveSamples(int count)
    {
        int remain = this.avail + buf_extra - count;
        this.avail -= count;

        {
            var s = sizeof(int);
            int* buf = sanmples;
            UnsafeUtility.MemMove(&buf[0], &buf[count], remain * s);
            UnsafeUtility.MemSet(&buf[remain], 0, count * s);
        }
         
    }

    public static void blip_delete(blip* left)
    {
        UnsafeUtility.Free(left, Unity.Collections.Allocator.Persistent);
    }

    public static blip* blip_new(int size)
    {
        var m = (byte*)UnsafeUtility.Malloc(sizeof(blip) + (size + buf_extra) * sizeof(int), sizeof(byte), Unity.Collections.Allocator.Persistent);
        var s = m + sizeof(blip);
        var b = (blip*)(m);
        b->sanmples = (int*)s;
        if (b != null)
        {
            b->factor = time_unit / blip_max_ratio;
            b->size = size;
            b->Clear();
        }
        return b;
    }
}

