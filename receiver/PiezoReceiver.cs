// DARVIS PIEZO RECEIVER
// Listens for the UDP packets broadcast by the ESP32 piezo sketch and shows them live.
// Each packet summarises one window of fast samples (50 ms of 1 kHz sampling):
//   {"seq":42,"ao":3100,"lo":180,"avg":900,"do":1,"n":50,"rssi":-58,"up":123456}
//   ao = peak, lo = minimum, avg = average, do = 1 if DO triggered in the window, n = samples
// Two alarms watch the AO peak, split into waves (rise over a level, then drop back to normal):
//   Silent swallow - more than N waves over its level within T seconds -> 1 s siren
//   Swallow        - one wave that reaches the higher swallow level, then back to normal -> 3 s siren
// The levels, N and T are set in the app and remembered in PiezoReceiver.settings.txt next to the exe.
// Build with build.bat (uses the C# compiler that ships with Windows).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Media;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace Darvis
{
    static class Config
    {
        public const int Port = 4210;            // must match RECEIVER_PORT in the ESP32 sketch
        public const int History = 600;          // windows shown on the graph (30 s at 20 per second)
        public const double HoldSeconds = 1.0;   // big readouts show the highest peak / any trigger over this long
        public const double StaleSeconds = 2.0;  // no packet for this long -> "NO DATA"
        public const int AoMax = 4095;           // ESP32 12-bit ADC
        public const double WaveRearm = 0.9;              // a wave is back to normal under 90% of the level it started at
        public const double SilentSirenSeconds = 1.0;     // siren length for a silent swallow
        public const double SwallowSirenSeconds = 3.0;    // siren length for a swallow
        // Defaults for the alarms; all of them can be changed in the app.
        public const int DefaultWavesMoreThan = 7;        // silent swallow: more than this many waves...
        public const int DefaultSilentThreshold = 750;    // ...over this AO peak...
        public const int DefaultWindowSeconds = 10;       // ...within this many seconds
        public const int DefaultSwallowThreshold = 1500;  // swallow: one wave reaching over this AO peak
    }

    static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(17, 20, 24);
        public static readonly Color Card = Color.FromArgb(26, 31, 38);
        public static readonly Color Text = Color.FromArgb(230, 233, 238);
        public static readonly Color Muted = Color.FromArgb(138, 148, 163);
        public static readonly Color Grid = Color.FromArgb(44, 51, 61);
        public static readonly Color Trace = Color.FromArgb(63, 182, 255);
        public static readonly Color Band = Color.FromArgb(70, 63, 182, 255);
        public static readonly Color DoHigh = Color.FromArgb(255, 159, 67);
        public static readonly Color Live = Color.FromArgb(61, 220, 132);
        public static readonly Color Stale = Color.FromArgb(255, 92, 92);
        public static readonly Color Swallow = Color.FromArgb(190, 130, 255);
        public static readonly Color Accent = Color.FromArgb(47, 111, 235);
    }

    struct Sample
    {
        public readonly DateTime Time;
        public readonly int Peak;
        public readonly int Min;
        public readonly bool Triggered;

        public Sample(DateTime time, int peak, int min, bool triggered)
        {
            Time = time;
            Peak = peak;
            Min = min;
            Triggered = triggered;
        }
    }

    // Builds the siren as an in-memory WAV, so no sound file has to ship with the app.
    static class Siren
    {
        public const int SampleRate = 22050;

        public static byte[] BuildWav(double seconds)
        {
            int n = (int)(SampleRate * seconds);
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write(Encoding.ASCII.GetBytes("RIFF"));
                w.Write(36 + n * 2);
                w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                w.Write(16);                // fmt chunk size
                w.Write((short)1);          // PCM
                w.Write((short)1);          // mono
                w.Write(SampleRate);
                w.Write(SampleRate * 2);    // bytes per second
                w.Write((short)2);          // bytes per sample
                w.Write((short)16);         // bits per sample
                w.Write(Encoding.ASCII.GetBytes("data"));
                w.Write(n * 2);

                double phase = 0;
                for (int i = 0; i < n; i++)
                {
                    double t = (double)i / SampleRate;
                    double sweep = 1 - Math.Abs(2 * (t % 1.0) - 1);  // 0 -> 1 -> 0 once per second
                    phase += 2 * Math.PI * (700 + 800 * sweep) / SampleRate;  // 700 Hz up to 1500 Hz and back
                    double s = (Math.Sin(phase) + 0.3 * Math.Sin(3 * phase)) / 0.92;  // 0.92 = this wave's peak
                    double fade = Math.Min(1.0, Math.Min(t, seconds - t) / 0.02);  // 20 ms fade avoids clicks
                    w.Write((short)(s * fade * 0.8 * short.MaxValue));
                }
                w.Flush();
                return ms.ToArray();
            }
        }
    }

    // Remembers the alarm settings between runs in a small "key=value" text file next to the exe.
    static class Settings
    {
        public const string WavesKey = "silent_swallow_waves_more_than";
        public const string SilentKey = "silent_swallow_threshold";
        public const string WindowKey = "silent_swallow_window_seconds";
        public const string SwallowKey = "swallow_threshold";
        // Names the silent swallow settings were saved under before the swallow alarm existed
        public const string OldWavesKey = "siren_waves_more_than";
        public const string OldSilentKey = "siren_threshold";
        public const string OldWindowKey = "siren_window_seconds";

        public static string FilePath = Path.Combine(
            Path.GetDirectoryName(typeof(Settings).Assembly.Location), "PiezoReceiver.settings.txt");

        // A missing file, missing key or unreadable value gives the fallback; out-of-range values are clamped.
        public static int Load(string key, int fallback, int min, int max)
        {
            try
            {
                foreach (string line in File.ReadAllLines(FilePath))
                {
                    string[] kv = line.Split('=');
                    int v;
                    if (kv.Length == 2 && kv[0].Trim() == key &&
                        int.TryParse(kv[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
                        return Math.Max(min, Math.Min(max, v));
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return fallback;
        }

        public static void Save(int wavesMoreThan, int silentThreshold, int windowSeconds, int swallowThreshold)
        {
            string text =
                WavesKey + "=" + wavesMoreThan.ToString(CultureInfo.InvariantCulture) + Environment.NewLine +
                SilentKey + "=" + silentThreshold.ToString(CultureInfo.InvariantCulture) + Environment.NewLine +
                WindowKey + "=" + windowSeconds.ToString(CultureInfo.InvariantCulture) + Environment.NewLine +
                SwallowKey + "=" + swallowThreshold.ToString(CultureInfo.InvariantCulture) + Environment.NewLine;
            try { File.WriteAllText(FilePath, text); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // Scrolling graph: peak line over a min-to-max band, with a strip underneath
    // that marks windows where DO triggered.
    class GraphPanel : Control
    {
        readonly Queue<Sample> samples;
        public string EmptyText = "";
        public int SilentLevel = Config.DefaultSilentThreshold;
        public int SwallowLevel = Config.DefaultSwallowThreshold;

        public GraphPanel(Queue<Sample> samples)
        {
            this.samples = samples;
            DoubleBuffered = true;
            ResizeRedraw = true;
            BackColor = Theme.Card;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float k = g.DpiX / 96f;
            float left = 52 * k, right = 16 * k, top = 28 * k, bottom = 14 * k;
            float stripH = 10 * k, gap = 12 * k;
            float plotW = Width - left - right;
            float plotH = Height - top - bottom - stripH - gap;
            if (plotW < 20 || plotH < 20) return;
            float stripY = top + plotH + gap;

            using (Pen gridPen = new Pen(Theme.Grid))
            using (Brush gridBrush = new SolidBrush(Theme.Grid))
            using (Brush muted = new SolidBrush(Theme.Muted))
            using (Font small = new Font("Segoe UI", 8f))
            using (StringFormat alignRight = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center })
            {
                g.DrawString("line = peak     band = min to max", small, muted, new RectangleF(left, 4 * k, plotW, 18 * k), alignRight);
                foreach (int v in new[] { 0, 1024, 2048, 3072, Config.AoMax })
                {
                    float y = ToY(v, top, plotH);
                    g.DrawLine(gridPen, left, y, left + plotW, y);
                    g.DrawString(v.ToString(), small, muted, new RectangleF(0, y - 10 * k, left - 8 * k, 20 * k), alignRight);
                }
                g.FillRectangle(gridBrush, left, stripY, plotW, stripH);
                g.DrawString("DO", small, muted, new RectangleF(0, stripY - 5 * k, left - 8 * k, stripH + 10 * k), alignRight);

                // The lower level gets its name under the line and the higher one above it, so they never overlap
                DrawLevel(g, small, alignRight, SilentLevel, Theme.Stale, "silent swallow " + SilentLevel, SilentLevel <= SwallowLevel, left, plotW, top, plotH, k);
                DrawLevel(g, small, alignRight, SwallowLevel, Theme.Swallow, "swallow " + SwallowLevel, SwallowLevel < SilentLevel, left, plotW, top, plotH, k);

                if (samples.Count == 0)
                {
                    using (StringFormat center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                        g.DrawString(EmptyText, Font, muted, new RectangleF(left, top, plotW, plotH), center);
                    return;
                }
            }

            // Newest window sits at the right edge. The band polygon runs along the
            // peaks left to right, then back along the minimums right to left.
            int n = samples.Count;
            float step = plotW / (Config.History - 1);
            float x0 = left + plotW - (n - 1) * step;
            PointF[] peaks = new PointF[n];
            PointF[] band = new PointF[2 * n];
            int i = 0;
            using (Brush doBrush = new SolidBrush(Theme.DoHigh))
            {
                foreach (Sample s in samples)
                {
                    float x = x0 + i * step;
                    peaks[i] = new PointF(x, ToY(s.Peak, top, plotH));
                    band[i] = peaks[i];
                    band[2 * n - 1 - i] = new PointF(x, ToY(s.Min, top, plotH));
                    if (s.Triggered)
                        g.FillRectangle(doBrush, x - step / 2, stripY, Math.Max(step, 1f), stripH);
                    i++;
                }
            }
            if (n > 1)
            {
                using (Brush bandBrush = new SolidBrush(Theme.Band))
                    g.FillPolygon(bandBrush, band);
                using (Pen trace = new Pen(Theme.Trace, 1.5f * k) { LineJoin = LineJoin.Round })
                    g.DrawLines(trace, peaks);
            }
        }

        static void DrawLevel(Graphics g, Font font, StringFormat align, int level, Color color, string name, bool nameBelow,
                              float left, float plotW, float top, float plotH, float k)
        {
            float y = ToY(level, top, plotH);
            using (Pen pen = new Pen(Color.FromArgb(170, color)) { DashStyle = DashStyle.Dash })
                g.DrawLine(pen, left, y, left + plotW, y);
            using (Brush brush = new SolidBrush(color))
                g.DrawString(name, font, brush, new RectangleF(left, nameBelow ? y + 2 * k : y - 16 * k, plotW - 4 * k, 14 * k), align);
        }

        static float ToY(int ao, float top, float plotH)
        {
            ao = Math.Max(0, Math.Min(Config.AoMax, ao));
            return top + plotH * (1f - ao / (float)Config.AoMax);
        }
    }

    public class MainForm : Form
    {
        static readonly Regex FieldRx = new Regex("\"(\\w+)\"\\s*:\\s*(-?\\d+)");

        readonly UdpClient udp;
        readonly Timer timer = new Timer();
        readonly Queue<Sample> samples = new Queue<Sample>();
        readonly Queue<DateTime> rxTimes = new Queue<DateTime>();
        readonly float scale;

        DateTime lastRx = DateTime.MinValue;
        long packetCount;
        long lastSeq = -1;
        long lostCount;
        string espIp = "-";
        int rssi;
        bool haveRssi;

        StreamWriter csv;
        string csvPath;
        long csvRows;

        enum SirenKind { None, Silent, Swallow }
        const string TestText = "▶  Test";
        const string StopText = "■  Stop";
        readonly SoundPlayer silentSiren, swallowSiren;
        SirenKind sirenPlaying = SirenKind.None;
        DateTime sirenUntil;

        int wavesMoreThan, silentThreshold, windowSeconds, swallowThreshold;
        NumericUpDown wavesBox, silentBox, windowBox, swallowBox;
        readonly Queue<DateTime> waveTimes = new Queue<DateTime>();  // when each recent silent-swallow wave ended
        bool inWave;
        int waveMax;  // highest peak of the wave in progress
        int swallowCount;

        Label statusLabel, ipLabel, rssiLabel, rateLabel, countLabel, lostLabel, aoLabel, doLabel, recLabel;
        Label silentTitle, waveLabel, swallowTitle, swallowLabel;
        Button recButton, silentButton, swallowButton;
        GraphPanel graph;

        public MainForm(int port, IPAddress bindAddress)
        {
            udp = new UdpClient(new IPEndPoint(bindAddress, port));  // throws if the port is already in use
            silentSiren = new SoundPlayer(new MemoryStream(Siren.BuildWav(Config.SilentSirenSeconds)));
            swallowSiren = new SoundPlayer(new MemoryStream(Siren.BuildWav(Config.SwallowSirenSeconds)));
            silentSiren.Load();
            swallowSiren.Load();
            wavesMoreThan = Settings.Load(Settings.WavesKey,
                Settings.Load(Settings.OldWavesKey, Config.DefaultWavesMoreThan, 0, 99), 0, 99);
            silentThreshold = Settings.Load(Settings.SilentKey,
                Settings.Load(Settings.OldSilentKey, Config.DefaultSilentThreshold, 0, Config.AoMax), 0, Config.AoMax);
            windowSeconds = Settings.Load(Settings.WindowKey,
                Settings.Load(Settings.OldWindowKey, Config.DefaultWindowSeconds, 1, 600), 1, 600);
            swallowThreshold = Settings.Load(Settings.SwallowKey, Config.DefaultSwallowThreshold, 0, Config.AoMax);
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f;

            Text = "DARVIS Piezo Receiver";
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = new Font("Segoe UI", 9.5f);
            ClientSize = new Size(S(920), S(700));
            MinimumSize = new Size(S(640), S(580));
            StartPosition = FormStartPosition.CenterScreen;

            // Rows: header, info, readouts, alarm cards, graph (takes the spare height), recording bar
            TableLayoutPanel root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(S(16), S(12), S(16), S(12)) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            for (int r = 0; r < 6; r++)
                root.RowStyles.Add(r == 4 ? new RowStyle(SizeType.Percent, 100f) : new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            TableLayoutPanel header = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0) };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.Controls.Add(MakeLabel("DARVIS PIEZO RECEIVER", 14f, FontStyle.Bold, Theme.Text), 0, 0);
            statusLabel = MakeLabel("", 10.5f, FontStyle.Bold, Theme.Muted);
            statusLabel.Anchor = AnchorStyles.Right;
            header.Controls.Add(statusLabel, 1, 0);
            root.Controls.Add(header, 0, 0);

            FlowLayoutPanel info = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 0, 0, S(8)) };
            ipLabel = AddInfo(info, "ESP32: -");
            rssiLabel = AddInfo(info, "Signal: -");
            rateLabel = AddInfo(info, "Rate: 0 /s");
            countLabel = AddInfo(info, "Packets: 0");
            lostLabel = AddInfo(info, "Lost: -");
            AddInfo(info, "UDP port: " + port);
            root.Controls.Add(info, 0, 1);

            FlowLayoutPanel values = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0) };
            aoLabel = AddReading(values, "AO peak, last 1 s  (GPIO34)");
            doLabel = AddReading(values, "DO trigger, last 1 s  (GPIO25)");
            root.Controls.Add(values, 0, 2);

            // Alarm cards right under the readouts, each with its own Test button
            Func<string, Label> ruleText = s =>
            {
                Label l = MakeLabel(s, 9f, FontStyle.Regular, Theme.Muted);
                l.Anchor = AnchorStyles.Left;
                return l;
            };
            wavesBox = MakeNumberBox(0, 99, wavesMoreThan, 1, 56);
            silentBox = MakeNumberBox(0, Config.AoMax, silentThreshold, 10, 80);
            windowBox = MakeNumberBox(1, 600, windowSeconds, 1, 64);
            swallowBox = MakeNumberBox(0, Config.AoMax, swallowThreshold, 10, 80);
            string silentLength = Config.SilentSirenSeconds.ToString(CultureInfo.InvariantCulture);
            string swallowLength = Config.SwallowSirenSeconds.ToString(CultureInfo.InvariantCulture);

            FlowLayoutPanel alarms = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, S(8), 0, 0) };

            FlowLayoutPanel silentCard = MakeCard(alarms);
            silentTitle = AddCardHeader(silentCard, "SILENT SWALLOW", out silentButton);
            silentButton.Click += (sender, e) => OnTestClick(SirenKind.Silent);
            AddRow(silentCard, ruleText("Siren " + silentLength + " s when more than"), wavesBox, ruleText("waves"));
            AddRow(silentCard, ruleText("go over"), silentBox, ruleText("within"), windowBox, ruleText("seconds"));
            waveLabel = MakeLabel("", 9f, FontStyle.Regular, Theme.Muted);
            silentCard.Controls.Add(waveLabel);

            FlowLayoutPanel swallowCard = MakeCard(alarms);
            swallowTitle = AddCardHeader(swallowCard, "SWALLOW", out swallowButton);
            swallowButton.Click += (sender, e) => OnTestClick(SirenKind.Swallow);
            AddRow(swallowCard, ruleText("Siren " + swallowLength + " s when one wave goes over"), swallowBox);
            AddRow(swallowCard, ruleText("and then drops back to normal"));
            swallowLabel = MakeLabel("Swallows detected: 0", 9f, FontStyle.Regular, Theme.Muted);
            swallowCard.Controls.Add(swallowLabel);
            root.Controls.Add(alarms, 0, 3);

            graph = new GraphPanel(samples)
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(0, S(6), 0, S(10)),
                SilentLevel = silentThreshold,
                SwallowLevel = swallowThreshold
            };
            graph.EmptyText = "Waiting for ESP32 data on UDP port " + port + " ...";
            root.Controls.Add(graph, 0, 4);

            FlowLayoutPanel bottom = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0) };
            recButton = MakeButton("Start recording", Theme.Accent);
            recButton.Click += OnRecordClick;
            recLabel = MakeLabel("Not recording", 9.5f, FontStyle.Regular, Theme.Muted);
            recLabel.Anchor = AnchorStyles.Left;
            recLabel.Margin = new Padding(S(12), 0, 0, 0);
            bottom.Controls.Add(recButton);
            bottom.Controls.Add(recLabel);
            root.Controls.Add(bottom, 0, 5);

            timer.Interval = 30;
            timer.Tick += OnTick;
            timer.Start();
            UpdateStatus();
        }

        void OnTick(object sender, EventArgs e)
        {
            bool gotData = false;
            try
            {
                for (int n = 0; n < 500 && udp.Available > 0; n++)
                {
                    IPEndPoint remote = null;
                    byte[] data = udp.Receive(ref remote);
                    if (HandlePacket(Encoding.UTF8.GetString(data), remote.Address.ToString()))
                        gotData = true;
                }
            }
            catch (SocketException) { }  // skip a bad datagram, keep listening

            if (gotData)
            {
                UpdateReadings();
                graph.Invalidate();
            }
            UpdateStatus();
            if (sirenPlaying != SirenKind.None && DateTime.Now >= sirenUntil) SirenFinished();
        }

        bool HandlePacket(string text, string fromIp)
        {
            Dictionary<string, long> f = new Dictionary<string, long>();
            foreach (Match m in FieldRx.Matches(text))
            {
                long v;
                if (long.TryParse(m.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
                    f[m.Groups[1].Value] = v;
            }
            if (!f.ContainsKey("ao") || !f.ContainsKey("do")) return false;  // not one of ours

            DateTime now = DateTime.Now;
            int peak = (int)f["ao"];
            int min = f.ContainsKey("lo") ? (int)f["lo"] : peak;  // older firmware sends only "ao"
            haveRssi = f.ContainsKey("rssi");
            if (haveRssi) rssi = (int)f["rssi"];
            espIp = fromIp;
            lastRx = now;
            rxTimes.Enqueue(now);
            packetCount++;

            // A gap in seq means windows went missing on the way; a smaller seq means the ESP32 restarted.
            if (f.ContainsKey("seq"))
            {
                long seq = f["seq"];
                if (lastSeq >= 0 && seq > lastSeq) lostCount += seq - lastSeq - 1;
                lastSeq = seq;
            }

            samples.Enqueue(new Sample(now, peak, min, f["do"] != 0));
            while (samples.Count > Config.History) samples.Dequeue();

            TrackWave(now, peak);

            if (csv != null)
            {
                try
                {
                    csv.WriteLine(string.Join(",",
                        now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
                        Opt(f, "up"), Opt(f, "seq"), Opt(f, "ao"), Opt(f, "lo"), Opt(f, "avg"),
                        Opt(f, "do"), Opt(f, "n"), Opt(f, "rssi")));
                    csvRows++;
                }
                catch (IOException ex)
                {
                    StopRecording();
                    MessageBox.Show(this, "Recording stopped:\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            return true;
        }

        static string Opt(Dictionary<string, long> f, string key)
        {
            long v;
            return f.TryGetValue(key, out v) ? v.ToString(CultureInfo.InvariantCulture) : "";
        }

        void UpdateReadings()
        {
            // Hold the highest peak and any trigger for a moment so the readouts are readable at 20 updates/s.
            DateTime since = DateTime.Now.AddSeconds(-Config.HoldSeconds);
            int peak = 0;
            bool triggered = false;
            foreach (Sample s in samples)
            {
                if (s.Time < since) continue;
                peak = Math.Max(peak, s.Peak);
                triggered |= s.Triggered;
            }
            SetText(aoLabel, peak.ToString());
            SetColor(aoLabel, peak > swallowThreshold ? Theme.Swallow : peak > silentThreshold ? Theme.Stale : Theme.Text);
            SetText(doLabel, triggered ? "TRIGGERED" : "IDLE");
            SetColor(doLabel, triggered ? Theme.DoHigh : Theme.Text);

            SetText(ipLabel, "ESP32: " + espIp);
            SetText(rssiLabel, haveRssi ? "Signal: " + rssi + " dBm" : "Signal: -");
            SetText(countLabel, "Packets: " + packetCount);
            if (lastSeq >= 0)
            {
                double pct = 100.0 * lostCount / (lostCount + packetCount);
                SetText(lostLabel, "Lost: " + lostCount + " (" + pct.ToString("0.0", CultureInfo.InvariantCulture) + "%)");
            }
            if (csv != null) SetText(recLabel, "Recording " + csvRows + " rows to " + Path.GetFileName(csvPath));
        }

        void UpdateStatus()
        {
            DateTime now = DateTime.Now;
            while (rxTimes.Count > 0 && (now - rxTimes.Peek()).TotalSeconds > 1.0) rxTimes.Dequeue();
            SetText(rateLabel, "Rate: " + rxTimes.Count + " /s");

            DropOldWaves(now);
            SetText(waveLabel, "Waves in last " + windowSeconds + " s: " + waveTimes.Count + " of " + (wavesMoreThan + 1));
            SetColor(waveLabel, waveTimes.Count > 0 ? Theme.DoHigh : Theme.Muted);

            double age = (now - lastRx).TotalSeconds;
            if (packetCount == 0)
                SetStatus("WAITING FOR ESP32", Theme.Muted);
            else if (age > Config.StaleSeconds)
                SetStatus("NO DATA  " + (int)age + " s", Theme.Stale);
            else
                SetStatus("● LIVE", Theme.Live);
        }

        void SetStatus(string text, Color color)
        {
            SetText(statusLabel, text);
            SetColor(statusLabel, color);
        }

        // The AO peak is split into waves: a wave starts when the peak rises over the lower of the two alarm
        // levels and ends once it is back to normal (under 90% of that level, so a signal hovering at the line
        // is one wave, not many). A finished wave is sorted by how high it got:
        //   over the swallow level         -> a swallow: 3 s siren straight away
        //   over the silent swallow level  -> one more silent-swallow wave: 1 s siren once there are more than N in T s
        void TrackWave(DateTime now, int peak)
        {
            int startLevel = Math.Min(silentThreshold, swallowThreshold);
            if (!inWave)
            {
                if (peak <= startLevel) return;
                inWave = true;
                waveMax = peak;
                return;
            }
            waveMax = Math.Max(waveMax, peak);
            if (peak >= startLevel * Config.WaveRearm) return;  // not back to normal yet
            inWave = false;

            if (waveMax > swallowThreshold)
            {
                swallowCount++;
                SetText(swallowLabel, "Swallows detected: " + swallowCount + "   (last at " + now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + ")");
                StartSiren(SirenKind.Swallow, false);
            }
            else if (waveMax > silentThreshold)
            {
                waveTimes.Enqueue(now);
                DropOldWaves(now);
                if (waveTimes.Count > wavesMoreThan)
                {
                    waveTimes.Clear();  // each siren needs a fresh run of waves
                    StartSiren(SirenKind.Silent, false);
                }
            }
        }

        void DropOldWaves(DateTime now)
        {
            while (waveTimes.Count > 0 && (now - waveTimes.Peek()).TotalSeconds > windowSeconds)
                waveTimes.Dequeue();
        }

        void OnSirenSettingChanged(object sender, EventArgs e)
        {
            waveTimes.Clear();  // waves counted under the old rule no longer apply
            inWave = false;
            wavesMoreThan = (int)wavesBox.Value;
            silentThreshold = (int)silentBox.Value;
            windowSeconds = (int)windowBox.Value;
            swallowThreshold = (int)swallowBox.Value;
            graph.SilentLevel = silentThreshold;
            graph.SwallowLevel = swallowThreshold;
            graph.Invalidate();
            if (packetCount > 0) UpdateReadings();
            Settings.Save(wavesMoreThan, silentThreshold, windowSeconds, swallowThreshold);
        }

        void OnTestClick(SirenKind kind)
        {
            if (sirenPlaying != kind)
            {
                StartSiren(kind, true);
                return;
            }
            silentSiren.Stop();  // stops whichever siren sound is playing
            SirenFinished();
        }

        // A siren that is already sounding keeps going, except that a swallow takes over from a silent
        // swallow. The Test buttons (manual) always switch to the siren asked for.
        void StartSiren(SirenKind kind, bool manual)
        {
            if (!manual && (sirenPlaying == kind || sirenPlaying == SirenKind.Swallow)) return;
            bool swallow = kind == SirenKind.Swallow;
            try { (swallow ? swallowSiren : silentSiren).Play(); }
            catch (Exception) { return; }  // no usable sound device: keep the readings running
            sirenPlaying = kind;
            sirenUntil = DateTime.Now.AddSeconds(swallow ? Config.SwallowSirenSeconds : Config.SilentSirenSeconds);
            RefreshSirenUi();
        }

        void SirenFinished()
        {
            sirenPlaying = SirenKind.None;
            RefreshSirenUi();
        }

        // The card whose siren is sounding gets a red title and a Stop button.
        void RefreshSirenUi()
        {
            SetSirenUi(silentButton, silentTitle, sirenPlaying == SirenKind.Silent);
            SetSirenUi(swallowButton, swallowTitle, sirenPlaying == SirenKind.Swallow);
        }

        static void SetSirenUi(Button button, Label title, bool on)
        {
            button.Text = on ? StopText : TestText;
            button.BackColor = on ? Theme.Stale : Theme.Accent;
            SetColor(title, on ? Theme.Stale : Theme.Text);
        }

        void OnRecordClick(object sender, EventArgs e)
        {
            if (csv != null)
            {
                StopRecording();
                return;
            }

            using (SaveFileDialog dlg = new SaveFileDialog())
            {
                dlg.Filter = "CSV files (*.csv)|*.csv";
                dlg.FileName = "piezo_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".csv";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    csv = new StreamWriter(dlg.FileName, false, new UTF8Encoding(false)) { AutoFlush = true };
                    csv.WriteLine("pc_time,esp_uptime_ms,seq,ao_peak,ao_min,ao_avg,do_triggered,samples,rssi");
                }
                catch (Exception ex)
                {
                    if (csv != null) csv.Dispose();
                    csv = null;
                    MessageBox.Show(this, "Could not create the file:\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                csvPath = dlg.FileName;
                csvRows = 0;
                recButton.Text = "Stop recording";
                recButton.BackColor = Theme.Stale;
                SetText(recLabel, "Recording 0 rows to " + Path.GetFileName(csvPath));
            }
        }

        void StopRecording()
        {
            if (csv == null) return;
            csv.Dispose();
            csv = null;
            recButton.Text = "Start recording";
            recButton.BackColor = Theme.Accent;
            SetText(recLabel, "Saved " + csvRows + " rows to " + csvPath);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            timer.Stop();
            silentSiren.Stop();  // stops whichever siren sound is playing
            StopRecording();
            udp.Close();
            base.OnFormClosing(e);
        }

        NumericUpDown MakeNumberBox(int min, int max, int value, int increment, int width)
        {
            NumericUpDown box = new NumericUpDown
            {
                Minimum = min,
                Maximum = max,
                Increment = increment,
                Value = value,
                Width = S(width),
                Font = new Font("Segoe UI", 10f),
                BackColor = Theme.Bg,
                ForeColor = Theme.Text,
                BorderStyle = BorderStyle.FixedSingle,
                TextAlign = HorizontalAlignment.Right,
                Anchor = AnchorStyles.Left
            };
            box.ValueChanged += OnSirenSettingChanged;
            return box;
        }

        FlowLayoutPanel MakeCard(Control parent)
        {
            FlowLayoutPanel card = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                BackColor = Theme.Card,
                Padding = new Padding(S(12), S(8), S(12), S(10)),
                Margin = new Padding(0, 0, S(12), 0)
            };
            parent.Controls.Add(card);
            return card;
        }

        Label AddCardHeader(Control card, string title, out Button test)
        {
            Label l = MakeLabel(title, 10f, FontStyle.Bold, Theme.Text);
            l.Anchor = AnchorStyles.Left;
            test = MakeButton(TestText, Theme.Accent);
            test.Margin = new Padding(S(12), 0, 0, S(4));
            AddRow(card, l, test);
            return l;
        }

        void AddRow(Control card, params Control[] items)
        {
            FlowLayoutPanel row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, S(2), 0, 0) };
            row.Controls.AddRange(items);
            card.Controls.Add(row);
        }

        Button MakeButton(string text, Color color)
        {
            Button b = new Button
            {
                Text = text,
                AutoSize = true,
                FlatStyle = FlatStyle.Flat,
                BackColor = color,
                ForeColor = Color.White,
                Padding = new Padding(S(8), S(3), S(8), S(3)),
                Cursor = Cursors.Hand
            };
            b.FlatAppearance.BorderSize = 0;
            return b;
        }

        Label AddInfo(Control parent, string text)
        {
            Label l = MakeLabel(text, 9.5f, FontStyle.Regular, Theme.Muted);
            l.Margin = new Padding(0, 0, S(22), 0);
            parent.Controls.Add(l);
            return l;
        }

        Label AddReading(Control parent, string caption)
        {
            FlowLayoutPanel box = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                Margin = new Padding(0, 0, S(40), 0)
            };
            box.Controls.Add(MakeLabel(caption, 9f, FontStyle.Regular, Theme.Muted));
            Label value = MakeLabel("—", 28f, FontStyle.Bold, Theme.Text);
            value.MinimumSize = new Size(S(180), 0);
            box.Controls.Add(value);
            parent.Controls.Add(box);
            return value;
        }

        static Label MakeLabel(string text, float size, FontStyle style, Color color)
        {
            return new Label { Text = text, AutoSize = true, Font = new Font("Segoe UI", size, style), ForeColor = color };
        }

        static void SetText(Label l, string s)
        {
            if (l.Text != s) l.Text = s;
        }

        static void SetColor(Label l, Color c)
        {
            if (l.ForeColor != c) l.ForeColor = c;
        }

        int S(int px)
        {
            return (int)Math.Round(px * scale);
        }
    }

    static class Program
    {
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main()
        {
            SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            MainForm form;
            try
            {
                form = new MainForm(Config.Port, IPAddress.Any);
            }
            catch (SocketException ex)
            {
                MessageBox.Show("Could not listen on UDP port " + Config.Port + ".\nIs another receiver already running?\n\n" + ex.Message,
                    "DARVIS Piezo Receiver", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            Application.Run(form);
        }
    }
}
