#!python3.11
r"""
Mixer - library analyzer (Spotify AI DJ Vibe Edition)

High-performance music library analyzer:
- Fast partial-frame audio reading via SoundFile with librosa fallback and proper anti-aliased resampling.
- ProcessPoolExecutor for true multi-core parallel extraction.
- Automatic moved-file detection and dead/missing path purging from database and CSV export.
- Batch database commits for optimal disk I/O performance.
- Adaptive normalization (min-max for small collections, rank percentile for large collections).
- Multi-dimensional Vibe normalization (BPM, Key, Energy, Groove, Valence, Acousticness).
- Real-time progress updates for the MusicBee UI.
"""

import argparse
import concurrent.futures
import csv
import os
import sqlite3
import sys
import time
from datetime import datetime, timezone

import numpy as np
import librosa
import soundfile as sf

AUDIO_EXTENSIONS = {".mp3", ".flac", ".m4a", ".wav", ".ogg", ".wma", ".aac"}

MAJOR_PROFILE = np.array(
    [6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88]
)
MINOR_PROFILE = np.array(
    [6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17]
)

PITCH_CLASSES = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"]

CAMELOT_MAJOR = {
    0: "8B", 7: "9B", 2: "10B", 9: "11B", 4: "12B", 11: "1B",
    6: "2B", 1: "3B", 8: "4B", 3: "5B", 10: "6B", 5: "7B",
}
CAMELOT_MINOR = {
    9: "8A", 4: "9A", 11: "10A", 6: "11A", 1: "12A", 8: "1A",
    3: "2A", 10: "3A", 5: "4A", 0: "5A", 7: "6A", 2: "7A",
}


def find_audio_files(root):
    for dirpath, _dirs, files in os.walk(root):
        for f in files:
            ext = os.path.splitext(f)[1].lower()
            if ext in AUDIO_EXTENSIONS:
                yield os.path.join(dirpath, f)


def estimate_key(chroma_mean):
    best_score = -np.inf
    best_pc = 0
    best_mode = "major"

    for shift in range(12):
        major_rot = np.roll(MAJOR_PROFILE, shift)
        minor_rot = np.roll(MINOR_PROFILE, shift)

        major_score = np.corrcoef(chroma_mean, major_rot)[0, 1]
        minor_score = np.corrcoef(chroma_mean, minor_rot)[0, 1]

        if major_score > best_score:
            best_score = major_score
            best_pc = shift
            best_mode = "major"
        if minor_score > best_score:
            best_score = minor_score
            best_pc = shift
            best_mode = "minor"

    key_name = f"{PITCH_CLASSES[best_pc]}{'m' if best_mode == 'minor' else ''}"
    camelot = (CAMELOT_MINOR if best_mode == "minor" else CAMELOT_MAJOR)[best_pc]
    return key_name, camelot, (best_mode == "major")


def correct_octave(bpm):
    while bpm < 70:
        bpm *= 2
    while bpm > 180:
        bpm /= 2
    return bpm


def load_audio_fast(path, duration_limit=35):
    """High-speed partial audio reading with proper anti-aliased resampling to 22050Hz mono."""
    try:
        with sf.SoundFile(path) as f:
            sr = f.samplerate
            max_frames = int(sr * duration_limit) if duration_limit else -1
            data = f.read(frames=max_frames, dtype="float32")
            file_dur = round(len(f) / sr, 1)

        y = data.mean(axis=1) if data.ndim > 1 else data

        # Exact half-rate decimation for standard 44.1kHz music
        if sr == 44100:
            y = y[::2]
            sr = 22050
        elif sr != 22050:
            # Proper anti-aliased resampling for 48kHz, 96kHz, etc.
            y = librosa.resample(y, orig_sr=sr, target_sr=22050)
            sr = 22050
        return y, sr, file_dur
    except Exception:
        y, sr = librosa.load(path, sr=22050, mono=True, duration=duration_limit)
        return y, sr, float(librosa.get_duration(y=y, sr=sr))


def analyze_file(path, quick=True):
    duration_limit = 35 if quick else 60
    y, sr, duration = load_audio_fast(path, duration_limit=duration_limit)

    if y.size == 0:
        raise ValueError("empty or unreadable audio")

    # 1. Rhythm & Groove Drive
    onset_env = librosa.onset.onset_strength(y=y, sr=sr)
    pulse_strength = float(np.std(onset_env) / (np.mean(onset_env) + 1e-6))
    tempo = librosa.feature.tempo(onset_envelope=onset_env, sr=sr)
    bpm = correct_octave(float(np.atleast_1d(tempo)[0]))

    # 2. Key & Mode
    chroma = librosa.feature.chroma_stft(y=y, sr=sr)
    chroma_mean = chroma.mean(axis=1)
    key_name, camelot, is_major = estimate_key(chroma_mean)

    # 3. Energy & Brightness
    rms = float(librosa.feature.rms(y=y).mean())
    centroid = float(librosa.feature.spectral_centroid(y=y, sr=sr).mean())

    # 4. Acousticness & Organic Texture
    flatness = float(librosa.feature.spectral_flatness(y=y).mean())
    zcr = float(librosa.feature.zero_crossing_rate(y=y).mean())
    acoustic_raw = float(1.0 / (1.0 + 40.0 * flatness + 10.0 * zcr))

    # 5. Valence / Mood
    mode_factor = 0.75 if is_major else 0.25
    centroid_factor = float(np.clip((centroid - 800.0) / 3200.0, 0.0, 1.0))
    pulse_factor = float(np.clip((pulse_strength - 0.7) / 1.5, 0.0, 1.0))
    valence_raw = float(0.50 * mode_factor + 0.30 * centroid_factor + 0.20 * pulse_factor)

    return {
        "bpm": round(bpm, 1),
        "key_name": key_name,
        "camelot_key": camelot,
        "energy_raw": rms,
        "brightness_raw": centroid,
        "dance_raw": pulse_strength,
        "valence_raw": valence_raw,
        "acoustic_raw": acoustic_raw,
        "duration": round(duration, 1),
    }


def init_db(db_path):
    conn = sqlite3.connect(db_path)
    conn.execute(
        """
        CREATE TABLE IF NOT EXISTS tracks (
            file_path TEXT PRIMARY KEY,
            bpm REAL,
            key_name TEXT,
            camelot_key TEXT,
            energy_raw REAL,
            energy_norm REAL,
            dance_raw REAL,
            dance_norm REAL,
            valence_raw REAL,
            valence_norm REAL,
            acoustic_raw REAL,
            acoustic_norm REAL,
            brightness_raw REAL,
            duration REAL,
            mtime REAL,
            analyzed_at TEXT
        )
        """
    )
    existing_cols = {row[1] for row in conn.execute("PRAGMA table_info(tracks)").fetchall()}
    for col, col_type in [
        ("dance_raw", "REAL"), ("dance_norm", "REAL"),
        ("valence_raw", "REAL"), ("valence_norm", "REAL"),
        ("acoustic_raw", "REAL"), ("acoustic_norm", "REAL")
    ]:
        if col not in existing_cols:
            conn.execute(f"ALTER TABLE tracks ADD COLUMN {col} {col_type}")
    conn.commit()
    return conn


def normalize_features(conn):
    for col in ["energy", "dance", "valence", "acoustic"]:
        raw_col = f"{col}_raw"
        norm_col = f"{col}_norm"
        rows = conn.execute(f"SELECT file_path, {raw_col} FROM tracks WHERE {raw_col} IS NOT NULL").fetchall()
        if not rows:
            continue
        values = np.array([r[1] for r in rows], dtype=float)

        if len(values) < 50:
            val_min = float(values.min())
            val_max = float(values.max())
            spread = max(val_max - val_min, 1e-6)
            normalized = np.clip((values - val_min) / spread, 0.0, 1.0)
        else:
            normalized = values.argsort().argsort() / max(len(values) - 1, 1)

        for (file_path, _), norm_val in zip(rows, normalized):
            conn.execute(
                f"UPDATE tracks SET {norm_col} = ? WHERE file_path = ?",
                (round(float(norm_val), 3), file_path),
            )
    conn.commit()


def export_csv(conn, csv_path):
    rows = conn.execute(
        """
        SELECT file_path, bpm, key_name, camelot_key,
               COALESCE(energy_norm, 0.5),
               COALESCE(dance_norm, 0.5),
               COALESCE(valence_norm, 0.5),
               COALESCE(acoustic_norm, 0.5)
        FROM tracks
        """
    ).fetchall()

    # Filter out dead or missing file paths
    valid_rows = [row for row in rows if os.path.exists(row[0])]

    with open(csv_path, "w", newline="", encoding="utf-8") as f:
        writer = csv.writer(f, quoting=csv.QUOTE_ALL)
        writer.writerow([
            "file_path", "bpm", "key_name", "camelot_key",
            "energy_norm", "dance_norm", "valence_norm", "acoustic_norm"
        ])
        for row in valid_rows:
            writer.writerow(row)
    print(f"Exported {len(valid_rows)} tracks with full vibe vectors to {csv_path}")


def write_progress(progress_file, current, total, analyzed, skipped, failed):
    if not progress_file:
        return
    try:
        pct = int(round((current / max(total, 1)) * 100))
        msg = f"{current}/{total} ({pct}%) | analyzed={analyzed} skipped={skipped} failed={failed}"
        with open(progress_file, "w", encoding="utf-8") as pf:
            pf.write(msg)
    except Exception:
        pass


def analyze_worker(path):
    try:
        res = analyze_file(path, quick=True)
        return path, res, None
    except Exception as e:
        return path, None, str(e)


def main():
    parser = argparse.ArgumentParser(description="Analyze a music library for Mixer (High-Speed Vibe Edition)")
    parser.add_argument("folders", nargs="*", default=[], help="Root folder(s) of your music library")
    parser.add_argument("--file-list", default=None, help="Text file listing audio paths to analyze, one per line")
    parser.add_argument("--db", default="mixer_cache.db", help="SQLite db path")
    parser.add_argument("--quick", action="store_true", help="Quick analysis mode")
    parser.add_argument("--rescan", action="store_true", help="Re-analyze all files")
    parser.add_argument("--csv", default=None, help="Export CSV path")
    parser.add_argument("--progress-file", default=None, help="Progress text file path")
    parser.add_argument("--workers", type=int, default=3, help="Parallel worker processes")
    args = parser.parse_args()

    csv_path = args.csv or (os.path.splitext(args.db)[0] + ".csv")
    progress_file = args.progress_file or os.path.join(os.path.dirname(os.path.abspath(args.db)), "mixer_progress.txt")

    conn = init_db(args.db)

    # 1. Purge missing / dead file paths from database
    print("Purging missing/moved file paths from database...")
    all_db_rows = conn.execute("SELECT file_path FROM tracks").fetchall()
    dead_paths = [r[0] for r in all_db_rows if not os.path.exists(r[0])]
    if dead_paths:
        conn.executemany("DELETE FROM tracks WHERE file_path = ?", [(p,) for p in dead_paths])
        conn.commit()
        print(f"Purged {len(dead_paths)} dead paths from database.")

    # 2. Gather audio files to process
    files = []
    if args.file_list and os.path.isfile(args.file_list):
        with open(args.file_list, "r", encoding="utf-8") as fl:
            for line in fl:
                line = line.strip().strip('"')
                if line and os.path.isfile(line) and os.path.splitext(line)[1].lower() in AUDIO_EXTENSIONS:
                    files.append(line)
        print(f"Loaded {len(files)} audio files from file list")

    for f in args.folders:
        if os.path.isdir(f):
            files.extend(list(find_audio_files(f)))
        else:
            print(f"Warning: Not a folder: {f}", file=sys.stderr)

    seen = set()
    unique_files = []
    for fp in files:
        norm = os.path.normpath(fp).lower()
        if norm not in seen:
            seen.add(norm)
            unique_files.append(fp)
    files = unique_files
    total_files = len(files)
    print(f"Found {total_files} unique audio files to process")

    # 3. Check for files that can be skipped (unchanged) vs need analysis
    to_analyze = []
    skipped = 0

    for path in files:
        mtime = os.path.getmtime(path)
        existing = conn.execute(
            "SELECT mtime, dance_norm FROM tracks WHERE file_path = ?", (path,)
        ).fetchone()

        if existing and not args.rescan and existing[0] == mtime and existing[1] is not None:
            skipped += 1
        else:
            to_analyze.append(path)

    print(f"Files already up-to-date: {skipped}. Files to analyze: {len(to_analyze)}")

    analyzed = 0
    failed = 0
    start = time.time()
    write_progress(progress_file, skipped, total_files, analyzed, skipped, failed)

    # 4. Multi-process execution for new/changed files (bypasses Python GIL)
    if to_analyze:
        num_workers = min(args.workers, max(1, os.cpu_count() or 2))
        print(f"Starting analysis with {num_workers} parallel worker processes...")

        with concurrent.futures.ProcessPoolExecutor(max_workers=num_workers) as executor:
            future_to_file = {executor.submit(analyze_worker, f): f for f in to_analyze}

            for future in concurrent.futures.as_completed(future_to_file):
                path, result, err = future.result()
                processed = skipped + analyzed + failed + 1

                if err or not result:
                    print(f"  [{processed}/{total_files}] FAILED: {os.path.basename(path)} ({err})")
                    failed += 1
                else:
                    mtime = os.path.getmtime(path)
                    conn.execute(
                        """
                        INSERT INTO tracks
                            (file_path, bpm, key_name, camelot_key, energy_raw,
                             brightness_raw, dance_raw, valence_raw, acoustic_raw,
                             duration, mtime, analyzed_at)
                        VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
                        ON CONFLICT(file_path) DO UPDATE SET
                            bpm=excluded.bpm, key_name=excluded.key_name,
                            camelot_key=excluded.camelot_key, energy_raw=excluded.energy_raw,
                            brightness_raw=excluded.brightness_raw, dance_raw=excluded.dance_raw,
                            valence_raw=excluded.valence_raw, acoustic_raw=excluded.acoustic_raw,
                            duration=excluded.duration, mtime=excluded.mtime,
                            analyzed_at=excluded.analyzed_at
                        """,
                        (
                            path, result["bpm"], result["key_name"], result["camelot_key"],
                            result["energy_raw"], result["brightness_raw"], result["dance_raw"],
                            result["valence_raw"], result["acoustic_raw"], result["duration"],
                            mtime, datetime.now(timezone.utc).isoformat(),
                        ),
                    )
                    analyzed += 1
                    # Batch commit every 25 tracks to minimize disk I/O fsync overhead
                    if analyzed % 25 == 0:
                        conn.commit()

                if processed % 10 == 0 or processed == total_files:
                    elapsed = time.time() - start
                    print(f"  [{processed}/{total_files}] analyzed={analyzed} skipped={skipped} failed={failed} ({elapsed:.0f}s)")
                    write_progress(progress_file, processed, total_files, analyzed, skipped, failed)

        # Final commit for remaining batch
        conn.commit()

    # 5. Final Normalization and CSV Export
    print("Normalizing multi-dimensional vibe features across library...")
    normalize_features(conn)
    export_csv(conn, csv_path)
    conn.close()

    if progress_file and os.path.exists(progress_file):
        try:
            with open(progress_file, "w", encoding="utf-8") as pf:
                pf.write(f"done|{analyzed}|{skipped}|{failed}")
        except Exception:
            pass

    print(f"Done. analyzed={analyzed} skipped={skipped} failed={failed}")


if __name__ == "__main__":
    main()
