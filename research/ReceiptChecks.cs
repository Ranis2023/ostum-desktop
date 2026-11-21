using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Sfera;

class ReceiptChecks {
    static int Main(string[] args){
        string folder=Directory.CreateDirectory(Path.GetFullPath(args[0])).FullName;
        typeof(Store).GetField("Home").SetValue(null,folder);
        if(!WorkPolicy.Within(Store.Chats,folder))throw new Exception("Test profile mismatch");
        var read=typeof(Desktop).Assembly.GetType("Sfera.DocumentFacts").GetMethod("ReceiptAnswer",BindingFlags.NonPublic|BindingFlags.Static);
        const string query="Командой проверьте окончательную сумму и код приёмки по накладной. Укажите источник.";
        const string amount="Согласованная сумма 2 700,50 рублей.";
        const string code="Код приёмки КА-301.";
        var rows=new List<object>();
        int failures=0;
        Action<string,string,string,bool,bool,bool> test=(id,text,task,truncated,second,expected)=>{
            var doc=new ResearchDocument{Id=Guid.NewGuid().ToString("N"),Name="Входящая.pdf",Truncated=truncated,Parts=new List<DocumentPart>{
                new DocumentPart{Label="стр. 1",Text="Предварительная стоимость 8400 рублей."},new DocumentPart{Label="стр. 2",Text=text}}};
            Documents.Save(doc);
            var history=new List<Turn>{new Turn{Role="user",DocumentId=doc.Id}};
            if(second)history.Add(new Turn{Role="user",DocumentId=Guid.NewGuid().ToString("N")});
            var sources=new List<Source>();var response=(string)read.Invoke(null,new object[]{task,history,sources});
            bool pass=expected?response!=null&&response.Contains("КА-301")&&response.Contains("2 700,50")&&response.Contains("[1]")&&response.Contains("стр. 2")&&!response.Contains("8400")&&sources.All(s=>s.DocumentId==doc.Id)
                :response==null&&sources.Count==0;
            if(!pass)failures++;
            rows.Add(new{id=id,passed=pass,answer=response});
        };
        test("receipt-both",amount+" "+code,query,false,false,true);
        test("receipt-two-amounts",amount+" Итоговая сумма 120 рублей. "+code,query,false,false,false);
        test("receipt-two-codes",amount+" "+code+" Код приёмки АБ-219.",query,false,false,false);
        test("receipt-missing-code",amount,query,false,false,false);
        test("receipt-preliminary","Предварительная сумма 150 рублей. "+code,query,false,false,false);
        test("receipt-truncated",amount+" "+code,query,true,false,false);
        test("receipt-two-docs",amount+" "+code,query,false,true,false);
        test("receipt-formatted",amount+" "+code,query+" Верни только JSON.",false,false,false);
        test("receipt-followup",amount+" "+code,query+" Рассчитай налог.",false,false,false);
        Store.Write(Path.Combine(folder,"checks.json"),rows);Console.WriteLine((rows.Count-failures)+"/"+rows.Count);
        return failures==0?0:1;
    }
}
