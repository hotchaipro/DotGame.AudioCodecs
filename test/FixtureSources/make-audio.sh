#!/bin/bash
# make-audio.sh <libopus> <work folder>: the audio fixtures in TestData/Opus and TestData/Flac, made
# from synthesized sources, and the expected values of OpusFileDecoderTests.
#
#   <libopus>: native libopus 1.6.1, its fixed-point build (configure --enable-fixed-point), as a
#   shared library: the decoder the managed one is a port of, which the hashes come from.
#
# Needs python3 with numpy (synth.py), ffmpeg with libopus, and flac 1.5.0. Prints each Opus
# fixture's channels, frames and SHA-256 for OpusFileDecoderTests.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
libopus=$1
mkdir -p "$2" && cd "$2"
mkdir -p wav opus flac
python3 "$here/synth.py" wav

B=(-hide_banner -loglevel error -y)
F=(-fflags +bitexact -map_metadata -1)

# Opus: CELT (music), SILK (narrow and wide band, 60 ms), hybrid, mono and stereo, 2.5 to 60 ms
# frames, and a stream that switches between hybrid and CELT
enc() { out=$1; src=$2; shift 2; ffmpeg "${B[@]}" -i "wav/$src" -c:a libopus "$@" "${F[@]}" "opus/$out.opus"; }
enc celt-stereo-112k music-st-4s.wav -application audio -b:a 112k
enc celt-mono-64k music-mono-3s.wav -application audio -b:a 64k
enc celt-2_5ms music-st-2s.wav -application audio -b:a 128k -frame_duration 2.5
enc celt-60ms music-st-2s.wav -application audio -b:a 96k -frame_duration 60
enc hybrid-mono speech-mono.wav -application voip -b:a 32k
enc hybrid-stereo speech-music-st-3s.wav -application audio -b:a 40k
enc silk-nb-mono speech-mono.wav -application voip -b:a 8k -cutoff 4000
enc silk-wb-mono speech-mono.wav -application voip -b:a 16k -cutoff 8000
enc silk-stereo speech-st.wav -application voip -b:a 20k -cutoff 8000
enc silk-60ms speech-mono.wav -application voip -b:a 12k -cutoff 8000 -frame_duration 60
python3 "$here/opushash.py" "$libopus" opus/*.opus

# FLAC: ffmpeg's encoder for the stereo modes, the fixed predictor, silence, an odd block size and
# 24-bit samples, from tones and noise
T=wav
ffmpeg "${B[@]}" -f lavfi -i "sine=frequency=440:sample_rate=44100:duration=0.3" -f lavfi -i "anoisesrc=d=0.3:r=44100:a=0.2:seed=7" \
  -filter_complex "[0][1]amerge=inputs=2,pan=stereo|c0=c0+0.2*c1|c1=0.7*c0+c1" -c:a pcm_s16le "$T/st.wav"
ffmpeg "${B[@]}" -f lavfi -i "sine=frequency=330:sample_rate=22050:duration=0.3" -c:a pcm_s16le "$T/mono.wav"
ffmpeg "${B[@]}" -f lavfi -i "sine=frequency=880:sample_rate=44100:duration=0.1" -f lavfi -i "anullsrc=r=44100:cl=mono:d=0.15" \
  -filter_complex "[0][1]concat=n=2:v=0:a=1,aformat=channel_layouts=stereo" -c:a pcm_s16le "$T/sil.wav"
ffmpeg "${B[@]}" -f lavfi -i "sine=frequency=500:sample_rate=48000:duration=0.2" -af "volume=0.9" -c:a pcm_s24le "$T/m24.wav"
for m in indep left_side right_side mid_side; do
  ffmpeg "${B[@]}" -i "$T/st.wav" -c:a flac -compression_level 8 -ch_mode $m "${F[@]}" "flac/stereo-$m.flac"
done
ffmpeg "${B[@]}" -i "$T/mono.wav" -c:a flac -compression_level 0 "${F[@]}" flac/mono-fixed.flac
ffmpeg "${B[@]}" -i "$T/sil.wav" -c:a flac -compression_level 12 "${F[@]}" flac/stereo-silence.flac
ffmpeg "${B[@]}" -i "$T/st.wav" -c:a flac -compression_level 5 -frame_size 1000 "${F[@]}" flac/stereo-oddblock.flac
ffmpeg "${B[@]}" -i "$T/m24.wav" -c:a flac -sample_fmt s32 -bits_per_raw_sample 24 -compression_level 8 "${F[@]}" flac/mono-24bit.flac

# FLAC: libFLAC's linear predictors, one file for each of the decoder's prediction paths. Many
# steady sinusoids make long predictors pay off; 24-bit samples need 64-bit sums.
flac -s -f --no-seektable --lax -l 32 -q 15 -o flac/stereo-lpc32.flac wav/partials-st-1s-24bit.wav
flac -s -f --no-seektable -8 -o flac/stereo-lpc12.flac wav/partials-st-1s-24bit.wav
flac -s -f --no-seektable -l 8 -q 12 -o flac/mono-lpc8.flac wav/partials-mono-1s.wav

# Each FLAC file's 16-bit PCM, as ffmpeg decodes it
for f in flac/*.flac; do ffmpeg "${B[@]}" -i "$f" -f s16le -c:a pcm_s16le "${f%.flac}.pcm"; done
