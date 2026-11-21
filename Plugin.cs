// Mixer - MusicBee plugin (Spotify AI DJ Vibe Edition)
//
// Complete logic overhaul:
// 1. Duplicate track suppression & title/artist fingerprinting with bounded eviction.
// 2. Pocket arc desynchronization fix on manual track changes.
// 3. Thread-safe snapshot cache swapping.
// 4. Settings persistence across MusicBee restarts.
// 5. Rebalanced distance metric with functional Bridge transitions.
// 6. Relative BPM tolerance (% based).
// 7. O(1) thread-safe history lookups via hash sets and fingerprint counters.
// 8. Elimination of API call storm by pre-filtering candidates.
// 9. Queue stacking prevention.
// 10. Fallback queueing for uncached tracks.
// 11. Cross-session history persistence with deferred tag resolution.
// 12. Mid-energy start ramp for "Play Random Song".
// 13. Dynamic script path discovery.
// 14. Real-time background scan progress polling in the UI.
// 15. Single-check file verification eliminating I/O storms on NAS/network drives.
// 16. Python environment detection & privacy-safe library fallback paths.
// 17. Multiplier-based user affinity weighting preserving audio match priority.
// 18. Self-capping debug log rotation at 2MB.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace MusicBeePlugin
{
    public partial class Plugin
    {
        private MusicBeeApiInterface mbApiInterface;
        private PluginInfo about = new PluginInfo();
        private volatile bool isScanning = false;
        private Label lblStatus;
        private bool autoMixEnabled = true;
        private bool vibeLockEnabled = false;
        private string lastQueuedForTrack = null;
        private string lastQueuedTrackUrl = null;

        // DJ Set Flow / Pocket tracking
        private int sessionPocketTrackCount = 0; // 0, 1, 2 = groove lock; 3 = bridge

        private Control mixerMainPanel;
        private Label lblNowPlayingTitle;
        private Label lblNowPlayingDetails;
        private Label lblUpNextTitle;
        private Label lblUpNextDetails;
        private Label lblMixerStats;
        private CheckBox chkAutoMix;
        private CheckBox chkVibeLock;
        private Button btnPlayRandom;
        private Button btnQueueNext;
        private Button btnRescanTab;
        private System.Windows.Forms.Timer uiUpdateTimer;

        private struct TrackFeatures
        {
            public double Bpm;
            public string CamelotKey;
            public double EnergyNorm;      // 0.0 to 1.0 (loudness/intensity)
            public double DanceNorm;       // 0.0 to 1.0 (rhythm drive & pulse strength)
            public double ValenceNorm;     // 0.0 to 1.0 (musical mood: 0=somber/dark, 1=euphoric/bright)
            public double AcousticNorm;    // 0.0 to 1.0 (0=synthesized/distorted, 1=acoustic/organic)
        }

        private class HistoryEntry
        {
            public string Url;
            public string FilenameKey;
            public string TitleArtistKey;
        }

        private readonly object libraryLock = new object();
        private Dictionary<string, TrackFeatures> library =
            new Dictionary<string, TrackFeatures>(StringComparer.OrdinalIgnoreCase);

        private readonly object historyLock = new object();
        private readonly LinkedList<HistoryEntry> recentHistory = new LinkedList<HistoryEntry>();
        private readonly HashSet<string> recentHistorySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> recentFingerprintCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        private readonly Random rng = new Random();
        private int totalLibraryFileCount = 0;

        private int HistoryLimit
        {
            get
            {
                int count;
                lock (libraryLock) { count = library.Count; }
                return count > 200 ? 50 : 20;
            }
        }

        private struct Candidate
        {
            public string Url;
            public double RawDistance;
            public double AdjustedDistance;
            public double BpmGap;
        }

        private struct PickResult
        {
            public string Url;
            public double Distance;
            public bool IsBridge;
            public int PocketIndex;
        }

        private string MixerFolder
        {
            get { return Path.Combine(mbApiInterface.Setting_GetPersistentStoragePath(), "Mixer"); }
        }

        private string CachePath
        {
            get { return Path.Combine(MixerFolder, "mixer_cache.csv"); }
        }

        private string SettingsPath
        {
            get { return Path.Combine(MixerFolder, "mixer_settings.ini"); }
        }

        private string HistoryPath
        {
            get { return Path.Combine(MixerFolder, "mixer_history.txt"); }
        }

        private string ProgressPath
        {
            get { return Path.Combine(MixerFolder, "mixer_progress.txt"); }
        }

        public PluginInfo Initialise(IntPtr apiInterfacePtr)
        {
            mbApiInterface = new MusicBeeApiInterface();
            mbApiInterface.Initialise(apiInterfacePtr);

            about.PluginInfoVersion = PluginInfoVersion;
            about.Name = "Mixer";
            about.Description = "Spotify AI DJ: Multi-Dimensional Vibe & Harmonic Transitions";
            about.Author = "Buch";
            about.TargetApplication = "Mixer";
            about.Type = PluginType.General;
            about.VersionMajor = 2;
            about.VersionMinor = 2;
            about.Revision = 0;
            about.MinInterfaceVersion = MinInterfaceVersion;
            about.MinApiRevision = MinApiRevision;
            about.ReceiveNotifications = ReceiveNotificationFlags.PlayerEvents;
            about.ConfigurationPanelHeight = 55;

            LoadSettings();
            LoadHistory();
            LoadCache();

            int cachedCount;
            lock (libraryLock) { cachedCount = library.Count; }
            WriteLog("Initialised Vibe DJ Edition v2.2. TracksLoaded=" + cachedCount);

            try
            {
                mbApiInterface.MB_AddMenuItem("mnuTools/Mixer: Play Random Song", "Play a random song with Mixer", (sender, e) =>
                {
                    PlayRandomSongAndMix();
                });
                mbApiInterface.MB_AddMenuItem("mnuTools/Mixer: Scan Library", "Scan entire library for Mixer", (sender, e) =>
                {
                    StartBackgroundScan(false);
                });
            }
            catch (Exception ex)
            {
                WriteLog("Could not register menu item: " + ex.Message);
            }

            return about;
        }

        public int OnDockablePanelCreated(Control panel)
        {
            if (panel.InvokeRequired)
            {
                panel.Invoke((MethodInvoker)(() => InitMixerTabUI(panel)));
            }
            else
            {
                InitMixerTabUI(panel);
            }
            return 0; // 0 indicates resizable dockable panel
        }

        public bool Configure(IntPtr panelHandle)
        {
            if (panelHandle != IntPtr.Zero)
            {
                Panel configPanel = (Panel)Panel.FromHandle(panelHandle);
                configPanel.Controls.Clear();

                Button btnScan = new Button
                {
                    Text = "Scan Library Now",
                    Location = new Point(10, 12),
                    Size = new Size(130, 28)
                };

                int cachedCount;
                lock (libraryLock) { cachedCount = library.Count; }

                lblStatus = new Label
                {
                    Text = isScanning ? "Status: Scanning in background..." : ("Status: " + cachedCount + " tracks in cache."),
                    Location = new Point(150, 18),
                    AutoSize = true
                };

                btnScan.Click += (s, e) =>
                {
                    StartBackgroundScan(false);
                };

                configPanel.Controls.Add(btnScan);
                configPanel.Controls.Add(lblStatus);
            }
            return false;
        }

        public void SaveSettings()
        {
            SaveSettingsToFile();
            SaveHistoryToFile();
        }

        public void Close(PluginCloseReason reason)
        {
            SaveSettingsToFile();
            SaveHistoryToFile();
            if (uiUpdateTimer != null)
            {
                uiUpdateTimer.Stop();
                uiUpdateTimer.Dispose();
            }
        }

        public void Uninstall()
        {
        }

        public void ReceiveNotification(string sourceFileUrl, NotificationType type)
        {
            if (type == NotificationType.PluginStartup)
            {
                // Defer background fingerprinting of loaded history to avoid startup API blocking
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        lock (historyLock)
                        {
                            foreach (var item in recentHistory)
                            {
                                if (item.TitleArtistKey == null && !string.IsNullOrEmpty(item.Url))
                                {
                                    string title = mbApiInterface.Library_GetFileTag(item.Url, MetaDataType.TrackTitle);
                                    string artist = mbApiInterface.Library_GetFileTag(item.Url, MetaDataType.Artist);
                                    if (!string.IsNullOrEmpty(title))
                                    {
                                        item.TitleArtistKey = (artist ?? "").Trim().ToLowerInvariant() + "|" + title.Trim().ToLowerInvariant();
                                        IncrementFingerprint(item.TitleArtistKey);
                                    }
                                }
                            }
                        }
                    }
                    catch {}
                });

                try
                {
                    string[] files;
                    bool hasFiles = mbApiInterface.Library_QueryFilesEx("", out files);
                    totalLibraryFileCount = (hasFiles && files != null) ? files.Length : 0;

                    int cachedCount;
                    lock (libraryLock) { cachedCount = library.Count; }

                    if (cachedCount == 0 || (totalLibraryFileCount > 0 && totalLibraryFileCount > cachedCount + 5))
                    {
                        WriteLog(string.Format(CultureInfo.InvariantCulture, "Auto-scan triggered on startup (library={0}, cached={1})", totalLibraryFileCount, cachedCount));
                        StartBackgroundScan(true);
                    }
                }
                catch (Exception ex)
                {
                    WriteLog("Error checking library on startup: " + ex.Message);
                }
            }
            else if (type == NotificationType.TrackChanged)
            {
                string url = NormalizeUrl(sourceFileUrl);
                if (string.IsNullOrEmpty(url))
                {
                    url = NormalizeUrl(mbApiInterface.NowPlaying_GetFileUrl());
                }

                // If user manually changed or skipped to a track not queued by Mixer, reset the pocket arc
                if (!string.IsNullOrEmpty(lastQueuedTrackUrl) && !string.Equals(url, lastQueuedTrackUrl, StringComparison.OrdinalIgnoreCase))
                {
                    sessionPocketTrackCount = 0;
                }

                RememberPlayed(url);

                if (autoMixEnabled)
                {
                    QueueNextIfNeeded(url, force: false);
                }
                else
                {
                    UpdateMixerUI(url, null, 0, false, 1);
                }
            }
        }

        // ---- Settings & History Persistence ----

        private void LoadSettings()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    string[] lines = File.ReadAllLines(SettingsPath);
                    foreach (string line in lines)
                    {
                        string trimmed = line.Trim();
                        if (trimmed.StartsWith("AutoMix=", StringComparison.OrdinalIgnoreCase))
                        {
                            bool.TryParse(trimmed.Substring(8), out autoMixEnabled);
                        }
                        else if (trimmed.StartsWith("VibeLock=", StringComparison.OrdinalIgnoreCase))
                        {
                            bool.TryParse(trimmed.Substring(9), out vibeLockEnabled);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                WriteLog("LoadSettings error: " + ex.Message);
            }
        }

        private void SaveSettingsToFile()
        {
            try
            {
                Directory.CreateDirectory(MixerFolder);
                string content = string.Format(CultureInfo.InvariantCulture, "AutoMix={0}\r\nVibeLock={1}\r\n", autoMixEnabled, vibeLockEnabled);
                File.WriteAllText(SettingsPath, content);
            }
            catch (Exception ex)
            {
                WriteLog("SaveSettings error: " + ex.Message);
            }
        }

        private void LoadHistory()
        {
            try
            {
                if (File.Exists(HistoryPath))
                {
                    string[] lines = File.ReadAllLines(HistoryPath);
                    lock (historyLock)
                    {
                        recentHistory.Clear();
                        recentHistorySet.Clear();
                        recentFingerprintCounts.Clear();

                        for (int i = lines.Length - 1; i >= 0; i--)
                        {
                            string url = lines[i].Trim();
                            if (!string.IsNullOrEmpty(url))
                            {
                                string fnKey = Path.GetFileNameWithoutExtension(url).Trim().ToLowerInvariant();
                                recentHistorySet.Add(url);
                                IncrementFingerprint(fnKey);
                                recentHistory.AddFirst(new HistoryEntry
                                {
                                    Url = url,
                                    FilenameKey = fnKey,
                                    TitleArtistKey = null
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                WriteLog("LoadHistory error: " + ex.Message);
            }
        }

        private void SaveHistoryToFile()
        {
            try
            {
                Directory.CreateDirectory(MixerFolder);
                List<string> list = new List<string>();
                lock (historyLock)
                {
                    foreach (var entry in recentHistory)
                    {
                        list.Add(entry.Url);
                    }
                }
                File.WriteAllLines(HistoryPath, list.ToArray());
            }
            catch (Exception ex)
            {
                WriteLog("SaveHistory error: " + ex.Message);
            }
        }

        private void IncrementFingerprint(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            int count;
            if (recentFingerprintCounts.TryGetValue(key, out count))
            {
                recentFingerprintCounts[key] = count + 1;
            }
            else
            {
                recentFingerprintCounts[key] = 1;
            }
        }

        private void DecrementFingerprint(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            int count;
            if (recentFingerprintCounts.TryGetValue(key, out count))
            {
                if (count <= 1)
                {
                    recentFingerprintCounts.Remove(key);
                }
                else
                {
                    recentFingerprintCounts[key] = count - 1;
                }
            }
        }

        // ---- UI: Mixer Dockable Panel / Tab ----

        private Color GetSkinColor(SkinElement element, ElementState state, ElementComponent component, Color fallback)
        {
            try
            {
                int argb = mbApiInterface.Setting_GetSkinElementColour(element, state, component);
                if (argb != 0)
                {
                    return Color.FromArgb(argb);
                }
            }
            catch
            {
            }
            return fallback;
        }

        private void InitMixerTabUI(Control parent)
        {
            try
            {
                mixerMainPanel = parent;
                parent.Controls.Clear();

                Color panelBg = GetSkinColor(SkinElement.SkinSubPanel, ElementState.ElementStateDefault, ElementComponent.ComponentBackground, Color.FromArgb(30, 30, 34));
                Color panelFg = GetSkinColor(SkinElement.SkinSubPanel, ElementState.ElementStateDefault, ElementComponent.ComponentForeground, Color.FromArgb(235, 235, 240));
                bool isDark = panelBg.GetBrightness() < 0.5f;

                Color cardBg = isDark ? Color.FromArgb(38, 38, 44) : Color.FromArgb(245, 245, 248);
                Color cardBorder = isDark ? Color.FromArgb(65, 65, 75) : Color.FromArgb(215, 215, 225);
                Color subText = isDark ? Color.FromArgb(165, 165, 180) : Color.FromArgb(105, 105, 115);
                Color accentBtnBg = Color.FromArgb(37, 99, 235);
                Color secondaryBtnBg = isDark ? Color.FromArgb(50, 50, 60) : Color.FromArgb(225, 225, 232);

                parent.BackColor = panelBg;

                Panel root = new Panel
                {
                    Dock = DockStyle.Fill,
                    AutoScroll = true,
                    Padding = new Padding(20),
                    BackColor = panelBg
                };
                parent.Controls.Add(root);

                // Header
                Label lblHeader = new Label
                {
                    Text = "Mixer AI DJ",
                    Font = new Font("Segoe UI", 16F, FontStyle.Bold),
                    ForeColor = panelFg,
                    AutoSize = true,
                    Location = new Point(20, 15)
                };
                root.Controls.Add(lblHeader);

                Label lblSubHeader = new Label
                {
                    Text = "Intelligent Vibe DJ • Harmonic Flow, Timbre Texture, Rhythm Dynamics & DJ Narrative Arcs",
                    Font = new Font("Segoe UI", 9F, FontStyle.Regular),
                    ForeColor = subText,
                    AutoSize = true,
                    Location = new Point(22, 45)
                };
                root.Controls.Add(lblSubHeader);

                // Action buttons flow panel
                FlowLayoutPanel btnFlow = new FlowLayoutPanel
                {
                    Location = new Point(20, 75),
                    Size = new Size(700, 48),
                    FlowDirection = FlowDirection.LeftToRight,
                    WrapContents = false,
                    AutoSize = true
                };

                btnPlayRandom = new Button
                {
                    Text = "Play Random Song",
                    Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                    Size = new Size(180, 38),
                    BackColor = accentBtnBg,
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Cursor = Cursors.Hand,
                    Margin = new Padding(0, 0, 10, 0)
                };
                btnPlayRandom.FlatAppearance.BorderSize = 0;
                btnPlayRandom.Click += (s, e) => PlayRandomSongAndMix();
                btnFlow.Controls.Add(btnPlayRandom);

                btnQueueNext = new Button
                {
                    Text = "Queue Next Match",
                    Font = new Font("Segoe UI", 9F, FontStyle.Regular),
                    Size = new Size(150, 38),
                    BackColor = secondaryBtnBg,
                    ForeColor = panelFg,
                    FlatStyle = FlatStyle.Flat,
                    Cursor = Cursors.Hand,
                    Margin = new Padding(0, 0, 10, 0)
                };
                btnQueueNext.FlatAppearance.BorderColor = cardBorder;
                btnQueueNext.Click += (s, e) => QueueAnotherMatch();
                btnFlow.Controls.Add(btnQueueNext);

                btnRescanTab = new Button
                {
                    Text = "Rescan Library",
                    Font = new Font("Segoe UI", 9F, FontStyle.Regular),
                    Size = new Size(130, 38),
                    BackColor = secondaryBtnBg,
                    ForeColor = panelFg,
                    FlatStyle = FlatStyle.Flat,
                    Cursor = Cursors.Hand,
                    Margin = new Padding(0, 0, 10, 0)
                };
                btnRescanTab.FlatAppearance.BorderColor = cardBorder;
                btnRescanTab.Click += (s, e) => StartBackgroundScan(false);
                btnFlow.Controls.Add(btnRescanTab);

                root.Controls.Add(btnFlow);

                // Checkboxes container
                FlowLayoutPanel chkFlow = new FlowLayoutPanel
                {
                    Location = new Point(20, 128),
                    Size = new Size(750, 32),
                    FlowDirection = FlowDirection.LeftToRight,
                    WrapContents = false,
                    AutoSize = true
                };

                chkAutoMix = new CheckBox
                {
                    Text = "Auto-mix: Instantly queue harmonic match whenever any song plays",
                    Checked = autoMixEnabled,
                    Font = new Font("Segoe UI", 9.25F, FontStyle.Regular),
                    ForeColor = panelFg,
                    AutoSize = true,
                    Cursor = Cursors.Hand,
                    Margin = new Padding(2, 2, 20, 2)
                };
                chkAutoMix.CheckedChanged += (s, e) =>
                {
                    autoMixEnabled = chkAutoMix.Checked;
                    SaveSettingsToFile();
                    UpdateStatsLabel();
                };
                chkFlow.Controls.Add(chkAutoMix);

                chkVibeLock = new CheckBox
                {
                    Text = "Vibe Lock: Lock current mood & energy (no bridge transitions)",
                    Checked = vibeLockEnabled,
                    Font = new Font("Segoe UI", 9.25F, FontStyle.Regular),
                    ForeColor = panelFg,
                    AutoSize = true,
                    Cursor = Cursors.Hand,
                    Margin = new Padding(2, 2, 2, 2)
                };
                chkVibeLock.CheckedChanged += (s, e) =>
                {
                    vibeLockEnabled = chkVibeLock.Checked;
                    SaveSettingsToFile();
                    UpdateStatsLabel();
                };
                chkFlow.Controls.Add(chkVibeLock);

                root.Controls.Add(chkFlow);

                // Currently Playing Card
                Panel cardNowPlaying = CreateCardPanel(
                    new Point(20, 165),
                    new Size(660, 95),
                    cardBg,
                    cardBorder,
                    "CURRENTLY PLAYING",
                    panelFg,
                    subText,
                    out lblNowPlayingTitle,
                    out lblNowPlayingDetails
                );
                root.Controls.Add(cardNowPlaying);

                // Up Next Card
                Panel cardUpNext = CreateCardPanel(
                    new Point(20, 275),
                    new Size(660, 95),
                    cardBg,
                    cardBorder,
                    "QUEUED NEXT BY MIXER (VIBE HARMONIC MATCH)",
                    panelFg,
                    subText,
                    out lblUpNextTitle,
                    out lblUpNextDetails
                );
                root.Controls.Add(cardUpNext);

                // Stats label
                lblMixerStats = new Label
                {
                    Text = GetStatsText(),
                    Font = new Font("Segoe UI", 9F, FontStyle.Regular),
                    ForeColor = subText,
                    Location = new Point(22, 385),
                    AutoSize = true
                };
                root.Controls.Add(lblMixerStats);

                // UI Progress Timer
                if (uiUpdateTimer != null)
                {
                    uiUpdateTimer.Stop();
                    uiUpdateTimer.Dispose();
                }
                uiUpdateTimer = new System.Windows.Forms.Timer { Interval = 1500 };
                uiUpdateTimer.Tick += (s, e) => OnUiTimerTick();
                uiUpdateTimer.Start();

                // Initial UI population
                string currentPlaying = NormalizeUrl(mbApiInterface.NowPlaying_GetFileUrl());
                if (!string.IsNullOrEmpty(currentPlaying))
                {
                    UpdateMixerUI(currentPlaying, null, 0, false, 1);
                }
                else
                {
                    lblNowPlayingTitle.Text = "Nothing currently playing";
                    lblNowPlayingDetails.Text = "Play any track from your library or click 'Play Random Song' above to begin.";
                    lblUpNextTitle.Text = "None queued";
                    lblUpNextDetails.Text = "";
                }
            }
            catch (Exception ex)
            {
                WriteLog("InitMixerTabUI error: " + ex.Message);
            }
        }

        private void OnUiTimerTick()
        {
            if (isScanning)
            {
                try
                {
                    if (File.Exists(ProgressPath))
                    {
                        string line = File.ReadAllText(ProgressPath).Trim();
                        if (!string.IsNullOrEmpty(line))
                        {
                            if (lblStatus != null && !lblStatus.IsDisposed)
                            {
                                lblStatus.Text = "Status: Scanning " + line;
                            }
                            if (lblMixerStats != null && !lblMixerStats.IsDisposed)
                            {
                                lblMixerStats.Text = "Scanning library: " + line;
                            }
                        }
                    }
                }
                catch
                {
                }
            }
        }

        private Panel CreateCardPanel(Point location, Size size, Color bgColor, Color borderColor, string headerText, Color textColor, Color subTextColor, out Label titleLabel, out Label detailsLabel)
        {
            Panel card = new Panel
            {
                Location = location,
                Size = size,
                BackColor = bgColor,
                BorderStyle = BorderStyle.FixedSingle,
                Padding = new Padding(12)
            };

            Label lblCardHeader = new Label
            {
                Text = headerText,
                Font = new Font("Segoe UI", 7.5F, FontStyle.Bold),
                ForeColor = subTextColor,
                Location = new Point(12, 10),
                AutoSize = true
            };
            card.Controls.Add(lblCardHeader);

            titleLabel = new Label
            {
                Text = "--",
                Font = new Font("Segoe UI", 11F, FontStyle.Bold),
                ForeColor = textColor,
                Location = new Point(12, 30),
                Size = new Size(size.Width - 24, 26),
                AutoEllipsis = true
            };
            card.Controls.Add(titleLabel);

            detailsLabel = new Label
            {
                Text = "--",
                Font = new Font("Segoe UI", 9F, FontStyle.Regular),
                ForeColor = subTextColor,
                Location = new Point(12, 60),
                Size = new Size(size.Width - 24, 22),
                AutoEllipsis = true
            };
            card.Controls.Add(detailsLabel);

            return card;
        }

        private string GetStatsText()
        {
            int cachedCount;
            lock (libraryLock) { cachedCount = library.Count; }

            int histCount;
            lock (historyLock) { histCount = recentHistory.Count; }

            string coverageText = (totalLibraryFileCount > 0)
                ? string.Format(CultureInfo.InvariantCulture, "{0} / {1} ({2:F1}%)", cachedCount, totalLibraryFileCount, (cachedCount * 100.0) / totalLibraryFileCount)
                : cachedCount.ToString(CultureInfo.InvariantCulture);

            string arcDesc = vibeLockEnabled
                ? "Locked Vibe"
                : ((sessionPocketTrackCount >= 3) ? "Bridge Transition" : string.Format(CultureInfo.InvariantCulture, "Groove Pocket ({0} of 3)", (sessionPocketTrackCount % 3) + 1));

            return string.Format(CultureInfo.InvariantCulture,
                "Library: {0} indexed   |   History: {1} tracks avoided   |   DJ Flow: {2}   |   Auto-Mix: {3}",
                coverageText, histCount, arcDesc, autoMixEnabled ? "Active" : "Paused");
        }

        private void UpdateStatsLabel()
        {
            if (lblMixerStats != null && !lblMixerStats.IsDisposed)
            {
                lblMixerStats.Text = GetStatsText();
            }
        }

        private static string FormatVibeProfile(TrackFeatures feat)
        {
            string energyDesc = feat.EnergyNorm >= 0.70 ? "High Energy" : (feat.EnergyNorm >= 0.40 ? "Mid Energy" : "Chill/Low Energy");
            string moodDesc = feat.ValenceNorm >= 0.60 ? "Uplifting" : (feat.ValenceNorm >= 0.40 ? "Balanced" : "Moody");
            string grooveDesc = feat.DanceNorm >= 0.65 ? "Groovy" : (feat.DanceNorm >= 0.35 ? "Steady" : "Atmospheric");
            string styleDesc = feat.AcousticNorm >= 0.55 ? "Organic" : "Electronic";

            return string.Format(CultureInfo.InvariantCulture,
                "Tempo: {0:F1} BPM  |  Key: {1}  |  Vibe: {2} • {3} • {4} • {5}",
                feat.Bpm, feat.CamelotKey, energyDesc, moodDesc, grooveDesc, styleDesc);
        }

        private static string FormatMatchDetails(TrackFeatures nextFeat, double distance, bool isBridge, int pocketIndex)
        {
            int matchPercent = Math.Max(10, Math.Min(99, (int)Math.Round((1.0 - Math.Min(distance / 4.0, 0.9)) * 100)));
            string arcDesc = isBridge ? "Bridge Transition" : string.Format(CultureInfo.InvariantCulture, "Groove Lock ({0} of 3)", pocketIndex);
            string quality = distance < 0.8 ? "Seamless Blend" : (distance < 1.8 ? "Close Match" : "Good Flow");

            return string.Format(CultureInfo.InvariantCulture,
                "Tempo: {0:F1} BPM  |  Key: {1}  |  Vibe Match: {2}% ({3})  |  DJ Arc: {4}",
                nextFeat.Bpm, nextFeat.CamelotKey, matchPercent, quality, arcDesc);
        }

        private void UpdateMixerUI(string currentUrl, string upNextUrl, double distance, bool isBridge, int pocketIndex)
        {
            if (mixerMainPanel == null || mixerMainPanel.IsDisposed) return;
            if (mixerMainPanel.InvokeRequired)
            {
                mixerMainPanel.BeginInvoke(new Action(() => UpdateMixerUI(currentUrl, upNextUrl, distance, isBridge, pocketIndex)));
                return;
            }

            try
            {
                // Update Currently Playing
                if (!string.IsNullOrEmpty(currentUrl))
                {
                    string title = mbApiInterface.Library_GetFileTag(currentUrl, MetaDataType.TrackTitle);
                    string artist = mbApiInterface.Library_GetFileTag(currentUrl, MetaDataType.Artist);
                    string displayTitle = !string.IsNullOrEmpty(title)
                        ? (!string.IsNullOrEmpty(artist) ? artist + " - " + title : title)
                        : Path.GetFileNameWithoutExtension(currentUrl);

                    if (lblNowPlayingTitle != null)
                    {
                        lblNowPlayingTitle.Text = displayTitle;
                    }

                    if (lblNowPlayingDetails != null)
                    {
                        TrackFeatures feat;
                        bool found;
                        lock (libraryLock)
                        {
                            found = library.TryGetValue(currentUrl, out feat);
                        }

                        if (found)
                        {
                            lblNowPlayingDetails.Text = FormatVibeProfile(feat);
                        }
                        else
                        {
                            lblNowPlayingDetails.Text = "Not yet analyzed in Mixer cache (Fallback matching applied)";
                        }
                    }
                }

                // Update Up Next
                if (!string.IsNullOrEmpty(upNextUrl))
                {
                    string nextTitle = mbApiInterface.Library_GetFileTag(upNextUrl, MetaDataType.TrackTitle);
                    string nextArtist = mbApiInterface.Library_GetFileTag(upNextUrl, MetaDataType.Artist);
                    string displayNext = !string.IsNullOrEmpty(nextTitle)
                        ? (!string.IsNullOrEmpty(nextArtist) ? nextArtist + " - " + nextTitle : nextTitle)
                        : Path.GetFileNameWithoutExtension(upNextUrl);

                    if (lblUpNextTitle != null)
                    {
                        lblUpNextTitle.Text = displayNext;
                    }

                    if (lblUpNextDetails != null)
                    {
                        TrackFeatures nextFeat;
                        bool found;
                        lock (libraryLock)
                        {
                            found = library.TryGetValue(upNextUrl, out nextFeat);
                        }

                        if (found)
                        {
                            lblUpNextDetails.Text = FormatMatchDetails(nextFeat, distance, isBridge, pocketIndex);
                        }
                        else
                        {
                            lblUpNextDetails.Text = "";
                        }
                    }
                }
                else if (lblUpNextTitle != null && lblUpNextTitle.Text == "--")
                {
                    lblUpNextTitle.Text = "None queued";
                    lblUpNextDetails.Text = "";
                }

                UpdateStatsLabel();
            }
            catch (Exception ex)
            {
                WriteLog("UpdateMixerUI error: " + ex.Message);
            }
        }

        // ---- DJ Mixing Operations ----

        private void PlayRandomSongAndMix()
        {
            Dictionary<string, TrackFeatures> libSnapshot;
            lock (libraryLock)
            {
                libSnapshot = new Dictionary<string, TrackFeatures>(library, StringComparer.OrdinalIgnoreCase);
            }

            if (libSnapshot.Count == 0)
            {
                MessageBox.Show("Mixer cache is empty. Please run a library scan first.", "Mixer", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // Energy Ramp: Start with a balanced mid-energy track (0.35 to 0.70)
            List<string> candidateKeys = new List<string>();
            foreach (var kvp in libSnapshot)
            {
                if (kvp.Value.EnergyNorm >= 0.35 && kvp.Value.EnergyNorm <= 0.70)
                {
                    candidateKeys.Add(kvp.Key);
                }
            }

            if (candidateKeys.Count == 0)
            {
                candidateKeys.AddRange(libSnapshot.Keys);
            }

            // Pick a track that exists on disk
            string randomTrack = null;
            while (candidateKeys.Count > 0)
            {
                int idx = rng.Next(candidateKeys.Count);
                string candidate = candidateKeys[idx];
                if (File.Exists(candidate))
                {
                    randomTrack = candidate;
                    break;
                }
                else
                {
                    candidateKeys.RemoveAt(idx);
                    lock (libraryLock) { library.Remove(candidate); }
                }
            }

            if (randomTrack == null)
            {
                MessageBox.Show("None of the cached tracks were found on disk. Please rescan your library.", "Mixer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                mbApiInterface.NowPlayingList_Clear();
            }
            catch (Exception ex)
            {
                WriteLog("NowPlayingList_Clear failed: " + ex.Message);
            }

            try
            {
                mbApiInterface.NowPlayingList_PlayNow(randomTrack);
            }
            catch (Exception ex)
            {
                WriteLog("NowPlayingList_PlayNow failed: " + ex.Message);
            }

            sessionPocketTrackCount = 0;
            RememberPlayed(randomTrack);
            QueueNextIfNeeded(randomTrack, force: true);
        }

        private void QueueAnotherMatch()
        {
            int cachedCount;
            lock (libraryLock) { cachedCount = library.Count; }

            if (cachedCount == 0)
            {
                MessageBox.Show("Mixer cache is empty. Please run a library scan first.", "Mixer", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string seedUrl = null;

            try
            {
                string[] files;
                if (mbApiInterface.NowPlayingList_QueryFilesEx("", out files) && files != null && files.Length > 0)
                {
                    seedUrl = NormalizeUrl(files[files.Length - 1]);
                }
            }
            catch (Exception ex)
            {
                WriteLog("NowPlayingList_QueryFilesEx error: " + ex.Message);
            }

            if (string.IsNullOrEmpty(seedUrl))
            {
                int currentIndex = mbApiInterface.NowPlayingList_GetCurrentIndex();
                seedUrl = currentIndex >= 0 ? NormalizeUrl(mbApiInterface.NowPlayingList_GetListFileUrl(currentIndex)) : null;
                if (string.IsNullOrEmpty(seedUrl))
                {
                    seedUrl = NormalizeUrl(mbApiInterface.NowPlaying_GetFileUrl());
                }
            }

            if (string.IsNullOrEmpty(seedUrl))
            {
                PlayRandomSongAndMix();
                return;
            }

            PickResult pick = PickNext(seedUrl);
            if (pick.Url != null)
            {
                mbApiInterface.NowPlayingList_QueueLast(pick.Url);
                lastQueuedTrackUrl = pick.Url;
                RememberPlayed(pick.Url);
                WriteLog(string.Format(CultureInfo.InvariantCulture, "Manually queued next match: {0} (after {1}, distance={2:F2})", pick.Url, seedUrl, pick.Distance));
                UpdateMixerUI(seedUrl, pick.Url, pick.Distance, pick.IsBridge, pick.PocketIndex);
            }
            else
            {
                MessageBox.Show("Could not find another match in cache for: " + Path.GetFileName(seedUrl), "Mixer", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        // ---- Background Scanning & Auto-Provisioning ----

        private string FindScannerExecutable()
        {
            // 1. Check Mixer persistent folder
            string primary = Path.Combine(MixerFolder, "mixer_scanner.exe");
            if (File.Exists(primary)) return primary;

            // 2. Check MusicBee Plugins folder
            try
            {
                string asmDir = Path.GetDirectoryName(typeof(Plugin).Assembly.Location);
                if (!string.IsNullOrEmpty(asmDir))
                {
                    string candidate = Path.Combine(asmDir, "mixer_scanner.exe");
                    if (File.Exists(candidate)) return candidate;

                    candidate = Path.Combine(asmDir, "Mixer", "mixer_scanner.exe");
                    if (File.Exists(candidate)) return candidate;
                }
            }
            catch {}

            // 3. Check development / workspace folder
            string localDev = @"E:\projects\musicbee plugin\dist\mixer_scanner.exe";
            if (File.Exists(localDev)) return localDev;

            return null;
        }

        private void DownloadScannerAndScan(bool silent)
        {
            isScanning = true;
            string targetPath = Path.Combine(MixerFolder, "mixer_scanner.exe");
            string tempPath = targetPath + ".download";

            if (lblStatus != null && !lblStatus.IsDisposed)
            {
                lblStatus.BeginInvoke(new Action(() => lblStatus.Text = "Status: Downloading audio scanner (0%)..."));
            }
            if (lblMixerStats != null && !lblMixerStats.IsDisposed)
            {
                lblMixerStats.BeginInvoke(new Action(() => lblMixerStats.Text = "Downloading audio analysis scanner from GitHub (0%)..."));
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    Directory.CreateDirectory(MixerFolder);
                    System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;

                    using (System.Net.WebClient wc = new System.Net.WebClient())
                    {
                        wc.Headers.Add("User-Agent", "Mixer-MusicBee-Plugin");
                        wc.DownloadProgressChanged += (s, e) =>
                        {
                            int pct = e.ProgressPercentage;
                            if (lblStatus != null && !lblStatus.IsDisposed)
                            {
                                lblStatus.BeginInvoke(new Action(() => lblStatus.Text = string.Format(CultureInfo.InvariantCulture, "Status: Downloading audio scanner ({0}%)...", pct)));
                            }
                            if (lblMixerStats != null && !lblMixerStats.IsDisposed)
                            {
                                lblMixerStats.BeginInvoke(new Action(() => lblMixerStats.Text = string.Format(CultureInfo.InvariantCulture, "Downloading standalone audio scanner from GitHub: {0}% ({1:F1} MB / {2:F1} MB)", pct, e.BytesReceived / 1048576.0, e.TotalBytesToReceive / 1048576.0)));
                            }
                        };

                        string downloadUrl = "https://github.com/Buch007/musicbee-mixer/releases/download/v1.0.1/mixer_scanner.exe";
                        wc.DownloadFile(new Uri(downloadUrl), tempPath);
                    }

                    if (File.Exists(targetPath))
                    {
                        try { File.Delete(targetPath); } catch {}
                    }
                    File.Move(tempPath, targetPath);

                    WriteLog("Standalone scanner downloaded successfully to: " + targetPath);
                    isScanning = false;

                    StartBackgroundScan(silent);
                }
                catch (Exception ex)
                {
                    isScanning = false;
                    WriteLog("Failed to download scanner: " + ex.Message);
                    if (lblStatus != null && !lblStatus.IsDisposed)
                    {
                        lblStatus.BeginInvoke(new Action(() => lblStatus.Text = "Status: Scanner download failed."));
                    }
                    if (!silent)
                    {
                        MessageBox.Show("Failed to download the audio scanner:\n" + ex.Message + "\n\nYou can manually download mixer_scanner.exe from GitHub Releases and place it in your MusicBee\\Plugins folder.", "Mixer", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            });
        }

        private static string GetPythonExecutable()
        {
            string[] candidates = new string[] { "py", "python", "python3" };
            foreach (var cmd in candidates)
            {
                try
                {
                    ProcessStartInfo testPsi = new ProcessStartInfo
                    {
                        FileName = cmd,
                        Arguments = "--version",
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        WindowStyle = ProcessWindowStyle.Hidden
                    };
                    using (Process p = Process.Start(testPsi))
                    {
                        if (p != null)
                        {
                            p.WaitForExit(2000);
                            if (p.ExitCode == 0) return cmd;
                        }
                    }
                }
                catch {}
            }
            return null;
        }

        private string FindAnalyzeScript()
        {
            string primary = Path.Combine(MixerFolder, "analyze_library.py");
            if (File.Exists(primary)) return primary;

            try
            {
                string asmDir = Path.GetDirectoryName(typeof(Plugin).Assembly.Location);
                if (!string.IsNullOrEmpty(asmDir))
                {
                    string candidate = Path.Combine(asmDir, "analyze_library.py");
                    if (File.Exists(candidate))
                    {
                        Directory.CreateDirectory(MixerFolder);
                        File.Copy(candidate, primary, true);
                        return primary;
                    }
                }
            }
            catch
            {
            }

            string localDev = @"E:\projects\musicbee plugin\analyze_library.py";
            if (File.Exists(localDev))
            {
                try
                {
                    Directory.CreateDirectory(MixerFolder);
                    File.Copy(localDev, primary, true);
                }
                catch {}
                return localDev;
            }

            return primary;
        }

        private void StartBackgroundScan(bool silent)
        {
            if (isScanning)
            {
                if (!silent)
                {
                    MessageBox.Show("A library scan is already running in the background.", "Mixer", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                return;
            }

            string scannerExe = FindScannerExecutable();
            string pythonExe = (scannerExe == null) ? GetPythonExecutable() : null;

            if (scannerExe == null && string.IsNullOrEmpty(pythonExe))
            {
                if (!silent)
                {
                    DialogResult dr = MessageBox.Show(
                        "Mixer requires the audio analysis scanner to profile your library.\n\nPython was not detected on your computer. Would you like Mixer to automatically download the standalone scanner component now (~114 MB)?",
                        "Mixer Setup",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question
                    );
                    if (dr == DialogResult.Yes)
                    {
                        DownloadScannerAndScan(silent);
                        return;
                    }
                }

                string msg = "Python (3.9+) or mixer_scanner.exe was not found. Please install Python or download mixer_scanner.exe from GitHub Releases.";
                WriteLog(msg);
                if (!silent)
                {
                    MessageBox.Show(msg, "Mixer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                return;
            }

            isScanning = true;
            if (lblStatus != null && !lblStatus.IsDisposed)
            {
                lblStatus.Text = "Status: Scanning in background...";
            }
            if (lblMixerStats != null && !lblMixerStats.IsDisposed)
            {
                lblMixerStats.BeginInvoke(new Action(() =>
                {
                    lblMixerStats.Text = "Scanning library with Vibe Audio analysis in background...";
                }));
            }

            string[] files = null;
            try
            {
                mbApiInterface.Library_QueryFilesEx("", out files);
                if (files != null) totalLibraryFileCount = files.Length;
            }
            catch (Exception ex)
            {
                WriteLog("Library_QueryFilesEx error: " + ex.Message);
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    string fileListArg = "";
                    if (files != null && files.Length > 0)
                    {
                        string listFile = Path.Combine(MixerFolder, "library_files.txt");
                        File.WriteAllLines(listFile, files);
                        fileListArg = string.Format(CultureInfo.InvariantCulture, "--file-list \"{0}\"", listFile);
                    }
                    else
                    {
                        string myMusic = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
                        if (!string.IsNullOrEmpty(myMusic) && Directory.Exists(myMusic))
                        {
                            fileListArg = string.Format(CultureInfo.InvariantCulture, "\"{0}\"", myMusic);
                        }
                    }

                    if (string.IsNullOrEmpty(fileListArg))
                    {
                        WriteLog("No audio files found to scan.");
                        if (!silent)
                        {
                            MessageBox.Show("No music files found in your MusicBee library to scan.", "Mixer", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                        isScanning = false;
                        return;
                    }

                    string dbPath = Path.Combine(MixerFolder, "mixer_cache.db");
                    string exeToRun;
                    string args;

                    if (!string.IsNullOrEmpty(scannerExe))
                    {
                        exeToRun = scannerExe;
                        args = string.Format(CultureInfo.InvariantCulture, "{0} --db \"{1}\" --quick --csv \"{2}\" --progress-file \"{3}\"",
                            fileListArg, dbPath, CachePath, ProgressPath);
                    }
                    else
                    {
                        string scriptPath = FindAnalyzeScript();
                        exeToRun = pythonExe;
                        args = string.Format(CultureInfo.InvariantCulture, "\"{0}\" {1} --db \"{2}\" --quick --csv \"{3}\" --progress-file \"{4}\"",
                            scriptPath, fileListArg, dbPath, CachePath, ProgressPath);
                    }

                    WriteLog("Background scan starting with executable: " + exeToRun + " and args: " + args);

                    ProcessStartInfo psi = new ProcessStartInfo
                    {
                        FileName = exeToRun,
                        Arguments = args,
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        WindowStyle = ProcessWindowStyle.Hidden
                    };

                    using (Process proc = Process.Start(psi))
                    {
                        proc.WaitForExit();
                    }

                    LoadCache();

                    int newCount;
                    lock (libraryLock) { newCount = library.Count; }
                    WriteLog("Background scan completed successfully. Tracks loaded: " + newCount);

                    if (lblStatus != null && !lblStatus.IsDisposed)
                    {
                        lblStatus.BeginInvoke(new Action(() =>
                        {
                            lblStatus.Text = string.Format(CultureInfo.InvariantCulture, "Status: Scan complete ({0} tracks in cache).", newCount);
                        }));
                    }
                    if (lblMixerStats != null && !lblMixerStats.IsDisposed)
                    {
                        lblMixerStats.BeginInvoke(new Action(() =>
                        {
                            UpdateStatsLabel();
                        }));
                    }
                }
                catch (Exception ex)
                {
                    WriteLog("Background scan error: " + ex.Message);
                    if (lblStatus != null && !lblStatus.IsDisposed)
                    {
                        lblStatus.BeginInvoke(new Action(() =>
                        {
                            lblStatus.Text = "Status: Scan error (see mixer_debug.log).";
                        }));
                    }
                }
                finally
                {
                    isScanning = false;
                }
            });
        }

        // ---- Cache Loading (Thread-Safe Snapshot Swap with Single-Pass File Check) ----

        private void LoadCache()
        {
            string path = CachePath;
            if (!File.Exists(path))
            {
                WriteLog("No cache file found at " + path + " - plugin will not queue anything yet.");
                return;
            }

            var newLibrary = new Dictionary<string, TrackFeatures>(StringComparer.OrdinalIgnoreCase);

            try
            {
                string[] lines = File.ReadAllLines(path);
                for (int i = 1; i < lines.Length; i++)
                {
                    string[] fields = ParseCsvLine(lines[i]);
                    if (fields.Length < 5) continue;

                    string trackPath = NormalizeUrl(fields[0]);
                    // Verify file exists on disk once at cache load time, avoiding per-track I/O storms later
                    if (!File.Exists(trackPath)) continue;

                    double bpm, energy;
                    if (!double.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out bpm)) continue;
                    if (!double.TryParse(fields[4], NumberStyles.Float, CultureInfo.InvariantCulture, out energy)) continue;

                    double dance = 0.5, valence = 0.5, acoustic = 0.5;
                    if (fields.Length >= 8)
                    {
                        double.TryParse(fields[5], NumberStyles.Float, CultureInfo.InvariantCulture, out dance);
                        double.TryParse(fields[6], NumberStyles.Float, CultureInfo.InvariantCulture, out valence);
                        double.TryParse(fields[7], NumberStyles.Float, CultureInfo.InvariantCulture, out acoustic);
                    }

                    newLibrary[trackPath] = new TrackFeatures
                    {
                        Bpm = bpm,
                        CamelotKey = fields[3],
                        EnergyNorm = energy,
                        DanceNorm = dance,
                        ValenceNorm = valence,
                        AcousticNorm = acoustic
                    };
                }

                lock (libraryLock)
                {
                    library = newLibrary;
                }
            }
            catch (Exception ex)
            {
                WriteLog("LoadCache error: " + ex.Message);
            }
        }

        private static string[] ParseCsvLine(string line)
        {
            var fields = new List<string>();
            int i = 0;
            while (i < line.Length)
            {
                if (line[i] == '"')
                {
                    i++;
                    var sb = new System.Text.StringBuilder();
                    while (i < line.Length)
                    {
                        if (line[i] == '"')
                        {
                            if (i + 1 < line.Length && line[i + 1] == '"')
                            {
                                sb.Append('"');
                                i += 2;
                            }
                            else
                            {
                                i++;
                                break;
                            }
                        }
                        else
                        {
                            sb.Append(line[i]);
                            i++;
                        }
                    }
                    fields.Add(sb.ToString());
                    if (i < line.Length && line[i] == ',') i++;
                }
                else
                {
                    int start = i;
                    while (i < line.Length && line[i] != ',') i++;
                    fields.Add(line.Substring(start, i - start));
                    if (i < line.Length && line[i] == ',') i++;
                }
            }
            return fields.ToArray();
        }

        private static string NormalizeUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;
            if (url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                Uri uri;
                if (Uri.TryCreate(url, UriKind.Absolute, out uri) && uri.IsFile)
                {
                    return uri.LocalPath.Trim();
                }
                if (url.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
                {
                    url = Uri.UnescapeDataString(url.Substring(8));
                }
            }
            return url.Replace('/', '\\').Trim();
        }

        // ---- Queueing & Deduplication Logic ----

        private void RememberPlayed(string url)
        {
            if (string.IsNullOrEmpty(url)) return;

            string filenameKey = Path.GetFileNameWithoutExtension(url).Trim().ToLowerInvariant();
            string titleArtistKey = null;

            try
            {
                string title = mbApiInterface.Library_GetFileTag(url, MetaDataType.TrackTitle);
                string artist = mbApiInterface.Library_GetFileTag(url, MetaDataType.Artist);
                if (!string.IsNullOrEmpty(title))
                {
                    titleArtistKey = (artist ?? "").Trim().ToLowerInvariant() + "|" + title.Trim().ToLowerInvariant();
                }
            }
            catch {}

            lock (historyLock)
            {
                LinkedListNode<HistoryEntry> node = recentHistory.First;
                while (node != null)
                {
                    if (string.Equals(node.Value.Url, url, StringComparison.OrdinalIgnoreCase))
                    {
                        DecrementFingerprint(node.Value.FilenameKey);
                        DecrementFingerprint(node.Value.TitleArtistKey);
                        recentHistory.Remove(node);
                        break;
                    }
                    node = node.Next;
                }

                recentHistorySet.Add(url);
                IncrementFingerprint(filenameKey);
                IncrementFingerprint(titleArtistKey);

                recentHistory.AddFirst(new HistoryEntry
                {
                    Url = url,
                    FilenameKey = filenameKey,
                    TitleArtistKey = titleArtistKey
                });

                while (recentHistory.Count > HistoryLimit)
                {
                    HistoryEntry removed = recentHistory.Last.Value;
                    recentHistory.RemoveLast();
                    recentHistorySet.Remove(removed.Url);
                    DecrementFingerprint(removed.FilenameKey);
                    DecrementFingerprint(removed.TitleArtistKey);
                }
            }
        }

        private bool IsTrackRecentlyPlayed(string candidateUrl)
        {
            if (string.IsNullOrEmpty(candidateUrl)) return true;

            lock (historyLock)
            {
                if (recentHistorySet.Contains(candidateUrl)) return true;

                string filenameKey = Path.GetFileNameWithoutExtension(candidateUrl).Trim().ToLowerInvariant();
                if (recentFingerprintCounts.ContainsKey(filenameKey)) return true;

                try
                {
                    string title = mbApiInterface.Library_GetFileTag(candidateUrl, MetaDataType.TrackTitle);
                    string artist = mbApiInterface.Library_GetFileTag(candidateUrl, MetaDataType.Artist);
                    if (!string.IsNullOrEmpty(title))
                    {
                        string key = (artist ?? "").Trim().ToLowerInvariant() + "|" + title.Trim().ToLowerInvariant();
                        if (recentFingerprintCounts.ContainsKey(key)) return true;
                    }
                }
                catch {}

                return false;
            }
        }

        private void QueueNextIfNeeded(string currentUrlOverride = null, bool force = false)
        {
            int cachedCount;
            lock (libraryLock) { cachedCount = library.Count; }
            if (cachedCount == 0) return;

            string currentUrl = currentUrlOverride;
            if (string.IsNullOrEmpty(currentUrl))
            {
                int currentIndex = mbApiInterface.NowPlayingList_GetCurrentIndex();
                currentUrl = currentIndex >= 0 ? NormalizeUrl(mbApiInterface.NowPlayingList_GetListFileUrl(currentIndex)) : null;
                if (string.IsNullOrEmpty(currentUrl))
                {
                    currentUrl = NormalizeUrl(mbApiInterface.NowPlaying_GetFileUrl());
                }
            }

            if (string.IsNullOrEmpty(currentUrl)) return;

            // Prevent duplicate queueing for the same track
            if (!force && string.Equals(lastQueuedForTrack, currentUrl, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // Queue Stacking Guard: Check if the following track is already our queued track
            if (!force && mbApiInterface.NowPlayingList_IsAnyFollowingTracks())
            {
                int curIdx = mbApiInterface.NowPlayingList_GetCurrentIndex();
                if (curIdx >= 0)
                {
                    string nextUrl = NormalizeUrl(mbApiInterface.NowPlayingList_GetListFileUrl(curIdx + 1));
                    if (!string.IsNullOrEmpty(nextUrl) && string.Equals(nextUrl, lastQueuedTrackUrl, StringComparison.OrdinalIgnoreCase))
                    {
                        return; // Already queued next
                    }
                }
            }

            PickResult pick = PickNext(currentUrl);
            if (pick.Url != null)
            {
                mbApiInterface.NowPlayingList_QueueNext(pick.Url);
                lastQueuedForTrack = currentUrl;
                lastQueuedTrackUrl = pick.Url;
                WriteLog(string.Format(CultureInfo.InvariantCulture, "Queued Vibe Match: {0} (after {1}, distance={2:F2}, bridge={3})", pick.Url, currentUrl, pick.Distance, pick.IsBridge));
                UpdateMixerUI(currentUrl, pick.Url, pick.Distance, pick.IsBridge, pick.PocketIndex);
            }
            else
            {
                WriteLog("No pick for current track: " + currentUrl + " (not in cache, or library exhausted)");
                UpdateMixerUI(currentUrl, null, 0, false, 1);
            }
        }

        private PickResult PickNext(string currentUrl)
        {
            Dictionary<string, TrackFeatures> libSnapshot;
            lock (libraryLock)
            {
                libSnapshot = new Dictionary<string, TrackFeatures>(library, StringComparer.OrdinalIgnoreCase);
            }

            TrackFeatures seed;
            bool seedFound = libSnapshot.TryGetValue(currentUrl, out seed);

            // Fallback for tracks not yet in cache: synthesize features from tags
            if (!seedFound)
            {
                double tagBpm = 120.0;
                try
                {
                    string bpmStr = mbApiInterface.Library_GetFileTag(currentUrl, MetaDataType.Tempo);
                    double parsedBpm;
                    if (!string.IsNullOrEmpty(bpmStr) && double.TryParse(bpmStr, NumberStyles.Float, CultureInfo.InvariantCulture, out parsedBpm) && parsedBpm > 40)
                    {
                        tagBpm = parsedBpm;
                    }
                }
                catch {}

                seed = new TrackFeatures
                {
                    Bpm = tagBpm,
                    CamelotKey = "8A",
                    EnergyNorm = 0.50,
                    DanceNorm = 0.50,
                    ValenceNorm = 0.50,
                    AcousticNorm = 0.50
                };
            }

            HashSet<string> histSetSnapshot;
            Dictionary<string, int> fpSnapshot;
            lock (historyLock)
            {
                histSetSnapshot = new HashSet<string>(recentHistorySet, StringComparer.OrdinalIgnoreCase);
                fpSnapshot = new Dictionary<string, int>(recentFingerprintCounts, StringComparer.OrdinalIgnoreCase);
            }

            var candidates = new List<Candidate>();
            bool isBridge = !vibeLockEnabled && (sessionPocketTrackCount >= 3);

            // Relative BPM tolerance (% based)
            double maxBpmGap = vibeLockEnabled ? Math.Max(seed.Bpm * 0.05, 5.0)
                : (isBridge ? Math.Max(seed.Bpm * 0.12, 12.0) : Math.Max(seed.Bpm * 0.06, 6.0));

            string currentCleanName = Path.GetFileNameWithoutExtension(currentUrl).Trim().ToLowerInvariant();

            foreach (var pair in libSnapshot)
            {
                if (string.Equals(pair.Key, currentUrl, StringComparison.OrdinalIgnoreCase)) continue;
                if (histSetSnapshot.Contains(pair.Key)) continue;

                // Eliminate identical audio files with duplicate paths or names
                string candidateCleanName = Path.GetFileNameWithoutExtension(pair.Key).Trim().ToLowerInvariant();
                if (string.Equals(candidateCleanName, currentCleanName, StringComparison.OrdinalIgnoreCase)) continue;
                if (fpSnapshot.ContainsKey(candidateCleanName)) continue;

                double gap = BpmGap(seed.Bpm, pair.Value.Bpm);
                double rawDistance = Distance(seed, pair.Value, isBridge);

                // Distance threshold: skip near-identical / duplicate tracks
                if (rawDistance < 0.05) continue;

                candidates.Add(new Candidate
                {
                    Url = pair.Key,
                    RawDistance = rawDistance,
                    AdjustedDistance = rawDistance,
                    BpmGap = gap
                });
            }

            if (candidates.Count == 0) return default(PickResult);

            // Filter by tempo pocket limit
            List<Candidate> pool = candidates.FindAll(c => c.BpmGap <= maxBpmGap);
            if (pool.Count == 0)
            {
                pool = candidates.FindAll(c => c.BpmGap <= Math.Max(seed.Bpm * 0.15, 15.0));
                if (pool.Count == 0) pool = candidates;
            }

            // Step 1: Pre-sort pool by raw audio distance (Fast in-memory sort)
            pool.Sort((x, y) => x.RawDistance.CompareTo(y.RawDistance));

            // Step 2: Evaluate User Affinity ONLY for the Top 15 Candidates (Drops API calls from 10,000+ to 15)
            int evalCount = Math.Min(15, pool.Count);
            List<Candidate> topPool = pool.GetRange(0, evalCount);

            for (int i = 0; i < topPool.Count; i++)
            {
                Candidate c = topPool[i];
                double affinityBonus = 0.0;

                try
                {
                    string ratingStr = mbApiInterface.Library_GetFileTag(c.Url, MetaDataType.Rating);
                    int rating;
                    if (int.TryParse(ratingStr, out rating) && rating > 0)
                    {
                        if (rating >= 80) affinityBonus += 0.30;
                        else if (rating >= 60) affinityBonus += 0.15;
                    }
                }
                catch {}

                try
                {
                    string playCountStr = mbApiInterface.Library_GetFileProperty(c.Url, FilePropertyType.PlayCount);
                    int playCount;
                    if (int.TryParse(playCountStr, out playCount) && playCount > 4)
                    {
                        affinityBonus += Math.Min(0.20, playCount * 0.015);
                    }
                }
                catch {}

                try
                {
                    string skipCountStr = mbApiInterface.Library_GetFileProperty(c.Url, FilePropertyType.SkipCount);
                    int skipCount;
                    if (int.TryParse(skipCountStr, out skipCount) && skipCount > 2)
                    {
                        affinityBonus -= Math.Min(0.60, skipCount * 0.15);
                    }
                }
                catch {}

                // Multiplier adjustment: subtle nudge (up to 25% discount or penalty), preserving audio match precedence
                double affinityMultiplier = 1.0 - Math.Min(0.25, Math.Max(-0.40, affinityBonus));
                c.AdjustedDistance = Math.Max(0.05, c.RawDistance * affinityMultiplier);
                topPool[i] = c;
            }

            // Step 3: Sort top candidates by adjusted distance
            topPool.Sort((x, y) => x.AdjustedDistance.CompareTo(y.AdjustedDistance));

            // Pick at random from the 3 closest candidates, verifying file exists on disk
            int topCount = Math.Min(3, topPool.Count);
            int selectedIndex = rng.Next(topCount);

            for (int attempt = 0; attempt < topPool.Count; attempt++)
            {
                int candidateIdx = (selectedIndex + attempt) % topPool.Count;
                string targetUrl = topPool[candidateIdx].Url;
                if (File.Exists(targetUrl))
                {
                    int pocketIndex = (sessionPocketTrackCount % 3) + 1;
                    if (!vibeLockEnabled)
                    {
                        sessionPocketTrackCount = (sessionPocketTrackCount + 1) % 4;
                    }

                    return new PickResult
                    {
                        Url = targetUrl,
                        Distance = topPool[candidateIdx].AdjustedDistance,
                        IsBridge = isBridge,
                        PocketIndex = pocketIndex
                    };
                }
                else
                {
                    lock (libraryLock)
                    {
                        library.Remove(targetUrl);
                    }
                }
            }

            return default(PickResult);
        }

        private static double BpmGap(double a, double b)
        {
            double direct = Math.Abs(a - b);
            double half = Math.Abs(a - b / 2.0);
            double dbl = Math.Abs(a - b * 2.0);
            return Math.Min(direct, Math.Min(half, dbl));
        }

        // Rebalanced multi-dimensional Vibe distance with dynamic DJ Arc handling
        private static double Distance(TrackFeatures a, TrackFeatures b, bool isBridge)
        {
            // 1. Tempo distance (always strict to preserve dance rhythm)
            double bpmDist = BpmGap(a.Bpm, b.Bpm) / 10.0;

            if (isBridge)
            {
                // Bridge Transition:
                // - Key distance penalty relaxed (weight 0.20 instead of 0.40) to encourage harmonic movement
                // - Energy distance weight reduced from 2.50 to 1.20 to allow mood/intensity progression
                // - Valence & Acousticness relaxed to permit vibe shifts
                double keyDist = CamelotDistance(a.CamelotKey, b.CamelotKey) * 0.20;
                double energyDist = Math.Abs(a.EnergyNorm - b.EnergyNorm) * 1.20;
                double danceDist = Math.Abs(a.DanceNorm - b.DanceNorm) * 1.50;
                double valenceDist = Math.Abs(a.ValenceNorm - b.ValenceNorm) * 1.20;
                double acousticDist = Math.Abs(a.AcousticNorm - b.AcousticNorm) * 1.20;
                return bpmDist + keyDist + energyDist + danceDist + valenceDist + acousticDist;
            }
            else
            {
                // Groove Lock:
                // - Tight harmonic key matching (weight 0.40)
                // - Strict energy consistency (weight 2.50)
                // - Tight groove, valence, and acoustic matching (weight 2.00)
                double keyDist = CamelotDistance(a.CamelotKey, b.CamelotKey) * 0.40;
                double energyDist = Math.Abs(a.EnergyNorm - b.EnergyNorm) * 2.50;
                double danceDist = Math.Abs(a.DanceNorm - b.DanceNorm) * 2.00;
                double valenceDist = Math.Abs(a.ValenceNorm - b.ValenceNorm) * 2.00;
                double acousticDist = Math.Abs(a.AcousticNorm - b.AcousticNorm) * 2.00;
                return bpmDist + keyDist + energyDist + danceDist + valenceDist + acousticDist;
            }
        }

        private static double CamelotDistance(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 4;
            if (a == b) return 0;

            int numA, numB;
            char letterA = a[a.Length - 1];
            char letterB = b[b.Length - 1];
            if (!int.TryParse(a.Substring(0, a.Length - 1), out numA)) return 4;
            if (!int.TryParse(b.Substring(0, b.Length - 1), out numB)) return 4;

            if (numA == numB && letterA != letterB) return 1; // relative major/minor

            int diff = Math.Min(((numA - numB) % 12 + 12) % 12, ((numB - numA) % 12 + 12) % 12);
            if (diff == 1 && letterA == letterB) return 1; // adjacent same mode
            if (diff == 1) return 2;                        // adjacent different mode

            return 2 + diff;
        }

        // ---- Debug Logging with 2MB Self-Capping ----

        private void WriteLog(string message)
        {
            try
            {
                Directory.CreateDirectory(MixerFolder);
                string logPath = Path.Combine(MixerFolder, "mixer_debug.log");
                FileInfo fi = new FileInfo(logPath);
                if (fi.Exists && fi.Length > 2 * 1024 * 1024)
                {
                    string oldLog = Path.Combine(MixerFolder, "mixer_debug.old.log");
                    try { File.Delete(oldLog); } catch {}
                    try { File.Move(logPath, oldLog); } catch {}
                }
                File.AppendAllText(
                    logPath,
                    DateTime.Now.ToString("s", CultureInfo.InvariantCulture) + "  " + message + Environment.NewLine
                );
            }
            catch
            {
            }
        }
    }
}
