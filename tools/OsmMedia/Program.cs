using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace OsmMedia
{
    /// <summary>
    /// Pomocnik OSMRoads: sterowanie muzyka Windows (Spotify, przegladarka, ...) z KSP.
    ///
    /// KSP (Mono) nie ma dostepu do API Windows od odtwarzaczy (System Media Transport
    /// Controls), wiec robi to ten maly program, uruchamiany przez mod. Rozmowa przez
    /// potoki standardowe, bez sieci:
    ///   stdout: "S\ttytul\twykonawca\talbum\tgra(0/1)\tpozycja s\tdlugosc s\tglosnosc(0-1, -1 brak)\taplikacja\twersja okladki"
    ///           "A\twersja\tbase64 okladki"      "N" = nic nie gra
    ///   stdin:  toggle | play | pause | next | prev | seek &lt;s&gt; | vol &lt;0-1&gt;
    /// Konczy sie, gdy KSP zamknie potok (albo zniknie proces rodzica).
    /// </summary>
    internal static class Program
    {
        private static GlobalSystemMediaTransportControlsSessionManager mgr;
        private static readonly object outGate = new object();
        private static string lastState = "", lastTrack = "";
        private static int artVersion;

        private static void Main(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            Console.InputEncoding = new UTF8Encoding(false);
            int parent = args.Length > 0 && int.TryParse(args[0], out int p) ? p : 0;

            try { mgr = GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask().Result; }
            catch (Exception e) { Send("E\t" + Clean(e.Message)); return; }

            var reader = new Thread(ReadCommands) { IsBackground = true };
            reader.Start();

            int tick = 0;
            while (true)
            {
                try { Poll(tick++ % 8 == 0); }
                catch (Exception e) { Send("E\t" + Clean(e.Message)); Thread.Sleep(1000); }

                if (parent != 0 && tick % 8 == 0)
                {
                    try { if (Process.GetProcessById(parent).HasExited) return; }
                    catch (ArgumentException) { return; }
                }
                Thread.Sleep(250);
            }
        }

        private static void Send(string line)
        {
            lock (outGate) { Console.Out.WriteLine(line); Console.Out.Flush(); }
        }

        private static string Clean(string s)
        {
            return (s ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        }

        private static string F(double v) { return v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture); }

        private static void Poll(bool force)
        {
            GlobalSystemMediaTransportControlsSession s = mgr.GetCurrentSession();
            if (s == null)
            {
                if (lastState != "N") { lastState = "N"; Send("N"); }
                return;
            }

            GlobalSystemMediaTransportControlsSessionMediaProperties props = s.TryGetMediaPropertiesAsync().AsTask().Result;
            GlobalSystemMediaTransportControlsSessionPlaybackInfo pb = s.GetPlaybackInfo();
            GlobalSystemMediaTransportControlsSessionTimelineProperties tl = s.GetTimelineProperties();
            bool playing = pb.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            // pozycja: odtwarzacze zglaszaja ja rzadko - doliczamy czas od ostatniej aktualizacji
            double pos = tl.Position.TotalSeconds, dur = (tl.EndTime - tl.StartTime).TotalSeconds;
            if (playing && tl.LastUpdatedTime.Year > 2000)
                pos += (DateTimeOffset.Now - tl.LastUpdatedTime).TotalSeconds;
            if (dur > 0) pos = Math.Max(0, Math.Min(dur, pos));

            string track = props.Title + "|" + props.Artist + "|" + props.AlbumTitle;
            if (track != lastTrack)
            {
                lastTrack = track;
                SendArt(props.Thumbnail);
            }

            float vol = AppVolume(s.SourceAppUserModelId, null);
            string state = string.Join("\t", "S", Clean(props.Title), Clean(props.Artist), Clean(props.AlbumTitle),
                                       playing ? "1" : "0", F(pos), F(dur), F(vol), Clean(s.SourceAppUserModelId), artVersion.ToString());
            if (force || state != lastState) { lastState = state; Send(state); }
        }

        private static void SendArt(IRandomAccessStreamReference thumb)
        {
            artVersion++;
            if (thumb == null) { Send("A\t" + artVersion + "\t"); return; }
            try
            {
                using (IRandomAccessStreamWithContentType st = thumb.OpenReadAsync().AsTask().Result)
                using (var dr = new DataReader(st))
                {
                    uint n = dr.LoadAsync((uint)st.Size).AsTask().Result;
                    var bytes = new byte[n];
                    dr.ReadBytes(bytes);
                    Send("A\t" + artVersion + "\t" + Convert.ToBase64String(bytes));
                }
            }
            catch (Exception) { Send("A\t" + artVersion + "\t"); }
        }

        /// <summary>Glosnosc ogolna Windows (glowne wyjscie dzwieku). set != null ustawia.</summary>
        private static float AppVolume(string aumid, float? set)
        {
            try
            {
                using (var en = new MMDeviceEnumerator())
                {
                    MMDevice dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    if (set.HasValue) dev.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Max(0f, Math.Min(1f, set.Value));
                    return dev.AudioEndpointVolume.MasterVolumeLevelScalar;
                }
            }
            catch (Exception) { return -1f; }
        }
        private static void ReadCommands()
        {
            string line;
            while ((line = Console.In.ReadLine()) != null)
            {
                try { Command(line.Trim()); }
                catch (Exception e) { Send("E\t" + Clean(e.Message)); }
            }
            Environment.Exit(0);          // KSP zamknal potok
        }

        private static void Command(string line)
        {
            GlobalSystemMediaTransportControlsSession s = mgr.GetCurrentSession();
            if (s == null) return;
            string[] a = line.Split(' ');
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            switch (a[0])
            {
                case "toggle": s.TryTogglePlayPauseAsync().AsTask().Wait(); break;
                case "play": s.TryPlayAsync().AsTask().Wait(); break;
                case "pause": s.TryPauseAsync().AsTask().Wait(); break;
                case "next": s.TrySkipNextAsync().AsTask().Wait(); break;
                case "prev": s.TrySkipPreviousAsync().AsTask().Wait(); break;
                case "seek":
                    if (a.Length > 1 && double.TryParse(a[1], System.Globalization.NumberStyles.Float, inv, out double sec))
                        s.TryChangePlaybackPositionAsync(TimeSpan.FromSeconds(sec).Ticks).AsTask().Wait();
                    break;
                case "vol":
                    if (a.Length > 1 && float.TryParse(a[1], System.Globalization.NumberStyles.Float, inv, out float v))
                        AppVolume(s.SourceAppUserModelId, v);
                    break;
            }
            lastState = "";        // od razu swiezy stan
        }
    }
}
