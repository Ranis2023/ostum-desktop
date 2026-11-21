using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Sfera;

// Run the same native window against a release and a candidate, with a fresh profile each time.
internal class ChatExperienceProbe {
    readonly Desktop desktop;
    readonly string output;
    readonly List<object> checks=new List<object>();
    readonly Dictionary<string,object> metrics=new Dictionary<string,object>();
    const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    Window Window {get{return desktop.Window;}}
    ChatExperienceProbe(Desktop app,string folder){desktop=app;output=folder;}
    T Get<T>(string name) where T:FrameworkElement{return (T)Window.FindName(name);}
    object Call(string name,params object[] values){return typeof(Desktop).GetMethod(name,Private).Invoke(desktop,values);}
    void Check(string id,bool pass){checks.Add(new{id=id,passed=pass});}
    static string CardText(Border card){
        var view=Children<RichTextBox>(card).First();
        return new TextRange(view.Document.ContentStart,view.Document.ContentEnd).Text;
    }
    Task Idle(){return Window.Dispatcher.InvokeAsync(()=>Window.UpdateLayout(),DispatcherPriority.ApplicationIdle).Task;}
    void Picture(string name){
        var view=(FrameworkElement)Window.Content;
        var bmp=new RenderTargetBitmap((int)Math.Ceiling(view.ActualWidth),(int)Math.Ceiling(view.ActualHeight),96,96,PixelFormats.Pbgra32);
        bmp.Render(view);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bmp));
        using(var f=File.Create(Path.Combine(output,name+".png")))png.Save(f);
    }
    static IEnumerable<T> Children<T>(DependencyObject root) where T:DependencyObject{
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){
            var child=VisualTreeHelper.GetChild(root,i);if(child is T)yield return (T)child;
            foreach(var next in Children<T>(child))yield return next;
        }
    }
    async Task Review(bool deep){
        await Idle();Picture("home");var send=Get<Button>("Send");var input=Get<TextBox>("Input");
        if(typeof(Desktop).Assembly.GetType("Sfera.OstumIcon")!=null)await IconStates();
        Check("empty-send-disabled",!send.IsEnabled);
        input.Text="Один вопрос";Check("text-enables-send",send.IsEnabled);input.Clear();
        Window.Width=760;Window.Height=560;await Idle();Picture("compact");
        var host=(FrameworkElement)Window.Content;
        Check("compact-send-inside-window",send.TranslatePoint(new Point(send.ActualWidth,send.ActualHeight),host).X<=host.ActualWidth);
        var stage=Get<Button>("QuickDocument");Check("compact-primary-visible",stage.IsVisible&&stage.ActualHeight>=32);
        if(deep){
            Check("compact-history-collapses",Get<Button>("ExpandSidebar").IsVisible);
            Get<Button>("ExpandSidebar").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Idle();
            Check("compact-history-can-reopen",Get<Button>("CollapseSidebar").IsVisible);
        }
        Window.Width=1150;Window.Height=770;await Idle();
        var c=(Conversation)typeof(Desktop).GetField("active",Private).GetValue(desktop);
        for(int i=0;i<160;i++)c.Turns.Add(new Turn{Role=i%2==0?"user":"assistant",Text=(i%2==0?"Сверь данные за неделю. Запрос ":"Проверка завершена. Запись ")+(i+1)+".\nМаркер "+i.ToString("000")+"."});
        var panel=Get<StackPanel>("Transcript");var scroller=Get<ScrollViewer>("TranscriptScroll");
        var times=new List<long>();
        for(int i=0;i<3;i++){
            var watch=Stopwatch.StartNew();Call("ShowChat");await Idle();watch.Stop();times.Add(watch.ElapsedMilliseconds);
        }
        metrics["render160Milliseconds"]=times;metrics["initialBubbles"]=panel.Children.OfType<Border>().Count();
        Check("history-initial-bounded",panel.Children.OfType<Border>().Count()<=32);
        Check("latest-message-present",CardText(panel.Children.OfType<Border>().Last()).Contains("Маркер 159"));
        var earlier=Get<Button>("EarlierTurns");
        if(earlier!=null){
            scroller.ScrollToTop();await Idle();
            var anchor=panel.Children.OfType<Border>().First();double anchorBefore=anchor.TranslatePoint(new Point(),scroller).Y;
            earlier.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Idle();
            double delta=Math.Abs(anchor.TranslatePoint(new Point(),scroller).Y-anchorBefore);
            metrics["anchorMovementDip"]=delta;Check("history-prepend-anchor",delta<=3);
            for(int i=0;i<10&&earlier.Visibility==Visibility.Visible;i++){earlier.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Idle();}
            var all=panel.Children.OfType<Border>().ToArray();
            Check("entire-history-reachable",all.Length==160&&earlier.Visibility==Visibility.Collapsed&&
                  all.Select((card,index)=>CardText(card).Contains("Маркер "+index.ToString("000"))).All(inOrder=>inOrder));
        }else{Check("history-prepend-anchor",false);Check("entire-history-reachable",panel.Children.OfType<Border>().Count()==160);}
        scroller.ScrollToTop();await Idle();var jump=Get<Button>("JumpLatest");
        Check("jump-when-reading",jump!=null&&jump.IsVisible);
        if(jump!=null){jump.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Idle();}
        Check("jump-returns-to-end",scroller.ScrollableHeight-scroller.VerticalOffset<2);Picture("conversation");
        if(typeof(Desktop).GetMethod("QueueEnd",Private)!=null){
            Call("QueueEnd",false);scroller.ScrollToTop();scroller.UpdateLayout();await Idle();
            Check("queued-stream-does-not-override-reading",scroller.VerticalOffset<2&&jump.IsVisible);
            jump.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Idle();
        }
        var jpg=new JpegBitmapEncoder();var pixels=BitmapSource.Create(32,32,96,96,PixelFormats.Gray8,null,new byte[1024],32);jpg.Frames.Add(BitmapFrame.Create(pixels));
        string b64;using(var m=new MemoryStream()){jpg.Save(m);b64=Convert.ToBase64String(m.ToArray());}
        var doc=new ResearchDocument{Name="Контрольный скан",Parts=new List<DocumentPart>(),ScanPictures=new Dictionary<string,string>{{"1",b64},{"2",b64}}};
        for(int i=0;i<30;i++)doc.Parts.Add(new DocumentPart{ScanNumber=i<15?1:2,Box=new[]{.1,.02*(i%15),.6,.04},Label="Стр. "+(i<15?1:2),Text="Контрольная строка "+i});
        Call("ReadOnlyPreview",doc,null);await Idle();var preview=Get<ContentControl>("PreviewHost");
        var list=Children<ListView>(preview).Single();var box=Children<Viewbox>(preview).First();var first=Children<Image>(box).First().Source;
        list.SelectedIndex=8;await Idle();var same=Children<Image>(box).First().Source;
        Check("ocr-reuses-page-bitmap",ReferenceEquals(first,same));
        list.SelectedIndex=20;await Idle();Check("ocr-switches-page",!ReferenceEquals(same,Children<Image>(box).First().Source));
        Window.Width=760;await Idle();Check("split-keeps-editor",Get<Grid>("ChatCanvas").ActualWidth>=310&&preview.ActualWidth>=250);Picture("split");
        Call("ClosePreview");Call("SetBusy",true);
        Check("busy-protects-document-and-tools",!Get<Button>("QuickDocument").IsEnabled&&!Get<Button>("Toolbox").IsEnabled&&!Get<Button>("PreviewToggle").IsEnabled);
        Call("SetBusy",false);Check("controls-restore",Get<Button>("QuickDocument").IsEnabled&&Get<Button>("Toolbox").IsEnabled);
        if(deep)await Interaction();
        Store.Write(Path.Combine(output,"results.json"),new{checks=checks,metrics=metrics});
    }
    static string[] Icons(DependencyObject host){
        return Children<FrameworkElement>(host).Where(x=>x.GetType().Name=="OstumIcon"&&x.IsVisible)
            .Select(x=>(string)x.GetType().GetProperty("Kind").GetValue(x,null)).ToArray();
    }
    async Task IconStates(){
        var names=new Dictionary<string,string>{{"Attach","attach"},{"Microphone","mic"},{"MicDevices","chevron"},{"Toolbox","tools"},{"Send","send"},
            {"Projects","workspace"},{"Models","settings"},{"FullHelp","help"},{"QuickDocument","file"},{"QuickAgents","team"},{"QuickPhoto","photo"},
            {"QuickArt","art"},{"PreviewToggle","files"},{"ChatActions","more"},{"ArchiveSwitch","archive"},{"ChatMode","auto"},{"NewChat","plus"}};
        foreach(var pair in names)Check("icon-"+pair.Key,Icons(Get<Button>(pair.Key)).Contains(pair.Value));
        var microphone=Get<Button>("Microphone");var send=Get<Button>("Send");
        Check("icon-microphone-hit",microphone.ActualWidth>=36&&microphone.ActualHeight>=36&&microphone.Focusable);
        Call("VoiceButtons",true,"Распознать");await Idle();
        Check("icon-live-recording",Icons(microphone).Contains("record")&&Icons(send).Contains("transcribe")&&
            System.Windows.Automation.AutomationProperties.GetName(microphone)=="Завершить запись");
        Picture("recording-controls");
        Call("VoiceButtons",true,"Отменить");await Idle();
        Check("icon-transcribing",Icons(microphone).Contains("transcribe")&&Icons(send).Contains("close")&&
            System.Windows.Automation.AutomationProperties.GetName(microphone)=="Отменить распознавание");
        Call("VoiceButtons",false,null);await Idle();
        Check("icon-microphone-restored",Icons(microphone).Contains("mic")&&Get<Button>("MicDevices").IsEnabled);
        Call("SetMode",true,false);await Idle();Check("icon-drawing-mode",Icons(send).Contains("art"));
        Call("SetMode",false,false);Call("SetBusy",true);await Idle();Check("icon-cancel-generation",Icons(send).Contains("stop"));Call("SetBusy",false);
        var type=typeof(Desktop).Assembly.GetType("Sfera.OstumIcon");
        var kinds=(IEnumerable<string>)type.GetProperty("Kinds",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null,null);
        metrics["vectorSymbols"]=kinds.Count();
        var panel=new System.Windows.Controls.Primitives.UniformGrid{Columns=8,Background=Brushes.White,Margin=new Thickness(0)};
        foreach(string kind in kinds){
            var icon=(FrameworkElement)Activator.CreateInstance(type);type.GetProperty("Kind").SetValue(icon,kind,null);
            icon.Width=icon.Height=32;icon.Margin=new Thickness(7);panel.Children.Add(icon);
        }
        panel.Measure(new Size(384,double.PositiveInfinity));panel.Arrange(new Rect(new Point(),panel.DesiredSize));panel.UpdateLayout();
        var bitmap=new RenderTargetBitmap((int)Math.Ceiling(panel.ActualWidth*1.5),(int)Math.Ceiling(panel.ActualHeight*1.5),144,144,PixelFormats.Pbgra32);
        bitmap.Render(panel);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using(var stream=File.Create(Path.Combine(output,"symbols-150.png")))encoder.Save(stream);
        byte[] pixels=new byte[bitmap.PixelWidth*bitmap.PixelHeight*4];bitmap.CopyPixels(pixels,bitmap.PixelWidth*4,0);
        bool allDrawn=true;
        foreach(FrameworkElement glyph in panel.Children){
            var box=glyph.TransformToAncestor(panel).TransformBounds(new Rect(glyph.RenderSize));int ink=0;
            for(int y=(int)(box.Top*1.5);y<(int)(box.Bottom*1.5);y++)for(int x=(int)(box.Left*1.5);x<(int)(box.Right*1.5);x++){
                int offset=4*(x+y*bitmap.PixelWidth);if(pixels[offset]<210&&pixels[offset+3]>200)ink++;
            }
            if(ink<18)allDrawn=false;
        }
        Check("all-symbols-render-at-150-percent",allDrawn);
    }
    async Task Interaction(){
        var mode=Get<Button>("ChatMode");mode.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Idle();
        var menu=mode.ContextMenu;Check("mode-menu-all-options",menu!=null&&menu.Items.Count==3);
        if(typeof(Desktop).Assembly.GetType("Sfera.OstumIcon")!=null)
            Check("mode-menu-vector-and-check",menu.Items.Cast<MenuItem>().All(m=>m.Icon!=null&&m.Icon.GetType().Name=="OstumIcon"&&m.IsCheckable)&&menu.Items.Cast<MenuItem>().Count(m=>m.IsChecked)==1);
        if(menu!=null){((MenuItem)menu.Items[1]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));menu.IsOpen=false;}
        Check("team-mode-selected",mode.Content.ToString().Contains("Команда"));
        var lightHost=new StackPanel();Call("BeginWorkers",lightHost);
        var worker=new WorkerEvent{Id="analyst",Name="Аналитик",Detail="Сверяет сроки",State="running",Started=DateTime.UtcNow};
        var second=new WorkerEvent{Id="facts",Name="Проверяющий",Detail="Проверяет источники",State="running",Started=DateTime.UtcNow};
        var transcript=Get<StackPanel>("Transcript");transcript.Children.Add(lightHost);
        Call("WorkerUpdate",worker);Call("WorkerUpdate",second);
        var lights=lightHost.Children.OfType<WrapPanel>().Single();
        Check("two-active-workers",lights.Children.Count==2);
        Call("ScrollEnd");await Idle();await Task.Delay(310);Picture("agents");
        Call("WorkerUpdate",worker.Finish("done"));Call("WorkerUpdate",second.Finish("done"));
        var trace=new Turn();Call("FinishWorkers",trace);transcript.Children.Remove(lightHost);
        await Task.Delay(1200);Check("agent-cards-retire",lights.Children.Count==0&&trace.Workers.Count==2);
        Call("SetMode",false,false);
        var net=Get<CheckBox>("WebSearch");net.IsChecked=true;
        Check("web-enabled-visible",Get<TextBlock>("HomePrivacy").Text.Contains("разрешён"));net.IsChecked=false;
        Check("offline-visible",Get<TextBlock>("HomePrivacy").Text.Contains("Локальный"));
        var screen=System.Windows.Forms.Screen.PrimaryScreen;
        var capture=Computer.Capture(screen);
        Check("capture-pixel-dimensions",capture.PixelWidth==screen.Bounds.Width&&capture.PixelHeight==screen.Bounds.Height);
        var input=Get<TextBox>("Input");
        input.Text="Кратко объясни, почему цвет листьев меняется осенью.";
        var pending=(Task)Call("Send");
        if(!pending.IsCompleted){
            var scroll=Get<ScrollViewer>("TranscriptScroll");
            await Task.Delay(350);
            var key=new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,PresentationSource.FromVisual(scroll),Environment.TickCount,System.Windows.Input.Key.Home);
            key.RoutedEvent=System.Windows.Input.Keyboard.PreviewKeyDownEvent;scroll.RaiseEvent(key);scroll.ScrollToTop();
            for(int i=0;i<30&&scroll.VerticalOffset>=8;i++){await Task.Delay(16);await Idle();}
            metrics["whileReading"]=new{offset=scroll.VerticalOffset,extent=scroll.ScrollableHeight,jump=Get<Button>("JumpLatest").IsVisible};
            await pending;await Idle();
            metrics["afterReading"]=new{offset=scroll.VerticalOffset,extent=scroll.ScrollableHeight,jump=Get<Button>("JumpLatest").IsVisible};
            Check("reading-position-after-response",scroll.ScrollableHeight>400&&scroll.VerticalOffset<8&&Get<Button>("JumpLatest").IsVisible);
        }else{await pending;Check("reading-position-after-response",false);}
        Call("NewChat");await Idle();var prefs=(Preferences)typeof(Desktop).GetField("prefs",Private).GetValue(desktop);
        prefs.Models=Path.Combine(output,"empty-library");Directory.CreateDirectory(prefs.Models);
        var conversation=(Conversation)typeof(Desktop).GetField("active",Private).GetValue(desktop);
        string occupied=Path.Combine(Store.Chats,conversation.Id+".json");Directory.CreateDirectory(occupied);
        input.Text="Сколько будет 17 * 4?";await (Task)Call("Send");await Idle();
        Check("failed-save-keeps-draft",input.Text.Contains("17")&&conversation.Turns.Count==0&&Get<TextBlock>("Activity").Text.Length>0);
        Directory.Delete(occupied);
        await (Task)Call("Send");await Idle();
        Check("calculator-without-weights",conversation.Turns.Count==2&&conversation.Turns.Last().Text.Contains("68"));
        var historySamples=new Dictionary<string,long[]>();
        for(int i=1;i<=300;i++){
            var sample=new Conversation{Title="Контрольная беседа "+i};
            for(int j=0;j<20;j++)sample.Turns.Add(new Turn{Role=j%2==0?"user":"assistant",Text="Контрольный текст о плане работ на неделю. Запись "+j});
            Store.Write(Path.Combine(Store.Chats,sample.Id+".json"),sample);
            if(i!=25&&i!=100&&i!=300)continue;
            var samples=new List<long>();
            for(int pass=0;pass<3;pass++){
                var watch=Stopwatch.StartNew();var list=Store.History();watch.Stop();
                if(list.Count<i)throw new Exception("Missing control histories.");
                samples.Add(watch.ElapsedMilliseconds);
            }
            historySamples.Add(i.ToString(),samples.ToArray());
        }
        metrics["historyLoadMs"]=historySamples;
        var headings=typeof(Store).GetMethod("HistoryHeadings");
        if(headings!=null){
            var watch=Stopwatch.StartNew();var items=(List<Conversation>)headings.Invoke(null,null);
            metrics["indexFirstBuildMs"]=watch.ElapsedMilliseconds;
            var warm=new List<long>();
            for(int pass=0;pass<7;pass++){watch.Restart();items=(List<Conversation>)headings.Invoke(null,null);warm.Add(watch.ElapsedMilliseconds);}
            metrics["indexWarm300Ms"]=warm;
            var sample=items.First(c=>c.Title.StartsWith("Контрольная беседа"));
            string index=Path.Combine(Path.GetDirectoryName(Store.Chats),"chat-headings-v1.json"),file=Path.Combine(Store.Chats,sample.Id+".json");
            Check("index-only-headings",items.Count>=300&&items.All(c=>c.Turns.Count==0)&&!File.ReadAllText(index).Contains("Контрольный текст"));
            Store.SetArchived(sample,true);var loaded=Store.History().Single(c=>c.Id==sample.Id);
            Check("archive-materializes-messages",loaded.Archived&&loaded.Turns.Count==20);
            loaded.Title="Новое внешнее название";Store.Write(file,loaded);
            items=(List<Conversation>)headings.Invoke(null,null);
            Check("index-detects-external-changes",items.Single(x=>x.Id==loaded.Id).Title==loaded.Title);
            File.WriteAllText(index,"{ damaged");items=(List<Conversation>)headings.Invoke(null,null);
            Check("index-recovers-from-corruption",items.Count>=300&&items.Single(x=>x.Id==loaded.Id).Archived);
            var bad=new Conversation{Title="Временно недоступный диалог"};Store.Save(bad);
            items=(List<Conversation>)headings.Invoke(null,null);var badHead=items.Single(c=>c.Id==bad.Id);
            string badFile=Path.Combine(Store.Chats,bad.Id+".json");File.WriteAllText(badFile,"{ broken");
            try{Store.Save(badHead);Check("never-overwrite-unreadable-heading",false);}catch{Check("never-overwrite-unreadable-heading",File.ReadAllText(badFile)=="{ broken");}
            items=(List<Conversation>)headings.Invoke(null,null);Check("index-skips-unreadable-chat",items.All(c=>c.Id!=bad.Id));
            File.Delete(badFile);Store.DeleteConversation(items.Single(x=>x.Id==loaded.Id));
            items=(List<Conversation>)headings.Invoke(null,null);
            Check("delete-removes-heading",items.All(c=>c.Id!=loaded.Id)&&!File.ReadAllText(index).Contains(loaded.Title));
        }
        string settings=Path.Combine(Store.Home,"preferences.json");
        var saved=Store.Load();saved.AllowWeb=saved.CheckForUpdates=saved.DownloadUpdates=true;
        saved.MicrophoneId="test-input-previous";Store.Save(saved);
        saved.MicrophoneId="test-input-current";Store.Save(saved);
        var original=File.ReadAllBytes(settings);
        try{
            File.WriteAllText(settings,"{ interrupted");
            try{
                var notice=typeof(Preferences).GetProperty("RecoveryNotice");
                var old=Store.Load();metrics["invalidSettingsRecovered"]=notice!=null&&notice.GetValue(old,null)!=null;
                Check("damaged-preferences-restored",old.MicrophoneId=="test-input-previous"&&!old.AllowWeb&&!old.CheckForUpdates&&!old.DownloadUpdates);
                Store.Save(old);Check("damaged-preferences-retained",File.ReadAllText(settings+".invalid")=="{ interrupted"&&File.Exists(settings+".previous"));
                File.WriteAllText(settings+".previous","{ invalid");File.WriteAllText(settings,"{ invalid");
                var fallback=Store.Load();Check("damaged-preferences-local-defaults",notice!=null&&notice.GetValue(fallback,null)!=null&&!fallback.AllowWeb&&!fallback.CheckForUpdates&&!fallback.Personalize);
            }
            catch(ArgumentException){metrics["invalidSettingsRecovered"]=false;}
            catch(InvalidOperationException){metrics["invalidSettingsRecovered"]=false;}
        }finally{File.WriteAllBytes(settings,original);}
    }
    [STAThread]static int Main(string[] args){
        string output=Directory.CreateDirectory(Path.GetFullPath(args[0])).FullName;int code=1;
        try{
            var profile=Path.Combine(output,"profile");typeof(Store).GetField("Home").SetValue(null,profile);
            if(!WorkPolicy.Within(Store.Chats,profile))throw new InvalidOperationException("Test profile mismatch");
            Store.Save(new Preferences{Models=args[1],CheckForUpdates=false,AllowWeb=false,DownloadUpdates=false,Personalize=false});
            var application=new Application();var desktop=new Desktop(args[2]);var w=desktop.Window;
            w.ShowInTaskbar=false;w.ShowActivated=false;w.WindowStartupLocation=WindowStartupLocation.Manual;w.Left=-6000;w.Top=10;
            w.ContentRendered+=async(s,e)=>{try{await new ChatExperienceProbe(desktop,output).Review(args.Length>3&&args[3]=="deep");code=0;}catch(Exception ex){File.WriteAllText(Path.Combine(output,"error.txt"),ex.ToString());}finally{w.Close();}};
            application.Run(w);
        }catch(Exception ex){File.WriteAllText(Path.Combine(output,"error.txt"),ex.ToString());}
        return code;
    }
}
