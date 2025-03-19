Blip_buf is a small waveform synthesis library meant for use in classic
video game sound chip emulation. It greatly simplifies sound chip
emulation code by handling all the details of resampling. The emulator
merely sets the input clock rate and output sample rate, adds waveforms
by specifying the clock times where their amplitude changes, then reads
the resulting output samples. For a more full-features synthesis
library, get Blip_Buffer.

Author  : Shay Green <gblargg@gmail.com>
Website : http://www.slack.net/~ant/
License : GNU Lesser General Public License (LGPL)

blip-buf.cs is C# port of the blip-buf.h
