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
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Sfera;

public static class WorkspaceChecks {
    static readonly string root=AppDomain.CurrentDomain.BaseDirectory;
    static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
    static IEnumerable<T> Visuals<T>(DependencyObject view) where T:DependencyObject{
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(view);i++){
            var child=VisualTreeHelper.GetChild(view,i);if(child is T)yield return (T)child;
            foreach(var next in Visuals<T>(child))yield return next;
        }
    }
    static void Denied(Action op){try{op();}catch(UnauthorizedAccessException){return;}throw new Exception("Expected a denial");}
    static object Invoke(object who,string name,params object[] args){return who.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic).Invoke(who,args);}
    static T Find<T>(Window w,string name) where T:FrameworkElement{return (T)((Grid)w.Content).FindName(name);}
    static DragEventArgs Drag(Window w,RoutedEvent route,string path,DragDropEffects effects){
        var data=new DataObject(DataFormats.FileDrop,new[]{path});var args=(DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs),BindingFlags.Instance|BindingFlags.NonPublic,null,new object[]{data,DragDropKeyStates.LeftMouseButton,effects,w,new Point(20,20)},null);
        args.RoutedEvent=route;w.RaiseEvent(args);return args;
    }
    static async Task Idle(ProjectWindow w){
        var flag=typeof(ProjectWindow).GetField("busy",BindingFlags.Instance|BindingFlags.NonPublic);
        for(int i=0;i<500&&(bool)flag.GetValue(w);i++)await Task.Delay(20);
        Check(!(bool)flag.GetValue(w),"UI action did not finish");
    }
    static void Render(Window w,string filename,int width,int height){
        var element=(FrameworkElement)w.Content;w.Width=width;w.Height=height;w.UpdateLayout();
        element.Measure(new Size(width,height));element.Arrange(new Rect(0,0,width,height));element.UpdateLayout();
        var actual=element.RenderSize;var bmp=new RenderTargetBitmap((int)Math.Floor(actual.Width),(int)Math.Floor(actual.Height),96,96,PixelFormats.Pbgra32);bmp.Render(element);
        var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bmp));using(var file=File.Create(Path.Combine(root,filename)))png.Save(file);
    }
    static async Task ChatUi(){
        var app=new Desktop("test");var window=app.Window;window.WindowStartupLocation=WindowStartupLocation.Manual;
        window.Left=-8000;window.Top=-8000;window.ShowActivated=false;window.ShowInTaskbar=false;window.Show();
        try {
            Render(window,"home.png",1100,702);
            var settings=new ModelSettings(window,new Preferences());
            var options=(Window)typeof(ModelSettings).GetField("window",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(settings);
            options.WindowStartupLocation=WindowStartupLocation.Manual;options.Left=-8000;options.Top=-8000;options.ShowInTaskbar=false;options.ShowActivated=false;options.Show();
            Render(options,"settings.png",660,720);
            foreach(var b in Visuals<Button>(options).Where(b=>b.Name!=""&&b.IsVisible&&b.Tag is string))
                Check(Visuals<OstumIcon>(b).Any(i=>OstumIcon.Known(i.Kind)),"Missing settings icon "+b.Name);
            options.Close();
            Invoke(app,"StartProgress");Invoke(app,"Progress","Аналитик сверяет числа");
            var stages=(WrapPanel)window.FindName("StageList");var stage=stages.Children[0];Invoke(app,"Progress","Читаю документ");
            Invoke(app,"Progress","Читаю документ снова");window.UpdateLayout();
            Check(stages.Children.Count==3&&stages.Children[0]==stage&&Visuals<OstumIcon>(stage).Any(i=>i.Kind=="check"),"Progress markers flickered or repeated");
            Invoke(app,"EndProgress");
            foreach(string name in new[]{"QuickDocument","QuickPhoto","QuickArt","QuickAgents","FullHelp"})
                Check(((Button)window.FindName(name)).IsVisible,"Missing home action "+name);
            Render(window,"home-compact.png",780,550);
            ((StackPanel)window.FindName("Welcome")).Visibility=Visibility.Collapsed;
            Invoke(app,"AddTurn",new Turn{Role="user",Text="Сверь сроки в двух предложениях и перечисли вопросы для поставщиков."},null);
            var host=(StackPanel)window.FindName("Transcript");host.Children.Add(new TextBlock{Text="Zarum",FontWeight=FontWeights.SemiBold});
            Invoke(app,"BeginWorkers",host);
            var first=new TaskCompletionSource<int>();var second=new TaskCompletionSource<int>();
            Action<WorkerEvent> update=evt=>window.Dispatcher.Invoke(()=>Invoke(app,"WorkerUpdate",evt));
            var read=Workers.Track(update,"analyst","Аналитик","Разбирает условия предложений",()=>first.Task);
            var check=Workers.Track(update,"facts","Проверка фактов","Сопоставляет сроки с источником",()=>second.Task);
            await Task.Delay(300);Render(window,"agents.png",1000,615);
            var panel=(WrapPanel)host.Children[host.Children.Count-1];Check(panel.Children.Count==2&&panel.IsVisible,"Concurrent agents missing");
            Check(panel.Children.Cast<FrameworkElement>().All(card=>card.ActualHeight<=31),"Agent chips are too tall");
            first.SetResult(1);second.SetResult(1);await Task.WhenAll(read,check);
            await Workers.Track(update,"facts","Проверка фактов","Повторная проверка",()=>Task.FromResult(1));
            var turn=new Turn{Role="assistant",Text="Проверка завершена."};Invoke(app,"FinishWorkers",turn);
            Check(turn.Workers.Count==3&&turn.Workers.All(x=>x.State=="done"),"Repeated role missing from the history");
            Console.WriteLine("PASS home, compact, two concurrently active agents and completion history");
        } finally{window.Close();}
    }
    static async Task Exercise(Window owner){
        var f=File.ReadAllLines(Path.Combine(root,"fixtures.txt"));var code=ProAccess.Device;
        Check(!ProAccess.Active,"Empty installation unlocked");
        string dir=Directory.CreateDirectory(Path.Combine(Store.Home,"Учебная папка")).FullName;
        var policy=new ProjectRule{Root=dir,Analysis=false,Training=false};
        Denied(()=>ProTools.UnderProject(policy,dir));Denied(()=>new ProjectWindow(owner,new Preferences(),null));
        Check(ProAccess.Verify(f[0],code)&&ProAccess.Verify(f[1],code)&&ProAccess.Verify(f[3],code),"Signed legacy or permanent receipt rejected");
        Check(!ProAccess.Verify(f[3],"AAAAAAAAAAAAAAAAAAAAAAAA"),"Foreign device unlocked");
        foreach(var bad in new[]{f[3].Replace("OP2.","OP1."),f[0]+"x",f[2],f[4],"yes"})Check(!ProAccess.Verify(bad,code),"Invalid receipt accepted");
        Check(Brand.SubscriptionBot=="rangoostum_bot","Wrong official Telegram bot");
        var gate=(Window)Activator.CreateInstance(typeof(ProAccessWindow),BindingFlags.Instance|BindingFlags.NonPublic,null,new object[]{owner},null);
        Render(gate,"gate.png",604,626);Check(Find<TextBox>(gate,"Request").Text=="p2_"+code,"Gate request mismatch");var qr=(BitmapSource)Find<Image>(gate,"Qr").Source;Check(qr.PixelWidth>=150,"QR missing");
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(qr));using(var stream=File.Create(Path.Combine(root,"qr-test.png")))encoder.Save(stream);gate.Close();
        ProAccess.Import(f[3]);Check(ProAccess.Active,"Activation failed");await ChatUi();
        using(var engine=new Engine("test")){
            var w=new ProjectWindow(owner,new Preferences(),engine);w.Left=-8000;w.Top=-8000;w.WindowStartupLocation=WindowStartupLocation.Manual;w.ShowInTaskbar=false;w.ShowActivated=false;w.Show();
            Render(w,"workspace.png",1040,650);Render(w,"compact.png",665,455);
            Check(Find<ScrollViewer>(w,"Landing").Visibility==Visibility.Visible&&Find<Border>(w,"Editor").Visibility==Visibility.Collapsed,"Landing visibility");
            Store.Write(WorkPolicy.PolicyPath,new CompanyRules{Managed=true,Projects=new[]{policy}});
            Invoke(w,"RefreshProjects",dir);await (Task)Invoke(w,"Example");
            var file=Directory.GetFiles(dir,"*.docx").Single();Check(Find<Border>(w,"Editor").Visibility==Visibility.Visible,"Word preview not open");
            var original=File.ReadAllBytes(file);var drag=Drag(w,DragDrop.PreviewDragOverEvent,file,DragDropEffects.Copy|DragDropEffects.Move);
            Check(drag.Handled&&drag.Effects==DragDropEffects.Copy&&Find<Border>(w,"DropOverlay").IsVisible,"Drop target did not appear");
            Drag(w,DragDrop.PreviewDropEvent,file,DragDropEffects.Copy);await Idle(w);
            Check(Find<Border>(w,"DropOverlay").Visibility==Visibility.Collapsed&&original.SequenceEqual(File.ReadAllBytes(file)),"Opening a dropped file changed it");
            Check(!Find<Button>(w,"Ask").IsEnabled&&!Find<Button>(w,"Train").IsEnabled,"Editor-only project permissions lost");
            var preview=(FlowDocumentScrollViewer)Find<ContentControl>(w,"Page").Content;
            var link=preview.Document.Blocks.OfType<Paragraph>().ElementAt(1).Inlines.OfType<Hyperlink>().Single();link.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
            Check(Find<Button>(w,"Edit").IsEnabled&&link.Background!=null,"Selecting a paragraph did not enable editing");
            var dialog=w.Dispatcher.BeginInvoke(new Action(()=>{var d=w.OwnedWindows.Cast<Window>().Single();var field=((Panel)d.Content).Children.OfType<TextBox>().Single();field.Text="Встреча в 14:00.";d.DialogResult=true;}));
            Find<Button>(w,"Edit").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await dialog.Task;
            Check(Find<TabControl>(w,"Tabs").SelectedIndex==1&&original.SequenceEqual(File.ReadAllBytes(file)),"Proposing a change wrote to the source");
            Find<Button>(w,"Save").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Idle(w);
            Check((await ProTools.Office(policy,file,"read",null,CancellationToken.None)).rows[1].value=="Встреча в 14:00.","Word edit/save");
            Render(w,"editor.png",1040,650);
            preview=(FlowDocumentScrollViewer)Find<ContentControl>(w,"Page").Content;
            preview.Document.Blocks.OfType<Paragraph>().ElementAt(1).Inlines.OfType<Hyperlink>().Single().RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
            var confirm=w.Dispatcher.BeginInvoke(new Action(()=>{var d=w.OwnedWindows.Cast<Window>().Single(x=>x.Title=="Поручение из документа");d.DialogResult=true;}));
            await (Task)Invoke(w,"AddTask");await confirm.Task;
            var register=Path.Combine(dir,"Поручения OSTUM.xlsx");Check(File.Exists(register),"No register created from a selected paragraph");
            await (Task)Invoke(w,"OpenRegister");
            var rg=(DataGrid)Find<ContentControl>(w,"RowsHost").Content;Find<TabControl>(w,"Tabs").SelectedIndex=2;
            rg.SelectedItem=rg.Items.Cast<OfficeRow>().First(r=>r.key=="A2");Invoke(w,"CompleteTask");Find<Button>(w,"Save").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Idle(w);
            Check((await ProTools.Office(policy,register,"read",null,CancellationToken.None)).rows.Any(r=>r.key=="D2"&&r.value=="Готово"),"Task completion failed");
            Find<TabControl>(w,"Tabs").SelectedIndex=2;rg.SelectedItem=rg.Items.Cast<OfficeRow>().First(r=>r.key=="A2");await(Task)Invoke(w,"TaskSource");
            Check(w.Title.Contains(Path.GetFileName(file))&&((OfficeRow)rg.SelectedItem).key=="2","Task failed to reopen its source paragraph");
            var book=Path.Combine(dir,"Смета.xlsx");await ProTools.Office(policy,book,"create",new System.Collections.Generic.Dictionary<string,object>{{"data",new[]{new[]{"Позиция","Сумма"},new[]{"Бумага","120"}}}},CancellationToken.None);
            Drag(w,DragDrop.PreviewDropEvent,book,DragDropEffects.Copy);await Idle(w);Check(Find<ComboBox>(w,"Sheets").Visibility==Visibility.Visible,"Dropped Excel did not open");
            await RepeatedOffice(policy,book);
            Check(Drag(w,DragDrop.PreviewDropEvent,file,DragDropEffects.Move).Effects==DragDropEffects.None&&Find<ComboBox>(w,"Sheets").Visibility==Visibility.Visible,"Move-only operation accepted");
            var outside=Directory.CreateDirectory(Path.Combine(root,"unapproved")).FullName;
            try{await (Task)Invoke(w,"OpenPaths",new[]{outside},false);throw new Exception("External project opened under company policy");}catch(UnauthorizedAccessException){}
            var locked=(Window)Activator.CreateInstance(typeof(ProAccessWindow),BindingFlags.Instance|BindingFlags.NonPublic,null,new object[]{owner},null);
            Check(Find<Button>(locked,"Verify").Visibility==Visibility.Collapsed&&Find<Button>(locked,"Channel").Visibility==Visibility.Collapsed,"Managed offline verification opened external controls");locked.Close();
            w.Close();await ChatWorkspace(dir,file,book);
        }
        Console.WriteLine("PASS gate, signature, perpetual grant, device, official bot, empty workspace, compact layout, routed drag and drop, DOCX/XLSX, manual proposal/save, managed folders and offline activation UI");
    }
    static Process ParkedReader(){
        var type=typeof(OfficeReader);var flags=BindingFlags.NonPublic;
        var reader=type.GetField("idle",flags|BindingFlags.Static).GetValue(null);
        return reader==null?null:(Process)type.GetField("child",flags|BindingFlags.Instance).GetValue(reader);
    }
    static async Task RepeatedOffice(ProjectRule rule,string source){
        string folder=Directory.CreateDirectory(Path.Combine(rule.Root,"Контроль чтения")).FullName;
        string book=Path.Combine(folder,"Две строки.xlsx");File.Copy(source,book);
        var ct=CancellationToken.None;
        string request=Store.Json().Serialize(new{path=book,action="read"});
        var ordinary=new List<long>();var repeated=new List<long>();
        var watch=new Stopwatch();int id=0;
        for(int i=0;i<=10;i++){
            watch.Restart();var text=await ProTools.Python("office.py",null,request,ct);
            long old=watch.ElapsedMilliseconds;var single=Store.Json().Deserialize<OfficeResult>(text);
            watch.Restart();var read=await ProTools.Office(rule,book,"read",null,ct);long warm=watch.ElapsedMilliseconds;
            Check(read.revision==single.revision&&read.rows.Length==single.rows.Length,"Reader and singleton disagree");
            if(i==0)id=ParkedReader().Id;
            else{ordinary.Add(old);repeated.Add(warm);Check(id==ParkedReader().Id,"Repeated reads spawned a new interpreter");}
        }
        var first=await ProTools.Office(rule,book,"read",null,ct);
        await ProTools.Office(rule,book,"commit",new Dictionary<string,object>{{"sheet",first.sheet},{"revision",first.revision},{"patches",new[]{new{key="B2",value="240"}}}},ct);
        var changed=await ProTools.Office(rule,book,"read",null,ct);
        Check(changed.revision!=first.revision&&changed.rows.Any(r=>r.key=="B2"&&r.value=="240"),"Warm reader did not see saved Excel edits");
        File.Copy(source,book,true);
        var external=await ProTools.Office(rule,book,"read",null,ct);
        Check(external.revision==first.revision&&external.rows.Any(r=>r.key=="B2"&&r.value=="120"),"Warm reader missed an external replacement");
        var concurrent=await Task.WhenAll(Enumerable.Range(0,3).Select(i=>ProTools.Office(rule,book,"read",null,ct)));
        Check(concurrent.All(r=>r.revision==first.revision),"Concurrent reads mixed responses");
        var crashed=ParkedReader();crashed.Kill();crashed.WaitForExit();
        Check((await ProTools.Office(rule,book,"read",null,ct)).revision==first.revision,"Reader did not recover from exit");
        await ProTools.Office(rule,book,"read",null,ct);
        var large=Path.Combine(folder,"Длинная таблица.xlsx");
        var rows=Enumerable.Range(0,3000).Select(i=>new[]{i.ToString(),"Ячейка "+i}).ToArray();
        await ProTools.Office(rule,large,"create",new Dictionary<string,object>{{"data",rows}},ct);
        using(var cancel=new CancellationTokenSource()){
            var pending=ProTools.Office(rule,large,"read",null,cancel.Token);cancel.Cancel();
            try{await pending;throw new Exception("Cancelled read returned a document");}catch(OperationCanceledException){}
        }
        Check((await ProTools.Office(rule,book,"read",null,ct)).revision==first.revision,"Cancelled reader affected the next request");
        try{await ProTools.Office(rule,book,"aggregate",new Dictionary<string,object>(),ct);throw new Exception("Unauthorized analysis succeeded");}catch(UnauthorizedAccessException){}
        int beforeSwitch=ParkedReader().Id;var savedPolicy=File.ReadAllBytes(WorkPolicy.PolicyPath);
        string otherDir=Directory.CreateDirectory(Path.Combine(root,"Other reader project")).FullName;
        var other=new ProjectRule{Root=otherDir};var otherBook=Path.Combine(otherDir,"Другой.xlsx");File.Copy(source,otherBook);
        try{
            Store.Write(WorkPolicy.PolicyPath,new CompanyRules{Managed=true,Projects=new[]{other}});
            try{await ProTools.Office(rule,book,"read",null,ct);throw new Exception("A revoked project reused its reader");}catch(UnauthorizedAccessException){}
            var otherResult=await ProTools.Office(other,otherBook,"read",null,ct);
            Check(otherResult.revision==first.revision&&ParkedReader().Id!=beforeSwitch,"Project switch did not isolate the interpreter");
            Check(!WorkPolicy.History&&Store.HistoryHeadings().Count==0,"Disabled history exposed headings");
            await Task.Delay(46000);
            Check(ParkedReader()==null,"Idle reader was not released");
        }finally{File.WriteAllBytes(WorkPolicy.PolicyPath,savedPolicy);}
        Store.Write(Path.Combine(root,"office-reader.json"),new{oneProcessPerReadMs=ordinary,warmReaderMs=repeated,
            passed=new[]{"identical-result","interpreter-reuse","saved-edit","external-replacement","parallel","crash-recovery","active-cancellation","permission-recheck","project-switch","idle-shutdown"}});
        Console.WriteLine("PASS Office worker, permission changes, cancellation, external saves and idle shutdown");
    }
    static async Task ChatWorkspace(string dir,string doc,string book){
        string retryFile=Path.Combine(root,"locked-write.json");
        Store.Write(retryFile,new {value=1});
        using(var reader=new FileStream(retryFile,FileMode.Open,FileAccess.Read,FileShare.Read)){
            var release=Task.Run(async()=>{await Task.Delay(90);reader.Dispose();});
            Store.Write(retryFile,new {value=2});await release;
        }
        Check(File.ReadAllText(retryFile).Contains("2"),"Transient sharing conflict lost the history update");
        var testRule=new ProjectRule{Root=dir,Analysis=false};
        Store.Write(WorkPolicy.PolicyPath,new CompanyRules{Managed=true,LocalAI=true,ChatHistory=true,Projects=new[]{testRule}});
        string modelLibrary=Environment.GetEnvironmentVariable("OSTUM_CHECK_MODELS");
        string engineFolder=Environment.GetEnvironmentVariable("OSTUM_CHECK_ENGINES");
        bool infer=!string.IsNullOrEmpty(modelLibrary)&&!string.IsNullOrEmpty(engineFolder);
        if(infer)Store.Save(new Preferences{Models=modelLibrary,Chat="quality",Personalize=false,AllowWeb=false,CheckForUpdates=false});
        var app=new Desktop(infer?engineFolder:"test");var win=app.Window;
        win.ShowInTaskbar=false;win.ShowActivated=false;win.WindowStartupLocation=WindowStartupLocation.Manual;win.Left=-7700;win.Top=0;win.Show();
        Func<string,Task<Turn>> send=async text=>{
            ((TextBox)win.FindName("Input")).Text=text;await(Task)Invoke(app,"Send");
            var chat=(Conversation)typeof(Desktop).GetField("active",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(app);
            var last=chat.Turns.Last();Check(last.Role=="assistant","Incomplete chat turn for "+text+": "+((TextBlock)win.FindName("Activity")).Text);return last;
        };
        try{
            var reply=await send("Открой документ «"+book+"»");Check(reply.ProjectOnly&&reply.Text.Contains(Path.GetFileName(book)),"Open in chat: "+reply.Text);
            Check(!WorkPolicy.Network&&!((CheckBox)win.FindName("WebSearch")).IsEnabled,"Web control was enabled after a managed request");
            var liveChat=(Conversation)typeof(Desktop).GetField("active",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(app);
            string historyPath=Path.Combine(Store.Chats,liveChat.Id+".json");
            var persistedAt=File.GetLastWriteTimeUtc(historyPath);
            var viewer=(Grid)((ContentControl)win.FindName("PreviewHost")).Content;
            Check(((ComboBox)viewer.FindName("Sheets")).IsVisible,"Spreadsheet did not render in chat");
            var cols=((Grid)win.FindName("Layout")).ColumnDefinitions;Check(cols.Count==4&&((GridSplitter)win.FindName("PreviewSplitter")).IsVisible,"Missing split view");
            reply=await send("Создай таблицу Смета отдела.xlsx");Check(reply.Text.Contains("Создан"),"Create workbook via chat: "+reply.Text);
            reply=await send("Открой таблицу «Сме»");Check(reply.Text.Contains("Уточните")&&reply.Text.Contains("Смета отдела.xlsx")&&reply.Text.Contains("Смета.xlsx"),"Ambiguous name was not clarified: "+reply.Text);
            reply=await send("Можешь открыть таблицу «Смета»?");Check(reply.Text.Contains("Открыт Смета.xlsx"),"Open workbook by name: "+reply.Text);
            var before=File.ReadAllBytes(book);
            reply=await send("Поставь 9471 в ячейку B2");Check(reply.Text.Contains("Правки")&&File.ReadAllBytes(book).SequenceEqual(before),"Edit wrote without review");
            Render(win,"chat-excel.png",1190,690);cols[3].Width=new GridLength(355);Render(win,"chat-excel-compact.png",780,560);
            Check(cols[3].ActualWidth>=290&&cols[1].ActualWidth>=300,"Compact split hides conversation or preview");
            reply=await send("Сохрани выбранные правки");Check(reply.Text.Contains("Сохранено"),"Commit from chat: "+reply.Text);
            Check(File.GetLastWriteTimeUtc(historyPath)==persistedAt,"Private turns rewrote their public history placeholder");
            var check=await ProTools.Office(testRule,book,"read",null,CancellationToken.None);Check(check.rows.Any(r=>r.key=="B2"&&r.value=="9471"),"Wrong saved value");
            reply=await send("О чём этот файл?");Check(reply.Text.Contains("редактор"),"Editor-only data entered inference: "+reply.Text);
            if(infer){
                testRule.Analysis=true;Store.Write(WorkPolicy.PolicyPath,new CompanyRules{Managed=true,LocalAI=true,ChatHistory=true,Projects=new[]{testRule}});
                reply=await send("О чём этот файл?");Check(reply.Text.Contains("выбранного фрагмента"),"Old grants unexpectedly expanded to the full document");
                testRule.DocumentAnalysis=true;Store.Write(WorkPolicy.PolicyPath,new CompanyRules{Managed=true,LocalAI=true,ChatHistory=true,Projects=new[]{testRule}});
                reply=await send("Какое значение в ячейке B2? Укажи число.");Check(reply.Text.Contains("9471"),"Local workbook grounding: "+reply.Text);
                reply=await send("Проверьте командой значение ячейки B2 в смете и назовите его.");
                Check(reply.Text.Contains("9471")&&reply.Workers.Any(w=>w.Id=="analyst")&&reply.Workers.Any(w=>w.Id=="facts"),"Team did not analyze the approved sheet: "+reply.Text);
            }
            reply=await send("Открой документ «"+doc+"»");Check(reply.Text.Contains(Path.GetFileName(doc)),"Word did not open");
            if(infer){
                var initial=File.ReadAllBytes(doc);reply=await send("Переформулируй абзац 2 проще");
                Check(reply.Text.Contains("Предложение")&&initial.SequenceEqual(File.ReadAllBytes(doc)),"Zarum rewrite did not use review: "+reply.Text);
                reply=await send("Сохрани выбранные правки");Check(reply.Text.Contains("Сохранено"),"Zarum rewrite commit");
                Console.WriteLine("PASS approved workbook question answered with 9471 and real Zarum Word rewrite preview and save");
            }
            reply=await send("Найди в документе встреч");Check(reply.Text.Contains("Найдено"),"Find in Word");
            foreach(string path in Directory.GetFiles(Store.Chats,"*.json")){
                var saved=Store.Json().Deserialize<Conversation>(File.ReadAllText(path));
                string content=Store.Json().Serialize(new {saved.Title,saved.Turns});
                Check(!content.Contains("9471")&&!content.Contains("Смета.xlsx")&&!content.Contains("я файл")&&
                    (saved.ProjectKey==null||System.Text.RegularExpressions.Regex.IsMatch(saved.ProjectKey,"^[A-F0-9]{64}$")),"Project material in persisted chat");
            }
            var names=(TextBox)viewer.FindName("FileFilter");names.Text="нет-такого-фильтра";Check(((ListBox)viewer.FindName("Files")).Items.Count==0,"File name filter");
            names.Clear();var bad=Path.Combine(root,"unapproved","new.docx");reply=await send("Открой документ «"+bad+"»");Check(!reply.Text.StartsWith("Открыт "),"Unauthorized file opened");
            Check((bool)Invoke(app,"ClosePreview")&&!((Border)win.FindName("PreviewPane")).IsVisible,"Close panel failed");
            File.Delete(WorkPolicy.PolicyPath);
            var consentDir=Directory.CreateDirectory(Path.Combine(root,"consent")).FullName;
            foreach(bool approve in new[]{false,true}){
                bool prompted=false;
                var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(70)};
                timer.Tick+=(s,e)=>{
                    var prompt=Application.Current.Windows.OfType<Window>().FirstOrDefault(v=>v.IsVisible&&v.Title=="Подключить папку");
                    if(prompt==null)return;prompted=true;timer.Stop();prompt.DialogResult=approve;
                };
                timer.Start();try{reply=await send("Подключи папку «"+consentDir+"»");}finally{timer.Stop();}
                Check(prompted&&WorkPolicy.Projects().Any(p=>WorkPolicy.Within(consentDir,p.Root))==approve,"Folder consent was skipped or denial was ignored");
            }
            var entries=CatalogEntry.All();Check(entries.Length==6&&entries.Count(e=>e.builtin)==1&&entries[0].files[0]==Store.Files["q9"]&&entries[0].files[1]==Store.Files["v9"],"Recommended catalog entry duplicates the bundled files");
            var catalogTimer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(100)};
            bool catalogShown=false;
            catalogTimer.Tick+=(s,e)=>{var dialog=Application.Current.Windows.OfType<Window>().FirstOrDefault(v=>v.IsVisible&&v.Title=="Модели для этого компьютера");if(dialog!=null){catalogTimer.Stop();catalogShown=true;Render(dialog,"catalog.png",680,575);dialog.DialogResult=false;}};
            catalogTimer.Start();try{ModelCatalogDialog.Choose(win,dir);}finally{catalogTimer.Stop();}
            Check(catalogShown,"Catalog did not open");
            var old=new ResearchDocument{Id=Guid.NewGuid().ToString("N"),Name="А.txt",Parts=new System.Collections.Generic.List<DocumentPart>{new DocumentPart{Label="1",Text="Цена 100"},new DocumentPart{Label="2",Text="Срок 3 дня"}}};
            var fresh=new ResearchDocument{Id=Guid.NewGuid().ToString("N"),Name="Б.txt",Parts=new System.Collections.Generic.List<DocumentPart>{new DocumentPart{Label="1",Text="Срок 3 дня"},new DocumentPart{Label="2",Text="Цена 110"}}};
            Documents.Save(old);Documents.Save(fresh);var turns=new System.Collections.Generic.List<Turn>{new Turn{DocumentId=old.Id},new Turn{DocumentId=fresh.Id}};
            var result=new Turn();string diff=DocumentComparison.Run(turns,result,CancellationToken.None);
            Check(diff.Contains("Цена 100")&&diff.Contains("Цена 110")&&result.Sources.Count==2&&DocumentComparison.Requested("Сравни две версии")&&!DocumentComparison.Requested("Сравни две версии и оцени риски"),"Comparison quotes or route");
            fresh.Parts=old.Parts.AsEnumerable().Reverse().ToList();Documents.Save(fresh);Check(DocumentComparison.Run(turns,result,CancellationToken.None).Contains("порядок"),"Reorder not reported");
            Check(LocalMusic.Requested("Сгенерируй музыку на 12 секунд")&&LocalMusic.Duration("на 12 секунд")==12&&!LocalMusic.Requested("Как сделать музыку?"),"Music intent");
            Console.WriteLine("PASS real chat open Word and Excel, resize, edit preview, save, editor-only enforcement, private history, folder filter, repeated roles, catalog, version comparison and music intent");
        }finally{win.Close();}
    }
    [STAThread] public static int Main(string[] args){
        try{
            Check(Store.Home.StartsWith(root)&&WorkPolicy.PolicyPath.StartsWith(root),"Test roots must be isolated");
            if(args[0]=="device"){Console.WriteLine(ProAccess.Device);return 0;}
            if(args[0]=="migrate"){
                var path=Path.Combine(Store.Home,"pro","subscription.dat");var original=File.ReadAllBytes(path);
                try{
                    var plain=System.Security.Cryptography.ProtectedData.Unprotect(original,null,System.Security.Cryptography.DataProtectionScope.CurrentUser);
                    var old=Store.Json().Deserialize<System.Collections.Generic.Dictionary<string,object>>(System.Text.Encoding.UTF8.GetString(plain));
                    old["Receipt"]=File.ReadAllLines(Path.Combine(root,"fixtures.txt"))[1];old["LastSeen"]=DateTimeOffset.UtcNow.ToUnixTimeSeconds()+80000;
                    File.WriteAllBytes(path,System.Security.Cryptography.ProtectedData.Protect(System.Text.Encoding.UTF8.GetBytes(Store.Json().Serialize(old)),null,System.Security.Cryptography.DataProtectionScope.CurrentUser));
                    Check(ProAccess.Active,"Installed 5.1 receipt did not migrate");Console.WriteLine("PASS automatic offline migration of expired signed 5.1 state");return 0;
                }finally{File.WriteAllBytes(path,original);}
            }
            if(args[0]=="restart"){Check(ProAccess.Active,"Persisted lease missing");Console.WriteLine("PASS offline restart");return 0;}
            var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};var host=new Window{Width=400,Height=300,Left=-9000,Top=-9000,ShowInTaskbar=false,ShowActivated=false,WindowStartupLocation=WindowStartupLocation.Manual};host.Show();int rc=1;
            app.Dispatcher.BeginInvoke(new Action(async()=>{try{if(args[0]=="productivity")await ProductivityChecks.Run(host);else await Exercise(host);rc=0;}catch(Exception ex){Console.Error.WriteLine(ex);}finally{app.Shutdown();}}));app.Run();return rc;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
