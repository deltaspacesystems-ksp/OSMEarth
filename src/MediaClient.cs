using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using UnityEngine;

namespace OSMRoads
{
    /// <summary>
    /// Muzyka z Windows (Spotify, przegladarka, ...) w KSP: stan i sterowanie przez
    /// program pomocniczy osmmedia.exe (PluginData/osmmedia - KSP nie laduje stamtad DLL-i).
    /// Pomocnik zyje tyle co gra: konczy sie, gdy KSP zamknie potok albo zniknie.
    /// </summary>
    public static class MediaClient
    {
        public static string Title = "", Artist = "", Album = "", App = "";
        public static bool Playing, HasSession;
        public static double Duration;
        public static float Volume = -1f;          // -1 = nie da sie ustawic glosnosci tej aplikacji
        public static Texture2D Art;
        public static string Error;

        private static double pos;
        private static float posAt;                 // Time.realtimeSinceStartup przy ostatnim stanie
        private static Process proc;
        private static StreamWriter stdin;
        private static readonly object gate = new object();
        private static byte[] pendingArt;
        private static bool artChanged;
        private static float startedAt = -100f;

        public static bool Running { get { return proc != null && !proc.HasExited; } }

        /// <summary>Pozycja utworu teraz (miedzy aktualizacjami doliczana lokalnie).</summary>
        public static double Position
        {
            get
            {
                double p = pos + (Playing ? Time.realtimeSinceStartup - posAt : 0f);
                return Duration > 0 ? Math.Max(0, Math.Min(Duration, p)) : p;
            }
        }

        private static string HelperPath
        {
            get { return Path.Combine(KSPUtil.ApplicationRootPath, "GameData/OSMRoads/PluginData/osmmedia/osmmedia.exe"); }
        }

        public static void EnsureStarted()
        {
            if (Running || Time.realtimeSinceStartup - startedAt < 10f) return;
            startedAt = Time.realtimeSinceStartup;
            try
            {
                if (!File.Exists(HelperPath)) { Error = "osmmedia.exe missing (PluginData/osmmedia)"; return; }
                var psi = new ProcessStartInfo(HelperPath, Process.GetCurrentProcess().Id.ToString())
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    StandardOutputEncoding = Encoding.UTF8
                };
                proc = Process.Start(psi);
                stdin = proc.StandardInput;
                stdin.AutoFlush = true;
                var t = new Thread(ReadLoop) { IsBackground = true, Name = "OSMRoads.Media" };
                t.Start();
                Error = null;
            }
            catch (Exception e) { Error = "music helper: " + e.Message; UnityEngine.Debug.LogWarning("[OSMRoads] " + Error); }
        }

        public static void Stop()
        {
            try { if (stdin != null) stdin.Close(); } catch (Exception) { }
            try { if (proc != null && !proc.HasExited) proc.Kill(); } catch (Exception) { }
            proc = null; stdin = null;
        }

        private static void ReadLoop()
        {
            Process p = proc;
            try
            {
                string line;
                while ((line = p.StandardOutput.ReadLine()) != null) Handle(line);
            }
            catch (Exception) { }
        }

        private static void Handle(string line)
        {
            string[] a = line.Split('\t');
            var inv = CultureInfo.InvariantCulture;
            switch (a[0])
            {
                case "S":
                    if (a.Length < 10) return;
                    lock (gate)
                    {
                        HasSession = true;
                        Title = a[1]; Artist = a[2]; Album = a[3];
                        Playing = a[4] == "1";
                        double.TryParse(a[5], NumberStyles.Float, inv, out pos);
                        double.TryParse(a[6], NumberStyles.Float, inv, out Duration);
                        float.TryParse(a[7], NumberStyles.Float, inv, out Volume);
                        App = AppName(a[8]);
                        posAt = -1f;           // ustawi watek glowny (Time tylko tam)
                    }
                    break;
                case "A":
                    lock (gate)
                    {
                        pendingArt = a.Length > 2 && a[2].Length > 0 ? Convert.FromBase64String(a[2]) : null;
                        artChanged = true;
                    }
                    break;
                case "N":
                    lock (gate) { HasSession = false; Playing = false; Title = ""; Artist = ""; }
                    break;
                case "E":
                    Error = a.Length > 1 ? a[1] : "error";
                    break;
            }
        }

        private static string AppName(string aumid)
        {
            string s = aumid ?? "";
            int bang = s.LastIndexOf('!');
            if (bang >= 0) s = s.Substring(bang + 1);
            if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 4);
            if (s.Contains("Chrome")) return "Chrome";
            if (s.Contains("MSEdge") || s.ToLowerInvariant().Contains("edge")) return "Edge";
            return s.Length > 0 ? char.ToUpperInvariant(s[0]) + s.Substring(1) : "";
        }

        /// <summary>Raz na klatke z watku glownego: okladka do tekstury, znacznik czasu pozycji.</summary>
        public static void Tick()
        {
            lock (gate)
            {
                if (posAt < 0f) posAt = Time.realtimeSinceStartup;
                if (!artChanged) return;
                artChanged = false;
                if (pendingArt == null) { Art = null; return; }
                var t = new Texture2D(2, 2, TextureFormat.RGB24, true);
                if (t.LoadImage(pendingArt)) { if (Art != null) UnityEngine.Object.Destroy(Art); Art = t; }
                pendingArt = null;
            }
        }

        private static void Send(string cmd)
        {
            EnsureStarted();
            try { if (stdin != null) stdin.WriteLine(cmd); } catch (Exception e) { Error = e.Message; }
        }

        public static void Toggle() { Send("toggle"); lock (gate) { Playing = !Playing; posAt = Time.realtimeSinceStartup; } }
        public static void Next() { Send("next"); }
        public static void Prev() { Send("prev"); }
        public static void Seek(double sec) { Send("seek " + sec.ToString("0.##", CultureInfo.InvariantCulture)); lock (gate) { pos = sec; posAt = Time.realtimeSinceStartup; } }
        public static void SetVolume(float v)
        {
            v = Mathf.Clamp01(v);
            Send("vol " + v.ToString("0.###", CultureInfo.InvariantCulture));
            Volume = v;
        }

        public static string Clock(double s)
        {
            if (s < 0 || double.IsNaN(s)) s = 0;
            int t = (int)s;
            return (t / 60) + ":" + (t % 60).ToString("00");
        }
    }
}
