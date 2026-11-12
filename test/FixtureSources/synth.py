# synth.py <out folder>: the sources of the audio fixtures in TestData/Opus and of the *-lpc* files
# in TestData/Flac, as WAV files. Everything is synthesized (tones, noise, filters), with fixed
# seeds, so the output is the same every time.
import sys, os, wave
import numpy as np

OUT = sys.argv[1]
os.makedirs(OUT, exist_ok=True)


def write(name, x, rate, bits=16):
    x = np.atleast_2d(x)
    if x.shape[0] < x.shape[1]:
        x = x.T
    x = np.clip(x, -1.0, 1.0)
    with wave.open(os.path.join(OUT, name), 'wb') as w:
        w.setnchannels(x.shape[1]); w.setframerate(rate)
        if bits == 16:
            w.setsampwidth(2); w.writeframes((np.round(x * 32767)).astype('<i2').tobytes())
        else:
            v = np.round(x * 8388607).astype('<i4')
            b = np.ascontiguousarray(v).view(np.uint8).reshape(-1, 4)[:, :3]
            w.setsampwidth(3); w.writeframes(b.tobytes())


def adsr(n, rate, a=0.01, d=0.08, s=0.6, r=0.15):
    t = np.arange(n) / rate
    env = np.ones(n) * s
    na, nd, nr = int(a * rate), int(d * rate), int(r * rate)
    env[:na] = np.linspace(0, 1, na, endpoint=False)
    env[na:na + nd] = np.linspace(1, s, nd, endpoint=False)
    if nr:
        env[-nr:] *= np.linspace(1, 0, nr)
    return env


def note(freq, dur, rate, harmonics=(1, 0.5, 0.33, 0.25, 0.2), detune=0.0, **env):
    n = int(dur * rate); t = np.arange(n) / rate
    x = sum(a * np.sin(2 * np.pi * freq * (k + 1) * (1 + detune) * t) for k, a in enumerate(harmonics))
    return x * adsr(n, rate, **env) / sum(harmonics)


def onepole(x, a):
    y = np.empty_like(x); acc = 0.0
    for i, v in enumerate(x):
        acc += a * (v - acc); y[i] = acc
    return y


def music(seconds, rate, channels, seed=1):
    # Chords, a bass line, a plucked melody and hi-hat noise, panned across the channels
    rng = np.random.default_rng(seed)
    n = int(round(seconds * rate)); out = np.zeros((2, n))
    beat = 0.25
    chords = [(220.00, 277.18, 329.63), (196.00, 246.94, 293.66), (174.61, 220.00, 261.63), (196.00, 246.94, 311.13)]
    melody = [659.25, 587.33, 523.25, 587.33, 659.25, 783.99, 659.25, 523.25, 493.88, 587.33, 698.46, 659.25]
    t = 0.0; step = 0
    while t < seconds:
        i = int(t * rate)
        if step % 8 == 0:
            for k, f in enumerate(chords[(step // 8) % 4]):
                x = note(f, beat * 8, rate, harmonics=(1, 0.3, 0.1), a=0.05, d=0.3, s=0.5, r=0.4) * 0.18
                pan = 0.3 + 0.2 * k
                m = min(len(x), n - i); out[0, i:i + m] += x[:m] * (1 - pan); out[1, i:i + m] += x[:m] * pan
        if step % 2 == 0:
            x = note(chords[(step // 8) % 4][0] / 2, beat * 2, rate, harmonics=(1, 0.6, 0.2), a=0.005, d=0.1, s=0.4, r=0.1) * 0.35
            m = min(len(x), n - i); out[:, i:i + m] += x[:m]
        x = note(melody[step % len(melody)], beat, rate, harmonics=(1, 0.4, 0.25, 0.12, 0.06), a=0.002, d=0.05, s=0.3, r=0.08) * 0.22
        m = min(len(x), n - i); out[0, i:i + m] += x[:m] * 0.7; out[1, i:i + m] += x[:m] * 0.4
        h = rng.standard_normal(int(0.04 * rate)) * np.exp(-np.arange(int(0.04 * rate)) / (0.008 * rate)) * 0.08
        h = h - onepole(h, 0.3)
        m = min(len(h), n - i); out[0, i:i + m] += h[:m] * 0.5; out[1, i:i + m] += h[:m]
        t += beat; step += 1
    out *= 0.9 / np.max(np.abs(out))
    return out[:channels] if channels == 2 else out.mean(axis=0)


def resonator(x, f, bw, rate):
    r = np.exp(-np.pi * bw / rate); c = 2 * r * np.cos(2 * np.pi * f / rate)
    y = np.zeros_like(x); y1 = y2 = 0.0
    for i, v in enumerate(x):
        y0 = v + c * y1 - r * r * y2; y[i] = y0; y2 = y1; y1 = y0
    return y * (1 - r)


VOWELS = [(730, 1090, 2440), (270, 2290, 3010), (300, 870, 2240), (530, 1840, 2480), (570, 840, 2410)]


def speech(frames, rate, pitch=120.0, seed=2):
    # Formant synthesis: a glottal pulse train with intonation through vowel resonators, noise
    # for fricatives, syllable envelopes and pauses. Speech-like, but no one's voice.
    rng = np.random.default_rng(seed)
    out = np.zeros(frames); pos = 0; syl = 0
    while pos < frames:
        dur = int(rate * (0.14 + 0.08 * rng.random()))
        n = min(dur, frames - pos); t = np.arange(n) / rate
        if syl % 5 == 4:
            pos += int(rate * 0.09); syl += 1; continue
        f0 = pitch * (1 + 0.15 * np.sin(2 * np.pi * (syl / 7)) + 0.1 * np.linspace(0.5, -0.5, n))
        phase = np.cumsum(f0 / rate)
        pulses = (np.diff(np.floor(phase), prepend=0) > 0).astype(float)
        src = onepole(pulses, 0.25) + 0.02 * rng.standard_normal(n)
        v = VOWELS[syl % len(VOWELS)]
        x = sum(resonator(src, f, 80 + 0.05 * f, rate) * g for f, g in zip(v, (1.0, 0.6, 0.3)))
        if syl % 3 == 1:
            fric = rng.standard_normal(int(rate * 0.05)); fric = fric - onepole(fric, 0.2)
            m = min(len(fric), n); x[:m] = x[:m] * np.linspace(0, 1, m) + fric[:m] * 0.15 * np.linspace(1, 0, m)
        env = np.sin(np.pi * np.arange(n) / n) ** 0.6
        out[pos:pos + n] += x * env
        pos += n; syl += 1
    return out * 0.85 / np.max(np.abs(out))


R = 48000
# Opus fixtures' sources
write('music-st-2s.wav', music(2, R, 2, seed=11), R)
write('music-st-4s.wav', music(4, R, 2, seed=12), R)
write('music-mono-3s.wav', music(3, R, 1, seed=13), R)
sp = 51409
write('speech-mono.wav', speech(sp, R, 120, seed=21), R)
left, right = speech(sp, R, 115, seed=22), speech(sp, R, 180, seed=23)
write('speech-st.wav', np.vstack([0.8 * left + 0.2 * right, 0.3 * left + 0.7 * right]), R)
# Speech, then music, for an encoder that switches modes in one stream
mix = np.zeros((2, 3 * R)); s2 = speech(int(1.3 * R), R, 140, seed=24); m2 = music(1.7, R, 2, seed=14)
mix[:, :len(s2)] = s2 * 0.8; mix[:, len(s2):len(s2) + m2.shape[1]] = m2[:, :3 * R - len(s2)]
write('speech-music-st-3s.wav', mix, R)


def partials(seconds, rate, channels, count, seed):
    # Many steady sinusoids and a little noise: a signal long linear predictors pay off on
    rng = np.random.default_rng(seed)
    t = np.arange(int(seconds * rate)) / rate; out = []
    for c in range(channels):
        f = rng.uniform(80, 9000, count); a = rng.uniform(0.2, 1.0, count) / np.sqrt(f / 80); ph = rng.uniform(0, 2 * np.pi, count)
        x = sum(ai * np.sin(2 * np.pi * fi * t + pi) for fi, ai, pi in zip(f, a, ph)) + 1e-4 * rng.standard_normal(len(t))
        out.append(x * 0.8 / np.max(np.abs(x)))
    return np.vstack(out) if channels == 2 else out[0]


write('partials-st-1s-24bit.wav', partials(1, R, 2, 16, 41), R, bits=24)
write('partials-mono-1s.wav', partials(1, R, 1, 4, 42), R)
