/***********************************************************************
Copyright (c) 2006-2011, Skype Limited. All rights reserved.
Ported to C# from libopus 1.6.1 (silk/control.h) by Hot Chai Games LLC
Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions
are met:
- Redistributions of source code must retain the above copyright notice,
this list of conditions and the following disclaimer.
- Redistributions in binary form must reproduce the above copyright
notice, this list of conditions and the following disclaimer in the
documentation and/or other materials provided with the distribution.
- Neither the name of Internet Society, IETF or IETF Trust, nor the
names of specific contributors, may be used to endorse or promote
products derived from this software without specific prior written
permission.
THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE
LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR
CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF
SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS
INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN
CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
POSSIBILITY OF SUCH DAMAGE.
***********************************************************************/

namespace DotGame.AudioCodecs.Opus.Silk;

/***********************************************/
/* Structure for controlling encoder operation */
/***********************************************/
internal sealed class silk_EncControlStruct
{
    /* I:   Number of channels; 1/2                                                         */
    internal int nChannelsAPI;

    /* I:   Number of channels; 1/2                                                         */
    internal int nChannelsInternal;

    /* I:   Input signal sampling rate in Hertz; 8000/12000/16000/24000/32000/44100/48000   */
    internal int API_sampleRate;

    /* I:   Maximum internal sampling rate in Hertz; 8000/12000/16000                       */
    internal int maxInternalSampleRate;

    /* I:   Minimum internal sampling rate in Hertz; 8000/12000/16000                       */
    internal int minInternalSampleRate;

    /* I:   Soft request for internal sampling rate in Hertz; 8000/12000/16000              */
    internal int desiredInternalSampleRate;

    /* I:   Number of samples per packet in milliseconds; 10/20/40/60                       */
    internal int payloadSize_ms;

    /* I:   Bitrate during active speech in bits/second; internally limited                 */
    internal int bitRate;

    /* I:   Uplink packet loss in percent (0-100)                                           */
    internal int packetLossPercentage;

    /* I:   Complexity mode; 0 is lowest, 10 is highest complexity                          */
    internal int complexity;

    /* I:   Flag to enable in-band Forward Error Correction (FEC); 0/1                      */
    internal int useInBandFEC;

    /* I:   Flag to enable in-band Deep REDundancy (DRED); 0/1                              */
    internal int useDRED;

    /* I:   Flag to actually code in-band Forward Error Correction (FEC) in the current packet; 0/1 */
    internal int LBRR_coded;

    /* I:   Flag to enable discontinuous transmission (DTX); 0/1                            */
    internal int useDTX;

    /* I:   Flag to use constant bitrate                                                    */
    internal int useCBR;

    /* I:   Maximum number of bits allowed for the frame                                    */
    internal int maxBits;

    /* I:   Causes a smooth downmix to mono                                                 */
    internal int toMono;

    /* I:   Opus encoder is allowing us to switch bandwidth                                 */
    internal int opusCanSwitch;

    /* I: Make frames as independent as possible (but still use LPC)                        */
    internal int reducedDependency;

    /* O:   Internal sampling rate used, in Hertz; 8000/12000/16000                         */
    internal int internalSampleRate;

    /* O: Flag that bandwidth switching is allowed (because low voice activity)             */
    internal int allowBandwidthSwitch;

    /* O:   Flag that SILK runs in WB mode without variable LP filter (use for switching between WB/SWB/FB) */
    internal int inWBmodeWithoutVariableLP;

    /* O:   Stereo width */
    internal int stereoWidth_Q14;

    /* O:   Tells the Opus encoder we're ready to switch                                    */
    internal int switchReady;

    /* O: SILK Signal type */
    internal int signalType;

    /* O: SILK offset (dithering) */
    internal int offset;

    /// <summary>C's struct assignment <c>*dst = *this</c>: copies every field.</summary>
    internal void CopyTo(silk_EncControlStruct dst)
    {
        dst.nChannelsAPI = this.nChannelsAPI;
        dst.nChannelsInternal = this.nChannelsInternal;
        dst.API_sampleRate = this.API_sampleRate;
        dst.maxInternalSampleRate = this.maxInternalSampleRate;
        dst.minInternalSampleRate = this.minInternalSampleRate;
        dst.desiredInternalSampleRate = this.desiredInternalSampleRate;
        dst.payloadSize_ms = this.payloadSize_ms;
        dst.bitRate = this.bitRate;
        dst.packetLossPercentage = this.packetLossPercentage;
        dst.complexity = this.complexity;
        dst.useInBandFEC = this.useInBandFEC;
        dst.useDRED = this.useDRED;
        dst.LBRR_coded = this.LBRR_coded;
        dst.useDTX = this.useDTX;
        dst.useCBR = this.useCBR;
        dst.maxBits = this.maxBits;
        dst.toMono = this.toMono;
        dst.opusCanSwitch = this.opusCanSwitch;
        dst.reducedDependency = this.reducedDependency;
        dst.internalSampleRate = this.internalSampleRate;
        dst.allowBandwidthSwitch = this.allowBandwidthSwitch;
        dst.inWBmodeWithoutVariableLP = this.inWBmodeWithoutVariableLP;
        dst.stereoWidth_Q14 = this.stereoWidth_Q14;
        dst.switchReady = this.switchReady;
        dst.signalType = this.signalType;
        dst.offset = this.offset;
    }
}
