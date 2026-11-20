using System;
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
            first.SetResult(1);second.SetResult(1);await Task.WhenAll(read,check);
            var turn=new Turn{Role="assistant",Text="Проверка завершена."};Invoke(app,"FinishWorkers",turn);
            Check(turn.Workers.Count==2&&turn.Workers.All(x=>x.State=="done"),"Concurrent work history incorrect");
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
            Check(Drag(w,DragDrop.PreviewDropEvent,file,DragDropEffects.Move).Effects==DragDropEffects.None&&Find<ComboBox>(w,"Sheets").Visibility==Visibility.Visible,"Move-only operation accepted");
            var outside=Directory.CreateDirectory(Path.Combine(root,"unapproved")).FullName;
            try{await (Task)Invoke(w,"OpenPaths",new[]{outside},false);throw new Exception("External project opened under company policy");}catch(UnauthorizedAccessException){}
            var locked=(Window)Activator.CreateInstance(typeof(ProAccessWindow),BindingFlags.Instance|BindingFlags.NonPublic,null,new object[]{owner},null);
            Check(Find<Button>(locked,"Verify").Visibility==Visibility.Collapsed&&Find<Button>(locked,"Channel").Visibility==Visibility.Collapsed,"Managed offline verification opened external controls");locked.Close();
            w.Close();
        }
        Console.WriteLine("PASS gate, signature, perpetual grant, device, official bot, empty workspace, compact layout, routed drag and drop, DOCX/XLSX, manual proposal/save, managed folders and offline activation UI");
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
            app.Dispatcher.BeginInvoke(new Action(async()=>{try{await Exercise(host);rc=0;}catch(Exception ex){Console.Error.WriteLine(ex);}finally{app.Shutdown();}}));app.Run();return rc;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
