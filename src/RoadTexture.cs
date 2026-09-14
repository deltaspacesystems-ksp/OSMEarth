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
            cache = new Texture2D[RoadClasses.Count];
            for (int i = 0; i < RoadClasses.Count; i++)
                cache[i] = Bake((RoadClass)i);
        }

        private static Texture2D Bake(RoadClass c)
        {
            // --- parametry klasy ---
            byte br, bg, bb;          // baza nawierzchni
            float edgeU;              // szerokosc linii krawedziowej w ulamku jezdni, 0 = brak
            bool centreDash;          // przerywana os jezdni
            bool gravel;              // ziarno grunt/tluczen zamiast asfaltu

            switch (c)
            {
                case RoadClass.Motorway:
                    br = 46; bg = 47; bb = 52; edgeU = 0.030f; centreDash = true; gravel = false; break;
                case RoadClass.Major:
                    br = 53; bg = 54; bb = 58; edgeU = 0.026f; centreDash = true; gravel = false; break;
                case RoadClass.Minor:
                    br = 60; bg = 60; bb = 62; edgeU = 0f; centreDash = false; gravel = false; break;
                case RoadClass.Service:
                    br = 76; bg = 75; bb = 72; edgeU = 0f; centreDash = false; gravel = false; break;
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

                    if (centreDash && Mathf.Abs(u - 0.5f) < 0.013f && v < 0.375f)
                        paint = true;    // 3 m kreski na 8 m cyklu

                    if (paint)
                    {
                        // Farba jest zuzyta - przepuszcza troche asfaltu spod spodu.
                        float wear = 0.80f + (Hash((uint)x, (uint)(y + 991u)) % 20u) / 100f;
                        r = Clamp(228 * wear); g = Clamp(226 * wear); b = Clamp(212 * wear);
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
