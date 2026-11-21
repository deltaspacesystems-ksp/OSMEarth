using UnityEngine;

namespace OSMRoads
{
    /// <summary>
    /// Uchwyt zmiany rozmiaru okna IMGUI w prawym dolnym rogu.
    ///
    /// IMGUI nie ma zmiany rozmiaru okien, wiec robimy ja sami: uchwyt rysowany w oknie
    /// zaczyna przeciaganie, a rozmiar liczymy z pozycji myszy w wspolrzednych EKRANU
    /// (Input.mousePosition), nie okna - inaczej rosnace okno przesuwaloby wlasny punkt
    /// odniesienia i rozmiar drgalby.
    ///
    /// Uzycie:
    ///   - w funkcji okna po narysowaniu zawartosci: grip.Draw(win);
    ///   - raz na klatke w Update: if (grip.Update(ref win, min, max)) { ... rozmiar sie zmienil ... }
    ///   - grip.Finished jest true w klatce, w ktorej puszczono mysz (zapisz rozmiar).
    /// </summary>
    public sealed class ResizeGrip
    {
        public const float Size = 18f;

        private bool active;
        private Vector2 startMouse, startSize;

        public bool Active { get { return active; } }
        public bool Finished { get; private set; }

        private static Vector2 ScreenMouse()
        {
            return new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
        }

        /// <summary>Rysuje uchwyt (trzy ukosne kreski) i przechwytuje klikniecie.
        /// win = rozmiar okna w tej chwili; wspolrzedne wewnatrz okna.</summary>
        public void Draw(Rect win)
        {
            var r = new Rect(win.width - Size - 2f, win.height - Size - 2f, Size, Size);

            Color prev = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, active ? 0.95f : 0.55f);
            for (int i = 0; i < 3; i++)
            {
                // kreski schodkowo: krotsze w rogu
                float len = 4f + i * 4f;
                GUI.DrawTexture(new Rect(r.xMax - 3f - len, r.yMax - 4f - i * 4f, len, 1.5f), Texture2D.whiteTexture);
            }
            GUI.color = prev;

            Event e = Event.current;
            if (e.type == EventType.MouseDown && e.button == 0 && r.Contains(e.mousePosition))
            {
                active = true;
                startMouse = ScreenMouse();
                startSize = new Vector2(win.width, win.height);
                e.Use();
            }
        }

        /// <summary>Wolac co klatke z Update. Zwraca true, gdy zmienil sie rozmiar okna.</summary>
        public bool Update(ref Rect win, Vector2 min, Vector2 max)
        {
            Finished = false;
            if (!active) return false;

            if (!Input.GetMouseButton(0))
            {
                active = false;
                Finished = true;
                return false;
            }

            Vector2 d = ScreenMouse() - startMouse;
            float w = Mathf.Clamp(startSize.x + d.x, min.x, Mathf.Max(min.x, max.x));
            float h = Mathf.Clamp(startSize.y + d.y, min.y, Mathf.Max(min.y, max.y));
            w = Mathf.Round(w / 4f) * 4f;
            h = Mathf.Round(h / 4f) * 4f;

            if (Mathf.Abs(w - win.width) < 0.5f && Mathf.Abs(h - win.height) < 0.5f) return false;
            win.width = w;
            win.height = h;
            return true;
        }

        /// <summary>Okno nie moze wystawac poza ekran po zmianie rozmiaru.</summary>
        public static void KeepOnScreen(ref Rect win)
        {
            win.x = Mathf.Clamp(win.x, -win.width + 80f, Screen.width - 80f);
            win.y = Mathf.Clamp(win.y, 0f, Screen.height - 40f);
        }
    }
}
