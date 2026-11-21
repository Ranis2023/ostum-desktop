using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Sfera;

// Exercises the shipped WPF Send path with synthetic data. User workspaces and accounts are never selected.
public class WorkdayFlows {
    const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr handle);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr FindWindow(string windowClass,string title);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint pid);
    [DllImport("user32.dll")] static extern IntPtr GetDlgItem(IntPtr hwnd,int id);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd,uint message,IntPtr wParam,IntPtr lParam);
    readonly List<object> results=new List<object>();
    readonly string output,fixtures;
    readonly Desktop app;
    Turn last;
    string evidence;
    bool hideText;
    WorkdayFlows(Desktop desktop,string directory,string samples){app=desktop;output=directory;fixtures=samples;}
    Conversation Active {get{return (Conversation)typeof(Desktop).GetField("active",Private).GetValue(app);}}
    T Control<T>(string name) where T:FrameworkElement {return (T)app.Window.FindName(name);}
    object Invoke(string name,params object[] args){return typeof(Desktop).GetMethod(name,Private).Invoke(app,args);}
    void Click(string name){Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));}
    void New(){Click("NewChat");}
    async Task<Turn> Send(string text,int interactiveBudgetSeconds=0){
        var current=Active;int before=current.Turns.Count;
        Control<TextBox>("Input").Text=text;var work=(Task)Invoke("Send");
        if(interactiveBudgetSeconds>0&&await Task.WhenAny(work,Task.Delay(TimeSpan.FromSeconds(interactiveBudgetSeconds)))!=work){
            Click("Send");evidence="Остановлено кнопкой чата по бюджету "+interactiveBudgetSeconds+" с.";
        }
        await work;
        if(current.Turns.Count<before+2||current.Turns.Last().Role!="assistant")throw new Exception("No completed assistant response. "+Control<TextBlock>("Activity").Text);
        return last=current.Turns.Last();
    }
    async Task Attach(string file){
        Invoke("LoadFile",Path.Combine(fixtures,file));
        var watch=Stopwatch.StartNew();
        while((bool)typeof(Desktop).GetField("reading",Private).GetValue(app)){
            if(watch.Elapsed.TotalSeconds>45)throw new TimeoutException("Attachment read timed out");
            await Task.Delay(30);
        }
    }
    static bool Has(string text,string part){return (text??"").IndexOf(part,StringComparison.OrdinalIgnoreCase)>=0;}
    static bool Role(Turn t,string id){return (t.Workers??new List<WorkerEvent>()).Any(w=>w.Id==id&&w.State=="done");}
    async Task Check(string id,string title,Func<Task<bool>> test){
        var sw=Stopwatch.StartNew();bool passed=false;string error=null;last=null;evidence="";hideText=false;
        try{passed=await test();}catch(Exception ex){error=ex.GetBaseException().Message;}
        results.Add(new {id=id,title=title,passed=passed,seconds=Math.Round(sw.Elapsed.TotalSeconds,3),evidence=evidence,error=error,
            answer=last==null||hideText?"":last.Text??"",steps=last==null?null:last.Steps,
            workers=last==null?null:last.Workers,sources=last==null||last.Sources==null?null:last.Sources.Select(s=>new{s.Title,s.Location,s.Url}).ToArray()});
        Store.Write(Path.Combine(output,"flows.json"),results);
        File.AppendAllText(Path.Combine(output,"progress.log"),id+" "+(passed?"PASS":"CHECK")+" "+Math.Round(sw.Elapsed.TotalSeconds,2)+" s\n");
    }
    async Task DialogAndFiles(){
        await Check("F01","Исправление ранее названного срока",async()=>{
            New();await Send("Код проекта П-75. Срок передачи был 11 декабря. Ответь «принято».");
            await Send("Поправка по П-75. Окончательный срок теперь 19 декабря. Ответь «принято».");
            return Regex.IsMatch((await Send("Какой окончательный срок по П-75? Ответь только датой.")).Text,@"19\s*(декабр|\.12)");
        });
        await Check("F02","Изоляция нового диалога",async()=>{
            New();await Send("Учебный код этой беседы ГАРФ-619. Ответь «принято».");
            New();var t=await Send("Какой учебный код назывался в ЭТОЙ беседе? Если код не называли, ответь «не указан».");
            return !Has(t.Text,"ГАРФ-619")&&Regex.IsMatch(t.Text,@"не указан|не называл|не сообщ|нет",RegexOptions.IgnoreCase);
        });
        await Check("F03","Рабочий профиль и защита от дубля",async()=>{
            New();await Send("Запомни на будущее: тестовые отчёты называю «Янтарь-421».");
            var duplicate=await Send("Запомни на будущее: тестовые отчёты называю «Янтарь-421».");
            New();var t=await Send("Покажи мой рабочий профиль");
            return Has(duplicate.Text,"уже")&&Regex.Matches(t.Text,"Янтарь-421").Count==1;
        });
        await Check("F04","Просьба забыть одно предпочтение",async()=>{
            var response=await Send("Забудь моё предпочтение про Янтарь-421. Удали запись из рабочего профиля.");
            var persisted=await Send("Покажи мой рабочий профиль");
            evidence="Ответ на удаление: "+response.Text+" | Запись после проверки: "+Has(persisted.Text,"Янтарь-421");
            return !Has(persisted.Text,"Янтарь-421");
        });
        await Check("F05","PDF с противоречащими суммами и точной страницей",async()=>{
            New();await Attach("invoice.pdf");var t=await Send("Какова окончательная согласованная сумма? Назови сумму и страницу источника.");
            bool page=t.Sources!=null&&t.Sources.Any(s=>s.Title=="invoice.pdf"&&s.Location.Contains("2"));
            return Regex.IsMatch(t.Text,@"7[\s\u202f]*340")&&page;
        });
        await Check("F06","Вопрос с отсутствующим полем во вложении",async()=>{
            var t=await Send("Верно ли по документу, что перевозчик называется «Горизонт»?");
            return Has(t.Text,"Недостаточно данных")&&t.Sources!=null&&t.Sources.Count>0;
        });
        await Check("F07","Экспорт ответа с источниками",async()=>{
            var t=Active.Turns.Last();string md=Agents.Export(t);File.WriteAllText(Path.Combine(output,"answer-with-sources.md"),md);
            evidence=md.Substring(0,Math.Min(490,md.Length));await Task.Yield();
            return Has(md,"invoice.pdf")&&Has(md,"источник")&&Has(md,t.Text);
        });
        await Check("F08","Различия двух приложенных редакций",async()=>{
            New();await Attach("old.txt");await Send("Назови код этого документа.");
            await Attach("new.txt");var t=await Send("Сравни две версии");
            return Role(t,"diff")&&Has(t.Text,"6100")&&Has(t.Text,"6500")&&t.Sources!=null&&t.Sources.Count==2;
        });
        await Check("F09","CSV с похожими артикулами",async()=>{
            New();await Attach("offers.csv");var t=await Send("Укажи сумму для СК-0089. Используй приложенную таблицу.");
            return Regex.IsMatch(t.Text,@"5\s*820")&&t.Sources!=null&&t.Sources.Any(s=>s.Title=="offers.csv");
        });
        await Check("F10","PDF без текстового слоя",async()=>{
            New();await Attach("scan-1.pdf");evidence=Control<TextBlock>("Activity").Text;
            var scan=(ResearchDocument)typeof(Desktop).GetField("document",Private).GetValue(app);
            if(scan==null||!Has(evidence,"скан")||!Has(evidence,"изображени")||!scan.Parts.Any(p=>p.Text.Contains("3174")&&p.ScanNumber==1&&p.Box.Length==4))return false;
            var amount=scan.Parts.Single(p=>p.Text.Contains("12680"));
            var panel=(DockPanel)Control<ContentControl>("PreviewHost").Content;
            panel.Children.OfType<ListView>().Single().SelectedItem=amount;
            var canvas=(Canvas)panel.Children.OfType<StackPanel>().Single().Children.OfType<Viewbox>().Single().Child;
            var mark=canvas.Children.OfType<Border>().Single();
            evidence+=" Выделена сумма на скане шириной "+canvas.Width+" пикселей.";
            return canvas.Width>=350&&mark.Width>30&&Math.Abs(Canvas.GetTop(mark)/canvas.Height-amount.Box[1])<.002;
        });
        await Check("F11","Пустое текстовое вложение",async()=>{
            New();await Attach("empty.txt");evidence=Control<TextBlock>("Activity").Text;
            return Has(evidence,"пуст")||Has(evidence,"нет текста")&&!Has(evidence,"скан");
        });
        await Check("F12","Предупреждение об усечённом документе",async()=>{
            New();await Attach("large.txt");var d=(ResearchDocument)typeof(Desktop).GetField("document",Private).GetValue(app);
            evidence=Control<TextBlock>("Activity").Text;
            return d!=null&&d.Truncated&&Has(evidence,"часть")&&!d.Parts.Any(p=>p.Text.Contains("ЛБ-714"));
        });
        await Check("F13","Отмена и следующий запрос",async()=>{
            New();Control<TextBox>("Input").Text="Напиши подробный справочник из тридцати больших глав о делопроизводстве.";
            Task run=(Task)Invoke("Send");await Task.Delay(650);Click("Send");await run;
            var stopped=Active.Turns.Last();var t=await Send("Сколько будет 44*3?");
            evidence="После остановки: "+stopped.Text.Substring(Math.Max(0,stopped.Text.Length-100));
            return Has(stopped.Text,"останов")&&Has(t.Text,"132");
        });
        await Check("F14","Сохранение истории, архив и возврат",async()=>{
            New();var t=await Send("Сколько будет 347+18?");var c=Active;
            (typeof(Desktop).GetMethod("ChangeHistory",Private)).Invoke(app,new object[]{c,false});
            bool archived=Store.History().Any(h=>h.Id==c.Id&&h.Archived);
            Store.SetArchived(c,false);bool restored=Store.History().Any(h=>h.Id==c.Id&&!h.Archived);
            bool removed=Store.DeleteConversation(c)&&!Store.History().Any(h=>h.Id==c.Id);
            evidence="Архив="+archived+", восстановление="+restored+", удаление тестового диалога="+removed;
            return Has(t.Text,"365")&&archived&&restored&&removed;
        });
        await Check("F15","Исправление раскладки клавиатуры",async()=>{
            New();var t=await Send("crjkmrj ,eltn 17*8?");
            var input=Active.Turns[0];evidence="Распознанный запрос: "+input.Text;
            return Has(input.Text,"сколько")&&Has(t.Text,"136");
        });
        await Check("F16","Поиск фамилии и кода в разных вложениях",async()=>{
            New();await Attach("invoice.pdf");await Send("Назови ответственного за приёмку.");
            await Attach("new.txt");var t=await Send("Из двух вложений укажи имя ответственного за приёмку и код КД из второго файла. Приведи оба источника.");
            return Has(t.Text,"Тих")&&Has(t.Text,"428")&&t.Sources!=null&&t.Sources.Select(s=>s.Title).Distinct().Count()>=2;
        });
    }
    async Task MediaAndTools(){
        await Check("M01","Анализ цвета фотографии в чате",async()=>{
            New();await Attach("blue.png");var t=await Send("Какого цвета этот квадрат? Ответь одним словом.");
            return Regex.IsMatch(t.Text,@"син|голуб|ультрамарин",RegexOptions.IgnoreCase);
        });
        await Check("M02","Редактирование приложенного изображения",async()=>{
            New();await Attach("blue.png");var t=await Send("Измени это изображение: сделай фон оранжевым.",360);
            if(!t.Generated||!File.Exists(t.Image))return false;
            File.Copy(t.Image,Path.Combine(output,"edited.png"),true);
            using(var bmp=new Bitmap(t.Image)){int warm=0,total=0;for(int x=3;x<bmp.Width;x+=13)for(int y=3;y<bmp.Height;y+=13){
                var c=bmp.GetPixel(x,y);if(c.R>c.B+35&&c.R>c.G&&c.G>c.B)warm++;total++;}
                evidence="Тёплые оранжевые пиксели "+warm+"/"+total+"; размер "+bmp.Width+" × "+bmp.Height;
                return total>0&&warm>total*.50;}
        });
        await Check("M03","Создание нового изображения",async()=>{
            New();Click("ArtMode");bool fallback=false;Turn t;
            var watchDialog=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromMilliseconds(300)};
            uint pid;using(var self=Process.GetCurrentProcess())pid=(uint)self.Id;
            watchDialog.Tick+=(s,e)=>{
                IntPtr dialog=FindWindow("#32770","Генерация на процессоре");uint owner;
                if(dialog==IntPtr.Zero||GetWindowThreadProcessId(dialog,out owner)==0||owner!=pid)return;
                IntPtr quick=GetDlgItem(dialog,7);
                if(quick!=IntPtr.Zero&&PostMessage(quick,245,IntPtr.Zero,IntPtr.Zero)){fallback=true;watchDialog.Stop();}
            };
            watchDialog.Start();
            try{t=await Send("Нарисуй белую фарфоровую чашку на синем столе без надписей.");}finally{watchDialog.Stop();}
            if(!t.Generated||!File.Exists(t.Image))return false;
            File.Copy(t.Image,Path.Combine(output,"created.png"),true);
            using(var bmp=new Bitmap(t.Image)){evidence=bmp.Width+" × "+bmp.Height+(fallback?" · подтверждён эскиз CPU":" · основной режим");return bmp.Width>=384&&bmp.Height>=384&&Role(t,"paint");}
        });
        await Check("M04","Музыка десять секунд и проверка PCM",async()=>{
            New();var t=await Send("Сгенерируй музыку на 10 секунд для заставки: негромкое фортепиано и щётки.");
            if(!Role(t,"music")||!File.Exists(t.Audio))return false;
            var bytes=File.ReadAllBytes(t.Audio);double energy=0;int rate=0,channels=0,bits=0,samples=0;
            for(int i=12;i+8<=bytes.Length;){int len=BitConverter.ToInt32(bytes,i+4);if(len<0||len>bytes.Length-i-8)break;
                string id=Encoding.ASCII.GetString(bytes,i,4);
                if(id=="fmt "&&len>=16){channels=BitConverter.ToUInt16(bytes,i+10);rate=BitConverter.ToInt32(bytes,i+12);bits=BitConverter.ToUInt16(bytes,i+22);}
                if(id=="data"&&bits==16){samples=len/2;for(int p=i+8;p+1<i+8+len;p+=2){double v=BitConverter.ToInt16(bytes,p);energy+=v*v;}}
                i+=8+len+(len&1);}
            evidence=channels+" канала, "+rate+" Гц, "+samples+" отсчётов, RMS "+(samples==0?0:(int)Math.Sqrt(energy/samples));
            return channels==2&&rate==48000&&samples==10*rate*channels&&energy/samples>100;
        });
        await Check("M05","Выбор оборудования для диктовки",async()=>{
            await Task.Yield();var devices=MicrophoneCapture.Inputs();Click("MicDevices");
            var menu=Control<Button>("MicDevices").ContextMenu;bool shown=menu!=null&&menu.IsOpen;if(shown)menu.IsOpen=false;
            evidence="Входов Windows "+devices.Count+"; меню "+shown+"; USB PnP "+devices.Any(d=>Has(d.Name,"USB PnP"));
            return shown&&devices.Any(d=>Has(d.Name,"USB PnP"));
        });
        await Check("M06","Сеть выключена для текущего запроса",async()=>{
            New();Control<CheckBox>("WebSearch").IsChecked=false;var t=await Send("Без интернета объясни в одном предложении, что такое HTTPS.");
            return t.Text.Length>15&&(t.Sources==null||t.Sources.All(s=>string.IsNullOrEmpty(s.Url)))&&!Role(t,"web");
        });
        await Check("M07","Публичная ссылка и проверяемый источник",async()=>{
            New();Control<CheckBox>("WebSearch").IsChecked=true;
            try{var t=await Send("Изучи сайт https://science.nasa.gov/moon/ и кратко скажи, вокруг какой планеты обращается Луна. Укажи источник.");
                return Has(t.Text,"Земл")&&t.Sources!=null&&t.Sources.Any(s=>Has(s.Url,"nasa.gov")&&s.Excerpt.Length>60);
            }finally{Control<CheckBox>("WebSearch").IsChecked=false;}
        });
        await Check("M08","Два агента одновременно по накладной",async()=>{
            New();await Attach("invoice.pdf");Click("AgentMode");var t=await Send("Командой проверьте окончательную сумму и код приёмки по накладной. Укажите источник.");
            var workers=t.Workers??new List<WorkerEvent>();var x=workers.FirstOrDefault(w=>w.Id=="analyst");var y=workers.FirstOrDefault(w=>w.Id=="facts");
            return x!=null&&y!=null&&x.Started<=y.Ended&&y.Started<=x.Ended&&Has(t.Text,"4172")&&Regex.IsMatch(t.Text,@"7\s*340");
        });
        await Check("M09","Свернуть только собственное тестовое окно",async()=>{
            New();string name="OSTUM-WindowQA-"+Guid.NewGuid().ToString("N").Substring(0,8);
            var info=new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,"--window "+name){UseShellExecute=false,CreateNoWindow=true};
            using(var fixture=Process.Start(info)){
                try{
                    DateTime end=DateTime.UtcNow.AddSeconds(12);while(DateTime.UtcNow<end){fixture.Refresh();if(fixture.MainWindowHandle!=IntPtr.Zero)break;await Task.Delay(100);}
                    var t=await Send("Сверни окно "+name);await Task.Delay(250);fixture.Refresh();
                    return Role(t,"windows")&&IsIconic(fixture.MainWindowHandle);
                }finally{if(!fixture.HasExited){fixture.CloseMainWindow();if(!fixture.WaitForExit(1200))fixture.Kill();}}
            }
        });
    }
    async Task Rechecks(){
        string[] queries={
            "Напиши только окончательный код серии. В первой версии указали ИГ-48, затем заменили его на ИГ-84. Окончательный код — ИГ-84.",
            "Ответь только актуальным артикулом. Старая маркировка ИГ-48. Сейчас действует ИГ-84.",
            "Извлеки только поле «Код». Код: ИГ-84; Старая маркировка: ИГ-48."
        };
        for(int i=0;i<queries.Length;i++){
            string question=queries[i];await Check("R0"+(i+1),"Выбор текстового инструмента для кода, вариант "+(i+1),async()=>{
                New();var t=await Send(question,180);evidence=question+" | Создано изображение="+(t.Generated&&File.Exists(t.Image));return !t.Generated&&t.Text.Trim(' ','«','»','.')=="ИГ-84";
            });
        }
        await Check("R04","Два источника в обратном порядке с явными именами",async()=>{
            New();await Attach("new.txt");await Send("Каков код КД в этой редакции?");await Attach("invoice.pdf");
            var t=await Send("Из файла invoice.pdf возьми имя ответственного за приёмку, из файла new.txt — код с префиксом КД. Укажи для каждого его источник.");
            return Has(t.Text,"Тих")&&Regex.IsMatch(t.Text,@"КД\s*[-–]?\s*428",RegexOptions.IgnoreCase)&&t.Sources!=null&&t.Sources.Select(s=>s.Title).Distinct().Count()>=2;
        });
        await Check("R05","Черновик письма с согласованными условиями",async()=>{
            New();var t=await Send("Составь короткий черновик письма поставщику. Просим поставить 14 коробок к 27 мая. Обязательно включи оба условия, без выдуманной цены.");
            return !t.Generated&&Has(t.Text,"14")&&Has(t.Text,"27 мая")&&!Regex.IsMatch(t.Text,@"\d+\s*(руб|₽)",RegexOptions.IgnoreCase);
        });
        await Check("R06","Импорт локальных весов и тест самой модели",async()=>{
            var prefs=Store.Load();var imported=LocalModel.Inspect(Store.Files["q9"].PathIn(prefs.Models),Store.Files["v9"].PathIn(prefs.Models));
            prefs.ImportedModels=new List<LocalModel>{imported};prefs.Chat=imported.Id;
            var engine=(Engine)typeof(Desktop).GetField("engine",Private).GetValue(app);engine.StopChat();
            string reply=await engine.Role(prefs,"Answer only with the number.","What is three times seven?",null,32,false,s=>{},s=>{},CancellationToken.None);
            evidence=imported.Name+" | "+reply;return imported.Available()&&Regex.IsMatch(reply,@"(?<!\d)21(?!\d)");
        });
        await Check("R07","Кнопка измерения скорости модели на ПК",async()=>{
            var engine=(Engine)typeof(Desktop).GetField("engine",Private).GetValue(app);
            string measure=await engine.Measure(Store.Load(),CancellationToken.None);evidence=measure;
            return Has(measure,"токен")&&Regex.IsMatch(measure,@"\d+[.,]?\d*");
        });
        await Check("R08","Проба более быстрого предпросмотра 384 × 384",async()=>{
            var engine=(Engine)typeof(Desktop).GetField("engine",Private).GetValue(app);engine.StopChat();
            using(var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(3))){
                var image=await Task.Run(()=>engine.Paint(Store.Load(),"a plain bright orange background, solid orange color",Path.Combine(fixtures,"blue.png"),384,384,s=>{},timeout.Token));
                File.Copy(image,Path.Combine(output,"edit-preview-384.png"),true);
                using(var bmp=new Bitmap(image)){int warm=0,total=0;for(int x=4;x<bmp.Width;x+=11)for(int y=4;y<bmp.Height;y+=11){var c=bmp.GetPixel(x,y);if(c.R>c.B+35&&c.R>c.G&&c.G>c.B)warm++;total++;}
                    evidence="Результат "+bmp.Width+" × "+bmp.Height+", доля тёплых оранжевых пикселей "+warm+"/"+total;
                    return bmp.Width==384&&bmp.Height==384&&warm>total*.5;}
            }
        });
    }
    [STAThread]static int Main(string[] args){
        if(args[0]=="--window"){var app=new Application();var w=new Window{Title=args[1],Width=120,Height=80,Left=-6000,Top=10,WindowStartupLocation=WindowStartupLocation.Manual,ShowActivated=false};app.Run(w);return 0;}
        string dest=Directory.CreateDirectory(Path.GetFullPath(args[0])).FullName;
        try{
            string profile=Path.Combine(dest,"temporary-profile");
            typeof(Store).GetField("Home").SetValue(null,profile);
            if(!WorkPolicy.Within(Store.Chats,profile)||!WorkPolicy.Within(Store.Images,profile))throw new InvalidOperationException("Test profile isolation failed");
            Store.Save(new Preferences{Models=args[2],AllowWeb=false,Personalize=false,CheckForUpdates=false,DownloadUpdates=false});
            var application=new Application();var desktop=new Desktop(args[3]);
            var suite=new WorkdayFlows(desktop,dest,Path.GetFullPath(args[1]));
            var win=desktop.Window;win.ShowInTaskbar=false;win.ShowActivated=false;win.WindowStartupLocation=WindowStartupLocation.Manual;win.Left=-6400;win.Top=15;int rc=1;
            win.ContentRendered+=async(s,e)=>{try{if(args[4]=="office")await suite.DialogAndFiles();else if(args[4]=="repeat")await suite.Rechecks();else await suite.MediaAndTools();rc=0;}
                catch(Exception ex){File.WriteAllText(Path.Combine(dest,"error.log"),ex.ToString());}finally{win.Close();}};
            application.Run(win);return rc;
        }catch(Exception ex){File.WriteAllText(Path.Combine(dest,"error.log"),ex.ToString());return 1;}
    }
}
