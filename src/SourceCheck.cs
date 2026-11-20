using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Sfera {
    public sealed class ClaimCheck {
        public string Claim,Material;
        public List<Source> Sources;
    }
    public static class SourceCheck {
        static readonly Regex WorkTerms=new Regex(@"смет|договор|макет|монтаж|закуп|объявлени|реклам|оплат|сч[её]т|плат[её]ж|выручк|клиент|заявк|цен[ауыуе]|доставк|постав|подряд|тираж|бриф|слайд|презентац|проект|услуг|кампани|исполнител|отч[её]т|бюджет|покуп|стоимост|участник|публикац|пакет|сайт|прототип|баннер|лицензи|печат|поддержк|склад|подписк|витрин|продаж|акт[аеу]?\b|провер[кч]|материал[аыов]*\b|образец|спецификац",RegexOptions.Compiled|RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
        public static bool UsesAdapter(string material,string claim){return WorkTerms.IsMatch(material+"\n"+claim);}
        public const string Instruction="Ты проверяешь одно утверждение только по предоставленному материалу. Начни ответ с «Да.», если оно следует из текста, с «Нет.», если текст ему прямо противоречит, или с «Недостаточно данных.», если в тексте нет ответа. После вердикта кратко поясни его по материалу. Не добавляй внешних фактов.";
        public static string Prompt(string material,string claim){return "Материал:\n"+ResearchText.Clip(material,5600)+"\n\nУтверждение:\n"+ResearchText.Clip(claim,900);}
        public static ClaimCheck Read(List<Turn> history){
            string text=history.Last().Text??"";
            var opts=RegexOptions.IgnoreCase|RegexOptions.Singleline|RegexOptions.CultureInvariant;
            var pair=Regex.Match(text,@"(?:^|\n)(?:материал|источник|выдержка|текст)\s*:\s*(?<m>.+?)\s*(?:\n|[.!?]\s+)(?:утверждение|тезис)\s*:\s*(?<h>.+)$",opts);
            if(!pair.Success)pair=Regex.Match(text,@"(?:^|\n)(?:утверждение|тезис)\s*:\s*(?<h>.+?)\s*(?:\n|[.!?]\s+)(?:материал|источник|выдержка|текст)\s*:\s*(?<m>.+)$",opts);
            if(pair.Success)return new ClaimCheck{Material=pair.Groups["m"].Value.Trim(),Claim=pair.Groups["h"].Value.Trim(),Sources=new List<Source>()};
            if(!history.Any(t=>t.DocumentId!=null))return null;
            var claim=Regex.Match(text,@"^\s*(?:подтверждает ли (?:этот |приложенный )?(?:документ|отч[её]т|файл|договор|материал)(?:\s+то)?[,]?\s+что|верно ли по (?:документу|материалу),? что|проверь (?:по (?:документу|файлу) )?(?:тезис|утверждение)\s*:?)\s*(?<h>.+)$",opts);
            if(!claim.Success)return null;
            var hypothesis=claim.Groups["h"].Value.Trim().TrimEnd('?');var sources=Documents.Find(history,hypothesis);
            return new ClaimCheck{Claim=hypothesis,Material=Excerpts(sources),Sources=sources};
        }
        public static string Excerpts(List<Source> sources){return string.Join("\n\n",sources.Take(5).Select((s,i)=>"["+(i+1)+"] "+ResearchText.Clip(s.Excerpt,850)));}
    }
}
