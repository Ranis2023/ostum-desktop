using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Sfera {
    public static class KeyboardLayout {
        const string Latin="qwertyuiop[]asdfghjkl;'zxcvbnm,./";
        const string Russian="йцукенгшщзхъфывапролджэячсмитьбю.";
        static readonly HashSet<string> common=new HashSet<string>(("а и в на с по у к за о из от для как что это такое значит работает мне мы я ты он она мой моё мои мою меня после того когда где чем если чтобы надо нужно хочу можно сделай сделаем сделайте сделать создай нарисуй посмотри просмотри расскажи покажи найди объясни проверь изучи помоги напиши переведи собери добавь дай запомни прям работу работы модель моделью ответ ответь ответы вопрос коротко кратко одним предложением картинку изображение интернет поиск агент агентов сфера сферы улучшить дизайн очень ещё все всё нам будет должен должна при этом затем про только уже ничего файл сайт слайд текст скажи сколько новый сейчас пожалуйста").Split(' '));
        static bool StrongSingleWord(string word){
            return word=="привет"||word=="здравствуйте"||word=="спасибо"||word=="продолжай";
        }
        static char ConvertKey(char c){
            int at=Latin.IndexOf(char.ToLowerInvariant(c));
            if(at>=0)return char.IsUpper(c)?char.ToUpperInvariant(Russian[at]):Russian[at];
            switch(c){
                case '\u0060':return 'ё';case '~':return 'Ё';case '{':return 'Х';case '}':return 'Ъ';
                case ':':return 'Ж';case '"':return 'Э';case '<':return 'Б';case '>':return 'Ю';
                case '?':return ',';case '@':return '"';case '#':return '№';case '$':return ';';case '^':return ':';case '&':return '?';
                default:return c;
            }
        }
        public static string Decode(string value){
            if(string.IsNullOrWhiteSpace(value)||value.Length>9000||Regex.IsMatch(value,@"\p{IsCyrillic}|https?://|www\.|[\w.+-]+@[\w.-]+\.\w+|[A-Z]:\\|=>|\x60{3}|\b(?:function|const|import|using|return|class|public|def|select)\b",RegexOptions.IgnoreCase))return value;
            string clean=Regex.Replace(value,@"(?<=[A-Za-z])\\+(?=\u0060)","");
            var b=new StringBuilder(clean.Length);
            foreach(char c in clean)b.Append(ConvertKey(c));
            string decoded=b.ToString();
            string[] words=Regex.Matches(decoded.ToLowerInvariant(),@"[а-яё]+").Cast<Match>().Select(m=>m.Value).ToArray();
            if(words.Length==1&&StrongSingleWord(words[0]))return decoded;
            var known=words.Where(common.Contains).Distinct().ToList();
            if(known.Count<2||!known.Any(w=>w.Length>=3))return value;
            int letters=words.Sum(w=>w.Length);
            int vowels=words.Sum(w=>w.Count(c=>"аеёиоуыэюя".Contains(c)));
            return letters>=9&&vowels>=letters*.20&&vowels<=letters*.68?decoded:value;
        }
    }
}
