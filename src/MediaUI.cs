using System;
using UnityEngine;

namespace OSMRoads
{
    /// <summary>
    /// Kontroler muzyki w stylu HUD-u nawigacji: ciemne polprzezroczyste tlo, cyjanowe akcenty.
    /// Full = okno z paska KSP; Compact = maly widzet (lewy gorny rog mapy planera).
    /// </summary>
    public static class MediaUI
    {
        private static Texture2D bg, white, iPrev, iNext, iPlay, iPause, iVol;
        private static GUIStyle title, artist, small, iconBtn;
        private static readonly Color Cyan = new Color(0.30f, 0.90f, 1.00f);
        private static bool seeking;

        /// <summary>Tekstury wolno robic wszedzie (tez z renderu ekranu RPM), style tylko w OnGUI -
        /// GUI.skin poza OnGUI rzuca wyjatek i zostawial style puste (panel bez tekstu i przyciskow).</summary>
        private static void EnsureTex()
        {
            if (bg != null) return;
            bg = Rounded(32, 9, new Color(0.04f, 0.06f, 0.09f, 0.86f), new Color(0.30f, 0.90f, 1f, 0.35f));
            white = new Texture2D(1, 1); white.SetPixel(0, 0, Color.white); white.Apply();
            iPrev = Icon(0); iNext = Icon(1); iPlay = Icon(2); iPause = Icon(3); iVol = Icon(4);
        }

        private static void Ensure()
        {
            EnsureTex();
            if (title != null) return;
            title = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, clipping = TextClipping.Clip, wordWrap = false };
            title.normal.textColor = Color.white;
            artist = new GUIStyle(GUI.skin.label) { clipping = TextClipping.Clip, wordWrap = false };
            artist.normal.textColor = new Color(0.78f, 0.88f, 0.95f);
            small = new GUIStyle(GUI.skin.label) { clipping = TextClipping.Clip, wordWrap = false };
            small.normal.textColor = new Color(0.55f, 0.70f, 0.78f);
            iconBtn = new GUIStyle();
            iconBtn.normal.background = null;
        }

        private static GUIStyle bgStyle;
        private static GUIStyle BgStyle()
        {
            if (bgStyle == null) { bgStyle = new GUIStyle(); bgStyle.normal.background = bg; bgStyle.border = new RectOffset(10, 10, 10, 10); }
            return bgStyle;
        }

        // ------------------------------------------------------------------

        /// <summary>Pelny panel: okladka, tytul, wykonawca, pasek postepu, przyciski, glosnosc.</summary>
        public static void DrawFull(Rect r, float scale, bool chrome = true)
        {
            Ensure();
            MediaClient.Tick();
            if (chrome) GUI.Box(r, GUIContent.none, BgStyle());          // w oknie DeltaUI tlo daje samo okno
            float pad = 10f * scale;
            float art = r.height - 2 * pad;
            DrawArt(new Rect(r.x + pad, r.y + pad, art, art));

            float x = r.x + pad * 2 + art, w = r.xMax - x - pad;
            title.fontSize = Mathf.RoundToInt(16 * scale); artist.fontSize = Mathf.RoundToInt(13 * scale); small.fontSize = Mathf.RoundToInt(11 * scale);

            if (!MediaClient.HasSession)
            {
                GUI.Label(new Rect(x, r.y + pad, w, 24 * scale), MediaClient.Error ?? "Nothing is playing", title);
                GUI.Label(new Rect(x, r.y + pad + 24 * scale, w, 20 * scale), "Start Spotify, YouTube or any player in Windows.", small);
                return;
            }

            GUI.Label(new Rect(x, r.y + pad * 0.6f, w, 24 * scale), MediaClient.Title, title);
            GUI.Label(new Rect(x, r.y + pad * 0.6f + 21 * scale, w, 20 * scale), MediaClient.Artist, artist);
            GUI.Label(new Rect(x, r.y + pad * 0.6f + 39 * scale, w, 18 * scale), MediaClient.App, small);

            // pasek postepu (klik / przeciagniecie = przewin)
            float by = r.y + pad + 60 * scale;
            Progress(new Rect(x, by, w, 6 * scale));
            GUI.Label(new Rect(x, by + 6 * scale, 60 * scale, 18 * scale), MediaClient.Clock(MediaClient.Position), small);
            var rt = new GUIStyle(small) { alignment = TextAnchor.UpperRight };
            GUI.Label(new Rect(x + w - 60 * scale, by + 6 * scale, 60 * scale, 18 * scale), MediaClient.Clock(MediaClient.Duration), rt);

            // przyciski
            float bs = 30 * scale, cy = r.yMax - pad - bs;
            float cx = x;
            if (Button(new Rect(cx, cy, bs, bs), iPrev)) MediaClient.Prev();
            if (Button(new Rect(cx + bs * 1.3f, cy - 3 * scale, bs + 6 * scale, bs + 6 * scale), MediaClient.Playing ? iPause : iPlay)) MediaClient.Toggle();
            if (Button(new Rect(cx + bs * 2.8f, cy, bs, bs), iNext)) MediaClient.Next();

            // glosnosc aplikacji
            if (MediaClient.Volume >= 0f)
            {
                float vx = cx + bs * 4.1f;
                GUI.color = new Color(0.7f, 0.85f, 0.95f);
                GUI.DrawTexture(new Rect(vx, cy + bs * 0.2f, bs * 0.6f, bs * 0.6f), iVol);
                GUI.color = Color.white;
                float nv = GUI.HorizontalSlider(new Rect(vx + bs * 0.75f, cy + bs * 0.35f, r.xMax - pad - vx - bs * 0.75f, 12 * scale), MediaClient.Volume, 0f, 1f);
                if (Mathf.Abs(nv - MediaClient.Volume) > 0.005f) MediaClient.SetVolume(nv);
            }
        }

        /// <summary>Maly widzet: okladka, tytul/wykonawca, trzy przyciski, cienki pasek postepu.</summary>
        public static void DrawCompact(Rect r)
        {
            Ensure();
            MediaClient.Tick();
            GUI.Box(r, GUIContent.none, BgStyle());
            float pad = 6f, art = r.height - 2 * pad - 5f;
            DrawArt(new Rect(r.x + pad, r.y + pad, art, art));
            float x = r.x + pad * 2 + art, w = r.xMax - x - pad;
            title.fontSize = 12; artist.fontSize = 11;
            if (!MediaClient.HasSession)
            {
                GUI.Label(new Rect(x, r.y + pad, w, 18), "Nothing is playing", artist);
                return;
            }
            GUI.Label(new Rect(x, r.y + pad - 2, w - 74, 18), MediaClient.Title, title);
            GUI.Label(new Rect(x, r.y + pad + 14, w - 74, 18), MediaClient.Artist, artist);
            float bs = 22f, bx = r.xMax - pad - bs * 3.2f, byy = r.y + pad + 4;
            if (Button(new Rect(bx, byy, bs, bs), iPrev)) MediaClient.Prev();
            if (Button(new Rect(bx + bs * 1.1f, byy, bs, bs), MediaClient.Playing ? iPause : iPlay)) MediaClient.Toggle();
            if (Button(new Rect(bx + bs * 2.2f, byy, bs, bs), iNext)) MediaClient.Next();
            Progress(new Rect(r.x + pad, r.yMax - pad - 3, r.width - 2 * pad, 3));
        }

        private static void DrawArt(Rect r)
        {
            if (MediaClient.Art != null) GUI.DrawTexture(r, MediaClient.Art, ScaleMode.ScaleAndCrop);
            else
            {
                GUI.color = new Color(0.12f, 0.18f, 0.24f, 1f);
                GUI.DrawTexture(r, white);
                GUI.color = new Color(0.3f, 0.9f, 1f, 0.5f);
                GUI.DrawTexture(new Rect(r.x + r.width * 0.25f, r.y + r.height * 0.25f, r.width * 0.5f, r.height * 0.5f), AppIconMusic());
                GUI.color = Color.white;
            }
        }

        private static Texture2D music;
        private static Texture2D AppIconMusic() { if (music == null) music = AppIcons.Music(); return music; }

        private static void Progress(Rect r)
        {
            double dur = MediaClient.Duration;
            float frac = dur > 0 ? (float)(MediaClient.Position / dur) : 0f;
            GUI.color = new Color(1f, 1f, 1f, 0.14f);
            GUI.DrawTexture(r, white);
            GUI.color = Cyan;
            GUI.DrawTexture(new Rect(r.x, r.y, r.width * Mathf.Clamp01(frac), r.height), white);
            GUI.color = Color.white;

            Event e = Event.current;
            var hit = new Rect(r.x, r.y - 6, r.width, r.height + 12);
            if (e.type == EventType.MouseDown && e.button == 0 && hit.Contains(e.mousePosition) && dur > 0) { seeking = true; e.Use(); }
            if (seeking && (e.type == EventType.MouseUp || e.rawType == EventType.MouseUp))
            {
                seeking = false;
                float f = Mathf.Clamp01((e.mousePosition.x - r.x) / r.width);
                MediaClient.Seek(f * dur);
                e.Use();
            }
        }

        private static bool Button(Rect r, Texture2D icon)
        {
            bool hover = r.Contains(Event.current.mousePosition);
            GUI.color = hover ? Cyan : new Color(0.88f, 0.94f, 1f);
            bool click = GUI.Button(r, GUIContent.none, iconBtn);
            GUI.DrawTexture(r, icon);
            GUI.color = Color.white;
            return click;
        }

        // ------------------------------------------------------------------
        //  Ikony w kodzie (biale ksztalty, 48 px)
        // ------------------------------------------------------------------

        private static Texture2D Icon(int kind)
        {
            const int N = 48;
            var t = new Texture2D(N, N, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[N * N];
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float fx = (x + 0.5f) / N, fy = (y + 0.5f) / N;       // fy: 0 dol
                    float a = 0f;
                    switch (kind)
                    {
                        case 0: a = Mathf.Max(Tri(1f - fx, fy, 0.2f), Bar(fx, 0.18f, 0.26f, fy)); break;   // |<  (poprzedni)
                        case 1: a = Mathf.Max(Tri(fx, fy, 0.2f), Bar(fx, 0.74f, 0.82f, fy)); break;          // >|  (nastepny)
                        case 2: a = Tri(fx + 0.08f, fy, 0.0f); break;                                       // >
                        case 3: a = Mathf.Max(Bar(fx, 0.27f, 0.42f, fy), Bar(fx, 0.58f, 0.73f, fy)); break; // ||
                        case 4: a = Speaker(fx, fy); break;
                    }
                    byte v = (byte)(255 * Mathf.Clamp01(a));
                    px[y * N + x] = new Color32(255, 255, 255, v);
                }
            t.SetPixels32(px);
            t.Apply(true);
            return t;
        }

        /// <summary>Trojkat grotem w prawo, od x = 0.3 do 0.72 (przesuniety o off).</summary>
        private static float Tri(float x, float y, float off)
        {
            float x0 = 0.3f + off * 0.5f, x1 = 0.72f + off * 0.5f;
            if (x < x0 || x > x1) return 0f;
            float half = 0.26f * (x1 - x) / (x1 - x0);
            return Mathf.Clamp01((half - Mathf.Abs(y - 0.5f)) * 60f);
        }

        private static float Bar(float x, float x0, float x1, float y)
        {
            return x >= x0 && x <= x1 && y > 0.24f && y < 0.76f ? 1f : 0f;
        }

        private static float Speaker(float x, float y)
        {
            float d = Mathf.Abs(y - 0.5f);
            if (x > 0.15f && x < 0.35f && d < 0.12f) return 1f;
            if (x >= 0.35f && x < 0.55f && d < 0.12f + (x - 0.35f) * 1.1f) return 1f;
            float r = Mathf.Sqrt((x - 0.5f) * (x - 0.5f) + d * d);
            if (x > 0.6f && Mathf.Abs(r - 0.25f) < 0.035f) return 1f;
            if (x > 0.62f && Mathf.Abs(r - 0.37f) < 0.035f) return 1f;
            return 0f;
        }

        private static Texture2D Rounded(int n, int rad, Color fill, Color edge)
        {
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float cx = Mathf.Clamp(x + 0.5f, rad, n - rad), cy = Mathf.Clamp(y + 0.5f, rad, n - rad);
                    float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(cx, cy));
                    Color c = d > rad - 1.5f ? Color.Lerp(fill, edge, 0.8f) : fill;
                    c.a *= Mathf.Clamp01(rad - d + 0.5f);
                    t.SetPixel(x, y, c);
                }
            t.Apply();
            return t;
        }

        public static Texture2D IconTexture(int kind) { EnsureTex(); return kind == 0 ? iPrev : kind == 1 ? iNext : kind == 2 ? iPlay : kind == 3 ? iPause : iVol; }
    }

    /// <summary>Odtwarzacz muzyki jako panel DeltaUI (przycisk na pasku aplikacji KSP, w locie i na mapie).</summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class MediaAddon : MonoBehaviour
    {
        private const string PanelId = "osmroads.music";
        private DeltaUI.LauncherButton appButton;

        public void Awake() { DontDestroyOnLoad(gameObject); }

        private bool show { get { return DeltaUI.Dock.IsOpen(PanelId); } }

        public void Start()
        {
            OsmSettings.EnsureLoaded();
            DeltaUI.Dock.Register(PanelId, "Music", DrawPanel, new Rect(Screen.width - 500f, 120f, 460f, 180f), false, AppIcons.Music, 30);
            DeltaUI.Dock.OpenChanged += OnPanelOpenChanged;
            var scenes = KSP.UI.Screens.ApplicationLauncher.AppScenes.SPACECENTER | KSP.UI.Screens.ApplicationLauncher.AppScenes.FLIGHT |
                         KSP.UI.Screens.ApplicationLauncher.AppScenes.MAPVIEW | KSP.UI.Screens.ApplicationLauncher.AppScenes.VAB |
                         KSP.UI.Screens.ApplicationLauncher.AppScenes.SPH | KSP.UI.Screens.ApplicationLauncher.AppScenes.TRACKSTATION;
            appButton = new DeltaUI.LauncherButton(AppIcons.Music,
                () => { DeltaUI.Dock.SetOpen(PanelId, true); MediaClient.EnsureStarted(); },
                () => DeltaUI.Dock.SetOpen(PanelId, false), scenes);
            appButton.Sync(show);
            if (OsmSettings.MusicWidget || show) MediaClient.EnsureStarted();
        }

        private void OnPanelOpenChanged(string id, bool open)
        {
            if (id != PanelId) return;
            if (appButton != null) appButton.Sync(open);
            if (open) MediaClient.EnsureStarted();
        }

        public void OnDestroy()
        {
            DeltaUI.Dock.OpenChanged -= OnPanelOpenChanged;
            DeltaUI.Dock.Unregister(PanelId);
            if (appButton != null) appButton.Destroy();
        }

        public void Update()
        {
            if (show || OsmSettings.MusicWidget) MediaClient.EnsureStarted();
        }

        /// <summary>DrawFull rysuje prostym GUI (nie GUILayout), wiec w przebiegu Layout wystarczy zarezerwowac miejsce.</summary>
        private void DrawPanel()
        {
            Rect r = GUILayoutUtility.GetRect(100f, 60f, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            if (Event.current.type == EventType.Layout) return;
            MediaUI.DrawFull(r, 1f, false);
        }
    }
}