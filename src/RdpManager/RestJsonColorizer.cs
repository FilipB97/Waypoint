using System.Collections.Generic;

namespace RdpManager
{
    /// <summary>Rodzaj tokenu JSON — mapowany na kolor w podglądzie odpowiedzi (Compass §4.4).</summary>
    public enum RestJsonTok { Punct, Key, Str, Num, Keyword, Plain }

    /// <summary>
    /// Prosty tokenizer JSON do kolorowania odpowiedzi: dzieli tekst na segmenty (tekst + rodzaj) bez
    /// walidacji — działa też na częściowym/niepoprawnym JSON (wtedy „resztki" jako Plain). Klucz vs wartość
    /// łańcuchowa rozróżniane po następnym niebiałym znaku (':'). Czysty i testowalny (bez WPF).
    /// </summary>
    public static class RestJsonColorizer
    {
        public static List<(string Text, RestJsonTok Kind)> Tokenize(string json)
        {
            var outp = new List<(string, RestJsonTok)>();
            if (string.IsNullOrEmpty(json)) return outp;
            int i = 0, n = json.Length;
            void Emit(string s, RestJsonTok k) { if (s.Length > 0) outp.Add((s, k)); }

            while (i < n)
            {
                char c = json[i];
                if (c == '"')
                {
                    int start = i; i++;
                    while (i < n) { if (json[i] == '\\') { i += 2; continue; } if (json[i] == '"') { i++; break; } i++; }
                    if (i > n) i = n;
                    string str = json.Substring(start, i - start);
                    int j = i; while (j < n && char.IsWhiteSpace(json[j])) j++;
                    bool isKey = j < n && json[j] == ':';
                    Emit(str, isKey ? RestJsonTok.Key : RestJsonTok.Str);
                }
                else if (c == '-' || (c >= '0' && c <= '9'))
                {
                    int start = i; i++;
                    while (i < n && ("0123456789.eE+-".IndexOf(json[i]) >= 0)) i++;
                    Emit(json.Substring(start, i - start), RestJsonTok.Num);
                }
                else if (char.IsLetter(c))
                {
                    int start = i; i++;
                    while (i < n && char.IsLetter(json[i])) i++;
                    string w = json.Substring(start, i - start);
                    Emit(w, (w == "true" || w == "false" || w == "null") ? RestJsonTok.Keyword : RestJsonTok.Plain);
                }
                else if (c == '{' || c == '}' || c == '[' || c == ']' || c == ':' || c == ',')
                {
                    Emit(c.ToString(), RestJsonTok.Punct); i++;
                }
                else
                {
                    // białe znaki i inne — jednym segmentem, aż do następnego znaczącego znaku
                    int start = i; i++;
                    while (i < n)
                    {
                        char d = json[i];
                        if (d == '"' || d == '-' || (d >= '0' && d <= '9') || char.IsLetter(d)
                            || d == '{' || d == '}' || d == '[' || d == ']' || d == ':' || d == ',') break;
                        i++;
                    }
                    Emit(json.Substring(start, i - start), RestJsonTok.Plain);
                }
            }
            return outp;
        }
    }
}
