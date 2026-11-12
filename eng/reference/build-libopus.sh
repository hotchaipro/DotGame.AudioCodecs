#!/bin/bash
# Builds the reference the checks compare with: libopus 1.6.1 from its release tarball (checked by its
# SHA-256), fixed point, generic C only, with no floating-point contraction, so its float analysis
# rounds as C# does; then the shim the checker calls. Output in eng/reference/.native.
#   build-libopus.sh            macOS or Linux, with curl, a C compiler and make
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
out="$here/.native"
url=https://downloads.xiph.org/releases/opus/opus-1.6.1.tar.gz
sha=6ffcb593207be92584df15b32466ed64bbec99109f007c82205f0194572411a1
mkdir -p "$out"
cd "$out"
if [ ! -f opus-1.6.1.tar.gz ] || ! echo "$sha  opus-1.6.1.tar.gz" | shasum -a 256 -c - > /dev/null 2>&1; then
  curl -fsSL -o opus-1.6.1.tar.gz "$url"
  echo "$sha  opus-1.6.1.tar.gz" | shasum -a 256 -c -
fi
rm -rf src && mkdir src && tar -xzf opus-1.6.1.tar.gz -C src --strip-components=1
cd src
./configure -q --prefix="$out/opus" --disable-doc --disable-extra-programs --enable-fixed-point --disable-intrinsics --with-pic \
  CFLAGS="-O2 -ffp-contract=off" > "$out/configure.log" 2>&1
make -j8 > "$out/make.log" 2>&1
make install > /dev/null 2>&1
cd "$out"
case "$(uname)" in
  Darwin) lib=libshim.dylib ;;
  *) lib=libshim.so ;;
esac
cc -O2 -ffp-contract=off -fPIC -shared -o "$lib" "$here/shim.c" -I"$out/opus/include/opus" "$out/opus/lib/libopus.a" -lm
echo "built $out/$lib"
