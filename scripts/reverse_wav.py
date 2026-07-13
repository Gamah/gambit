#!/usr/bin/env python3
"""
Reverse a WAV by frame (not by byte): writes <name>_rev.wav containing the source
file's audio frames in reverse order, header and per-sample byte order preserved.

This is the operation that produces the cabinet "ascend" slide sounds (issue #54)
— the same descend WAV played backwards. gen_sounds.py bakes the reverse inline;
this standalone tool derives it straight from an existing forward .wav, so the
result is provably the source's frames mirrored, independent of the synth.

NB: reversing the raw *bytes* of a WAV would corrupt the header and byte-swap every
sample into noise. We reverse whole frames (sampwidth*nchannels bytes each) instead.

Usage:
  python scripts/reverse_wav.py a.wav b.wav ...   # each -> a_rev.wav, b_rev.wav
  python scripts/reverse_wav.py                    # default: the 4 slide_servo_* files
"""
import os
import sys
import wave

SFX = "gambit/Assets/sounds/sfx"
DEFAULTS = [
    f"{SFX}/slide_servo_classic.wav",
    f"{SFX}/slide_servo_heavy.wav",
    f"{SFX}/slide_servo_quick.wav",
    f"{SFX}/slide_servo_ratchet.wav",
]


def reverse_wav(src):
    root, ext = os.path.splitext(src)
    dst = f"{root}_rev{ext}"
    with wave.open(src, "rb") as f:
        params = f.getparams()
        frames = f.readframes(params.nframes)

    frame = params.sampwidth * params.nchannels  # bytes per frame
    rev = b"".join(frames[i : i + frame] for i in range(len(frames) - frame, -1, -frame))

    with wave.open(dst, "wb") as f:
        f.setparams(params)
        f.writeframes(rev)
    print(f"  {src} -> {dst}  ({params.nframes / params.framerate * 1000:.0f} ms)")


def main():
    targets = sys.argv[1:] or DEFAULTS
    print("Reversing WAV frames…")
    for src in targets:
        if src.endswith("_rev.wav"):
            print(f"  skip {src} (already a reverse)")
            continue
        reverse_wav(src)
    print("Done.")


if __name__ == "__main__":
    main()
