using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Sfera {
    public static class ChatContext {
        static readonly Regex Recall = new Regex(@"\b(?:вспомни|напомни|ранее|прежде|договаривались|обсуждали|упоминал[аи]?|мы\s+(?:\w+\s+){0,2}(?:выбрали|согласовали|записали|решили|назвали)|как\s+был[аио]?\s+записан|remind me|we (?:agreed|chose|called)|earlier in (?:this|our) chat)\b", RegexOptions.IgnoreCase);
        static readonly Regex Words = new Regex(@"[\p{L}\p{Nd}]+", RegexOptions.Compiled);
        static readonly HashSet<string> Common = new HashSet<string>(
            "напомни вспомни какой какая какие какое каком когда только ответь одним словом значением название назывался называлась согласовали выбрали записали записан проекта проект образца шифр дай для наш был ранее какой chose agreed remind earlier about project what which code please".Split(' ')
                .Select(Stem), StringComparer.Ordinal);

        static string Stem(string word) {
            string normalized=word.ToLowerInvariant().Replace('ё','е');
            return normalized.Substring(0,Math.Min(6,normalized.Length));
        }
        public static bool IsRecall(string task) {
            return Recall.IsMatch(task) && !Regex.IsMatch(task,@"(?:напомни (?:мне )?(?:через|завтра|кажд)|remind me (?:in|at|tomorrow))",RegexOptions.IgnoreCase);
        }
        sealed class Hit {
            public int Order;
            public string Text;
            public Dictionary<string,int> Positions;
            public double Score;
        }
        public static string RecallEarlier(List<Turn> history,int before) {
            if(before<1||history.Count==0)return "";
            string task=history[history.Count-1].Text??"";
            if(!IsRecall(task))return "";
            var terms=new HashSet<string>(Words.Matches(task).Cast<Match>().Where(m=>m.Length>=3||m.Value.Any(char.IsDigit)).Select(m=>Stem(m.Value)),StringComparer.Ordinal);
            terms.ExceptWith(Common);
            var named=Regex.Match(task,@"\b(?:проекта?|заказа?|клиента?|project)\s+(?:[«""](?<title>[^»""]{2,90})[»""]|(?<title>[\p{Lu}\d][\p{L}\d -]{1,60}?)(?=[?.,!;\r\n]|$))");
            var mustMatch=named.Success?Words.Matches(named.Groups["title"].Value).Cast<Match>().Select(m=>Stem(m.Value)).Where(k=>k.Length>=3&&!Common.Contains(k)).ToArray():new string[0];
            if(terms.Count==0)return "";
            var matches=new List<Hit>();var frequency=new Dictionary<string,int>();int records=0;
            for(int i=0;i<before;i++){
                if(history[i].Role!="user")continue;
                records++;
                string original=history[i].Text??"";
                if(i+1<before&&history[i+1].Role=="assistant")original+="\nОтвет в чате: "+(history[i+1].Text??"");
                var positions=new Dictionary<string,int>();
                foreach(Match m in Words.Matches(original)){
                    string key=Stem(m.Value);
                    if(terms.Contains(key)&&!positions.ContainsKey(key))positions[key]=m.Index;
                }
                if(positions.Count==0||mustMatch.Any(k=>!positions.ContainsKey(k)))continue;
                foreach(string key in positions.Keys){int n;frequency.TryGetValue(key,out n);frequency[key]=n+1;}
                matches.Add(new Hit{Order=i,Text=original,Positions=positions});
            }
            foreach(var hit in matches)
                hit.Score=hit.Positions.Keys.Where(k=>frequency[k]<=Math.Max(1,(records+1)/2)).Sum(k=>Math.Log(1.0+records/(double)frequency[k]));
            var selected=matches.Where(h=>h.Score>0).OrderByDescending(h=>h.Score).ThenByDescending(h=>h.Order).Take(2).ToList();
            if(selected.Count==0)return "";
            double top=selected[0].Score;
            var quotes=selected.Where(h=>h.Score>=top*.85).OrderBy(h=>h.Order).Select(h=>{
                int focus=h.Positions.OrderBy(k=>frequency[k.Key]).ThenBy(k=>k.Value).First().Value;
                int start=Math.Max(0,Math.Min(focus-100,h.Text.Length-390));
                return (start>0?"…":"")+h.Text.Substring(start,Math.Min(390,h.Text.Length-start))+(start+390<h.Text.Length?"…":"");
            });
            return Store.Json().Serialize(quotes.ToArray());
        }
    }

    public static class ReplyFormat {
        static readonly Regex JsonRequest=new Regex(@"\b(?:верни(?:те)?|выведи(?:те)?|представь(?:те)?|пришли(?:те)?|сформируй(?:те)?|дай(?:те)?|ответь(?:те)?|return|output|give)\s+(?:(?:только|строго|ровно|ответ|результат|один|как|в|виде|формате|only|a|an|as|valid|single|the|response)\s+){0,6}json\b",RegexOptions.IgnoreCase);
        public static bool Object(string task){
            return JsonRequest.IsMatch(task)&&!Regex.IsMatch(task,@"json[-\s]+(?:массив|array)|(?:массив|array)\s+(?:в\s+)?json|пример\s+json|example\s+json",RegexOptions.IgnoreCase);
        }
        static readonly Regex FieldRequest=new Regex(@"^\s*(?:пожалуйста[, ]+)?(?:извлеки|прочитай|назови|верни|выведи|extract|return)\s+(?:мне\s+)?(?:только|исключительно|only)\s+",RegexOptions.IgnoreCase);
        static bool SourceTask(string text){
            return Regex.IsMatch(text,@"\b(?:пол[еяю]|из (?:записи|карточки|текст[а]|таблицы)|field|from the record)\b",RegexOptions.IgnoreCase);
        }
        public static bool Field(string task){return FieldRequest.IsMatch(task)&&SourceTask(task);}
        static readonly Regex Quoted=new Regex(@"«[^»\r\n]*»|“[^”\r\n]*”|""[^""\r\n]*""");
        static readonly Regex Assign=new Regex(@"(?:^|[.?!;:\r\n])\s*(?<key>[\p{L}][\p{L}\d _-]{0,52}?)\s*=\s*(?<value>[^=;\r\n.!?]{1,145}?)(?=[.;!?\r\n]|$)",RegexOptions.Multiline);
        public static string LiteralField(string task){
            if(!Field(task))return null;
            var boundary=Regex.Match(task,@"[.!?\r\n]");
            if(!boundary.Success)return null;
            string instruction=task.Substring(0,boundary.Index),record=task.Substring(boundary.Index+1);
            string plain=Quoted.Replace(record,"\uFFFC");
            var labels=Quoted.Matches(instruction);
            if(labels.Count>1)return null;
            if(labels.Count==1){
                string label=labels[0].Value;
                label=label.Substring(1,label.Length-2).Trim();
                if(label.Length==0||label.Length>70)return null;
                string key=@"(?:^|(?<=[;:.!?\r\n]))[ \t]*"+Regex.Escape(label)+@"[ \t]*[:=][ \t]*";
                var occurrences=Regex.Matches(plain,key,RegexOptions.IgnoreCase);
                if(occurrences.Count!=1)return null;
                var entry=occurrences[0];
                var tail=plain.Substring(entry.Index+entry.Length);
                // A dot between digits belongs to a decimal or a date, and must not truncate the value.
                var literal=Regex.Match(tail,@"^(?<value>(?:[^.!?;:\r\n=]|(?<=\d)\.(?=\d)){1,145})(?=[;\r\n]|[.!?](?:\s|$)|$)");
                if(!literal.Success)return null;
                string literalValue=literal.Groups["value"].Value.Trim();
                if(literalValue.Length==0||literalValue.IndexOf('\uFFFC')>=0)return null;
                return literalValue;
            }
            var matches=Assign.Matches(plain).Cast<Match>().Where(m=>{
                string label=m.Groups["key"].Value.Trim();
                return Regex.IsMatch(instruction,@"(?<![\p{L}\d_])"+Regex.Escape(label)+@"(?![\p{L}\d_])",RegexOptions.IgnoreCase);
            }).ToList();
            if(matches.Count!=1)return null;
            var value=matches[0].Groups["value"];
            string answer=value.Value.Trim();
            if(answer.Length==0||answer.Contains("\uFFFC")||value.Index+value.Length+1<plain.Length&&plain[value.Index+value.Length]=='.'&&char.IsLetterOrDigit(plain[value.Index+value.Length+1]))return null;
            // A quoted or multiline value needs the model to interpret the source intact.
            return answer;
        }
    }
}
