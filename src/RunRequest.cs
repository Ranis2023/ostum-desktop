using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace Sfera {
    public sealed partial class Desktop {
        async Task RunRequest(List<Turn> turns,Turn reply,Action<string> stream,Action<string> activity,Action<WorkerEvent> changed,CancellationToken token) {
            var user=turns.Last();string request=user.Text??"";
            string memory=Personal.ExplicitRequest(request,prefs);
            if(memory!=null){reply.Text=memory;reply.Steps=new List<string>{"Оркестратор · рабочий профиль на компьютере"};return;}
            if(!art&&!agents&&user.Image==null&&user.DocumentId==null){
                string value=Calculator.Direct(request);
                if(value!=null){
                    reply.Text=await Workers.Track(changed,"calc","Калькулятор","Вычисляет выражение",()=>Task.FromResult(value));
                    reply.Steps=new List<string>{"Калькулятор · прямой расчёт"};return;
                }
                value=ReplyFormat.LiteralField(request);
                if(value!=null){
                    reply.Text=await Workers.Track(changed,"facts","Проверяющий","Извлекает указанное поле",()=>Task.FromResult(value));
                    reply.Steps=new List<string>{"Оркестратор · значение из указанной записи"};return;
                }
            }
            engine.SelectSlots(agents||Orchestrator.WantsTeam(request),turns.Sum(t=>(long)(t.Text??"").Length));
            bool field=ReplyFormat.Field(request);
            var local=field?null:Computer.WindowRequest(request,new System.Windows.Interop.WindowInteropHelper(Window).Handle);
            if(local!=null){Progress("Компьютер обрабатывает окно");reply.Text=local;reply.Steps=new List<string>{"Оркестратор · окна Windows"};return;}
            string launch=field?null:Computer.LaunchIntent(request);
            if(launch!=null){Progress("Компьютер открывает приложение");reply.Text=Computer.Launch(launch);reply.Steps=new List<string>{"Оркестратор · запуск приложения Windows"};return;}
            bool permitted=Find<CheckBox>("WebSearch").IsChecked==true;
            var verification=SourceCheck.Read(turns);
            if(verification!=null&&!art&&!agents&&!Orchestrator.WantsTeam(request)){
                Progress("Проверяющий сверяет утверждение с материалом");
                reply.Sources=verification.Sources;
                reply.Text=await Workers.Track(changed,"facts","Проверяющий","Сверяет тезис с исходным текстом",()=>Task.Run(()=>engine.VerifyClaim(prefs,verification.Material,verification.Claim,200,stream,activity,token)));
                reply.Steps=new List<string>{"Оркестратор · проверка утверждения по материалу"};return;
            }
            var route=await Workers.Track(changed,"route","Оркестратор","Выбирает инструменты для запроса",()=>Task.Run(()=>Orchestrator.Decide(engine,prefs,turns,art,agents,permitted,activity,token)));
            if(route.missingReference){reply.Text="Прикрепите изображение, которое нужно изменить. Можно перетащить его в чат.";reply.Steps=new List<string>{"Оркестратор · исходное изображение"};return;}
            string mode=route.answer=="image"?"изображение":route.team?"команда агентов":route.web?"веб-поиск":user.Image!=null?"анализ изображения":"ответ";
            var trace=new List<string>{"Оркестратор · "+mode+(route.web&&mode!="веб-поиск"?" и веб-поиск":"")};
            if(route.team)engine.TeamMode(true);
            string guidance="";
            bool exactText=ReplyFormat.Object(request)||field||ChatContext.IsRecall(request);
            if((route.answer!="image"||route.team)&&!exactText)guidance=await Task.Run(()=>engine.WorkContext(prefs,request,activity,token));
            bool docs=turns.Any(t=>t.DocumentId!=null);
            if(route.answer=="image") {
                if(!Store.DrawingOn(prefs)||!Store.Ready(Store.Art(prefs),prefs.Models)) {
                    if(!new ModelSettings(Window,prefs,true).Show()||!Store.DrawingOn(prefs)){reply.Text="Подключите генерацию изображений в «Модели и настройки».";return;}
                }
                string reference=user.Image,brief=request;
                if(reference==null&&route.reference)reference=turns.Where(t=>!string.IsNullOrEmpty(t.Image)&&File.Exists(t.Image)).Select(t=>t.Image).LastOrDefault();
                if(route.team||route.web||docs){
                    string research=await Task.Run(()=>Agents.Run(engine,prefs,turns,route.team,route.web,reply,t=>{},activity,token,route.query,guidance,changed));
                    brief=request+"\nДетали для иллюстрации:\n"+ResearchText.Clip(research,1100);reply.Text=research;
                }
                int side=1024,w=side,h=side;
                if(reference!=null){var im=OpenImage(reference,1024);double k=(double)side/Math.Max(im.PixelWidth,im.PixelHeight);w=Math.Max(256,64*(int)Math.Round(im.PixelWidth*k/64));h=Math.Max(256,64*(int)Math.Round(im.PixelHeight*k/64));}
                Progress("Генератор создаёт изображение");
                reply.Image=await Workers.Track(changed,"paint","Художник","Создаёт изображение по описанию",()=>Task.Run(()=>engine.Paint(prefs,brief,reference,w,h,activity,token)));
                if(string.IsNullOrWhiteSpace(reply.Text))reply.Text="Изображение готово.";reply.Generated=true;
            }else if(route.team||route.web||docs||(!exactText&&Agents.HasArithmetic(request)))
                reply.Text=await Task.Run(()=>Agents.Run(engine,prefs,turns,route.team,route.web,reply,stream,activity,token,route.query,guidance,changed));
            else reply.Text=await Workers.Track(changed,"chat","Zarum",user.Image==null?"Готовит ответ":"Рассматривает изображение",()=>Task.Run(()=>engine.Chat(prefs,turns,prefs.Think,stream,activity,token,guidance)));
            if(reply.Steps!=null)trace.AddRange(reply.Steps);reply.Steps=trace;
        }
    }
}
