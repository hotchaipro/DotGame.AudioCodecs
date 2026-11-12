#!/bin/bash
# Runs the benchmarks (README.md) on macOS or Linux and prints the README's tables.
#
#   bench/run.sh <folder of 16-bit WAV files> [libopus fixed-point prefix] [folder with dr_flac.h]
#
# The C libraries come from pkg-config (opus, ogg, flac): Homebrew's on macOS. A second libopus,
# built with --enable-fixed-point and installed to the prefix given, adds the fixed-point column;
# dr_flac (https://github.com/mackron/dr_libs, one header) adds a column to the FLAC decode.
# Each WAV is encoded by the C libraries first; their files are what both sides decode.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
inputs=$1
fixed=${2:-}
drflac=${3:-}
out=$here/out
mkdir -p "$out"

flags=$(pkg-config --cflags --libs opus ogg flac)
if [ -n "$drflac" ]; then
    cc -O2 -DWITH_DR_FLAC -I"$drflac" -o "$out/audiobench" "$here/native/audiobench.c" $flags
else
    cc -O2 -o "$out/audiobench" "$here/native/audiobench.c" $flags
fi
if [ -n "$fixed" ]; then
    cc -O2 -o "$out/audiobench-fixed" "$here/native/audiobench.c" -I"$fixed/include/opus" "$fixed/lib/libopus.a" \
        $(pkg-config --cflags --libs ogg flac)
fi
dotnet build "$here/DotGame.AudioCodecs.Bench.csproj" -c Release -nologo -v q -o "$out/jit" > "$out/build.log"
dotnet publish "$here/DotGame.AudioCodecs.Bench.csproj" -c Release -nologo -v q -p:PublishAot=true -o "$out/aot" > "$out/publish.log"

ratio() { awk -v a="$1" -v b="$2" 'BEGIN { printf "%.2fx", a / b }'; }
kb() { awk -v b="$1" 'BEGIN { printf "%\047d KB", b / 1024 }'; }
row() {   # mode, input, label
    local mode=$1 input=$2 label=$3 native fixedTime jit aot
    read -r native nbytes < <("$out/audiobench" "$mode" "$input")
    read -r jit jbytes < <("$out/jit/DotGame.AudioCodecs.Bench" "$mode" "$input")
    read -r aot _ < <("$out/aot/DotGame.AudioCodecs.Bench" "$mode" "$input")
    local line
    if [[ $mode == *encode ]]; then
        line="| $label | $native ms, $(kb "$nbytes")"
        if [[ $mode == opus* && -n "$fixed" ]]; then
            read -r fixedTime fbytes < <("$out/audiobench-fixed" "$mode" "$input")
            line="$line | $(ratio "$fixedTime" "$native"), $(kb "$fbytes")"
        fi
        line="$line | $(ratio "$jit" "$native"), $(kb "$jbytes") | $(ratio "$aot" "$native") |"
    else
        line="| $label | $native ms"
        if [[ $mode == opus* && -n "$fixed" ]]; then
            read -r fixedTime _ < <("$out/audiobench-fixed" "$mode" "$input")
            line="$line | $(ratio "$fixedTime" "$native")"
        fi
        if [[ $mode == flac-decode && -n "$drflac" ]]; then
            read -r drTime _ < <("$out/audiobench" drflac-decode "$input")
            line="$line | $(ratio "$drTime" "$native")"
        fi
        line="$line | $(ratio "$jit" "$native") | $(ratio "$aot" "$native") |"
    fi
    echo "$line"
}

for wav in "$inputs"/*.wav; do
    name=$(basename "$wav" .wav)
    "$out/audiobench" opus-encode "$wav" "$out/$name.opus" > /dev/null
    "$out/audiobench" flac-encode "$wav" "$out/$name.flac" > /dev/null
done

for mode in opus-decode opus-encode flac-decode flac-encode; do
    echo "$mode:"
    for wav in "$inputs"/*.wav; do
        name=$(basename "$wav" .wav)
        case $mode in
            *decode) input=$out/$name.${mode%-decode} ;;
            *) input=$wav ;;
        esac
        row "$mode" "$input" "$name"
    done
    echo
done
