using System;
using System.Collections.Generic;
using System.Text;

namespace <APP>
{
    // Phonetic transliteration (not layout conversion).
    internal static class Translit
    {
        private static readonly Dictionary<char, string> Cyr2Lat = new Dictionary<char, string>();
        private static readonly List<KeyValuePair<string, string>> Lat2Cyr = new List<KeyValuePair<string, string>>();

        static Translit()
        {
            // Ukrainian national romanization (works for Russian letters too).
            string[,] map = {
                {"а","a"},{"б","b"},{"в","v"},{"г","h"},{"ґ","g"},{"д","d"},{"е","e"},{"є","ie"},
                {"ж","zh"},{"з","z"},{"и","y"},{"і","i"},{"ї","i"},{"й","i"},{"к","k"},{"л","l"},
                {"м","m"},{"н","n"},{"о","o"},{"п","p"},{"р","r"},{"с","s"},{"т","t"},{"у","u"},
                {"ф","f"},{"х","kh"},{"ц","ts"},{"ч","ch"},{"ш","sh"},{"щ","shch"},{"ь",""},{"ю","iu"},
                {"я","ia"},{"ъ",""},{"ы","y"},{"э","e"},{"ё","e"}
            };
            for (int i = 0; i < map.GetLength(0); i++)
                Cyr2Lat[map[i, 0][0]] = map[i, 1];

            // Latin -> Cyrillic (Ukrainian). Longer sequences first.
            string[,] rev = {
                {"shch","щ"},{"zh","ж"},{"kh","х"},{"ts","ц"},{"ch","ч"},{"sh","ш"},{"ie","є"},
                {"iu","ю"},{"ia","я"},{"yi","ї"},
                {"a","а"},{"b","б"},{"v","в"},{"h","г"},{"g","ґ"},{"d","д"},{"e","е"},{"z","з"},
                {"y","и"},{"i","і"},{"k","к"},{"l","л"},{"m","м"},{"n","н"},{"o","о"},{"p","п"},
                {"r","р"},{"s","с"},{"t","т"},{"u","у"},{"f","ф"},{"c","к"},{"j","й"},{"q","к"},
                {"w","в"},{"x","кс"}
            };
            for (int i = 0; i < rev.GetLength(0); i++)
                Lat2Cyr.Add(new KeyValuePair<string, string>(rev[i, 0], rev[i, 1]));
        }

        public static string CyrToLat(string s)
        {
            var sb = new StringBuilder(s.Length * 2);
            foreach (char c in s)
            {
                char low = char.ToLowerInvariant(c);
                string t;
                if (Cyr2Lat.TryGetValue(low, out t))
                {
                    if (char.IsUpper(c) && t.Length > 0)
                        sb.Append(char.ToUpperInvariant(t[0])).Append(t.Substring(1));
                    else sb.Append(t);
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        public static string LatToCyr(string s)
        {
            var sb = new StringBuilder(s.Length);
            int i = 0;
            while (i < s.Length)
            {
                bool matched = false;
                foreach (var kv in Lat2Cyr)
                {
                    string key = kv.Key;
                    if (i + key.Length <= s.Length &&
                        string.Compare(s, i, key, 0, key.Length, StringComparison.OrdinalIgnoreCase) == 0)
                    {
                        bool upper = char.IsUpper(s[i]);
                        string val = kv.Value;
                        if (upper && val.Length > 0)
                            sb.Append(char.ToUpperInvariant(val[0])).Append(val.Substring(1));
                        else sb.Append(val);
                        i += key.Length;
                        matched = true;
                        break;
                    }
                }
                if (!matched) { sb.Append(s[i]); i++; }
            }
            return sb.ToString();
        }
    }
}
