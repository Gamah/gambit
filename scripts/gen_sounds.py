#!/usr/bin/env python3
"""
Generates synthesized WAV sound effects for Gambit.
Run from the repo root: python scripts/gen_sounds.py
Requires: numpy

The board set — one per moment, all fired through Gambit.Audio.SoundPlayer:
  tick      - White moved. Short high sine blip.
  tock      - Black moved. Same envelope, lower.
  pop       - a capture. Ascending C-major arpeggio.
  check     - the king is attacked. Rising double pip; deliberately the only
              two-part sound up here, so it can't be mistaken for a move.
  gameover  - the game ended, any way it ended. Descending triad; the mirror
              of pop, which is what makes it read as closing rather than
              opening.
  offer     - a draw or takeback was offered. Soft mid knock-knock: someone
              is asking you a question, not announcing a result.
  panic     - your clock is under TimeControl.PanicSeconds. Fires once per
              SECOND, which is why it is the shortest and driest sound here —
              anything with a tail becomes a drone at 1 Hz.

The cabinet slide set is separate; see SLIDE_VARIANTS below.

A note on the .sound assets these feed: a 3D variant is a .sound file with
"UI": false pointing at the SAME .vsnd — tick3d/tock3d/pop3d/gameover3d add no
WAV here. Don't generate a second copy for one.
"""
import math
import wave
import os
import numpy as np

SR = 44100


def write_wav(path, samples):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    pcm = np.clip(samples * 32767, -32767, 32767).astype(np.int16)
    with wave.open(path, "w") as f:
        f.setnchannels(1)
        f.setsampwidth(2)
        f.setframerate(SR)
        f.writeframes(pcm.tobytes())
    print(f"  {path}  ({len(samples) / SR * 1000:.0f} ms)")


def exp_ramp(n, t0, v0, t1, v1):
    """Build an n-sample envelope: exponential from v0@t0 to v1@t1, holds v1 after."""
    env = np.zeros(n)
    s0, s1 = int(t0 * SR), min(int(t1 * SR), n)
    if s0 < s1:
        env[s0:s1] = np.exp(np.linspace(math.log(v0), math.log(v1), s1 - s0))
    if s1 < n:
        env[s1:] = v1
    return env


def gen_tick():
    # Sine 1100 Hz, 40 ms; gain 0.07 → 0.001 expo over first 35 ms
    n = int(0.040 * SR)
    t = np.arange(n) / SR
    osc = np.sin(2 * math.pi * 1100 * t)
    env = exp_ramp(n, 0.0, 0.07, 0.035, 0.001)
    return osc * env


def gen_tock():
    # Opponent selector move — same envelope as tick but lower pitched (770 Hz)
    n = int(0.040 * SR)
    t = np.arange(n) / SR
    osc = np.sin(2 * math.pi * 770 * t)
    env = exp_ramp(n, 0.0, 0.07, 0.035, 0.001)
    return osc * env



def gen_pop():
    # Four triangle notes (C-major arpeggio), staggered 70 ms each; each 340 ms long
    return arpeggio([523.25, 659.25, 783.99, 1046.50], stagger=0.070, note_dur=0.340)


def arpeggio(freqs, stagger, note_dur, peak=0.14):
    """Staggered triangle notes — pop's shape, factored out so gen_gameover can be
    its mirror rather than a second copy of the same twenty lines."""
    total_n = int(((len(freqs) - 1) * stagger + note_dur) * SR)
    result = np.zeros(total_n)

    for i, hz in enumerate(freqs):
        onset = int(i * stagger * SR)
        n = int(note_dur * SR)
        t = np.arange(n) / SR

        # Triangle wave via arcsin
        osc = (2 / math.pi) * np.arcsin(np.sin(2 * math.pi * hz * t))

        # Attack: 0 → peak linear over 15 ms; decay: expo → 0.001 over the rest
        attack = int(0.015 * SR)
        decay_end = int((note_dur - 0.020) * SR)
        env = np.zeros(n)
        env[:attack] = np.linspace(0, peak, attack)
        if decay_end > attack:
            env[attack:decay_end] = np.exp(
                np.linspace(math.log(peak), math.log(0.001), decay_end - attack)
            )
        env[decay_end:] = 0.001

        note = osc * env
        end = min(onset + n, total_n)
        result[onset:end] += note[: end - onset]

    return result


def gen_check():
    # Two rising sine pips, 1400 → 1900 Hz, 28 ms each, 55 ms apart. Two parts on
    # purpose: it lands ON TOP of a tick/tock (the move that gave check plays both),
    # so it has to be separable from one by shape, not just by pitch.
    pips = [(0.000, 1400), (0.055, 1900)]
    total_n = int(0.110 * SR)
    result = np.zeros(total_n)
    for onset_s, hz in pips:
        n = int(0.028 * SR)
        t = np.arange(n) / SR
        osc = np.sin(2 * math.pi * hz * t)
        env = exp_ramp(n, 0.0, 0.075, 0.026, 0.001)
        onset = int(onset_s * SR)
        end = min(onset + n, total_n)
        result[onset:end] += (osc * env)[: end - onset]
    return result


def gen_gameover():
    # pop's arpeggio inverted: C-major descending, slower and longer-tailed. The
    # game ending is the one moment worth half a second of anyone's attention.
    return arpeggio([1046.50, 783.99, 659.25, 523.25], stagger=0.105, note_dur=0.520,
                    peak=0.12)


def gen_offer():
    # Knock-knock: two soft mid sines, 520 Hz, 90 ms apart, with a slow attack so it
    # reads as a question rather than an event. Quieter than a move — the opponent is
    # asking, and you may well decline.
    total_n = int(0.230 * SR)
    result = np.zeros(total_n)
    for onset_s in (0.000, 0.090):
        n = int(0.130 * SR)
        t = np.arange(n) / SR
        osc = np.sin(2 * math.pi * 520 * t)
        attack = int(0.012 * SR)
        env = np.zeros(n)
        env[:attack] = np.linspace(0, 0.055, attack)
        env[attack:] = np.exp(np.linspace(math.log(0.055), math.log(0.001), n - attack))
        onset = int(onset_s * SR)
        end = min(onset + n, total_n)
        result[onset:end] += (osc * env)[: end - onset]
    return result


def gen_panic():
    # A dry low pulse, 600 Hz, 55 ms. This one fires once a SECOND for the last ten
    # seconds of your clock, so every property is chosen to survive repetition: no
    # tail (it would drone), no pitch movement (it would read as melody), and quiet
    # enough that ten of them in a row is pressure rather than an alarm.
    n = int(0.055 * SR)
    t = np.arange(n) / SR
    osc = np.sin(2 * math.pi * 600 * t)
    env = exp_ramp(n, 0.0, 0.06, 0.050, 0.001)
    return osc * env


def lowpass(osc, cutoff):
    """Single-pole IIR lowpass."""
    rc = 1.0 / (2 * math.pi * cutoff)
    alpha = (1.0 / SR) / (rc + 1.0 / SR)
    out = np.zeros_like(osc)
    out[0] = osc[0] * alpha
    for i in range(1, len(osc)):
        out[i] = out[i - 1] + alpha * (osc[i] - out[i - 1])
    return out


def saw(freq):
    """Sawtooth via phase accumulation from a per-sample frequency array."""
    phase = np.cumsum(freq / SR) % 1.0
    return 2 * phase - 1


# --- Cabinet slide sounds (issue #54) ---
# Four variations on the "mechanical servo" descend sound; the ascend sound is the
# same WAV reversed, so lowering and raising are mirror images. Each is peak-
# normalized to NORM below (~3x louder than the original servo) on write. Pick one
# via ArcadeRing.SlideSound — see SoundPlayer.PlaySlideAt.

def gen_servo(dur, f0, f1, cutoff, rumble_hz, rumble_amp, am_hz=0.0):
    # Sawtooth pitch sweep f0→f1 Hz over `dur`, + low motor rumble, lowpassed.
    # am_hz > 0 adds a gear-tooth amplitude gate for a ratcheting grind.
    n = int(dur * SR)
    t = np.arange(n) / SR
    freq = np.exp(np.linspace(math.log(f0), math.log(f1), n))
    sig = lowpass(saw(freq), cutoff) + rumble_amp * np.sin(2 * math.pi * rumble_hz * t)
    if am_hz > 0:
        sig *= 0.55 + 0.45 * (np.sin(2 * math.pi * am_hz * t) > 0)  # gear-tooth gate
    env = exp_ramp(n, 0.0, 0.001, 0.02, 0.18)  # quick fade-in, then hold
    out = sig * env * np.linspace(0.6, 1.0, n)  # gentle taper
    # Fade out the last 30 ms to zero — otherwise the file ends mid-waveform and the
    # *reversed* (ascend) clip would start on that discontinuity = an audible pop.
    fade = int(0.030 * SR)
    out[-fade:] *= np.linspace(1.0, 0.0, fade)
    return out


SLIDE_NORM = 0.7  # peak-normalize each variant to this (old servo peaked ~0.24)

SLIDE_VARIANTS = {
    # name              dur   f0   f1   cutoff rumble amp   am
    "slide_servo_classic": (0.70, 300,  90, 1200, 55, 0.40, 0.0),   # the original, beefed up
    "slide_servo_heavy":   (0.85, 210,  60,  800, 45, 0.55, 0.0),   # deep, slow, big motor
    "slide_servo_quick":   (0.50, 480, 150, 1800, 70, 0.25, 0.0),   # smaller, faster servo
    "slide_servo_ratchet": (0.70, 300,  90, 1300, 55, 0.40, 30.0),  # grinding gear-tooth
}


out = "client/Assets/sounds/sfx"
print("Generating sound effects…")
write_wav(f"{out}/tick.wav", gen_tick())
write_wav(f"{out}/tock.wav", gen_tock())
write_wav(f"{out}/pop.wav", gen_pop())
write_wav(f"{out}/check.wav", gen_check())
write_wav(f"{out}/gameover.wav", gen_gameover())
write_wav(f"{out}/offer.wav", gen_offer())
write_wav(f"{out}/panic.wav", gen_panic())

# Cabinet slide options — descend + reversed (ascend) for each (issue #54)
for name, args in SLIDE_VARIANTS.items():
    samples = gen_servo(*args)
    samples *= SLIDE_NORM / np.abs(samples).max()  # equal-loudness across variants
    write_wav(f"{out}/{name}.wav", samples)
    write_wav(f"{out}/{name}_rev.wav", samples[::-1].copy())
print("Done.")
