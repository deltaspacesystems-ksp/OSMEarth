using System;
using KSP.UI.Screens;
using UnityEngine;

namespace OSMRoads
{
    /// <summary>
    /// Przycisk moda na pasku aplikacji KSP (prawy gorny rog) - jeden na okno.
    ///
    /// Pasek bywa gotowy dopiero chwile po starcie sceny, wiec przycisk dodaje sie
    /// na zdarzenie onGUIApplicationLauncherReady, a nie w Start. Stan przycisku
    /// jest zsynchronizowany ze skrotem klawiszowym: otwarcie okna klawiszem
    /// podswietla przycisk i odwrotnie.
    /// </summary>
    public sealed class AppButton
    {
        private readonly Func<Texture2D> icon;
        private readonly Action onOpen, onClose;
        private readonly ApplicationLauncher.AppScenes scenes;
        private ApplicationLauncherButton button;

        public AppButton(Func<Texture2D> icon, Action onOpen, Action onClose,
                         ApplicationLauncher.AppScenes scenes)
        {
            this.icon = icon;
            this.onOpen = onOpen;
            this.onClose = onClose;
            this.scenes = scenes;
            GameEvents.onGUIApplicationLauncherReady.Add(OnReady);
            GameEvents.onGUIApplicationLauncherUnreadifying.Add(OnUnready);
            if (ApplicationLauncher.Ready) OnReady();
        }

        private void OnReady()
        {
            if (button != null || ApplicationLauncher.Instance == null) return;
            button = ApplicationLauncher.Instance.AddModApplication(
                () => onOpen(), () => onClose(), null, null, null, null, scenes, icon());
        }

        private void OnUnready(GameScenes scene)
        {
            Remove();
        }

        /// <summary>Ustawia wyglad przycisku bez wywolywania akcji - do synchronizacji ze skrotem.</summary>
        public void Sync(bool open)
        {
            if (button == null) return;
            if (open) button.SetTrue(false); else button.SetFalse(false);
        }

        private void Remove()
        {
            if (button != null && ApplicationLauncher.Instance != null)
                ApplicationLauncher.Instance.RemoveModApplication(button);
            button = null;
        }

        public void Destroy()
        {
            GameEvents.onGUIApplicationLauncherReady.Remove(OnReady);
            GameEvents.onGUIApplicationLauncherUnreadifying.Remove(OnUnready);
            Remove();
        }
    }

    /// <summary>Ikony 38x38 rysowane w kodzie - biale ksztalty na przezroczystym tle,
    /// jak ikony stockowe. Bez plikow graficznych do dystrybucji.</summary>
    public static class AppIcons
    {
        private const int S = 38;

        public static Texture2D Roads()
        {
            var px = New();
            // jezdnia w perspektywie: trapez zwezajacy sie ku gorze, z przerywana osia
            for (int y = 3; y < S - 3; y++)
            {
                float t = (y - 3) / (float)(S - 7);          // 0 dol, 1 gora
                float half = 15f - 9f * t;
                float cx = S / 2f;
                for (int x = 0; x < S; x++)
                {
                    float d = Mathf.Abs(x + 0.5f - cx);
                    if (d > half - 2.2f && d < half) Set(px, x, y, 1f);            // krawedzie
                    bool dash = (y / 5) % 2 == 0;
                    if (dash && d < 1.1f - 0.4f * t) Set(px, x, y, 0.9f);          // os
                }
            }
            return Make(px);
        }

        public static Texture2D Planner()
        {
            var px = New();
            // skladana mapa: trzy panele z zagieciami
            for (int y = 6; y < 32; y++)
                for (int x = 4; x < 34; x++)
                {
                    int panel = (x - 4) / 10;
                    int skew = panel == 1 ? -2 : 0;
                    int yy = y + skew;
                    if (yy < 6 || yy >= 32) continue;
                    bool edge = x == 4 || x == 33 || yy == 6 || yy == 31 || (x - 4) % 10 == 0;
                    Set(px, x, y, edge ? 1f : 0.28f);
                }
            // pinezka
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    float dx = x - 24.5f, dy = y - 22.5f;
                    if (dx * dx + dy * dy < 16f) Set(px, x, y, 1f);
                    if (Mathf.Abs(dx) < 1.2f && y > 13 && y <= 19) Set(px, x, y, 1f);
                }
            return Make(px);
        }

        public static Texture2D Globe()
        {
            var px = New();
            const float r = 15f, c = S / 2f;
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    float dx = x + 0.5f - c, dy = y + 0.5f - c;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    if (d > r) continue;
                    if (d > r - 1.8f) { Set(px, x, y, 1f); continue; }                 // obrys
                    if (Mathf.Abs(dy) < 0.8f || Mathf.Abs(dy - 7f) < 0.7f || Mathf.Abs(dy + 7f) < 0.7f)
                    { Set(px, x, y, 0.85f); continue; }                                  // rownolezniki
                    float ex = dx / Mathf.Sqrt(Mathf.Max(0.01f, 1f - (dy * dy) / (r * r)));
                    if (Mathf.Abs(dx) < 0.8f || Mathf.Abs(Mathf.Abs(ex) - 8f) < 1.0f)
                    { Set(px, x, y, 0.85f); continue; }                                  // poludniki
                    Set(px, x, y, 0.18f);
                }
            return Make(px);
        }

        private static Color32[] New() { return new Color32[S * S]; }

        private static void Set(Color32[] px, int x, int y, float a)
        {
            if (x < 0 || y < 0 || x >= S || y >= S) return;
            byte al = (byte)Mathf.Clamp(a * 255f, 0f, 255f);
            if (px[y * S + x].a < al) px[y * S + x] = new Color32(255, 255, 255, al);
        }

        private static Texture2D Make(Color32[] px)
        {
            var t = new Texture2D(S, S, TextureFormat.RGBA32, false);
            t.SetPixels32(px);
            t.Apply();
            return t;
        }
    }
}
