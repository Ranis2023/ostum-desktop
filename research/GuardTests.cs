using System;
using System.Collections.Generic;
using System.Linq;
using System.Web.Script.Serialization;
using Sfera;

class GuardTests {
    static void Main(){
        var checks=new Dictionary<string,bool>();
        checks["numeric precedence"]=Calculator.Direct("Вычисли (7 + 5)^2 / 6")=="24";
        checks["unary power"]=Calculator.Direct("Посчитай −2^2")=="-4";
        checks["locale decimals"]=Calculator.Direct("Посчитай: 1,25 × 8")=="10";
        checks["literal date"]=Calculator.Direct("2026-11-20")==null;
        checks["translation with expression"]=Calculator.Direct("Переведи: осталось 5 + 7 дней.")==null;
        checks["bad syntax"]=Calculator.Direct("2 ** 2")==null;
        checks["division by zero"]=Calculator.Direct("30 ÷ 0").Contains("ноль");
        checks["object"]=ReplyFormat.Object("Верни результат в формате JSON.");
        checks["array"]=!ReplyFormat.Object("Верни JSON-массив из трёх чисел");
        checks["json explanation"]=!ReplyFormat.Object("Объясни синтаксис JSON");
        checks["unique field"]=ReplyFormat.LiteralField("Назови только значение поля «Зал». Зал = Зелёный. Заметка: «Зал = Синий».")=="Зелёный";
        checks["ambiguous field"]=ReplyFormat.LiteralField("Извлеки только поле «Тон». Тон = Алый. Тон = Лиловый.")==null;
        checks["quoted value"]=ReplyFormat.LiteralField("Извлеки только поле «Зал». Зал = «Первый» (этаж 2).")==null;
        checks["decimal field"]=ReplyFormat.LiteralField("Извлеки только поле «Цена». Цена = 2.99.")==null;
        var turns=new List<Turn>();
        Action<string,string> add=(q,a)=>{turns.Add(new Turn{Role="user",Text=q});turns.Add(new Turn{Role="assistant",Text=a});};
        add("Проект «Солнечный луг». Согласована зелёная обложка.","Принято.");
        add("Проект «Солнечный зал». Выбрана красная обложка.","Принято.");
        add(new string('а',2000)+" Проект «Орион». Срок сдачи 24 мая. "+new string('б',1000),"Принято.");
        for(int i=0;i<14;i++)add("Уточните тему встречи "+i,"Ожидается");
        turns.Add(new Turn{Role="user"});
        Func<string,string> find=q=>{turns.Last().Text=q;return ChatContext.RecallEarlier(turns,turns.Count-20);};
        string hit=find("Напомни обложку проекта «Солнечный зал».");
        checks["distinguish names"]=hit.Contains("красная")&&!hit.Contains("зелёная");
        checks["unknown named record"]=find("Напомни обложку проекта «Солнечный дом».")=="";
        checks["ordinary request"]=find("Предложи обложку проекта «Солнечный зал».")=="";
        checks["reminder"]=find("Напомни мне завтра выбрать обложку проекта «Солнечный зал».")=="";
        string longHit=find("Когда мы согласовали срок проекта «Орион»?");
        checks["bounded excerpt"]=longHit.Contains("24 мая")&&longHit.Length<550;
        Console.WriteLine(new JavaScriptSerializer().Serialize(checks));
        Environment.ExitCode=checks.Values.Any(v=>!v)?1:0;
    }
}
