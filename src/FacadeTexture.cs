using System;
using UnityEngine;

namespace OSMRoads
{
    /// <summary>
    /// Proceduralne tekstury elewacji generowane w runtime - zero plikow do dystrybucji.
    ///
    /// Albedo jest celowo prawie biale w miejscu sciany, zeby kolor budynku dalo sie
    /// ustawic przez _Color materialu. Szyby zostaja ciemne niezaleznie od odcienia.
    ///
    /// Wzor swiadomie NIE jest regularna krata: czesc kolumn jest slepa (pilastry,
    /// sciany szczytowe), a jasnosc i swiecenie zmieniaja sie per okno. Regularna
    /// krata to wlasnie to, co daje wrazenie "backrooms".
    /// </summary>
    public static class FacadeTexture
    {
        public const int Size = 1024;
        public const int Cells = 16;          // 16x16 okien na kafel -> petla widoczna 2x rzadziej
        public const float WindowSpacingM = 3.5f;
        public const float FloorHeightM = 3.0f;

        // Jedna jednostka UV to CALY kafel tekstury, czyli Cells okien - nie jedno.
        // Pominiecie tego mnoznika dawalo okna Cells razy za geste.
        public const float UvPerMeterU = 1f / (WindowSpacingM * Cells);
        public const float UvPerMeterV = 1f / (FloorHeightM * Cells);

        // --- parter ---
        public const float PlinthHeightM = 4.0f;      // wysokosc pasa parteru
        public const float PlinthMinBuildingM = 6.0f; // ponizej tego budynek nie dostaje parteru
        public const int PlinthCells = 4;             // witryn w poziomie na kafel
        public const float PlinthSpacingM = 6.0f;
        public const float PlinthUvPerMeterU = 1f / (PlinthSpacingM * PlinthCells);

        // --- dachy ---
        public const float ParapetHeightM = 0.8f;    // attyka nad plaskim dachem
        public const float RoofTileM = 14f;          // metry na jedno powtorzenie tekstury papy
        public const float RoofPitchTileM = 5f;      // metry na powtorzenie dachowki
        public const float RoofUvPerMeter = 1f / RoofTileM;
        public const float RoofPitchUvPerMeter = 1f / RoofPitchTileM;

        /// <summary>Ponizej tej powierzchni i wysokosci budynek dostaje dach spadzisty,
        /// o ile obrys jest w miare prostokatny. Blokowisko plaskich pudelek to
        /// polowa wrazenia "to nie jest prawdziwe miasto".</summary>
        public const float PitchedMaxArea = 260f;
        public const float PitchedMaxHeight = 13f;

        private static Texture2D albedo, emission, plinthAlbedo, plinthEmission;
        private static Texture2D roofFlat, roofTiles;

        public static Texture2D RoofFlat { get { EnsureRoofs(); return roofFlat; } }
        public static Texture2D RoofTiles { get { EnsureRoofs(); return roofTiles; } }

        /// <summary>Papa z zwirem + jasniejsze laty i ciemne skrzynki wentylacji,
        /// oraz dachowka w poziome rzedy. Oba mapowane metrycznie, wiec nie rozciagaja sie.</summary>
        private static void EnsureRoofs()
        {
            if (roofFlat != null) return;

            const int S = 512;
            var flat = new Color32[S * S];
            var tiles = new Color32[S * S];

            for (int y = 0; y < S; y++)
            {
                for (int x = 0; x < S; x++)
                {
                    int i = y * S + x;

                    // --- papa: drobny szum zwiru + wieksze plamy ---
                    uint hp = Hash((uint)x, (uint)y);
                    float grain = 0.86f + (hp % 28u) / 100f;
                    float patch = 0.82f + 0.30f * SmoothNoise(x, y, 64, S);
                    byte b = (byte)Mathf.Clamp(150f * grain * patch, 0f, 255f);
                    flat[i] = new Color32(b, b, (byte)Mathf.Min(255, b + 6), 255);

                    // --- dachowka: poziome rzedy z przesunieciem co drugi rzad ---
                    int row = y / 26;
                    int shift = (row % 2) * 13;
                    int col = (x + shift) / 26;
                    bool groove = (y % 26) < 3 || ((x + shift) % 26) < 2;
                    uint ht = Hash((uint)col, (uint)row);
                    float tv = 0.80f + (ht % 40u) / 100f;
                    byte tr = (byte)Mathf.Clamp(150f * tv, 0f, 255f);
                    byte tg = (byte)Mathf.Clamp(88f * tv, 0f, 255f);
                    byte tb = (byte)Mathf.Clamp(72f * tv, 0f, 255f);
                    if (groove) { tr = (byte)(tr * 0.62f); tg = (byte)(tg * 0.62f); tb = (byte)(tb * 0.62f); }
                    tiles[i] = new Color32(tr, tg, tb, 255);
                }
            }

            // kilka skrzynek wentylacji / klimatyzacji na papie
            for (int k = 0; k < 7; k++)
            {
                uint h = Hash((uint)k, 99u);
                int bx = (int)(h % 440u), by = (int)((h >> 9) % 440u);
                int bw = 26 + (int)((h >> 17) % 40u), bh = 22 + (int)((h >> 23) % 34u);
                for (int y = by; y < by + bh && y < S; y++)
                    for (int x = bx; x < bx + bw && x < S; x++)
                    {
                        bool edge = x == bx || y == by || x == bx + bw - 1 || y == by + bh - 1;
                        byte v = edge ? (byte)70 : (byte)122;
                        flat[y * S + x] = new Color32(v, v, (byte)(v + 8), 255);
                    }
            }

            roofFlat = Make(S, flat, 4);
            roofTiles = Make(S, tiles, 4);
        }

        public static Texture2D Albedo { get { EnsureFacade(); return albedo; } }
        public static Texture2D Emission { get { EnsureFacade(); return emission; } }
        public static Texture2D PlinthAlbedo { get { EnsurePlinth(); return plinthAlbedo; } }
        public static Texture2D PlinthEmission { get { EnsurePlinth(); return plinthEmission; } }

        private static void EnsureFacade()
        {
            if (albedo != null) return;

            int cell = Size / Cells;
            var aPix = new Color32[Size * Size];
            var ePix = new Color32[Size * Size];

            int wx0 = (int)(cell * 0.20f), wx1 = (int)(cell * 0.80f);
            int wy0 = (int)(cell * 0.22f), wy1 = (int)(cell * 0.76f);

            for (int cy = 0; cy < Cells; cy++)
            {
                for (int cx = 0; cx < Cells; cx++)
                {
                    uint h = Hash((uint)cx, (uint)cy);

                    // Slepa kolumna: ~18% pionowych pasow bez okien. Rozbija krate
                    // i czyta sie jak pilaster albo sciana szczytowa.
                    bool blindColumn = (Hash((uint)cx, 7777u) % 100u) < 18u;

                    bool lit = !blindColumn && (h % 100u) < 22u;   // mniej swiatel niz wczesniej
                    float warm = 0.72f + ((h >> 7) % 60u) / 200f;
                    float dim = 0.45f + ((h >> 13) % 110u) / 200f;
                    float wallV = 0.84f + ((h >> 19) % 32u) / 200f;
                    float glass = 0.85f + ((h >> 23) % 40u) / 100f;

                    for (int y = 0; y < cell; y++)
                    {
                        for (int x = 0; x < cell; x++)
                        {
                            int i = (cy * cell + y) * Size + (cx * cell + x);
                            bool inWindow = !blindColumn &&
                                            x >= wx0 && x < wx1 && y >= wy0 && y < wy1;

                            // Rama okna 2 px, slupek i powiemie - bez nich okno z bliska
                            // bylo jednolitym czarnym prostokatem i wygladalo jak dziura.
                            bool frame = !blindColumn &&
                                         x >= wx0 - 2 && x < wx1 + 2 && y >= wy0 - 2 && y < wy1 + 2 &&
                                         !inWindow;
                            bool mullion = inWindow && (Math.Abs(x - (wx0 + wx1) / 2) < 1 ||
                                                        Math.Abs(y - (wy0 + (wy1 - wy0) * 65 / 100)) < 1);
                            bool sill = !blindColumn && y >= wy0 - 5 && y < wy0 - 2 && x >= wx0 - 4 && x < wx1 + 4;

                            if (inWindow && !mullion)
                            {
                                // Szyba odbija niebo: jasniej u gory, ciemniej u dolu.
                                // Pelna czern czytala sie z bliska jak otwor w scianie.
                                float gy = (y - wy0) / (float)(wy1 - wy0);
                                float tint = glass * (0.72f + 0.55f * gy);
                                aPix[i] = new Color32((byte)Math.Min(255f, 62 * tint), (byte)Math.Min(255f, 74 * tint),
                                                      (byte)Math.Min(255f, 92 * tint), 255);
                                ePix[i] = lit
                                    ? new Color32((byte)(255 * dim),
                                                  (byte)(255 * dim * warm),
                                                  (byte)(255 * dim * warm * 0.62f), 255)
                                    : new Color32(0, 0, 0, 255);
                            }
                            else if (mullion || frame)
                            {
                                aPix[i] = new Color32(214, 212, 206, 255);
                                ePix[i] = new Color32(0, 0, 0, 255);
                            }
                            else if (sill)
                            {
                                aPix[i] = new Color32(176, 174, 168, 255);
                                ePix[i] = new Color32(0, 0, 0, 255);
                            }
                            else
                            {
                                byte v = (byte)(255 * wallV);
                                aPix[i] = new Color32(v, v, v, 255);
                                ePix[i] = new Color32(0, 0, 0, 255);
                            }
                        }
                    }
                }
            }

            albedo = Make(Size, aPix, 4);
            emission = Make(Size, ePix, 1);
        }

        /// <summary>
        /// Parter: szerokie witryny zamiast okien, ciemniejszy cokol u dolu.
        /// v = 0 to grunt, v = 1 to gora pasa parteru - mapowane raz, bez powtarzania.
        /// </summary>
        private static void EnsurePlinth()
        {
            if (plinthAlbedo != null) return;

            const int W = 512, H = 256;
            int cell = W / PlinthCells;
            var aPix = new Color32[W * H];
            var ePix = new Color32[W * H];

            int socle = (int)(H * 0.16f);                 // cokol
            int gy0 = socle, gy1 = (int)(H * 0.80f);      // szyba witryny
            int gx0 = (int)(cell * 0.10f), gx1 = (int)(cell * 0.90f);

            for (int cx = 0; cx < PlinthCells; cx++)
            {
                uint h = Hash((uint)cx, 4242u);
                bool litShop = (h % 100u) < 45u;          // parter swieci czesciej niz mieszkania
                float dim = 0.55f + ((h >> 9) % 80u) / 200f;

                for (int y = 0; y < H; y++)
                {
                    for (int x = 0; x < cell; x++)
                    {
                        int i = y * W + (cx * cell + x);
                        bool glass = y >= gy0 && y < gy1 && x >= gx0 && x < gx1;

                        if (y < socle)
                        {
                            aPix[i] = new Color32(96, 94, 90, 255);      // cokol
                            ePix[i] = new Color32(0, 0, 0, 255);
                        }
                        else if (glass)
                        {
                            aPix[i] = new Color32(30, 36, 44, 255);
                            ePix[i] = litShop
                                ? new Color32((byte)(255 * dim), (byte)(245 * dim), (byte)(215 * dim), 255)
                                : new Color32(0, 0, 0, 255);
                        }
                        else
                        {
                            aPix[i] = new Color32(206, 203, 197, 255);
                            ePix[i] = new Color32(0, 0, 0, 255);
                        }
                    }
                }
            }

            plinthAlbedo = Make2(W, H, aPix, 4);
            plinthEmission = Make2(W, H, ePix, 1);
        }

        private static Texture2D Make(int size, Color32[] pix, int aniso)
        {
            return Make2(size, size, pix, aniso);
        }

        private static Texture2D Make2(int w, int h, Color32[] pix, int aniso)
        {
            var t = new Texture2D(w, h, TextureFormat.RGB24, true);
            t.SetPixels32(pix);
            t.Apply(true);
            t.wrapMode = TextureWrapMode.Repeat;
            t.filterMode = FilterMode.Bilinear;
            t.anisoLevel = aniso;
            return t;
        }

        /// <summary>Szum wartosciowy o oczku "cell", wygladzony i zawijany na
        /// krawedzi tekstury "size" - bez widocznych blokow i bez szwu.</summary>
        private static float SmoothNoise(int x, int y, int cell, int size)
        {
            int n = size / cell;
            int gx = x / cell, gy = y / cell;
            float fx = (x % cell) / (float)cell, fy = (y % cell) / (float)cell;
            fx = fx * fx * (3f - 2f * fx);
            fy = fy * fy * (3f - 2f * fy);
            float a = Lat(gx % n, gy % n), b = Lat((gx + 1) % n, gy % n);
            float c = Lat(gx % n, (gy + 1) % n), d = Lat((gx + 1) % n, (gy + 1) % n);
            float top = a + (b - a) * fx, bot = c + (d - c) * fx;
            return top + (bot - top) * fy;
        }

        private static float Lat(int x, int y)
        {
            return (Hash((uint)x, (uint)y + 5000u) % 1024u) / 1023f;
        }

        private static uint Hash(uint x, uint y)
        {
            uint h = x * 374761393u + y * 668265263u;
            h = (h ^ (h >> 13)) * 1274126177u;
            return h ^ (h >> 16);
        }
    }
}
