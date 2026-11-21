using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using Sfera;

class ImprovementChecks {
    public class Sample{public int batch,page;public string number,amount;}
    class Outcome{public string id{get;set;}public bool passed{get;set;}public string observed{get;set;}}
    static readonly List<Outcome> results=new List<Outcome>();
    static void Add(string id,bool pass,string detail){results.Add(new Outcome{id=id,passed=pass,observed=detail});}
    [STAThread]static int Main(string[] args){
        var dir=Directory.CreateDirectory(Path.GetFullPath(args[0])).FullName;
        typeof(Store).GetField("Home").SetValue(null,dir);
        var settings=new Preferences{Personalize=false,AllowWeb=false};
        string[] phrases={"окончательный код серии","номер последней накладной","дату передачи","имя ответственного","согласованную сумму"};
        int n=0;
        foreach(string verb in new[]{"Напиши","Укажи","Верни","Выведи","Назови","Пришли"})
          foreach(string field in phrases){
            string task=verb+" только "+field+". Сначала договорились о старом варианте, затем уточнили сведения в описании иллюстрации.";
            var r=Orchestrator.Decide(null,settings,new List<Turn>{new Turn{Role="user",Text=task}},false,false,true,s=>{},CancellationToken.None).GetAwaiter().GetResult();
            Add("route-"+(++n),r.answer=="text"&&!r.web&&!r.team,task+" → "+r.answer);
          }
        var answer=typeof(Desktop).Assembly.GetType("Sfera.DocumentFacts").GetMethod("Answer",BindingFlags.NonPublic|BindingFlags.Static);
        for(int i=0;i<20;i++){
            string name="Марина "+new[]{"Тихая","Лесная","Речная","Степная"}[i%4],code="КД-"+(373+13*i),wrong="ТК-"+(4172+i);
            var a=new ResearchDocument{Id=Guid.NewGuid().ToString("N"),Name="Поставка "+i+".txt",Parts=new List<DocumentPart>{new DocumentPart{Label="стр. 2",Text="Код приёмки "+wrong+". Ответственный за приёмку "+name+"."}}};
            var b=new ResearchDocument{Id=Guid.NewGuid().ToString("N"),Name="Заказ "+i+".txt",Parts=new List<DocumentPart>{new DocumentPart{Label="строка 3",Text="Код "+code+". Стоимость 1800 рублей."}}};
            Documents.Save(a);Documents.Save(b);
            var history=(i%2==0?new[]{a,b}:new[]{b,a}).Select(d=>new Turn{Role="user",DocumentId=d.Id}).ToList();
            string task=i%3==0?"Из файла "+a.Name+" возьми имя ответственного за приёмку, из файла "+b.Name+" — код с префиксом КД. Укажи для каждого его источник.":"Из двух вложений укажи имя ответственного за приёмку и код КД из "+(i%2==0?"второго":"первого")+" файла. Приведи оба источника.";
            var sources=Documents.Find(history,task);string text=(string)answer.Invoke(null,new object[]{task,history,sources});
            string[] lines=(text??"").Split('\n');
            bool pass=lines.Any(l=>l.Contains(name)&&l.Contains(a.Name))&&lines.Any(l=>l.Contains(code)&&l.Contains(b.Name))&&!lines.Any(l=>l.Contains(wrong));
            Add("source-"+i,pass,text);
        }
        var samples=Store.Json().Deserialize<Sample[]>(File.ReadAllText(Path.Combine(args[1],"answers.json")));
        foreach(var group in samples.GroupBy(s=>s.batch)){
            var doc=Documents.Read(Path.Combine(args[1],"batch"+group.Key+".pdf"));
            foreach(var item in group){
                var parts=doc.Parts.Where(p=>p.ScanNumber==item.page&&p.Box!=null&&p.Box.Length==4&&p.Box.All(x=>x>=0&&x<=1)).ToList();
                var observed=string.Join(" / ",parts.Select(p=>p.Text));
                Add("scan-"+group.Key+"-"+item.page,Regex.IsMatch(observed,@"(?<!\d)"+item.number+@"(?!\d)")&&Regex.IsMatch(observed,@"(?<!\d)"+item.amount+@"(?!\d)"),observed);
            }
        }
        Store.Write(Path.Combine(dir,"gates.json"),results);
        Console.WriteLine("Passed {0}/{1}",results.Count(x=>x.passed),results.Count);
        return results.All(x=>x.passed)?0:1;
    }
}
