using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Sfera;

// This probe requires the isolated signed fixture created by workspace_access_test.ps1.
class WorkdayProject {
    const BindingFlags Private=BindingFlags.NonPublic|BindingFlags.Instance;
    readonly List<object> rows=new List<object>();
    string output,details="";
    Turn answer;
    Desktop desktop;
    async Task<Turn> Send(string request){
        ((TextBox)desktop.Window.FindName("Input")).Text=request;
        await (Task)typeof(Desktop).GetMethod("Send",Private).Invoke(desktop,null);
        var c=(Conversation)typeof(Desktop).GetField("active",Private).GetValue(desktop);
        answer=c.Turns.Last();if(answer.Role!="assistant")throw new Exception("Assistant turn did not complete");return answer;
    }
    async Task Test(string id,string title,Func<Task<bool>> fn){
        var sw=Stopwatch.StartNew();string error=null;bool passed=false;details="";answer=null;
        try{passed=await fn();}catch(Exception e){error=e.GetBaseException().Message;}
        rows.Add(new{id=id,title=title,passed=passed,evidence=details,answer=answer==null?"":answer.Text,seconds=Math.Round(sw.Elapsed.TotalSeconds,3),error=error});
        Store.Write(Path.Combine(output,"project.json"),rows);
        File.AppendAllText(Path.Combine(output,"progress.log"),id+" "+(passed?"PASS":"CHECK")+"\n");
    }
    static string Value(OfficeResult sheet,string address){return sheet.rows.Single(r=>r.key==address).value;}
    async Task Run(string[] args){
        output=Directory.CreateDirectory(args[0]).FullName;
        string box=AppDomain.CurrentDomain.BaseDirectory;
        if(!WorkPolicy.Within(Store.Home,box)||!WorkPolicy.Within(WorkPolicy.PolicyPath,box)||!File.Exists(Path.Combine(box,"fixture-public.xml")))
            throw new Exception("Expected the isolated signed QA workspace");
        string project=Directory.CreateDirectory(Path.Combine(output,"Проверяемый проект")).FullName;
        var rule=new ProjectRule{Root=project,Analysis=true,DocumentAnalysis=true,Training=true};
        Store.Write(WorkPolicy.PolicyPath,new CompanyRules{Managed=true,LocalAI=true,Training=true,ChatHistory=true,Projects=new[]{rule}});
        var p=new Preferences{Models=args[1],CheckForUpdates=false,DownloadUpdates=false,AllowWeb=false,Personalize=false};Store.Save(p);
        string sheet=Path.Combine(project,"Реестр.xlsx");
        var data=new List<object[]>{new object[]{"Позиция","Количество","Тариф"}};
        for(int i=1;i<=30;i++)data.Add(new object[]{"Позиция "+i,i,20});
        await ProTools.Office(rule,sheet,"create",new Dictionary<string,object>{{"data",data}},CancellationToken.None);
        desktop=new Desktop(args[2]);var w=desktop.Window;w.ShowActivated=false;w.ShowInTaskbar=false;w.WindowStartupLocation=WindowStartupLocation.Manual;w.Left=-6300;w.Top=10;w.Show();
        try{
            await Test("P01","Подключение реального реестра в чат",async()=>{var t=await Send("Открой документ «"+sheet+"»");return t.ProjectOnly&&t.Text.Contains("Открыт Реестр.xlsx");});
            await Test("P02","Число по одному адресу",async()=>{var t=await Send("Какое число записано в ячейке B19? Назови число.");return Regex.IsMatch(t.Text,@"(?<!\d)18(?!\d)");});
            await Test("P03","Сумма всех строк диапазона B2:B31",async()=>{var t=await Send("Посчитай сумму всех 30 значений в диапазоне B2:B31. В ответе укажи итоговое число.");return Regex.IsMatch(t.Text,@"(?<!\d)465(?!\d)")&&!Regex.IsMatch(t.Text,@"не могу|невозможно",RegexOptions.IgnoreCase);});
            await Test("P04","Продолжение разговора об открытом документе",async()=>{
                await Send("Назовём ячейку B17 кодовым словом «Маяк». Подтверди адрес одним словом.");
                var t=await Send("Какую ячейку мы в предыдущем сообщении назвали «Маяк»? Ответь адресом.");return t.Text.Contains("B17");});
            await Test("P05","Две адресные правки одним сообщением",async()=>{
                var initial=File.ReadAllBytes(sheet);var proposal=await Send("Измени ячейку B2 на 9750 и C2 на 30");
                bool reviewed=initial.SequenceEqual(File.ReadAllBytes(sheet));
                var confirmed=await Send("Сохрани выбранные правки");
                var result=await ProTools.Office(rule,sheet,"read",null,CancellationToken.None);var b=Value(result,"B2");var c=Value(result,"C2");
                details="До подтверждения исходник сохранён="+reviewed+". После подтверждения B2=«"+b+"», C2=«"+c+"». Ответ на запрос: "+proposal.Text;
                return reviewed&&b=="9750"&&c=="30"&&confirmed.Text.Contains("Сохранено");});
            await Test("P06","Одиночная правка после составной команды",async()=>{
                await Send("Поставь 4 в ячейку B2");await Send("Сохрани выбранные правки");
                var r=await ProTools.Office(rule,sheet,"read",null,CancellationToken.None);details="B2="+Value(r,"B2");return Value(r,"B2")=="4";});
            await Test("P07","Поиск по разрешённому проекту из чата",async()=>{
                bool matched=false;var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(70)};
                timer.Tick+=(s,e)=>{
                    var dialog=Application.Current.Windows.OfType<Window>().FirstOrDefault(x=>x.IsVisible&&x.Title.StartsWith("Результаты"));
                    if(dialog==null)return;timer.Stop();var list=((Panel)dialog.Content).Children.OfType<ListBox>().Single();
                    var hit=list.Items.Cast<string>().FirstOrDefault(x=>x.Contains("Позиция 29")&&x.Contains("A30"));
                    matched=hit!=null;if(matched)list.SelectedItem=hit;dialog.DialogResult=matched;
                };
                timer.Start();try{var t=await Send("Найди в проекте Позиция 29");details="Источник Реестр.xlsx, ячейка A30: "+matched;return t.Text.Contains("Поиск")&&t.ProjectOnly&&matched;}
                finally{timer.Stop();}});
            await Test("P08","Изменение ширины панели с клавиатуры",async()=>{
                var divider=(GridSplitter)w.FindName("PreviewSplitter");var columns=((Grid)w.FindName("Layout")).ColumnDefinitions;
                w.Width=1140;w.UpdateLayout();double before=columns[3].ActualWidth;
                divider.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(divider),0,Key.Left){RoutedEvent=Keyboard.KeyDownEvent});
                await w.Dispatcher.InvokeAsync(()=>w.UpdateLayout(),DispatcherPriority.Background);
                details="Ширина "+before+" → "+columns[3].ActualWidth+" DIP";return Math.Abs(before-columns[3].ActualWidth)>=1;});
            await Test("P09","Контекст рабочего файла после закрытия панели",async()=>{
                string note=Path.Combine(project,"Пропуск.docx");
                await ProTools.Office(rule,note,"create",new Dictionary<string,object>{{"data",new[]{"Пропуск склада ЛЮТИК-6439."}}},CancellationToken.None);
                await Send("Открой документ «"+note+"»");var fromFile=await Send("Какой код пропуска склада указан в открытом документе?");
                ((Button)w.FindName("ClosePreview")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var general=await Send("Какой код пропуска был в рабочем документе? Если сведений нет, ответь «неизвестно».");
                details="Код обнаружен внутри рабочей папки="+fromFile.Text.Contains("6439")+"; обычный чат повторил код="+general.Text.Contains("6439");
                return fromFile.ProjectOnly&&fromFile.Text.Contains("6439")&&!general.ProjectOnly&&!general.Text.Contains("6439");});
            await Test("P10","Переход «В чат» сохраняет ожидающую правку",async()=>{
                bool before=File.Exists(sheet);string setupError=null;var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(90)};
                timer.Tick+=async(s,e)=>{
                    var editor=Application.Current.Windows.OfType<ProjectWindow>().FirstOrDefault(x=>x.IsVisible);
                    if(editor==null)return;timer.Stop();
                    try{
                        typeof(ProjectWindow).GetMethod("SelectLocation",Private).Invoke(editor,new object[]{project,true});
                        await (Task)typeof(ProjectWindow).GetMethod("Open",Private).Invoke(editor,new object[]{sheet,null});
                        typeof(ProjectWindow).GetMethod("AddPatch",Private).Invoke(editor,new object[]{"B4","3","88","replace","Проверка перехода"});
                        var back=(Button)((FrameworkElement)editor.Content).FindName("BackToChat");back.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }catch(Exception ex){setupError=ex.GetBaseException().Message;editor.Hide();}
                };
                timer.Start();try{((Button)w.FindName("Projects")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));}finally{timer.Stop();}
                if(setupError!=null)throw new InvalidOperationException(setupError);
                var prior=await ProTools.Office(rule,sheet,"read",null,CancellationToken.None);
                var t=await Send("Сохрани выбранные правки");
                var final=await ProTools.Office(rule,sheet,"read",null,CancellationToken.None);details="B4 до подтверждения="+Value(prior,"B4")+", после="+Value(final,"B4");
                return before&&Value(prior,"B4")=="3"&&t.ProjectOnly&&Value(final,"B4")=="88";});
            string inbox=Directory.CreateDirectory(Path.Combine(project,"Входящие")).FullName;
            await Send("Открой папку «"+inbox+"»");
            File.Copy(Path.Combine(Path.GetDirectoryName(args[3]),"scan-1.pdf"),Path.Combine(inbox,"Накладная.pdf"));
            for(int i=1;i<=19;i++)File.WriteAllText(Path.Combine(inbox,"Поставка "+i+".txt"),
                "Счёт № "+(5000+i)+" от 01.11.2026. Итого к оплате "+(2000+i)+" рублей.");
            string invoices=Path.Combine(inbox,"Реестр счетов OSTUM.xlsx");
            Func<bool,Task<Turn>> import=async accept=>{
                var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(40)};
                timer.Tick+=(s,e)=>{var win=Application.Current.Windows.OfType<Window>().FirstOrDefault(v=>v.IsVisible&&v.Title=="Проверка счетов перед записью");if(win!=null){timer.Stop();win.DialogResult=accept;}};
                timer.Start();try{return await Send("Собери реестр счетов");}finally{timer.Stop();}
            };
            await Test("N01","Отмена черновика не создаёт реестр",async()=>{await import(false);return !File.Exists(invoices);});
            await Test("N02","Один скан и 19 счетов из папки в числовой реестр",async()=>{
                var t=await import(true);if(!File.Exists(invoices))return false;var book=await ProTools.Office(rule,invoices,"read",null,CancellationToken.None);
                details=t.Text;return book.rows.Count(c=>Regex.IsMatch(c.key,@"^A\d+$"))==21&&book.rows.Any(c=>c.value=="12680")&&book.rows.Any(c=>c.value=="3174")&&book.rows.Any(c=>c.value.Contains("стр. 1"));});
            await Test("N03","Повторный импорт не изменяет готовый реестр",async()=>{
                var before=File.ReadAllBytes(invoices);var response=await import(true);details=response.Text;return before.SequenceEqual(File.ReadAllBytes(invoices));});
            string memo=Path.Combine(inbox,"Поручение.docx"),taskBook=Path.Combine(inbox,"Поручения OSTUM.xlsx");
            await ProTools.Office(rule,memo,"create",new Dictionary<string,object>{{"data",new[]{"Проверить накладную."}}},CancellationToken.None);
            var original=await ProTools.Office(rule,memo,"read",null,CancellationToken.None);
            for(int i=0;i<3;i++)await ProTools.Office(rule,taskBook,"task",new Dictionary<string,object>{{"other",memo},{"revision",original.revision},{"key","1"},{"sheet",""},{"owner","Склад"},{"title",new[]{"Передать счёт","Закрыть неделю","Проверено ранее"}[i]},{"due",DateTime.Today.AddDays(i==1?0:-1).ToString("yyyy-MM-dd")}},CancellationToken.None);
            var beforeDone=await ProTools.Office(rule,taskBook,"read",null,CancellationToken.None);
            await ProTools.Office(rule,taskBook,"commit",new Dictionary<string,object>{{"sheet","Поручения"},{"revision",beforeDone.revision},{"patches",new[]{new OfficePatch{key="D4",value="Готово",operation="replace"}}}},CancellationToken.None);
            await Test("N04","Просроченные поручения со ссылкой на источник",async()=>{var t=await Send("Что просрочено?");return t.Text.Contains("Передать счёт")&&t.Text.Contains("Поручение.docx")&&!t.Text.Contains("Закрыть неделю")&&!t.Text.Contains("Проверено ранее");});
            await Test("N05","Открытые сроки до воскресенья",async()=>{var t=await Send("Что сдаём на неделе?");return t.Text.Contains("Закрыть неделю")&&!t.Text.Contains("Передать счёт")&&!t.Text.Contains("Проверено ранее");});
            await Test("N06","Повторный экспорт календаря сохраняет UID",async()=>{
                var req=new Dictionary<string,object>{{"period","calendar"},{"today",DateTime.Today.ToString("yyyy-MM-dd")},{"calendar",true}};
                var first=await ProTools.Office(rule,taskBook,"deadlines",req,CancellationToken.None);var again=await ProTools.Office(rule,taskBook,"deadlines",req,CancellationToken.None);
                Func<string,string[]> ids=text=>text.Split('\n').Where(l=>l.StartsWith("UID:")).ToArray();
                File.WriteAllText(Path.Combine(output,"Сроки.ics"),again.calendar);return ids(first.calendar).Length==2&&ids(first.calendar).SequenceEqual(ids(again.calendar))&&again.calendar.Contains("DUE;VALUE=DATE:")&&!again.calendar.Contains("Проверено ранее");});
        }finally{w.Close();}
        AdapterVersion version=null;
        var pairs=Store.Json().Deserialize<List<TrainingPair>>(File.ReadAllText(args[3]));
        await Test("T01","Проверка минимального набора обучения",async()=>{
            try{await ProjectTraining.Train(rule,p,pairs.Take(2).ToList(),"",s=>{},CancellationToken.None);return false;}
            catch(InvalidDataException e){details=e.Message;return true;}});
        await Test("T02","Обучение отдельного проектного адаптера",async()=>{
            using(var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(9))){
                await ProjectTraining.Train(rule,p,pairs,"",s=>File.AppendAllText(Path.Combine(output,"training.log"),s+"\n"),timeout.Token);}
            version=ProjectTraining.Versions(rule).Single();var report=ProjectTraining.Report(rule,version);Store.Write(Path.Combine(output,"evaluation.json"),report);
            details=string.Format(CultureInfo.InvariantCulture,"Обучение {0}; контроль {1}; F1 {2:F3} → {3:F3}; ранг {4}",report.training,report.held_out,report.baseline,report.adapted,report.rank);
            return report.training>0&&report.held_out>0&&report.training+report.held_out==pairs.Count&&report.rank>0;});
        await Test("T03","Выбор адаптера ограничен его проектом",async()=>{
            await Task.Yield();if(version==null)throw new InvalidOperationException("Training produced no version");
            ProjectTraining.Select(rule,version);p.ProjectScope=project;string scoped=ProjectTraining.Resolve(p);
            p.ProjectScope=null;bool general=ProjectTraining.Resolve(p)==null;details="Активен в проекте="+File.Exists(scoped)+", общий чат изолирован="+general;
            return File.Exists(scoped)&&general;});
        await Test("T04","Отзыв тестовой версии",async()=>{
            await Task.Yield();if(version==null)throw new InvalidOperationException("Training produced no version");
            ProjectTraining.Revoke(rule,version);p.ProjectScope=project;
            return ProjectTraining.Resolve(p)==null&&ProjectTraining.Versions(rule).Single().Revoked;});
    }
    [STAThread]static int Main(string[] args){
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};int result=1;
        app.Startup+=async(s,e)=>{try{await new WorkdayProject().Run(args);result=0;}catch(Exception ex){File.WriteAllText(Path.Combine(args[0],"error.log"),ex.ToString());}finally{app.Shutdown();}};
        app.Run();return result;
    }
}
