# Mixer for MusicBee

An intelligent vibe-matching Auto DJ plugin for MusicBee.

Standard Auto DJ typically relies on broad metadata like genre tags or basic shuffle rules, which often leads to abrupt mood changes. Mixer analyzes the actual acoustic characteristics of your audio library to queue songs that genuinely fit together in tempo, harmony, and energy.

## How It Works

Mixer profiles each track across several musical dimensions:
- Tempo and rhythm: Matches BPM accurately, including half-time and double-time compatibility.
- Harmonic key: Follows the Camelot wheel to ensure smooth musical transitions between keys.
- Energy and danceability: Matches dynamic intensity, rhythm strength, and perceived volume.
- Mood and acoustic texture: Aligns valence (positive/negative feel) and instrumentation density.
- Groove pockets and bridge transitions: Keeps a continuous flow across 3-song micro-sets before introducing gentle progression.

## Installation

1. Download the latest `MB_Mixer.dll` from the Releases page.
2. Copy `MB_Mixer.dll` into your MusicBee Plugins folder:
   - Installer version: `%APPDATA%\MusicBee\Plugins\`
   - Portable version: `MusicBee\Plugins\`
3. Install the analysis dependencies if running the scanner locally:
   ```bash
   pip install librosa soundfile numpy
   ```
4. Run the library analyzer once to index your music files:
   ```bash
   python analyze_library.py
   ```
   Note: Scanning can take some time depending on how many songs you have in your library.
5. Restart MusicBee and enable Mixer under Edit > Preferences > Plugins.
6. Open the Mixer tab from the top bar, dock it into your layout, or access it from MusicBee > Tools > Mixer to start auto-mixing.

## Building from Source

Mixer targets .NET Framework 4.7.2 (x86) to match the MusicBee plugin architecture.

```bash
dotnet build -c Release
```

The output DLL will be located at `bin/Release/net472/MB_Mixer.dll`.

---

Author: buch

[buymeacoffee.com/buch](https://buymeacoffee.com/buch)
