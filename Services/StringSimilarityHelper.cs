using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CatastroFixer.Services
{
    public static class StringSimilarityHelper
    {
        private static readonly HashSet<string> StopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "el", "la", "los", "las", "de", "del", "al", "y", "en"
        };

        public static string NormalizeText(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return "";

            string s = input.Trim().ToUpperInvariant();

            // Remover nombres o abreviaturas de Estado
            s = Regex.Replace(s, @"\b(?:CHIAPAS|CHIS\.?|ESTADO DE CHIAPAS)\b", "").Trim();
            // Reemplazar signos de puntuación por espacios
            s = Regex.Replace(s, @"[,\.;\-_\/]+", " ");

            // Normalizar abreviaturas administrativas comunes en Chiapas (rescatado de CatastroMover3000)
            if (Regex.IsMatch(s, @"^(?:TGZ|T\s*G\s*Z|T\s*G|TUXTLA\s+GTZ|TUXTLA\s+G|TUXTLA)$", RegexOptions.IgnoreCase) && !s.Contains("CHICO"))
            {
                s = "TUXTLA GUTIERREZ";
            }
            s = Regex.Replace(s, @"\b(?:T\.G\.Z\.?|T\.G\.?|TGZ|TUXTLA\s+GTZ|TUXTLA\s+G)\b", "TUXTLA GUTIERREZ");
            s = Regex.Replace(s, @"\b(?:GTZ|GUTZ)\b", "GUTIERREZ");
            s = Regex.Replace(s, @"\bCD\b", "CIUDAD");
            s = Regex.Replace(s, @"\bSCLC\b", "SAN CRISTOBAL DE LAS CASAS");
            s = Regex.Replace(s, @"\bPTO\b", "PUERTO");
            s = Regex.Replace(s, @"\bSTA\b", "SANTA");
            s = Regex.Replace(s, @"\bSTO\b", "SANTO");
            s = Regex.Replace(s, @"\bSN\b", "SAN");
            s = Regex.Replace(s, @"\bVLLA\b", "VILLA");
            s = Regex.Replace(s, @"\bV\s+FLORES\b", "VILLAFLORES");

            string normalized = s.ToLowerInvariant().Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder();
            foreach (char c in normalized)
            {
                var uc = CharUnicodeInfo.GetUnicodeCategory(c);
                if (uc != UnicodeCategory.NonSpacingMark && (char.IsLetterOrDigit(c) || char.IsWhiteSpace(c)))
                {
                    sb.Append(c);
                }
            }

            return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
        }

        public static string NormalizeWithoutArticles(string input)
        {
            string norm = NormalizeText(input);
            if (string.IsNullOrEmpty(norm)) return "";
            var tokens = norm.Split(' ').Where(t => !string.IsNullOrEmpty(t) && !StopWords.Contains(t));
            return string.Join(" ", tokens);
        }

        public static int LevenshteinDistance(string s, string t)
        {
            if (string.IsNullOrEmpty(s)) return string.IsNullOrEmpty(t) ? 0 : t.Length;
            if (string.IsNullOrEmpty(t)) return s.Length;

            int[,] d = new int[s.Length + 1, t.Length + 1];
            for (int i = 0; i <= s.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= t.Length; j++) d[0, j] = j;

            for (int j = 1; j <= t.Length; j++)
            {
                for (int i = 1; i <= s.Length; i++)
                {
                    int cost = (s[i - 1] == t[j - 1]) ? 0 : 1;
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                }
            }
            return d[s.Length, t.Length];
        }

        public static double CalculateSimilarity(string s1, string s2)
        {
            string n1 = NormalizeText(s1);
            string n2 = NormalizeText(s2);
            if (string.IsNullOrEmpty(n1) || string.IsNullOrEmpty(n2)) return 0.0;
            if (n1 == n2) return 1.0;

            string a1 = NormalizeWithoutArticles(s1);
            string a2 = NormalizeWithoutArticles(s2);
            if (!string.IsNullOrEmpty(a1) && a1 == a2) return 0.98;

            if (n1.Contains(n2) || n2.Contains(n1)) return 0.92;

            int maxLen = Math.Max(n1.Length, n2.Length);
            double levFull = 1.0 - ((double)LevenshteinDistance(n1, n2) / maxLen);

            if (!string.IsNullOrEmpty(a1) && !string.IsNullOrEmpty(a2))
            {
                int maxArtLen = Math.Max(a1.Length, a2.Length);
                double levArt = 1.0 - ((double)LevenshteinDistance(a1, a2) / maxArtLen);
                return Math.Max(levFull, levArt);
            }

            return levFull;
        }
    }
}
