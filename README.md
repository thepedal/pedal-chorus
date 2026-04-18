# Pedal Chorus

A managed effect machine for [ReBuzz](https://github.com/wasteddesign/ReBuzz) — a dense, CPU-efficient
**4-voice stereo chorus** designed specifically for drum sounds.

## Why 4 voices in quadrature?

Most chorus effects use a single LFO shared across all voices, which produces a
recognisable "waggle". Pedal Chorus gives each of the four delay taps its own LFO
phase offset (0°, 90°, 180°, 270°). The voices always move in opposite directions,
so the pitch modulation largely cancels out while the stereo movement and thickening
remain — exactly what you want on kick, snare and room mics.

## Features

| Feature | Detail |
|---|---|
| Voices | 4 independent delay taps with quadrature LFO phases |
| Rate | 0.1 – 5.0 Hz LFO sweep |
| Depth | 0 – ±8 ms modulation depth |
| Base Delay | 5 – 25 ms unmodulated tap delay |
| Spread | Equal-power stereo pan of all four voices |
| Tone | One-pole LP roll-off on the wet path (keeps transients tight) |
| Mix | Full dry-to-wet blend |

## Requirements

- [ReBuzz](https://github.com/wasteddesign/ReBuzz) (1812-preview or later)
- [.NET 10.0 Desktop Runtime (Windows x64)](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)
- [.NET 10.0 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) — to build from source

## Installation

1. Build from source (see below), **or** copy the pre-built `Pedal Chorus.NET.dll` directly.
2. Place `Pedal Chorus.NET.dll` in your ReBuzz Effects gear folder:
   ```
   C:\Program Files\ReBuzz\Gear\Effects\
   ```
3. Restart ReBuzz. **Pedal Chorus** will appear in the Effects section of the machine list.

## Building from source

```powershell
dotnet build PedalChorus.csproj -c Release
```

The DLL is written directly to `C:\Program Files\ReBuzz\Gear\Effects\`.

If ReBuzz is installed in a non-default location, pass the path on the command line:

```powershell
dotnet build PedalChorus.csproj -c Release /p:BuzzDir="D:\MyReBuzz"
```

## Parameters

| Parameter | Range | Default | Description |
|---|---|---|---|
| Rate | 0 – 100 | 25 | LFO speed: 0 ≈ 0.1 Hz (slow shimmer), 100 ≈ 5 Hz (fast wobble) |
| Depth | 0 – 100 | 35 | Modulation depth: 0 = none, 100 = ±8 ms |
| Delay | 5 – 25 ms | 12 | Base (unmodulated) delay time per voice |
| Spread | 0 – 100 | 80 | Stereo width: 0 = mono, 100 = fully spread |
| Tone | 0 – 100 | 20 | Wet-path HF roll-off: 0 = bright, 100 = dark |
| Mix | 0 – 100 | 45 | Wet/dry blend: 0 = dry, 100 = wet |

## Recommended starting points for drums

**Snare thickener** — Rate 20, Depth 30, Delay 10, Spread 85, Tone 25, Mix 40  
**Room widener** — Rate 15, Depth 20, Delay 18, Spread 100, Tone 40, Mix 35  
**Subtle glue** — Rate 10, Depth 15, Delay 8, Spread 60, Tone 15, Mix 25  
**Wet shimmer** — Rate 45, Depth 55, Delay 15, Spread 90, Tone 30, Mix 55  

## Design notes

- **Quadrature LFOs** cancel pitch modulation across voices, so drums stay punchy and
  in time even at high Depth settings.
- **Equal-power panning** ensures consistent perceived loudness as voices spread across
  the stereo field — no centre drop-out.
- **Tone filter** sits exclusively in the wet path, so the dry transient is always
  preserved at full bandwidth regardless of the Tone setting.
- **Power-of-two ring buffer** (65 536 samples) uses bitwise AND instead of modulo —
  the hot sample loop has zero integer division.

## License

MIT
