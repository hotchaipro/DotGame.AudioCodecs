# opushash.py <libopus.dylib> <file.opus>...: decodes each Ogg Opus file with native libopus (the
# fixed-point build) at 48 kHz, drops the pre-skip, trims the end to the last granule position, and
# prints channels, frames and the SHA-256 of the 16-bit samples: the expected values of
# OpusFileDecoderTests.
import ctypes, hashlib, struct, sys

lib = ctypes.CDLL(sys.argv[1])
lib.opus_decoder_create.restype = ctypes.c_void_p
lib.opus_decoder_create.argtypes = [ctypes.c_int32, ctypes.c_int, ctypes.POINTER(ctypes.c_int)]
lib.opus_decode.argtypes = [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_int32, ctypes.POINTER(ctypes.c_int16), ctypes.c_int, ctypes.c_int]


def packets(data):
    pos = 0; cur = b''; out = []; last = 0
    while pos < len(data):
        assert data[pos:pos + 4] == b'OggS'
        gp = struct.unpack_from('<q', data, pos + 6)[0]
        n = data[pos + 26]; p = pos + 27 + n
        for s in data[pos + 27:pos + 27 + n]:
            cur += data[p:p + s]; p += s
            if s < 255:
                out.append(cur); cur = b''
        if gp >= 0: last = gp
        pos = p
    return out, last


for path in sys.argv[2:]:
    ps, gp = packets(open(path, 'rb').read())
    ch = ps[0][9]; preskip = struct.unpack_from('<H', ps[0], 10)[0]
    err = ctypes.c_int()
    dec = lib.opus_decoder_create(48000, ch, ctypes.byref(err)); assert err.value == 0
    buf = (ctypes.c_int16 * (5760 * ch))()
    pcm = bytearray()
    for p in ps[2:]:
        if not p: continue
        n = lib.opus_decode(dec, p, len(p), buf, 5760, 0); assert n > 0, n
        pcm += bytes(memoryview(buf).cast('B')[:n * ch * 2])
    frames = gp - preskip
    pcm = pcm[preskip * ch * 2:][:frames * ch * 2]
    assert len(pcm) == frames * ch * 2
    print(path.rsplit('/', 1)[-1][:-5], ch, frames, hashlib.sha256(pcm).hexdigest())
