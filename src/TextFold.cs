using System;
using System.Globalization;
using System.Text;

namespace OSMRoads
{
    /// <summary>
    /// Normalizacja tekstu do wyszukiwania: male litery, bez znakow diakrytycznych,
    /// interpunkcja i myslniki zamienione na pojedyncze spacje. Dzieki temu "Lodz",
    /// "Łódź" i "lodz" sa tym samym, a "Saint-Denis" pasuje do "saint denis".
    ///
    /// Pismo nielacinskie (cyrylica, CJK) zostaje jak jest, tylko z malymi literami -
    /// wyszukiwanie po lokalnej nazwie dziala, a nazwy angielskie duzych miast siedza
    /// osobno w cities.bin.
    /// </summary>
    public static class TextFold
    {
        public static string Fold(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";

            string d;
            try { d = s.ToLowerInvariant().Normalize(NormalizationForm.FormD); }
            catch (Exception) { d = s.ToLowerInvariant(); }

            var sb = new StringBuilder(d.Length);
            bool space = true;
            foreach (char c in d)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;

                string rep = null;
                switch (c)
                {
                    case 'ł': rep = "l"; break;      // ł
                    case 'ø': rep = "o"; break;      // ø
                    case 'đ': rep = "d"; break;      // đ
                    case 'ð': rep = "d"; break;      // ð
                    case 'ß': rep = "ss"; break;     // ß
                    case 'æ': rep = "ae"; break;     // æ
                    case 'œ': rep = "oe"; break;     // œ
                    case 'ı': rep = "i"; break;      // ı
                    case 'þ': rep = "th"; break;     // þ
                }
                if (rep != null) { sb.Append(rep); space = false; continue; }

                // Zapas, gdy Normalize nie rozklada znakow (tryb InvariantGlobalization itp.).
                int k = c >= 0xE0 && c < 0x250 ? Plain.IndexOf(c) : -1;
                if (k >= 0) { sb.Append(Base[k]); space = false; continue; }

                if (char.IsLetterOrDigit(c)) { sb.Append(c); space = false; }
                else if (!space) { sb.Append(' '); space = true; }
            }
            return sb.ToString().TrimEnd();
        }

        private const string Plain = "àáâãäåçèéêëìíîïñòóôõöùúûüýÿāăąćĉċčďēĕėęěĝğġģĥīĭįķĺļľńņňōŏőŕŗřśŝşšţťũūŭůűųŵŷźżžșț";
        private const string Base  = "aaaaaaceeeeiiiinooooouuuuyyaaaccccdeeeeegggghiiiklllnnnooorrrssssttuuuuuuwyzzzst";

        /// <summary>Slowa zapytania po normalizacji (puste slowa odpadaja).</summary>
        public static string[] Words(string folded)
        {
            return folded.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        }
    }
}
