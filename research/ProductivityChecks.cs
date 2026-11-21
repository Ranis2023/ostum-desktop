using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Sfera;

internal static class ProductivityChecks {
    static readonly List<string> passed=new List<string>();
    static readonly CancellationToken Token=CancellationToken.None;
    static void Check(bool ok,string name){if(!ok)throw new Exception(name);passed.Add(name);Console.WriteLine("PASS "+name);}
    static void Reject(Action action,string name){try{action();}catch(IOException){Check(true,name);return;}catch(UnauthorizedAccessException){Check(true,name);return;}throw new Exception(name);}
    static Dictionary<string,object> Args(params object[] values){var result=new Dictionary<string,object>();for(int i=0;i<values.Length;i+=2)result.Add((string)values[i],values[i+1]);return result;}
    static T Field<T>(object obj,string name){return (T)obj.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(obj);}
    static object Call(object obj,string name,params object[] args){return obj.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic).Invoke(obj,args);}
    static IEnumerable<T> Visuals<T>(DependencyObject root) where T:DependencyObject{
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);if(child is T)yield return (T)child;foreach(var n in Visuals<T>(child))yield return n;}
    }
    static bool Shows(DependencyObject root,string value){
        return Visuals<TextBlock>(root).Any(x=>x.Text.Contains(value))||Visuals<RichTextBox>(root).Any(b=>
            new System.Windows.Documents.TextRange(b.Document.ContentStart,b.Document.ContentEnd).Text.Contains(value));
    }
    static async Task Capture(Window w,string name,int width,int height){
        w.Width=width;w.Height=height;w.UpdateLayout();
        await w.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ContextIdle);await Task.Delay(120);
        w.UpdateLayout();var client=(FrameworkElement)VisualTreeHelper.GetChild(w,0);
        int cw=(int)Math.Ceiling(client.ActualWidth),ch=(int)Math.Ceiling(client.ActualHeight);
        var surface=new DrawingVisual();using(var drawing=surface.RenderOpen()){
            var area=new Rect(0,0,cw,ch);drawing.DrawRectangle(w.Background,null,area);drawing.DrawRectangle(new VisualBrush(client){Stretch=Stretch.Fill},null,area);
        }
        var bitmap=new RenderTargetBitmap(cw,ch,96,96,PixelFormats.Pbgra32);bitmap.Render(surface);
        var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));
        using(var file=File.Create(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,name)))png.Save(file);
    }
    static async Task Dialog(string title,Action open,Func<Window,Task> inspect){
        var done=new TaskCompletionSource<bool>();var timeout=Stopwatch.StartNew();var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(45)};
        timer.Tick+=async(s,e)=>{
            var w=Application.Current.Windows.OfType<Window>().FirstOrDefault(v=>v.IsVisible&&v.Title==title);
            if(w==null&&timeout.Elapsed.TotalSeconds<8)return;timer.Stop();
            try{if(w==null)throw new Exception("Missing dialog "+title);await inspect(w);done.SetResult(true);}
            catch(Exception ex){done.SetException(ex);}finally{if(w!=null&&w.IsVisible)w.Close();}
        };
        timer.Start();try{open();await done.Task;}finally{timer.Stop();}
    }
    static async Task History(string root,ProjectRule project){
        var chat=new Conversation{Title="Контрольный диалог"};
        for(int i=0;i<88;i++)chat.Turns.Add(new Turn{Role=i%2==0?"user":"assistant",Text=i==5?"Проект маяк-57 начат":"Реплика "+i});
        chat.Turns.Insert(2,new Turn{Role="assistant",Text="закрытая реплика-57",ProjectOnly=true});Store.Save(chat);
        Store.AssignProject(chat,Store.ProjectId(project.Root));var plain=new Conversation{Title="Архив"};plain.Turns.Add(new Turn{Role="user",Text="МАЯК-57 завершён"});Store.Save(plain);Store.SetArchived(plain,true);
        var found=Store.SearchMessages("МАЯК-57",chat.ProjectKey,false,Token);
        Check(found.Count==1&&found[0].Position==5&&Store.LocateHit(chat,found[0])==6,"history-project-and-live-position");
        Check(Store.SearchMessages("маяк-57",null,true,Token).Count==2&&Store.SearchMessages("маяк-57","",false,Token).Count==0,"archive-and-personal-filter");
        Check(Store.SearchMessages("закрытая реплика-57",null,true,Token).Count==0&&!File.ReadAllText(Path.Combine(Store.Chats,chat.Id+".json")).Contains("закрытая"),"private-turn-stays-out-of-search");
        var app=new Desktop("fixture");app.Window.Left=-8000;app.Window.ShowActivated=false;app.Window.ShowInTaskbar=false;app.Window.Show();
        try{
            var search=(Button)app.Window.FindName("HistorySearch");
            await Dialog("Найти сообщение",()=>search.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)),async w=>{
                Visuals<TextBox>(w).First().Text="маяк-57";var table=Visuals<DataGrid>(w).Single();
                for(int i=0;i<120&&table.Items.Count==0;i++)await Task.Delay(35);
                Check(table.Items.Count==1,"sidebar-search-shows-exact-result");
                await Capture(w,"search-messages.png",770,460);
                Visuals<Button>(w).Single(b=>Equals(b.Content,"К сообщению")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            var host=(StackPanel)app.Window.FindName("Transcript");Check(host.Children.Count<=24&&Shows(host,"Проект маяк-57"),"deep-link-renders-exact-bounded-page");
            ((Button)app.Window.FindName("LaterTurns")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(host.Children.Count>24,"browse-forward-from-search");
            ((Button)app.Window.FindName("JumpLatest")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(Shows(host,"Реплика 87")&&host.Children.Count<=24,"return-to-latest-message");
        }finally{app.Window.Close();}
        Store.Write(WorkPolicy.PolicyPath,new CompanyRules{Managed=true,ChatHistory=false});try{Check(Store.SearchMessages("маяк-57",null,true,Token).Count==0,"managed-history-disabled");}finally{File.Delete(WorkPolicy.PolicyPath);}
    }
    static async Task Excel(Window owner,string root,ProjectRule project){
        string a=Path.Combine(root,"До.xlsx"),b=Path.Combine(root,"После.xlsx"),output=Path.Combine(root,"Отчёт.xlsx");
        var books=new[]{a,b};string[] rev=new string[2];string sheet=null;
        for(int i=0;i<2;i++){
            await ProTools.Office(project,books[i],"create",Args("data",new[]{new[]{"Артикул","Цена"},new[]{"A17",i==0?"90":"100"}}),Token);
            var book=await ProTools.Office(project,books[i],"read",null,Token);rev[i]=book.revision;sheet=book.sheet;
        }
        var recipe=new ReconcileRecipe{Name="Еженедельная сверка",Sheets=new[]{sheet,sheet},Keys=new[]{"Артикул","Артикул"}};recipe.Save(project);
        var saved=ReconcileRecipe.List(project).Single();Check(saved.Name==recipe.Name&&saved.Keys.SequenceEqual(recipe.Keys)&&!File.ReadAllText(Directory.GetFiles(ReconcileRecipe.Folder(project)).Single()).Contains(a),"workflow-persisted-without-source-paths");
        Check(ChatFileIntent.Read("Запусти сценарий сверки").Action=="recipes","chat-orchestrator-workflow-route");
        var request=Args("sources",books,"sheets",saved.Sheets,"keys",saved.Keys,"revisions",rev);
        var preview=await ProTools.Office(project,a,"preview_reconcile",request,Token);
        Check(preview.differences.Length==2&&preview.differences[1][3]=="90"&&preview.differences[1][4]=="100"&&preview.differences[1][6].EndsWith("!B2"),"preview-compares-values-and-cell-addresses");
        using(var engine=new Engine("fixture")){
            var workspace=new ProjectWindow(owner,new Preferences(),engine);workspace.ShowInTaskbar=false;workspace.ShowActivated=false;workspace.Left=-8000;workspace.Show();
            try{await Dialog("Проверка различий",()=>Call(workspace,"ReviewReconcile",preview.differences,saved),async w=>{
                Check(Visuals<DataGrid>(w).Single().Items.Count==1,"review-displays-difference-before-write");await Capture(w,"reconcile-preview.png",950,490);w.DialogResult=false;
            });}finally{workspace.Close();}
        }
        Check(!File.Exists(output),"cancel-preview-creates-no-workbook");
        await ProTools.Office(project,output,"reconcile",request,Token);
        Check((await ProTools.Office(project,output,"read",null,Token)).rows.Any(c=>c.value=="A17"),"approved-report-created");
        rev[0]="outdated";bool stale=false;try{await ProTools.Office(project,Path.Combine(root,"Устарело.xlsx"),"reconcile",request,Token);}catch(IOException){stale=true;}
        Check(stale&&!File.Exists(Path.Combine(root,"Устарело.xlsx")),"changed-source-rejected");
        var next=new[]{Path.Combine(root,"Неделя-1.xlsx"),Path.Combine(root,"Неделя-2.xlsx")};
        for(int i=0;i<2;i++){
            await ProTools.Office(project,next[i],"create",Args("data",new[]{new[]{"Артикул","Цена"},new[]{"B24",i==0?"120":"135"}}),Token);
            rev[i]=(await ProTools.Office(project,next[i],"read",null,Token)).revision;
        }
        request["sources"]=next;
        var rerun=await ProTools.Office(project,next[0],"preview_reconcile",request,Token);
        Check(rerun.differences[1][1]=="B24"&&rerun.differences[1][4]=="135","saved-workflow-runs-on-another-pair");
        request["keys"]=new[]{"Несуществующий заголовок","Артикул"};bool missing=false;
        try{await ProTools.Office(project,next[0],"preview_reconcile",request,Token);}catch(IOException){missing=true;}
        Check(missing,"missing-key-requires-workflow-correction");
    }
    static async Task Meetings(Window owner,string root){
        var lines=new List<MeetingLine>{new MeetingLine{Id=1,Start=1000,End=3500,Speaker="Иван",Text="Иван подготовит отчёт к пятнице."},new MeetingLine{Id=2,Start=5000,End=8500,Speaker="Анна",Text="Решили открыть филиал."}};
        var parsed=MeetingMinutes.Parse("{\"items\":[{\"id\":1,\"kind\":\"task\",\"title\":\"Подготовить отчёт\",\"quote\":\"подготовит отчёт\",\"owner\":\"Иван\",\"due\":\"к пятнице\"},{\"id\":2,\"kind\":\"decision\",\"title\":\"Открыть филиал\",\"quote\":\"открыть филиал\",\"owner\":\"Несуществующий\",\"due\":\"никогда\"},{\"id\":17,\"kind\":\"task\",\"title\":\"Лишнее\",\"quote\":\"Да\"}]}",lines);
        Check(parsed.Count==2&&parsed[0].Due=="к пятнице"&&parsed[1].Owner==""&&parsed[1].Due==""&&parsed.All(i=>!i.Selected),"minutes-validate-source-owner-deadline-and-selection");
        string source=Path.Combine(root,"meeting.wav"),outFile=Path.Combine(root,"Протокол.txt");
        File.WriteAllBytes(source,new byte[12]);Reject(()=>MeetingMinutes.Save(source,outFile,lines,parsed,false),"empty-approval-rejected");parsed[0].Selected=true;
        MeetingMinutes.Save(source,outFile,lines,parsed,false);string minutes=File.ReadAllText(outFile);
        Check(minutes.Contains("Иван")&&minutes.Contains("к пятнице")&&minutes.Contains("00:00:01")&&!minutes.Contains("филиал"),"only-approved-task-exported-with-evidence");
        Reject(()=>MeetingMinutes.Save(source,outFile,lines,parsed,true),"existing-minutes-preserved");
        Reject(()=>MeetingMinutes.Save(source,Path.Combine(Path.GetDirectoryName(root),"чужой.txt"),lines,parsed,true),"minutes-cannot-cross-project-boundary");
        var mixed=Enumerable.Range(0,8).Select(i=>new MeetingLine{Id=i,Voice=i%2==0?new[]{0.0,.1}:new[]{2.1,2.0}}).ToList();MeetingAudio.LabelSpeakers(mixed,2);
        Check(mixed[0].Speaker!=mixed[1].Speaker&&mixed[0].Speaker==mixed[2].Speaker,"mono-timbre-clustering");
        var prefs=new Preferences();string models=Environment.GetEnvironmentVariable("OSTUM_QA_MODELS"),engines=Environment.GetEnvironmentVariable("OSTUM_QA_ENGINES"),wav=Environment.GetEnvironmentVariable("OSTUM_QA_STEREO");
        using(var engine=new Engine(engines??"fixture")){
            var meeting=new MeetingWindow(owner,prefs,engine,engines??"fixture");
            await Dialog("Совещание",meeting.Show,async w=>{
                Check(Visuals<Button>(w).Any(b=>Equals(b.Content,"Распознать")&&!b.IsEnabled)&&Visuals<Button>(w).Any(b=>Equals(b.Content,"Записать с микрофона…")),"meeting-controls-initial-state");
                Call(meeting,"Reset",source);Field<List<MeetingLine>>(meeting,"lines").AddRange(lines);Field<DataGrid>(meeting,"speech").Items.Refresh();
                var candidates=Field<System.Collections.ObjectModel.ObservableCollection<MeetingItem>>(meeting,"items");foreach(var row in parsed)candidates.Add(row);
                Field<TabControl>(meeting,"tabs").SelectedIndex=1;Call(meeting,"Update");
                await Capture(w,"meeting-review.png",940,520);
            });
            if(!string.IsNullOrEmpty(wav)){
                var real=await Task.Run(()=>MeetingAudio.Transcribe(wav,engines,models,0,s=>{},Token));
                Store.Write(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"meeting-control.json"),real);
                Check(real.Any(l=>l.Speaker=="Канал 1"&&l.Text.Contains("Иван"))&&real.Any(l=>l.Speaker=="Канал 2"&&l.Text.ToLowerInvariant().Contains("решили")&&l.Start>=2500)&&real.All(l=>l.End>l.Start),"real-whisper-stereo-separation-and-timecodes");
                if(Environment.GetEnvironmentVariable("OSTUM_QA_ZARUM")=="1"){
                    prefs.Models=models;var suggestions=await MeetingMinutes.Analyze(wav,real,prefs,engine,s=>{},Token);
                    Check(suggestions.Any(i=>i.Type=="Поручение"&&i.Line==1)&&suggestions.Any(i=>i.Type=="Решение"&&i.Line==2),"real-zarum-extracts-task-and-decision");
                    foreach(var item in suggestions)item.Selected=true;
                    string target=Path.Combine(Path.GetDirectoryName(wav),"qa-minutes-"+Guid.NewGuid().ToString("N")+".txt");
                    try{MeetingMinutes.Save(wav,target,real,suggestions,true);Check(File.ReadAllText(target).Contains("Канал 2"),"stereo-audio-to-reviewed-minutes-end-to-end");}
                    finally{if(File.Exists(target))File.Delete(target);}
                }
            }
        }
    }
    static async Task Storage(Window owner){
        var prefs=new Preferences();Directory.CreateDirectory(prefs.Models);string part=Store.Files["voice"].PathIn(prefs.Models)+".part";File.WriteAllText(part,"fixture");
        var items=StorageManager.Scan(prefs,Token);var candidate=items.Single(x=>x.Path==part);
        Check(candidate.Removable&&items.Any(x=>x.Group=="Личные данные"&&!x.Removable),"storage-protects-personal-data");
        File.AppendAllText(part," changed");Reject(()=>StorageManager.Remove(candidate,prefs,Token),"stale-cleanup-selection-rejected");
        var fresh=StorageManager.Scan(prefs,Token).Single(x=>x.Path==part);StorageManager.Remove(fresh,prefs,Token);Check(!File.Exists(part),"selected-download-cache-removed");
        var userFile=Path.Combine(prefs.Models,"Пользовательский файл.txt");File.WriteAllText(userFile,"keep");
        Reject(()=>StorageManager.Remove(new StorageItem{Path=userFile,Removable=true,Kind="model"},prefs,Token),"arbitrary-path-cannot-be-deleted");
        string keep=Store.Files["q9"].PathIn(prefs.Models);File.WriteAllText(keep,"synthetic active file");
        Check(!StorageManager.Scan(prefs,Token).Single(x=>x.Path==keep).Removable&&File.Exists(userFile),"active-model-and-unlisted-file-preserved");
        string old=Path.Combine(Store.Home,"runtime",new string('A',64));Directory.CreateDirectory(old);File.WriteAllText(Path.Combine(old,"ready"),Path.GetFileName(old));
        File.WriteAllText(Path.Combine(old,"office.py"),"fixture");File.WriteAllText(Path.Combine(old,"user.txt"),"keep");
        Check(!StorageManager.Scan(prefs,Token).Single(x=>x.Path==old).Removable,"unrecognized-runtime-file-protected");
        File.Delete(Path.Combine(old,"user.txt"));var cache=StorageManager.Scan(prefs,Token).Single(x=>x.Path==old);StorageManager.Remove(cache,prefs,Token);
        Check(!Directory.Exists(old),"old-allowlisted-runtime-removable");
        await Dialog("Место на диске",()=>StorageWindow.Show(owner,prefs),async w=>{
            var grid=Visuals<DataGrid>(w).Single();for(int i=0;i<100&&grid.Items.Count==0;i++)await Task.Delay(35);
            Check(grid.Items.Count>0&&Visuals<TextBlock>(w).Any(t=>t.Text.Contains("свободно")),"storage-view-shows-free-space-and-inventory");
            await Capture(w,"storage-review.png",810,485);
        });
    }
    internal static async Task Run(Window owner){
        string test=AppDomain.CurrentDomain.BaseDirectory;
        if(!WorkPolicy.Within(Store.Home,test)||!WorkPolicy.Within(WorkPolicy.PolicyPath,test))throw new Exception("Acceptance tests require an isolated profile.");
        if(File.Exists(WorkPolicy.PolicyPath))File.Delete(WorkPolicy.PolicyPath);ProAccess.Import(File.ReadAllLines(Path.Combine(test,"fixtures.txt"))[3]);
        string root=Directory.CreateDirectory(Path.Combine(test,"Productivity-"+Guid.NewGuid().ToString("N"))).FullName;
        WorkPolicy.Register(root,true,false);var project=WorkPolicy.Projects().First(x=>x.Root==root);
        await History(root,project);await Excel(owner,root,project);await Meetings(owner,root);await Storage(owner);
        Store.Write(Path.Combine(test,"productivity-results.json"),passed);
    }
}
