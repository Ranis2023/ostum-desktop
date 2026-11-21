using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Sfera {
    internal static class DocumentFacts {
        static readonly Regex ReceiptRequest=new Regex(
            @"^\s*(?:(?:командой|пожалуйста)[,\s]+)*(?:проверь(?:те)?|укажи(?:те)?|назови(?:те)?|выпиши(?:те)?)\s+"+
            @"(?:(?:согласованную|итоговую|окончательную)\s+)?сумму\s+и\s+код\s+при[её]мки"+
            @"(?:\s+(?:по|в|из)\s+(?:накладной|документ[ае]?|сч[её]т[ае]?))?[.!?]?\s*"+
            @"(?:(?:укажи(?:те)?|приведи(?:те)?)\s+источник[.!?]?)?\s*$",RegexOptions.IgnoreCase);
        static readonly Regex ReceiptAmount=new Regex(
            @"\b(?:(?:согласованная|итоговая|окончательная)\s+сумма|итого\s+к\s+оплате)\s*[:—–-]?\s*"+
            @"\d+(?:[ \u00a0\u202f]\d{3})*(?:[.,]\d{1,2})?\s*(?:руб(?:л(?:ь|я|ей))?\.?|₽|RUB\b|евро\b|EUR\b|USD\b|€|\$)(?!\p{L})",
            RegexOptions.IgnoreCase);
        static readonly Regex ReceiptCode=new Regex(
            @"\bкод\s+при[её]мки\s*[:№—–-]?\s*(?=[\p{L}\d/–‑-]*\d)[\p{L}\d]+(?:[–‑/-][\p{L}\d]+)*\b",RegexOptions.IgnoreCase);
        static Source UniqueField(ResearchDocument doc,Regex field){
            Source result=null;
            foreach(var part in doc.Parts){
                foreach(Match match in field.Matches(part.Text??"")){
                    if(result!=null&&!string.Equals(result.Excerpt,match.Value,StringComparison.OrdinalIgnoreCase))return null;
                    if(result==null)result=new Source{DocumentId=doc.Id,Title=doc.Name,Location=part.Label,Excerpt=match.Value};
                }
            }
            return result;
        }
        internal static string ReceiptAnswer(string request,List<Turn> history,List<Source> sources){
            if(!ReceiptRequest.IsMatch(request))return null;
            var ids=history.Select(t=>t.DocumentId).Where(id=>!string.IsNullOrEmpty(id)).Distinct().Take(2).ToList();
            if(ids.Count!=1)return null;
            var doc=Documents.Load(ids[0]);
            if(doc==null||doc.Truncated||doc.ScanNotice!=null)return null;
            var amount=UniqueField(doc,ReceiptAmount);var code=UniqueField(doc,ReceiptCode);
            if(amount==null||code==null)return null;
            var result=new List<string>();
            foreach(var field in new[]{amount,code}){
                int index=sources.FindIndex(s=>s.DocumentId==field.DocumentId&&s.Location==field.Location&&
                    (s.Excerpt??"").IndexOf(field.Excerpt,StringComparison.OrdinalIgnoreCase)>=0);
                if(index<0){index=sources.Count;sources.Add(field);}
                result.Add(field.Excerpt+" ["+(index+1)+"] · "+field.Title+", "+field.Location);
            }
            return string.Join("\n",result);
        }
        // Match short factual requests to literal sentences. Keep the file identity
        // attached to the quote throughout selection, even for equal filenames.
        internal static string Answer(string request,List<Turn> history,List<Source> sources){
            var ids=history.Select(t=>t.DocumentId).Where(id=>id!=null).Reverse().Distinct().Take(4).Reverse().ToList();
            if(ids.Count<2||!Regex.IsMatch(request,@"\b(?:укажи|назови|извлеки|выпиши|возьми|приведи|како[йе]|какая)\b",RegexOptions.IgnoreCase)||!Regex.IsMatch(request,@"(?:из (?:двух|обоих|файл)|(?:перв|втор)\w* (?:файл|вложени)|оба источника)",RegexOptions.IgnoreCase))return null;
            string main=Regex.Split(request,@"[.!?]\s+(?=(?:приведи|укажи|назови)\b)",RegexOptions.IgnoreCase)[0];
            var fields=Regex.Split(main,@"\s+и\s+|,\s*(?=из файла\b)",RegexOptions.IgnoreCase);
            if(fields.Length!=2)return null;
            var output=new List<string>();
            foreach(string clause in fields){
                var files=sources.Where(s=>s.DocumentId!=null&&clause.IndexOf(s.Title,StringComparison.OrdinalIgnoreCase)>=0).Select(s=>s.DocumentId).Distinct().ToList();
                if(files.Count>1)return "Есть несколько вложений с одинаковым именем. Укажите первое или второе вложение.";
                bool first=Regex.IsMatch(clause,@"\bперв\w* (?:файл|вложени)",RegexOptions.IgnoreCase),second=Regex.IsMatch(clause,@"\bвтор\w* (?:файл|вложени)",RegexOptions.IgnoreCase);
                string chosen=files.FirstOrDefault()??(first?ids[0]:second?ids[1]:null);
                var prefix=Regex.Match(clause,@"(?i:код(?:\s+с\s+префиксом)?|артикул(?:\s+с\s+префиксом)?)\s+[«""]?([А-ЯЁA-Z]{2,8})\b");
                var terms=Regex.Matches(clause.ToLowerInvariant().Replace('ё','е'),@"[\p{L}]{4,}").Cast<Match>()
                    .Where(m=>!Regex.IsMatch(m.Value,@"^(?:ответ|ответе|ответом)$")).Select(m=>m.Value.Substring(0,Math.Min(5,m.Length)))
                    .Where(s=>!Regex.IsMatch(s,@"^(?:файл|вложе|приве|укажи|источ|втор|перв|возьм|обоих|двух|префи)")).Distinct().ToList();
                var hits=sources.Where(s=>s.DocumentId!=null&&(chosen==null||chosen==s.DocumentId)).SelectMany(s=>
                    Regex.Split(s.Excerpt,@"(?<=[.!?])\s+|[\r\n]+").Select(line=>{
                        int score=terms.Count(w=>line.ToLowerInvariant().Replace('ё','е').Contains(w));
                        if(prefix.Success)score=Regex.IsMatch(line,@"(?<![\p{L}\d])"+Regex.Escape(prefix.Groups[1].Value)+@"[-–]\d",RegexOptions.IgnoreCase)?score+8:0;
                        return new{Source=s,Line=line.Trim(),Score=score};
                    })).Where(h=>h.Score>0).OrderByDescending(h=>h.Score).ToList();
                if(hits.Count==0){output.Add("Для поля «"+ResearchText.Clip(clause,120)+"» подтверждающая цитата не найдена.");continue;}
                foreach(var h in hits.Where(h=>h.Score==hits[0].Score).GroupBy(h=>h.Source.DocumentId+"\n"+h.Line).Select(g=>g.First()).Take(4))
                    output.Add(h.Line+" ["+(sources.IndexOf(h.Source)+1)+"] · "+h.Source.Title+", "+h.Source.Location);
            }
            return string.Join("\n",output.Distinct());
        }
    }
}
