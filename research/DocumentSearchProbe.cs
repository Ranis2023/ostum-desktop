using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Sfera;

// Runs against an installed or candidate assembly using only generated documents.
class DocumentSearchProbe {
    static int Main(string[] args){
        string folder=Path.GetFullPath(args[0]);Directory.CreateDirectory(folder);
        typeof(Store).GetField("Home").SetValue(null,folder);
        var doc=new ResearchDocument{Id=Guid.NewGuid().ToString("N"),Name="Технические условия.txt",Parts=new List<DocumentPart>()};
        string details=string.Concat(Enumerable.Repeat("Материалы осматривает сотрудник склада при передаче партии. ",13));
        string[] fields={"срок поставки","допуск","цена","масса","расход","гарантия","температура","напряжение"};
        string[] values={"11 рабочих дней","0,17 мм","18 730 рублей","32,8 кг","47 литров","26 месяцев","85 градусов","48 вольт"};
        var queries=new List<string>();
        for(int i=0;i<240;i++){
            int f=i%fields.Length;
            string code="МП-41-"+(100+i);
            string text="Каталожный лист. "+details+code+". "+fields[f]+" для данной позиции — "+(i>=232?values[f]:"см. базовую спецификацию")+".";
            doc.Parts.Add(new DocumentPart{Label="лист "+i,Text=text});
            if(i>=232)queries.Add("Укажи "+fields[f]+" для "+code+".");
        }
        Documents.Save(doc);var turns=new List<Turn>{new Turn{Role="user",DocumentId=doc.Id}};
        var cold=Stopwatch.StartNew();Documents.Find(turns,queries[0]);cold.Stop();
        var times=new List<double>();int hits=0;
        for(int pass=0;pass<3;pass++)for(int i=0;i<queries.Count;i++){
            var timer=Stopwatch.StartNew();var sources=Documents.Find(turns,queries[i]);timer.Stop();times.Add(timer.Elapsed.TotalMilliseconds);
            if(pass==0&&sources.Any(s=>ResearchText.Clip(s.Excerpt,680).Contains(values[i])))hits++;
        }
        Console.WriteLine("evidence hits {0}/{1}; warm median {2:F2} ms; cold {3:F2} ms",hits,queries.Count,times.OrderBy(n=>n).ElementAt(times.Count/2),cold.Elapsed.TotalMilliseconds);
        return args.Length>1&&hits!=queries.Count?1:0;
    }
}
