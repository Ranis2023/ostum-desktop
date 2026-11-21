using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Sfera {
    internal sealed class ChatFileIntent {
        internal string Action,Value,Target;
        internal List<ChatFileIntent> Edits;
        static readonly Regex EditSeparator=new Regex(@"(?:\s*[,;]\s*|\s+и\s+)(?=(?:(?:измени|замени|поменяй|поставь\s+в)\s+)?(?:ячейку\s+)?[A-Z]{1,3}[1-9]\d{0,6}\b)",RegexOptions.IgnoreCase);
        static readonly Dictionary<string,string> verbs=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase){
            {"открыть","открой"},{"показать","покажи"},{"создать","создай"},{"подключить","подключи"},
            {"изменить","измени"},{"сохранить","сохрани"},{"записать","запиши"},{"найти","найди"},
            {"переписать","перепиши"},{"переформулировать","переформулируй"},{"сократить","сократи"},{"сравнить","сравни"}
        };
        static ChatFileIntent Command(string action,string value="",string target=""){
            return new ChatFileIntent{Action=action,Value=value.Trim().Trim('«','»','"'),Target=target};
        }
        internal static ChatFileIntent Read(string request){
            const RegexOptions opt=RegexOptions.IgnoreCase|RegexOptions.CultureInvariant;
            string t=Regex.Replace((request??"").Trim(),@"^(?:пожалуйста|будь добр[аы]?|будьте добры)[, ]+","",opt);
            var polite=Regex.Match(t,@"^(?:можешь|можете|прошу)\s+(?:пожалуйста[, ]+)?(\p{L}+)\b",opt);
            string verb;
            if(polite.Success&&verbs.TryGetValue(polite.Groups[1].Value,out verb))t=(verb+t.Substring(polite.Length)).TrimEnd('?').TrimEnd();
            t=Regex.Replace(t,@"^(\S+?)(?:,?\s+пожалуйста,?|\s+мне)\s+","$1 ",opt);
            var m=Regex.Match(t,@"^(?:открой|подключи|выбери|покажи)\s+(?:рабочую\s+)?папку(?:\s+(.+?))?[.!?]*$",opt);
            if(m.Success)return Command("folder",m.Groups[1].Value);
            m=Regex.Match(t,@"^(?:открой|покажи|выбери)\s+(?:(?:файл|документ|таблицу|книгу)(?:\s+(?:Word|Excel))?\s*)?(.*?\.(?:docx|xlsx)[»""]?|(?:документ|таблицу|файл|книгу)(?:\s+(?:Word|Excel))?)\s*[.!?]*$",opt);
            if(m.Success)return Command("open",Regex.IsMatch(m.Groups[1].Value,@"\.(docx|xlsx)",opt)?m.Groups[1].Value:"");
            m=Regex.Match(t,@"^(?:открой|покажи|выбери)\s+(документ|таблицу|книгу|файл)\s+(?:с названием\s+)?(.+?)\s*[.!?]*$",opt);
            if(m.Success){
                string kind=m.Groups[1].Value.ToLowerInvariant();
                return Command("open",m.Groups[2].Value,(kind=="таблицу"||kind=="книгу")?".xlsx":kind=="документ"?".docx":"");
            }
            m=Regex.Match(t,@"^(?:создай|добавь)\s+(?:нов(?:ый|ую)\s+)?(документ|таблицу|книгу)(?:\s+(?:Word|Excel))?(?:\s+(?:с названием\s+)?[«""]([^»""]+)[»""]|\s+([^\/\\:*?""<>|]+\.(?:docx|xlsx)))?\s*[.!?]*$",opt);
            if(m.Success)return Command(m.Groups[1].Value.ToLowerInvariant()=="документ"?"new-word":"new-sheet",m.Groups[2].Success?m.Groups[2].Value:m.Groups[3].Value);
            if(Regex.IsMatch(t,@"^(?:измени|замени|поменяй|запиши\s+в|поставь\s+в)\s+(?:ячейку\s+)?[a-z]{1,3}[1-9]\d*\b",opt)){
                var parts=EditSeparator.Split(t);
                if(parts.Length>1){
                    var batch=Command("replace-many");batch.Edits=new List<ChatFileIntent>();
                    foreach(string part in parts){
                        var edit=Regex.Match(part,@"^(?:(?:измени|замени|поменяй|запиши\s+в|поставь\s+в)\s+)?(?:ячейку\s+)?([a-z]{1,3}[1-9]\d{0,6})\s*(?:на\s+|=\s*|:\s*|значение\s+)(.+)$",opt);
                        if(!edit.Success||batch.Edits.Exists(x=>x.Target.Equals(edit.Groups[1].Value,StringComparison.OrdinalIgnoreCase)))return Command("clarify-edits");
                        batch.Edits.Add(Command("replace",edit.Groups[2].Value,edit.Groups[1].Value.ToUpperInvariant()));
                    }
                    return batch;
                }
            }
            m=Regex.Match(t,@"^(?:измени|замени|поменяй|запиши\s+в|поставь\s+в)\s+(?:(?:ячейку|абзац)\s+)?([a-z]{1,3}[1-9]\d{0,6}|[1-9]\d{0,5})\s*(?:на\s+|=\s*|:\s*|значение\s+)([\s\S]+)$",opt);
            if(m.Success)return Command("replace",m.Groups[2].Value,m.Groups[1].Value.ToUpperInvariant());
            m=Regex.Match(t,@"^(?:запиши|впиши|введи|поставь)\s+(?:значение\s+)?(.+?)\s+в\s+(?:ячейку\s+)?([a-z]{1,3}[1-9]\d{0,6})[.!?]*$",opt);
            if(m.Success)return Command("replace",m.Groups[1].Value,m.Groups[2].Value.ToUpperInvariant());
            m=Regex.Match(t,@"^(?:запиши|впиши|введи|поставь)\s+в\s+(?:(?:ячейку|абзац)\s+)?([a-z]{1,3}[1-9]\d{0,6}|[1-9]\d{0,5})\s+([\s\S]+)$",opt);
            if(m.Success)return Command("replace",m.Groups[2].Value,m.Groups[1].Value.ToUpperInvariant());
            m=Regex.Match(t,@"^(?:перепиши|переформулируй|сократи|переведи|исправь опечатки в)\s+(?:(?:выбранн\w*|текущ\w*)\s+)?(?:абзац\w*|ячейк\w*|фрагмент\w*)(?:\s+([a-z]{1,3}[1-9]\d{0,6}|[1-9]\d{0,5})(?=\s|[.!?,:]|$))?(?:\s|[.!?,:]|$)",opt);
            if(m.Success)return Command("rewrite",t,m.Groups[1].Value.ToUpperInvariant());
            m=Regex.Match(t,@"^(?:найди|поищи)\s+в\s+(файле|документе|таблице|проекте|папке)\s+(.+)$",opt);
            if(m.Success)return Command(Regex.IsMatch(m.Groups[1].Value,"проект|папк",opt)?"search-project":"find",m.Groups[2].Value);
            if(Regex.IsMatch(t,@"^(?:собери|создай|импортируй|обнови)\s+реестр\s+(?:входящих\s+)?сч[её]тов\b",opt))return Command("invoices");
            if(Regex.IsMatch(t,@"^(?:покажи|открой)\s+источник\s+(?:выбранного\s+)?сч[её]та\b",opt))return Command("invoice-source");
            if(Regex.IsMatch(t,@"^(?:что просрочено|(?:покажи|найди) просроченные поручения)\b",opt))return Command("overdue");
            if(Regex.IsMatch(t,@"^(?:что (?:сда[её]м|нужно сдать) на (?:этой )?неделе|(?:покажи )?поручения на (?:эту )?неделю)\b",opt))return Command("week");
            if(Regex.IsMatch(t,@"^(?:экспортируй|сохрани|выгрузи)\s+(?:сроки|поручения)\s+в\s+(?:календарь|ics)\b",opt))return Command("calendar");
            if(Regex.IsMatch(t,@"^(?:покажи|открой) (?:предложенные |мои )?правки[.!?]*$",opt))return Command("patches");
            if(Regex.IsMatch(t,@"^сохрани (?:файл|выбранные правки|изменения|правки)[.!?]*$",opt))return Command("save");
            if(Regex.IsMatch(t,@"^(?:покажи|открой) (?:список файлов|файлы) (?:в |из )?(?:папки|папке|проекта)[.!?]*$",opt))return Command("list");
            if(Regex.IsMatch(t,@"^(?:(?:покажи[, ]+)?что|какие файлы) (?:есть |лежит |находится )?в (?:этой |рабочей )?папке[.!?]*$",opt))return Command("list");
            if(Regex.IsMatch(t,@"^(?:сверь|сравни) (?:две )?таблицы Excel[.!?]*$",opt))return Command("reconcile");
            if(Regex.IsMatch(t,@"^(?:покажи|открой|запусти|создай|настрой)\s+(?:мои\s+)?сценари[йи]\s+сверки(?:\s+(?:таблиц|Excel))?[.!?]*$",opt))return Command("recipes");
            if(Regex.IsMatch(t,@"^(?:покажи|открой) реестр поручений[.!?]*$",opt))return Command("register");
            if(Regex.IsMatch(t,@"^создай поручение(?: из (?:фрагмента|абзаца|ячейки))?[.!?]*$",opt))return Command("task");
            if(Regex.IsMatch(t,@"^(?:покажи|открой) (?:версии файла|историю версий)[.!?]*$",opt))return Command("versions");
            if(Regex.IsMatch(t,@"^сравни (?:с другой редакцией|с другим DOCX)[.!?]*$",opt))return Command("compare");
            if(Regex.IsMatch(t,@"^(?:добавь комментарий|измени формат|открой шаблоны|открой обучение|предложи правку|операции с файлами)[.!?]*$",opt))
                return Command("tool",t.TrimEnd('.','!','?').ToLowerInvariant());
            return null;
        }
    }
}
