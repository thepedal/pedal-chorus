// Pedal Chorus — ReBuzz managed effect machine
// A 4-voice stereo chorus optimised for drum sounds.
//
// Algorithm:
//   Four independent delay taps share a single stereo ring buffer.
//   Each tap has its own LFO phase offset (0°, 90°, 180°, 270°) so the
//   voices always move in quadrature — producing dense, organic movement
//   without the "waggle" of a single LFO.
//
//   Voices are spread across the stereo field with equal-power panning;
//   the amount of spread is controlled by the Spread parameter.
//
//   A gentle one-pole low-pass (Tone) rolls off the top octave of the wet
//   signal, keeping hi-hat transients tight while kick / snare stay thick.
//
// CPU saving:
//   When ReBuzz delivers WM_NOIO the machine returns false immediately.
//   When real input goes silent the machine keeps running until the chorus
//   tail (max delay + depth + one extra buffer of headroom) has cleared,
//   then returns false so ReBuzz can skip it on subsequent silent buffers.
//
// Build:
//   dotnet build PedalChorus.csproj -c Release
//   → C:\Program Files\ReBuzz\Gear\Effects\Pedal Chorus.NET.dll

using System;
using Buzz.MachineInterface;

namespace WDE.PedalChorus
{
    [MachineDecl(
        Name        = "Pedal Chorus",
        ShortName   = "PdlChorus",
        Author      = "WDE",
        MaxTracks   = 0,
        InputCount  = 1,
        OutputCount = 1)]
    public class PedalChorusMachine : IBuzzMachine
    {
        IBuzzMachineHost host;

        // ── Ring buffer ───────────────────────────────────────────────────────
        // Power-of-two length → fast modulo via bitwise AND.
        // 65 536 samples ≈ 1 365 ms @ 48 kHz — well beyond any chorus delay.
        const int BUF_MASK = 65535;
        readonly float[] bufL = new float[BUF_MASK + 1];
        readonly float[] bufR = new float[BUF_MASK + 1];
        int writePos;

        // ── LFO state — one phase accumulator per voice ───────────────────────
        const int VOICES = 4;
        readonly double[] lfoPhase = new double[VOICES];

        // ── Tone filter state (one-pole LP on the wet path only) ─────────────
        float toneStateL;
        float toneStateR;

        // ── Silence / tail tracking ───────────────────────────────────────────
        // How long to keep running after input goes quiet so the chorus tail
        // (the delayed copies still in the ring buffer) can clear.
        // Max tail = BaseDelayMs(25) + Depth modulation(8) = 33 ms.
        // We add a generous safety margin: 100 ms expressed in samples,
        // recomputed each block from the current sample rate.
        // _tailCountdown counts down in samples; when it reaches 0 and input
        // is still silent we return false to save CPU.
        int  _tailCountdown;
        bool _inputWasSilent;

        // Threshold below which a sample is considered silent (−90 dBFS ≈ 3e-5).
        const float SILENCE_THRESHOLD = 3e-5f;

        // Tail hold time in ms — covers the longest possible delay + modulation
        // depth plus margin.  Recalculated to samples each block.
        const float TAIL_HOLD_MS = 100f;

        public PedalChorusMachine(IBuzzMachineHost host)
        {
            this.host = host;
            // Spread LFO phases evenly in quadrature: 0, 0.25, 0.5, 0.75
            for (int v = 0; v < VOICES; v++)
                lfoPhase[v] = v / (double)VOICES;
        }

        // =========================================================================
        // Parameters
        // =========================================================================

        /// <summary>LFO speed. 0 ≈ 0.1 Hz (slow shimmer), 100 ≈ 5 Hz (fast wobble).
        /// For drums, 15–40 is a sweet spot.</summary>
        [ParameterDecl(
            Name        = "Rate",
            Description = "Chorus LFO rate — 0=slow (0.1 Hz), 100=fast (5 Hz)",
            MinValue    = 0,
            MaxValue    = 100,
            DefValue    = 25)]
        public int Rate { get; set; } = 25;

        /// <summary>LFO depth. 0 = none, 100 = ±8 ms.
        /// Shallow depths (10–30) suit drums well.</summary>
        [ParameterDecl(
            Name        = "Depth",
            Description = "Modulation depth — 0=none, 100=±8 ms",
            MinValue    = 0,
            MaxValue    = 100,
            DefValue    = 35)]
        public int Depth { get; set; } = 35;

        /// <summary>Base (unmodulated) delay time per voice, 5–25 ms.
        /// Shorter = tighter chorus; longer = more spacious.</summary>
        [ParameterDecl(
            Name        = "Delay",
            Description = "Base delay per voice in ms (5–25)",
            MinValue    = 5,
            MaxValue    = 25,
            DefValue    = 12)]
        public int BaseDelayMs { get; set; } = 12;

        /// <summary>Stereo spread of the four voices.
        /// 0 = all voices centred (mono chorus), 100 = hard-panned.</summary>
        [ParameterDecl(
            Name        = "Spread",
            Description = "Stereo width — 0=mono, 100=wide",
            MinValue    = 0,
            MaxValue    = 100,
            DefValue    = 80)]
        public int Spread { get; set; } = 80;

        /// <summary>High-frequency roll-off on the wet signal only.
        /// 0 = bright (flat), 100 = dark (strong LP).
        /// Keeps hats crisp while thickening kick/snare.</summary>
        [ParameterDecl(
            Name        = "Tone",
            Description = "Wet-path HF roll-off — 0=bright, 100=dark",
            MinValue    = 0,
            MaxValue    = 100,
            DefValue    = 20)]
        public int Tone { get; set; } = 20;

        /// <summary>Wet/dry blend.
        /// 30–60 % wet tends to fatten drums without washing out transients.</summary>
        [ParameterDecl(
            Name        = "Mix",
            Description = "Wet/dry mix — 0=dry, 100=wet",
            MinValue    = 0,
            MaxValue    = 100,
            DefValue    = 45)]
        public int Mix { get; set; } = 45;

        // =========================================================================
        // Audio processing
        // =========================================================================

        public bool Work(Sample[] output, Sample[] input, int n, WorkModes mode)
        {
            // ── Fast path: ReBuzz signals no I/O this buffer ──────────────────
            // No need to touch the ring buffer or LFOs — they stay where they
            // are and will resume correctly when signal returns.
            if (mode == WorkModes.WM_NOIO) return false;

            int sr = host.MasterInfo.SamplesPerSec;

            // ── Silence detection ─────────────────────────────────────────────
            // Scan input for any sample above the silence floor.
            bool inputSilent = true;
            for (int i = 0; i < n; i++)
            {
                if (Math.Abs(input[i].L) > SILENCE_THRESHOLD ||
                    Math.Abs(input[i].R) > SILENCE_THRESHOLD)
                {
                    inputSilent = false;
                    break;
                }
            }

            if (!inputSilent)
            {
                // Live signal: reset the tail countdown so we keep running for
                // the full tail duration after this burst of input ends.
                _tailCountdown  = (int)(TAIL_HOLD_MS * sr / 1000f) + n;
                _inputWasSilent = false;
            }
            else
            {
                // Input is silent this buffer.
                if (_tailCountdown > 0)
                {
                    // Still within the tail hold window — keep processing so
                    // the delayed copies in the ring buffer can play out.
                    _tailCountdown -= n;
                    if (_tailCountdown < 0) _tailCountdown = 0;
                }
                else
                {
                    // Tail has fully cleared.  Zero the tone-filter state so it
                    // doesn't inject a DC offset when signal returns, then tell
                    // ReBuzz we produced silence — it can skip us next buffer.
                    if (!_inputWasSilent)
                    {
                        toneStateL      = 0f;
                        toneStateR      = 0f;
                        _inputWasSilent = true;
                    }
                    return false;
                }
            }

            // ── Map parameters once per block ─────────────────────────────────

            // LFO: 0.1 – 5.0 Hz
            double lfoRate      = 0.1 + Rate / 100.0 * 4.9;
            double lfoInc       = lfoRate / sr;

            // Depth: 0 – 8 ms in samples
            double depthSamples = Depth / 100.0 * 8.0 * sr / 1000.0;

            // Base delay: 5 – 25 ms in samples
            double baseSamples  = BaseDelayMs * (double)sr / 1000.0;

            // Mix
            float wet = Mix  / 100.0f;
            float dry = 1.0f - wet;

            // One-pole LP for Tone: cutoff sweeps 22 kHz → 2 kHz
            float toneFreq  = (float)(22000.0 - Tone / 100.0 * 20000.0);
            float toneCoeff = 1.0f - (float)Math.Exp(-2.0 * Math.PI * toneFreq / sr);

            // Equal-power pan table — voices spread across the stereo field.
            // Positions: −1, −0.33, +0.33, +1  scaled by Spread.
            float sp = Spread / 100.0f;
            float[] pos  = { -sp, -sp * 0.333f, sp * 0.333f, sp };
            float[] panL = new float[VOICES];
            float[] panR = new float[VOICES];
            for (int v = 0; v < VOICES; v++)
            {
                double angle = Math.PI / 4.0 * (1.0 + pos[v]);
                panL[v] = (float)Math.Cos(angle);
                panR[v] = (float)Math.Sin(angle);
            }

            // Gain per voice: ×2 compensates for the mono-sum halving.
            float voiceGain = 2.0f / VOICES;

            // ── Sample loop ───────────────────────────────────────────────────
            for (int i = 0; i < n; i++)
            {
                float inL = input[i].L;
                float inR = input[i].R;

                // Write dry signal into the ring buffer
                bufL[writePos] = inL;
                bufR[writePos] = inR;

                float sumL = 0f, sumR = 0f;

                for (int v = 0; v < VOICES; v++)
                {
                    // Sine LFO
                    double lfo = Math.Sin(lfoPhase[v] * 2.0 * Math.PI);

                    // Fractional delay in samples
                    double delaySamples = baseSamples + lfo * depthSamples;
                    if (delaySamples < 1.0) delaySamples = 1.0;

                    // Linear interpolation in the ring buffer
                    int   d0   = (int)delaySamples;
                    float frac = (float)(delaySamples - d0);
                    int   r0   = (writePos - d0)     & BUF_MASK;
                    int   r1   = (writePos - d0 - 1) & BUF_MASK;

                    float sL   = bufL[r0] + frac * (bufL[r1] - bufL[r0]);
                    float sR   = bufR[r0] + frac * (bufR[r1] - bufR[r0]);
                    float mono = (sL + sR) * 0.5f;

                    // Spread voice across stereo field
                    sumL += mono * panL[v];
                    sumR += mono * panR[v];

                    // Advance this voice's LFO
                    lfoPhase[v] += lfoInc;
                    if (lfoPhase[v] >= 1.0) lfoPhase[v] -= 1.0;
                }

                sumL *= voiceGain;
                sumR *= voiceGain;

                // Tone filter (wet path only — dry transient stays untouched)
                toneStateL += toneCoeff * (sumL - toneStateL);
                toneStateR += toneCoeff * (sumR - toneStateR);
                sumL = toneStateL;
                sumR = toneStateR;

                // Wet/dry blend
                output[i].L = dry * inL + wet * sumL;
                output[i].R = dry * inR + wet * sumR;

                writePos = (writePos + 1) & BUF_MASK;
            }

            return true;
        }
    }
}
