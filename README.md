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

## Installation Methods

Choose any of the three installation methods below:

### Method 1: 1-Click Installer (Recommended)
1. Download `MB_Mixer.dll` and `install.bat` from the latest Releases page.
2. Place both files in the same folder and double-click `install.bat`.
3. The installer automatically detects your MusicBee plugins directory, copies the plugin, and prepares the audio scanning engine.
4. Restart MusicBee, enable Mixer under **Edit > Preferences > Plugins**, and start auto-mixing.

### Method 2: Standalone Setup (No Python Required)
1. Download `MB_Mixer.dll` and `mixer_scanner.exe` from the latest Releases page.
2. Copy `MB_Mixer.dll` into your MusicBee Plugins folder:
   - Standard installer: `%APPDATA%\MusicBee\Plugins\`
   - Portable version: `MusicBee\Plugins\`
3. Copy `mixer_scanner.exe` into `%APPDATA%\MusicBee\Mixer\` (or directly into the `Plugins\` folder).
4. Restart MusicBee and enable Mixer under **Edit > Preferences > Plugins**.
5. Open the Mixer tab or go to **MusicBee > Tools > Mixer > Scan Library**.

*Automatic In-App Download*: If you only install `MB_Mixer.dll` on a PC without Python, Mixer will automatically prompt to download the standalone `mixer_scanner.exe` for you directly inside MusicBee when you click Scan Library.

### Method 3: Manual Python Setup (Advanced)
1. Download `MB_Mixer.dll` and copy it into your MusicBee Plugins folder:
   - `%APPDATA%\MusicBee\Plugins\`
2. Install the audio analysis dependencies:
   ```bash
   pip install librosa soundfile numpy
   ```
3. Run the library analyzer once to index your music files:
   ```bash
   python analyze_library.py
   ```
   Note: Initial scanning can take some time depending on how many songs you have in your library.
4. Restart MusicBee and enable Mixer under **Edit > Preferences > Plugins**.

## Using Mixer

Once installed and enabled:
- Open the **Mixer** tab from the top bar, dock it anywhere in your MusicBee panel layout, or access options from **MusicBee > Tools > Mixer**.
- Click **Play Random Song** to start a new set, or simply play any song from your library with **Auto-mix** enabled.
- Toggle **Vibe Lock** if you want to stay strictly within the current song's energy and mood without bridge progressions.

## Building from Source

Mixer targets .NET Framework 4.7.2 (x86) to match the MusicBee plugin architecture.

```bash
dotnet build -c Release
```

The output DLL will be located at `bin/Release/net472/MB_Mixer.dll`.

---

Author: buch

[buymeacoffee.com/buch](https://buymeacoffee.com/buch)
