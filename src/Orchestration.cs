using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Sfera {
    public sealed class Route {
        public string answer {get;set;}
        public bool team {get;set;}
        public bool web {get;set;}
        public string query {get;set;}
        public bool reference {get;set;}
        public bool missingReference {get;set;}
    }
    public static class Orchestrator {
        static readonly string routePrompt=ReadRoutePrompt();
        static string ReadRoutePrompt(){
            using(var reader=new StreamReader(typeof(Orchestrator).Assembly.GetManifestResourceStream("RoutePrompt.txt"),Encoding.UTF8))return reader.ReadToEnd().Trim();
        }
        static bool Match(string text,string pattern) {return Regex.IsMatch(text,pattern,RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);}
        public static bool Offline(string text) {return Match(text,@"без (интернета|сети|поиска)|не (?:ищи|ходи|выходи) в (?:сети|интернет)|офф?лайн|offline|do not (?:browse|search)");}
        public static bool WantsScreen(string text) {return Match(text,@"(?:что (?:сейчас )?(?:видно |происходит )?на (?:мо[её]м )?экране|(?:посмотри|опиши|прочитай|проанализируй|покажи|сделай снимок) (?:что (?:сейчас )?на |на )?(?:мо[йиеё]м? |текущий |мой )?экран|(?:look at|describe|capture) (?:my |the )?screen)");}
        public static bool WantsTeam(string text){return Match(text,@"с (?:помощью )?агент|команд[ау] агент|мультиагент|поручи агентам|глубокое исследование|(?:проверьте|изучите|сравните|разберите|исследуйте) командой|командой (?:проверьте|изучите|сравните|разберите|исследуйте)|между (?:несколькими )?(?:агентами|исследователями)");}
        public static bool Routine(string t){
            return Match(t,@"^\s*(?:пожалуйста[, ]+)?(?:что такое\b|что значит\b|объясни\b|поясни\b|как нарисовать\b|как работает\b|переведи\b|исправь опечатки\b|сократи\b|перефразируй\b|подбери название\b|придумай (?:название|стих|историю)|translate\b|explain\b|перескажи\b|распиши подробнее\b|продолжи текст\b)|^\s*(привет|здравствуй(?:те)?|спасибо|добрый (?:день|вечер)|hello|hi|как дела)[?!. ]*$");
        }
        public static async Task<Route> Decide(Engine e,Preferences p,List<Turn> history,bool forceArt,bool forceTeam,bool permitted,Action<string> stage,CancellationToken ct) {
            Turn last=history.Last();string t=last.Text??"";bool enabled=permitted&&!Offline(t);
            if(!forceArt&&!forceTeam&&ReplyFormat.Field(t))return new Route{answer="text",query=""};
            bool explicitWeb=Match(t,@"(?:найди|поищи|проверь|посмотри|изучи|проведи поиск).{0,22}(?:в интернет|в сети|сайт|источник)|погугли|https?://|search (?:the )?web|browse (?:the )?web");
            bool fresh=Match(t,@"свежие (?:данные|новости)|последние новости|актуальн.{0,16}(?:цен|данн|ставк|верси)|на сегодняшний день|прямо сейчас|курс валют|прогноз погоды");
            bool create=Match(t,@"^\s*(?:пожалуйста[, ]+)?(?:нарисуй|сгенерируй (?:мне )?(?:картин|изображ|иллюстр|фото)|создай (?:мне )?(?:картин|изображ|иллюстр)|draw (?:me |an? )?|generate an? image)");
            bool change=Match(t,@"^\s*(?:измени|замени|перекрась|убери|добавь|сделай вариацию).{0,55}(?:на (?:фото|рисунк|картинк)|фон|цвет|вариаци)")&&history.Any(x=>!string.IsNullOrEmpty(x.Image));
            bool team=forceTeam||WantsTeam(t);
            bool imageAction=Match(t,@"\bнарисуй(?:те)?\b|\bизобрази(?:те)?\b|сгенерируй(?:те)?|(?:созда|сдела)(?:й|йте|ть) (?:готов\w* |нов\w* |итогов\w* |финальн\w* )?(?:картин|иллюстр|афиш|открытк|изображ|плакат|постер|баннер|обложк)");
            bool compound=Match(t,@"сначала|затем|после (?:поиска|анализа)|потом")||imageAction&&(team||explicitWeb||fresh);
            var r=new Route{answer="text",team=team,web=enabled&&(explicitWeb||fresh),query=""};
            if(!forceArt&&!team&&!explicitWeb&&!fresh&&!imageAction&&!compound&&(ChatContext.IsRecall(t)||ReplyFormat.Object(t)))return r;
            bool explicitRoute=!compound&&(explicitWeb||team&&!enabled||(!fresh&&(Routine(t)||Agents.HasArithmetic(t)||last.Image!=null&&Match(t,@"^(опиши|что (?:это|изображено)|прочитай текст|describe)"))));
            if(forceArt||((create||change)&&!compound)) {r.answer="image";r.reference=last.Image!=null||change||Match(t,@"вариац|предыдущ|по (?:фото|образцу|референсу)");}
            else if(!explicitRoute) {
                string context=string.Join("\n",history.Take(Math.Max(0,history.Count-1)).Reverse().Take(2).Reverse().Select(x=>ResearchText.Clip(x.Text,160)));
                bool hasImage=history.Any(turn=>!string.IsNullOrEmpty(turn.Image));
                string prompt=string.Format(routePrompt,context,hasImage?"да":"нет",ResearchText.Clip(t,1200));
                try{
                    string code=await e.RouteChoice(p,prompt,stage,ct).ConfigureAwait(false);
                    if(code=="C"&&!hasImage){
                        r.missingReference=Match(t,@"(?:на |эт[уаои]\w* |исходн\w* |предыдущ\w* |прошл\w* |приложен\w* )(?:фото|рисунк|картин|баннер|афиш|макет|изображ|снимк|снимок|обложк)");
                        code="A";
                    }
                    r.answer=code.Length==1&&"BCGHI".Contains(code)?"image":"text";
                    r.reference=code=="C"||last.Image!=null;r.team|=code=="E"||code=="F"||code=="H"||code=="I";
                    r.web=enabled&&(r.web||code=="D"||code=="F"||code=="G"||code=="I");
                }catch(Exception ex){ct.ThrowIfCancellationRequested();if(!(ex is IOException||ex is HttpRequestException||ex is ArgumentException||ex is InvalidOperationException))throw;
                    r.answer=create||change?"image":"text";r.team|=Match(t,@"сравни|исследуй|стратеги|compare|research");}
            }
            if(r.web&&!r.team){
                var link=Regex.Match(t,@"https?://[^\s<>""']+",RegexOptions.IgnoreCase);
                if(link.Success)r.query=link.Value;
                else{
                    string clean=Regex.Replace(t,@"^\s*(?:пожалуйста[, ]*)?(?:найди|поищи|проверь|посмотри|изучи|погугли)\s+(?:(?:в интернете|в сети|на сайте)\s+)?","",RegexOptions.IgnoreCase).Trim();
                    if(clean.Length<=105&&!Match(clean,@"@|\+?\d[\d ()-]{8,}\d|[\r\n]"))r.query=clean;
                    else r.query=ResearchText.Clip(await e.Role(p,"Сформулируй один короткий публичный поисковый запрос до восьми слов по просьбе. Исключи контакты и личные данные. Выведи только запрос.",ResearchText.Clip(t,1100),null,36,false,x=>{},stage,ct).ConfigureAwait(false),140);
                }
            }
            return r;
        }
    }

    public static class Personal {
        public static string DirectoryPath {get{return Path.Combine(Store.Home,"personal");}}
        public static string ProfilePath {get{return Path.Combine(DirectoryPath,"profile.txt");}}
        public static string HistoryPath {get{return Path.Combine(DirectoryPath,"history-preferences.txt");}}
        public static string Adapter {get{return Path.Combine(DirectoryPath,"work-formats.gguf");}}
        public static string ReadText(string file){
            try{return File.Exists(file)?File.ReadAllText(file).Trim():"";}catch(IOException){return "";}
        }
        public static void WriteProfile(string text){
            Directory.CreateDirectory(DirectoryPath);
            string temp=ProfilePath+"."+Guid.NewGuid().ToString("N")+".tmp";
            try{File.WriteAllText(temp,text.Trim());if(File.Exists(ProfilePath))File.Replace(temp,ProfilePath,null);else File.Move(temp,ProfilePath);}
            finally{if(File.Exists(temp))File.Delete(temp);}
        }
        public static string ExplicitRequest(string text,Preferences p) {
            if(Regex.IsMatch(text,@"^\s*(покажи (?:мне )?мой рабочий профиль|что ты запомнил[аи]? обо мне)[.?! ]*$",RegexOptions.IgnoreCase)){
                string profile=ReadText(ProfilePath),derived=ReadText(HistoryPath);
                return string.IsNullOrEmpty(profile+derived)?"Рабочий профиль пока пуст.":string.Join("\n\n",new[]{profile,derived}.Where(s=>s.Length>0));
            }
            var m=Regex.Match(text,@"^\s*(?:запомни (?:на будущее|для следующих (?:чатов|бесед)|мо[её] предпочтение)\s*[:,—-]\s*|запомни,? что (?=я |мне |мой |моя |мо[её] |мои ))(?<note>.+)$",RegexOptions.IgnoreCase|RegexOptions.Singleline);
            if(!m.Success)return null;
            string note=ResearchText.Plain(m.Groups["note"].Value),old=ReadText(ProfilePath);
            if(old.IndexOf(note,StringComparison.OrdinalIgnoreCase)>=0)return "Это уже есть в рабочем профиле.";
            string updated=(old+"\nМоя заметка: "+note).Trim();
            if(updated.Length>2400)return "Рабочий профиль заполнен. Откройте «Мой рабочий профиль» через меню «…» и сократите запись.";
            WriteProfile(updated);
            return p.Personalize?"Запомнила. Буду учитывать это в следующих беседах.":"Записала в рабочий профиль. Учитывать его в ответах можно через «…» → «Мой рабочий профиль».";
        }
        public static string Get(Preferences prefs) {
            if(!prefs.Personalize)return "";
            return string.Join("\n",new[]{ResearchText.Clip(ReadText(ProfilePath),2400),ResearchText.Clip(ReadText(HistoryPath),620)}.Where(s=>s.Length>0));
        }
        public static string DirectNotes(Preferences prefs){
            if(!prefs.Personalize)return "";
            return string.Join("\n",ReadText(ProfilePath).Split('\n').Where(s=>s.StartsWith("Моя заметка: ",StringComparison.OrdinalIgnoreCase)));
        }
        public static string Prompt(string task) {return "Рабочее поручение: "+task+"\nОпредели подходящий формат работы. 0 — обычный ответ; 1 — структура слайдов; 2 — работа с макетом; 3 — раскадровка; 4 — привлечение клиентов; 5 — таблица для сравнения; 6 — исследовательская записка; 7 — работающий сайт или интерфейс. Код:";}
        public static string Guide(int code) {
            string[] templates={"","Для презентации обозначь аудиторию и цель, дай заголовок и конкретное наполнение каждого нужного слайда.","Для работы с макетом учитывай сетку, читаемость, исходный формат, референс и неизменяемые элементы.","Для видео распиши необходимые сцены по времени с действием, камерой, титрами и переходами.","Для привлечения клиентов предложи применимый текст под канал, конкретное предложение и следующий шаг.","Для сравнения дай структуру таблицы, сопоставимые единицы, условия и отсутствующие данные.","Для исследования приведи первичные источники, даты, проверяемые факты, разногласия и ограничения.","Для разработки сайта или интерфейса учитывай целевое действие и мобильную версию. Проверяй нормальный пользовательский сценарий; честно сообщай, какие действия выполнила программа."};
            return code>=0&&code<templates.Length?templates[code]:"";
        }
    }
}
