using System.Globalization;
using TMPro;

namespace GemStore.Payment
{
    /// <summary>
    /// Formats prices in Thai baht for TextMeshPro labels.
    /// The default TMP font (Liberation Sans) has no baht sign, so when the font lacks the glyph the sign is drawn
    /// from a "B" with a vertical bar through it using rich text. If a Thai-capable font is later added as a TMP
    /// fallback, the real glyph is used automatically.
    /// </summary>
    public static class PriceFormatter
    {
        private const char BahtGlyph = '฿';
        private const string BahtComposite = "B<space=-0.464em>|<space=0.23em>";

        /// <summary>Price for a TMP label, for example "฿12,900". Pass the label's font so the glyph check matches.</summary>
        public static string Format(int baht, TMP_FontAsset font = null)
        {
            return BahtSign(font) + Digits(baht);
        }

        /// <summary>Plain text version (always the real baht sign) for logs and non-TMP use.</summary>
        public static string FormatPlain(int baht)
        {
            return BahtGlyph + Digits(baht);
        }

        private static string Digits(int baht)
        {
            return baht.ToString("N0", CultureInfo.InvariantCulture);
        }

        private static string BahtSign(TMP_FontAsset font)
        {
            font = font != null ? font : TMP_Settings.defaultFontAsset;
            bool supported = font != null && font.HasCharacter(BahtGlyph, true);
            return supported ? BahtGlyph.ToString() : BahtComposite;
        }
    }
}
