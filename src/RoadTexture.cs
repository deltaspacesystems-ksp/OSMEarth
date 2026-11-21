using UnityEngine;

namespace OSMRoads
{
    /// <summary>
    /// Proceduralne tekstury nawierzchni, jedna na klase drogi. Generowane
    /// w runtime, wiec nie ma zadnych plikow do dystrybucji.
    ///
    /// Uklad UV wstegi: u biegnie W POPRZEK jezdni (0 = lewa krawedz, 1 = prawa),
    /// v biegnie WZDLUZ i powtarza sie co RoadClasses.TileLengthM metrow. Stad
    /// dwie wlasciwosci, ktore razem daja poprawny wyglad:
    ///
    ///   - kreska przerywana ma zawsze te sama dlugosc w METRACH, bo v jest
    ///     mapowane metrycznie;
    ///   - linie krawedziowe skaluja sie z szerokoscia jezdni, bo u jest
    ///     znormalizowane. To zamierzone: autostrada naprawde ma szersze
    ///     oznakowanie niz uliczka osiedlowa.
    /// </summary>
    public static class RoadTexture
    {
        /// <summary>Szerokosc pasa pobocza na brzegach tekstury; jezdnia to U w [VergeU, 1-VergeU].</summary>
        public const float VergeU = 0.035f;

        private const int W = 128;    // w poprzek jezdni
        private const int H = 256;    // wzdluz, jeden cykl kreski

        private static Texture2D[] cache;

        public static Texture2D For(RoadClass c)
        {
            Ensure();
            return cache[(int)c];
        }

        private static void Ensure()
        {
            if (cache != null) return;
            cache = new Texture2D[RoadMeshBuilder.Submeshes];
            for (int i = 0; i < RoadClasses.Count; i++)
                cache[i] = Bake((RoadClass)i);
            for (int i = RoadClasses.Count; i < cache.Length; i++)
                cache[i] = i == RoadMeshBuilder.SubPlainMotorway || i == RoadMeshBuilder.SubPlainMajor || i == RoadMeshBuilder.SubPlainRunway
                    ? Bake(RoadMeshBuilder.PlainClass(i), false)
                    : i == RoadMeshBuilder.SubLampPool ? null : BakeStreetside(i);
        }

        /// <summary>
        /// Tekstury pobocza ulicy, mapowane w metrach: U = 2 m w poprzek, V = 4 m wzdluz
        /// (StreetsideBuilder). Kraweznik: jasny beton z fugami co metr. Kostka: szara
        /// "cegielka" 20x10 cm w wiazaniu, z lekko rozna barwa kamieni. Trawa: zielen z plamami.
        /// </summary>
        private static Texture2D BakeStreetside(int sub)
        {
            var pix = new Color32[W * H];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    uint h = Hash((uint)x + 7919u * (uint)sub, (uint)y);
                    float n = 0.9f + 0.2f * SmoothNoise(x, y, 32);
                    float r, g, b;
                    if (sub == RoadMeshBuilder.SubCurb)
                    {
                        float k = (0.93f + (h % 12u) / 100f) * n;
                        bool joint = (y % 64) == 0;                       // co 1 m
                        r = 176 * k; g = 174 * k; b = 166 * k;
                        if (joint) { r *= 0.6f; g *= 0.6f; b *= 0.6f; }
                    }
                    else if (sub == RoadMeshBuilder.SubPaving)
                    {
                        // 64 px = 1 m: kamien 13 x 6.5 px, co drugi rzad przesuniety o pol kamienia
                        int row = y / 13;
                        int xs = x + ((row & 1) != 0 ? 3 : 0);
                        int col = xs / 7;
                        bool joint = (y % 13) == 0 || (xs % 7) == 0;
                        uint st = Hash((uint)col + 31u, (uint)row + 17u);
                        float tone = 0.86f + (st % 22u) / 100f;
                        float grain = 0.95f + (h % 10u) / 100f;
                        r = 150 * tone * grain * n; g = 146 * tone * grain * n; b = 140 * tone * grain * n;
                        if (joint) { r *= 0.62f; g *= 0.62f; b *= 0.6f; }
                    }
                    else if (sub == RoadMeshBuilder.SubConcrete)
                    {
                        // plyty betonowe: jasnoszare, poprzeczne fugi co 5 m (ok. 160 px przy 8 m na teksture) i fuga w osi
                        float k = (0.92f + (h % 10u) / 100f) * n;
                        bool joint = (y % 160) < 2 || Mathf.Abs(x - W / 2) < 1;
                        r = 150 * k; g = 150 * k; b = 146 * k;
                        if (joint) { r *= 0.55f; g *= 0.55f; b *= 0.55f; }
                    }
                    else if (sub == RoadMeshBuilder.SubSett)
                    {
                        // bruk / kocie lby: kamienie ok. 12 px, zaokraglone, rozne odcienie
                        int cx = x / 12, cy = y / 12;
                        uint st = Hash((uint)cx + 91u, (uint)cy + 13u);
                        float dx = (x % 12) - 5.5f, dy = (y % 12) - 5.5f;
                        float d = Mathf.Sqrt(dx * dx + dy * dy) / 7f;
                        float tone = 0.75f + (st % 30u) / 100f;
                        float shade = d > 0.85f ? 0.45f : 1f - 0.25f * d;
                        r = 118 * tone * shade; g = 114 * tone * shade; b = 108 * tone * shade;
                    }
                    else if (sub == RoadMeshBuilder.SubGravel)
                    {
                        // szuter / zuzel: szare kamyki na jasniejszym tle, dwie koleiny
                        float k = (0.75f + (h % 40u) / 100f) * (0.85f + 0.3f * SmoothNoise(x, y, 8));
                        float u = (x + 0.5f) / W;
                        float track = Mathf.Min(Mathf.Abs(u - 0.3f), Mathf.Abs(u - 0.7f)) < 0.07f ? 0.9f : 1f;
                        r = 128 * k * track; g = 124 * k * track; b = 116 * k * track;
                    }
                    else if (sub == RoadMeshBuilder.SubDirt)
                    {
                        float k = (0.8f + (h % 30u) / 100f) * (0.8f + 0.4f * SmoothNoise(x, y, 16));
                        float u = (x + 0.5f) / W;
                        bool grass = Mathf.Abs(u - 0.5f) < 0.1f && (h % 5u) != 0;        // trawa miedzy koleinami
                        r = grass ? 72 * k : 112 * k; g = grass ? 96 * k : 88 * k; b = grass ? 46 * k : 62 * k;
                    }
                    else if (sub == RoadMeshBuilder.SubFence)
                    {
                        // plot z desek / sztachet: pionowe deski 16 px z przerwami
                        bool gap = (x % 16) >= 13;
                        float k = 0.85f + (Hash((uint)(x / 16), 7u) % 20u) / 100f;
                        r = gap ? 40 : 96 * k; g = gap ? 38 : 72 * k; b = gap ? 34 : 50 * k;
                    }
                    else if (sub == RoadMeshBuilder.SubHedge)
                    {
                        float k = (0.7f + (h % 50u) / 100f) * (0.8f + 0.4f * SmoothNoise(x, y, 8));
                        r = 44 * k; g = 86 * k; b = 36 * k;
                    }
                    else if (sub == RoadMeshBuilder.SubWall)
                    {
                        // mur z cegly / pustakow
                        int row = y / 10; int xs = x + ((row & 1) != 0 ? 11 : 0);
                        bool joint = (y % 10) == 0 || (xs % 22) == 0;
                        uint st = Hash((uint)(xs / 22), (uint)row);
                        float tone = 0.85f + (st % 20u) / 100f;
                        r = 150 * tone; g = 142 * tone; b = 130 * tone;
                        if (joint) { r *= 0.6f; g *= 0.6f; b *= 0.6f; }
                    }
                    else
                    {
                        float k = (0.85f + (h % 30u) / 100f) * (0.8f + 0.4f * SmoothNoise(x, y, 16));
                        r = 70 * k; g = 104 * k; b = 44 * k;
                    }
                    pix[y * W + x] = new Color32(Clamp(r), Clamp(g), Clamp(b), 255);
                }
            var t = new Texture2D(W, H, TextureFormat.RGB24, true);
            t.SetPixels32(pix);
            t.Apply(true);
            t.wrapMode = TextureWrapMode.Repeat;
            t.filterMode = FilterMode.Bilinear;
            t.anisoLevel = 9;
            return t;
        }

        private static Texture2D Bake(RoadClass c, bool markings = true)
        {
            // --- parametry klasy ---
            byte br, bg, bb;          // baza nawierzchni
            float edgeU;              // szerokosc linii krawedziowej w ulamku jezdni, 0 = brak
            bool centreDash;          // przerywana os jezdni
            bool gravel;              // ziarno grunt/tluczen zamiast asfaltu
            float dashW = 0.013f;     // polszerokosc osi
            bool yellowAxis = false;  // ciagla zolta os (droga kolowania)

            switch (c)
            {
                case RoadClass.Motorway:
                    br = 72; bg = 73; bb = 78; edgeU = 0.030f; centreDash = true; gravel = false; break;
                case RoadClass.Major:
                    br = 84; bg = 85; bb = 88; edgeU = 0.026f; centreDash = true; gravel = false; break;
                case RoadClass.Minor:
                    br = 92; bg = 92; bb = 94; edgeU = 0f; centreDash = false; gravel = false; break;
                case RoadClass.Service:
                    br = 98; bg = 97; bb = 94; edgeU = 0f; centreDash = false; gravel = false; break;
                case RoadClass.Runway:
                    br = 70; bg = 71; bb = 74; edgeU = 0.010f; centreDash = true; gravel = false; dashW = 0.008f; break;
                case RoadClass.Taxiway:
                    br = 80; bg = 80; bb = 82; edgeU = 0f; centreDash = false; gravel = false; yellowAxis = true; dashW = 0.012f; break;
                default:
                    br = 98; bg = 86; bb = 68; edgeU = 0f; centreDash = false; gravel = true; break;
            }

            var pix = new Color32[W * H];

            for (int y = 0; y < H; y++)
            {
                float v = (float)y / H;

                for (int x = 0; x < W; x++)
                {
                    float u = (x + 0.5f) / W;
                    int i = y * W + x;

                    // --- ziarno nawierzchni ---
                    uint h = Hash((uint)x, (uint)y);
                    // Ziarno pojedynczego piksela. Dla tluczna slabsze niz moglby
                    // sugerowac material: sam szum wysokiej czestotliwosci migocze
                    // przy oddaleniu, wieksza czesc kontrastu bierze sie z plam.
                    float grain = gravel
                        ? 0.84f + (h % 32u) / 100f
                        : 0.90f + (h % 20u) / 100f;

                    // Wieksze plamy: lata po remontach, rozne partie masy.
                    // Szum INTERPOLOWANY, nie blokowy: przy 128 px szerokosci
                    // oczko 32 px dawaloby raptem cztery kolumny, a ostre granice
                    // miedzy nimi czytaly sie jak krata kafelkow, nie jak asfalt.
                    int patchCell = gravel ? 16 : 32;
                    float patchAmp = gravel ? 0.26f : 0.13f;
                    float patch = (1f - patchAmp * 0.5f) + patchAmp * SmoothNoise(x, y, patchCell);

                    // Koleiny: dwa pasy wypolerowane oponami, ciemniejsze i gladsze.
                    // Bez nich jezdnia czyta sie jak rownomierna szara tasma.
                    float track = 1f;
                    if (!gravel)
                    {
                        float d1 = Mathf.Abs(u - 0.29f);
                        float d2 = Mathf.Abs(u - 0.71f);
                        float d = Mathf.Min(d1, d2);
                        if (d < 0.10f) track = 0.90f + 0.10f * (d / 0.10f);
                    }

                    // Pobocze: ciemniejszy pas tuz przy krawedzi. Oddziela jezdnie
                    // od terenu, dzieki czemu wstega nie wtapia sie w trawe.
                    float shoulder = 1f;
                    float edgeDist = Mathf.Min(u, 1f - u);
                    if (edgeDist < 0.05f) shoulder = 0.62f + 0.38f * (edgeDist / 0.05f);

                    float k = grain * patch * track * shoulder;
                    byte r = Clamp(br * k), g = Clamp(bg * k), b = Clamp(bb * k);

                    // --- oznakowanie poziome ---
                    bool paint = false;

                    if (edgeU > 0f && (edgeDist > 0.055f) && edgeDist < 0.055f + edgeU)
                        paint = true;

                    if (centreDash && Mathf.Abs(u - 0.5f) < dashW && v < 0.375f)
                        paint = true;    // 3 m kreski na 8 m cyklu
                    if (markings && yellowAxis && Mathf.Abs(u - 0.5f) < dashW)
                    {
                        float wy = 0.80f + (Hash((uint)x, (uint)(y + 577u)) % 20u) / 100f;
                        r = Clamp(214 * wy); g = Clamp(170 * wy); b = Clamp(40 * wy);
                    }

                    if (!markings) paint = false;
                    if (paint)
                    {
                        // Farba jest zuzyta - przepuszcza troche asfaltu spod spodu.
                        float wear = 0.80f + (Hash((uint)x, (uint)(y + 991u)) % 20u) / 100f;
                        r = Clamp(228 * wear); g = Clamp(226 * wear); b = Clamp(212 * wear);
                    }

                    // Pas pobocza (U < VergeU) - wstega zaczyna sie dopiero od VergeU. Tu siedza
                    // pochyle pobocza wchodzace pod teren: szary zwir/ziemia zamiast czarnego
                    // asfaltu, zeby wystajace z trawy kawalki nie wygladaly jak rozlana smola.
                    if (u < VergeU || u > 1f - VergeU)
                    {
                        float kv = (0.86f + (h % 24u) / 100f) * (0.9f + 0.2f * SmoothNoise(x, y, 16));
                        r = Clamp(112 * kv); g = Clamp(106 * kv); b = Clamp(92 * kv);
                    }

                    pix[i] = new Color32(r, g, b, 255);
                }
            }

            var t = new Texture2D(W, H, TextureFormat.RGB24, true);
            t.SetPixels32(pix);
            t.Apply(true);
            t.wrapMode = TextureWrapMode.Repeat;
            t.filterMode = FilterMode.Bilinear;
            // Drogi ogladamy pod bardzo ostrym katem - anizotropia decyduje tu
            // o czytelnosci bardziej niz rozdzielczosc.
            t.anisoLevel = 9;
            return t;
        }

        /// <summary>
        /// Szum wartosciowy o oczku "cell" pikseli, interpolowany wygladzajaco
        /// (smoothstep) i ZAWIJANY na krawedziach tekstury - inaczej powtarzana
        /// wstega mialaby widoczny szew co osiem metrow.
        /// </summary>
        private static float SmoothNoise(int x, int y, int cell)
        {
            int nx = W / cell, ny = H / cell;
            int gx = x / cell, gy = y / cell;

            float fx = (x % cell) / (float)cell;
            float fy = (y % cell) / (float)cell;
            fx = fx * fx * (3f - 2f * fx);
            fy = fy * fy * (3f - 2f * fy);

            float v00 = Lattice(gx, gy, nx, ny);
            float v10 = Lattice(gx + 1, gy, nx, ny);
            float v01 = Lattice(gx, gy + 1, nx, ny);
            float v11 = Lattice(gx + 1, gy + 1, nx, ny);

            float a = v00 + (v10 - v00) * fx;
            float b = v01 + (v11 - v01) * fx;
            return a + (b - a) * fy;
        }

        private static float Lattice(int gx, int gy, int nx, int ny)
        {
            return (Hash((uint)(gx % nx), (uint)(gy % ny)) % 1024u) / 1023f;
        }

        private static byte Clamp(float v)
        {
            return (byte)Mathf.Clamp(v, 0f, 255f);
        }

        private static uint Hash(uint x, uint y)
        {
            uint h = x * 374761393u + y * 668265263u;
            h = (h ^ (h >> 13)) * 1274126177u;
            return h ^ (h >> 16);
        }
    }
}
