using System;
using System.Collections.Generic;
using System.Diagnostics;
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
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

[assembly: AssemblyTitle("Ostum Desktop")]
[assembly: AssemblyProduct("OSTUM")]
[assembly: AssemblyVersion("5.7.0.0")]
[assembly: AssemblyDescription("Автономный ИИ для Windows с локальной моделью Zarum 9B")]

namespace Sfera {
    static class Program {
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);
        [STAThread] public static int Main(string[] args) {
            if (args.Length == 2 && args[0] == "--import-models") {
                try { return InstallModels.FromFolder(args[1],new Preferences().Models); }
                catch (Exception) { return 1; }
            }
            bool first;
            using (var mutex = new Mutex(true, @"Local\SferaAI_Desktop_1", out first)) {
                if (!first) {
                    foreach (var p in Process.GetProcessesByName(Process.GetCurrentProcess().ProcessName)) {
                        try { if (p.MainWindowHandle != IntPtr.Zero) { ShowWindowAsync(p.MainWindowHandle, 9); SetForegroundWindow(p.MainWindowHandle); break; } } finally { p.Dispose(); }
                    }
                    return 0;
                }
                try {
                    var app = new Application();
                    var desktop = new Desktop(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "engines"));
                    app.Run(desktop.Window);
                } catch (Exception ex) { MessageBox.Show(ex.Message, "Ostum Desktop", MessageBoxButton.OK, MessageBoxImage.Error); return 1; }
            }
            return 0;
        }
    }

    public sealed partial class Desktop {
        public readonly Window Window;
        readonly Preferences prefs;
        readonly Engine engine;
        readonly List<Conversation> history;
        Conversation active;
        readonly TextBox input;
        readonly ListBox list;
        readonly TextBlock hint, status;
        readonly ScrollViewer scroll;
        readonly StackPanel transcript;
        readonly DispatcherTimer idle = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        readonly DispatcherTimer prepareDraft=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(400)};
        readonly CancellationTokenSource draftLifetime=new CancellationTokenSource();
        bool preparingDraft;
        BitmapSource attached;
        string attachmentName;
        bool art, agents, rebuilding, closing;
        CancellationTokenSource work;
        T Find<T>(string id) where T:FrameworkElement { return (T)Window.FindName(id); }
        static Brush Color(string hex) { return (Brush)new BrushConverter().ConvertFromString(hex); }
        public Desktop(string engines) {

            prefs = Store.Load();
            engine = new Engine(engines);
            idle.Tick += (s,e) => { idle.Stop(); if (work == null) engine.StopChat(); };
            history = Store.HistoryHeadings();
            Window = OstumActionIcons.Install((Window)XamlReader.Parse(Store.Resource("MainWindow.xaml")));
            Window.Icon = BitmapFrame.Create(new MemoryStream(Convert.FromBase64String(Store.Resource("icon.b64"))), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            input = Find<TextBox>("Input"); hint = Find<TextBlock>("Placeholder"); status = Find<TextBlock>("Activity");
            list = Find<ListBox>("HistoryList"); scroll = Find<ScrollViewer>("TranscriptScroll"); transcript = Find<StackPanel>("Transcript");
            list.SelectionChanged += (s,e) => {
                if (rebuilding || work != null || speechWork!=null || (voice!=null&&voice.Recording) || list.SelectedItem == null || list.SelectedItem == active) return;
                var selected=(Conversation)list.SelectedItem;
                if(!TryLoadChat(selected)||!ClosePreview()){list.SelectedItem=active;return;}
                active=selected;ClearDraft();ShowChat();
            };
            Find<Button>("NewChat").Click += (s,e) => NewChat();
            Find<Button>("Projects").Click += (s,e)=>{if(work!=null||voice.Recording||speechWork!=null)return;if(!ProAccessWindow.Ensure(Window))return;prepareDraft.Stop();if(chatProject!=null){input.Focus();return;}engine.StopChat();var workspace=new ProjectWindow(Window,prefs,engine);workspace.OfferChat();workspace.ShowDialog();if(workspace.RequestedChat){chatProject=workspace;Find<CheckBox>("UseFileContext").IsChecked=true;DisplayPane(workspace.DockInChat(),true);input.Focus();}};
            Find<Button>("Models").Click += (s,e) => Settings();
            Find<Button>("ChatMode").Click += (s,e) => ShowModes();
            Find<Button>("AgentMode").Click += (s,e) => SetMode(false,true);
            Find<Button>("QuickAgents").Click += (s,e) => SetMode(false,true);
            Find<Button>("ArtMode").Click += (s,e) => SetMode(true);
            Find<Button>("Attach").Click += (s,e) => {if(art)ChoosePhoto();else ChooseFile();};
            Find<Button>("QuickDocument").Click += (s,e) => {SetMode(false);ChooseFile(true);};
            Find<Button>("FullHelp").Click += (s,e) => WorkspaceUi.Help(Window);
            SetupPreview();
            Find<Button>("QuickPhoto").Click += (s,e) => { SetMode(false); ChoosePhoto(); };
            Find<Button>("QuickArt").Click += (s,e) => SetMode(true);
            Find<Button>("RemoveAttach").Click += (s,e) => {if(reading)CancelAttachment();else SetAttachment(null,null);};
            Find<Button>("Send").Click += async (s,e) => { if (work != null) work.Cancel(); else if(voice.Recording||speechWork!=null)await Dictate(); else await Send(); };
            prepareDraft.Tick+=PrepareWhileTyping;
            input.TextChanged += (s,e) => {
                hint.Visibility = string.IsNullOrEmpty(input.Text) ? Visibility.Visible : Visibility.Collapsed;
                UpdateSendEnabled();
                if(input.Text.Trim().Length<3)prepareDraft.Stop();
                else if(work==null&&!closing&&!preparingDraft&&engine.Backend==null&&!prepareDraft.IsEnabled)prepareDraft.Start();
            };
            DataObject.AddPastingHandler(input,(s,e)=>{if(!WorkPolicy.CaptureAllowed){e.CancelCommand();status.Text="Буфер обмена отключён для защищённых проектов.";}});
            input.PreviewKeyDown += async (s,e) => {
                if ((e.Key == Key.Return || e.Key == Key.Enter) && (Keyboard.Modifiers & ModifierKeys.Shift) == 0) {
                    e.Handled = true; if (work == null) await Send();
                } else if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && work == null) {
                    try {
                        if (Clipboard.ContainsImage()) {e.Handled=true;if(!WorkPolicy.CaptureAllowed){status.Text="Изображения из буфера заблокированы политикой проекта.";return;}var img = Clipboard.GetImage(); if (img != null) SetAttachment(Scale(img), "Из буфера обмена");}
                    } catch (Exception ex) { status.Text=ex.Message; }
                }
            };
            Window.PreviewKeyDown += async (s,e) => {
                if(e.Key==Key.F1){e.Handled=true;WorkspaceUi.Help(Window,PreviewOpen?"projects":"start");return;}
                if(voice.Recording&&e.Key==Key.Enter&&(Keyboard.Modifiers&ModifierKeys.Shift)==0) {e.Handled=true;if(!e.IsRepeat)await Dictate();return;}
                if(e.Key==Key.Escape&&(voice.Recording||speechWork!=null)){e.Handled=true;CancelVoice();return;}
                if(e.Key==Key.Escape&&reading){e.Handled=true;CancelAttachment();return;}
                if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control && work == null) { NewChat(); e.Handled=true; }
                if (e.Key == Key.Escape && work != null) { work.Cancel(); e.Handled=true; }
            };
            Window.PreviewDragOver+=(s,e)=>{
                if(chatProject!=null&&Find<Border>("PreviewPane").IsMouseOver)return;
                if(!e.Data.GetDataPresent(DataFormats.FileDrop))return;
                var paths=e.Data.GetData(DataFormats.FileDrop) as string[];
                bool accept=work==null&&speechWork==null&&!voice.Recording&&paths!=null&&paths.Length==1;
                e.Effects=accept?e.AllowedEffects&DragDropEffects.Copy:DragDropEffects.None;
                Find<Border>("ChatDrop").Visibility=e.Effects==DragDropEffects.None?Visibility.Collapsed:Visibility.Visible;e.Handled=true;
            };
            Window.PreviewDragLeave+=(s,e)=>{var p=e.GetPosition(Window);if(p.X<=0||p.Y<=0||p.X>=Window.ActualWidth-1||p.Y>=Window.ActualHeight-1)Find<Border>("ChatDrop").Visibility=Visibility.Collapsed;};
            Window.PreviewDrop+=async(s,e)=>{
                if(chatProject!=null&&Find<Border>("PreviewPane").IsMouseOver)return;
                if(!e.Data.GetDataPresent(DataFormats.FileDrop))return;
                e.Handled=true;Find<Border>("ChatDrop").Visibility=Visibility.Collapsed;
                if(work!=null||speechWork!=null||voice.Recording)return;
                var paths=e.Data.GetData(DataFormats.FileDrop) as string[];
                if(paths==null||paths.Length!=1){status.Text="Прикрепите один файл к сообщению.";return;}
                string path=paths[0];
                if(Directory.Exists(path)||Path.GetExtension(path).Equals(".xlsx",StringComparison.OrdinalIgnoreCase)){
                    await OpenInChat(path);
                }else LoadFile(path);
            };
            Window.Closing += (s,e) => { if(!ClosePreview()){e.Cancel=true;return;} closing = true; StopAudio();idle.Stop();prepareDraft.Stop();draftLifetime.Cancel();if(attachmentRead!=null)attachmentRead.Cancel();if (work != null) work.Cancel(); engine.Dispose(); };
            Window.Loaded += (s,e) => input.Focus();
            SetupUpdates();
            SetupStudio(engines); SetupHistory(); SetupTranscript();
            Find<Image>("SidebarMark").Source=Window.Icon;
            Find<TextBlock>("AppVersion").Text="Desktop  "+Brand.Version;
            active = NextChat();
            RefreshList(); ShowChat(); SetMode(false);
            if (!Store.Ready(Store.Chat(prefs), prefs.Models))
                status.Text = "Откройте «Модели и настройки», чтобы выбрать папку или скачать модели.";
            if(prefs.RecoveryNotice!=null)status.Text=prefs.RecoveryNotice;
        }
        async void PrepareWhileTyping(object sender,EventArgs e){
            prepareDraft.Stop();
            if(closing||meetingOpen||work!=null||preparingDraft||art||reading||input.Text.Trim().Length<3||!Store.Ready(Store.Chat(prefs),prefs.Models))return;
            if(FileTurn(KeyboardLayout.Decode(input.Text))||LocalMusic.Requested(input.Text)||DocumentComparison.Requested(input.Text))return;
            if(!agents&&attached==null&&document==null&&(Calculator.Direct(input.Text)!=null||ReplyFormat.LiteralField(input.Text)!=null))return;
            bool photo=attached!=null||active.Turns.Any(t=>!string.IsNullOrEmpty(t.Image)&&File.Exists(t.Image));
            engine.SelectSlots(agents||Orchestrator.WantsTeam(KeyboardLayout.Decode(input.Text)),active.Turns.Sum(t=>(long)(t.Text??"").Length));
            idle.Stop();preparingDraft=true;
            try{await Task.Run(()=>engine.PrepareDraft(prefs,photo,draftLifetime.Token));}
            catch(OperationCanceledException){}
            catch(IOException){}
            catch(InvalidOperationException){}
            catch(System.ComponentModel.Win32Exception){}
            catch(TimeoutException){}
            catch(UnauthorizedAccessException){}
            finally{preparingDraft=false;if(!closing&&!meetingOpen&&work==null){idle.Stop();idle.Start();}}
        }
        void Settings() {
            if (work != null) return;
            prepareDraft.Stop();engine.StopChat();
            if (new ModelSettings(Window, prefs,art).Show()) { engine.StopChat(); status.Text = "Настройки сохранены."; Labels(); }
            input.Focus();
        }
        void NewChat() { if (work != null || speechWork!=null || voice.Recording || !ClosePreview()) return; archiveView=false; active = new Conversation(); ClearDraft(); RefreshList(); ShowChat(); }
        void ClearDraft() { input.Clear(); SetAttachment(null,null); voice.StopSpeech(); SetMode(false); status.Text = ""; input.Focus(); }
        void RefreshList() {
            rebuilding=true;
            list.ItemsSource=null; list.ItemsSource=history.Where(c=>c.Archived==archiveView).OrderByDescending(c => c.Updated).ToList();
            Find<Button>("ArchiveSwitch").Content=archiveView?"История":"Архив · "+history.Count(c=>c.Archived);
            Find<TextBlock>("HistoryHeading").Text=archiveView?"Архив":"История";
            list.SelectedItem=active!=null&&active.Archived==archiveView&&history.Contains(active) ? active : null; rebuilding=false;
        }
        void Labels() {
            Model m=art ? Store.Art(prefs) : Store.Chat(prefs);
            Find<TextBlock>("ModelLabel").Text=m.Name;
            var ready=Store.Ready(m,prefs.Models);
            Find<System.Windows.Shapes.Ellipse>("ModelLight").Fill=Color(ready ? "#4D856B" : "#B78246");
            Find<TextBlock>("ModelLabel").ToolTip=ready ? "Модель на этом компьютере"+(!art&&engine.Backend!=null?" · "+engine.Backend:"") : "Файлы модели нужно скачать";
            Find<TextBlock>("ChatTitle").Text=active.Title+(active.Archived?" · архив":"");
            Find<Button>("ChatActions").IsEnabled=history.Contains(active)&&CanManageHistory;
        }
        void SetMode(bool image,bool team=false) {
            art=image; agents=team;
            if(art&&(document!=null||reading))SetAttachment(null,null);
            Find<CheckBox>("WebSearch").Visibility=Visibility.Visible;
            foreach (var name in new [] {"ChatMode","AgentMode","ArtMode"}) {
                bool selected = name==(image?"ArtMode":team?"AgentMode":"ChatMode");
                var b=Find<Button>(name); b.Background=Color(selected ? "#E1EDDC" : "#EEF2E9"); b.Foreground=Color(selected ? "#2F563B" : "#536258");
            }
            var modeButton=Find<Button>("ChatMode");
            modeButton.Content=team?"Команда":image?"Рисование":"Авто";
            System.Windows.Automation.AutomationProperties.SetName(modeButton,"Режим ответа · "+(team?"Команда":image?"Рисование":"Авто"));
            PromptHint();
            Find<Button>("Attach").IsEnabled=work == null;
            if (work == null && !voice.Recording && speechWork==null) Find<Button>("Send").Content=art ? "Нарисовать" : "Отправить";
            UpdateSendEnabled(); Labels(); input.Focus();
        }
        static BitmapSource Scale(BitmapSource img) {
            double factor=Math.Min(1,1440.0/Math.Max(img.PixelWidth,img.PixelHeight));
            BitmapSource result=factor < 1 ? new TransformedBitmap(img,new ScaleTransform(factor,factor)) : img;
            if (result.CanFreeze) result.Freeze(); return result;
        }
        static BitmapSource OpenImage(string path, int max) {
            using (var stream=File.OpenRead(path)) {
                var frame=BitmapFrame.Create(stream,BitmapCreateOptions.DelayCreation,BitmapCacheOption.None);
                int w=frame.PixelWidth,h=frame.PixelHeight, orientation=1;
                try {
                    var meta=frame.Metadata as BitmapMetadata;
                    if (meta != null && meta.ContainsQuery("/app1/ifd/{ushort=274}")) orientation=Convert.ToInt32(meta.GetQuery("/app1/ifd/{ushort=274}"));
                } catch (NotSupportedException) {}
                stream.Position=0;
                var image=new BitmapImage(); image.BeginInit(); image.CacheOption=BitmapCacheOption.OnLoad;
                image.StreamSource=stream; if (Math.Max(w,h)>max) { if(w>=h)image.DecodePixelWidth=max; else image.DecodePixelHeight=max; }
                image.EndInit(); image.Freeze();
                if (orientation<2 || orientation>8) return image;
                var transform=new TransformGroup();
                if (orientation == 2 || orientation == 4 || orientation == 5 || orientation == 7) transform.Children.Add(new ScaleTransform(-1,1));
                int[] rotations={0,0,0,180,180,270,90,90,270};
                transform.Children.Add(new RotateTransform(rotations[orientation]));
                var result=new TransformedBitmap(image,transform); result.Freeze(); return result;
            }
        }
        void SetAttachment(BitmapSource src,string name) {
            attachmentVersion++;reading=false;document=null;
            if(attachmentRead!=null)attachmentRead.Cancel();
            attached=src; attachmentName=name;
            Find<ContentControl>("DocIcon").Visibility=Visibility.Collapsed;
            Find<Image>("AttachPreview").Visibility=src==null?Visibility.Collapsed:Visibility.Visible;
            Find<StackPanel>("AttachmentBar").Visibility=src==null ? Visibility.Collapsed : Visibility.Visible;
            Find<Image>("AttachPreview").Source=src; Find<TextBlock>("AttachName").Text=name ?? "";
            PromptHint();UpdateSendEnabled();
        }
        void CancelAttachment(){
            SetAttachment(null,null);status.Text="Чтение файла отменено.";input.Focus();
        }
        void UpdateSendEnabled(){
            Find<Button>("Send").IsEnabled=work!=null||speechWork!=null||(voice!=null&&voice.Recording)||
                (!reading&&(!string.IsNullOrWhiteSpace(input.Text)||attached!=null||document!=null));
        }
        void PromptHint() {
            hint.Text = art ? (attached == null ? "Опишите картинку, которую хотите создать…" : "Опишите вариацию по этому изображению…") :
                document!=null ? "Что узнать из документа?" : agents ? "Какую задачу поручить агентам?" : "Напишите сообщение…";
        }
        void LoadPhoto(string path) {
            try { WorkPolicy.CheckFile(path,true);if(!WorkPolicy.GlobalFile(path))throw new UnauthorizedAccessException("Для файлов проекта используйте его редактор.");var img=OpenImage(path,1440); SetAttachment(img,Path.GetFileName(path)); input.Focus(); }
            catch (Exception ex) { status.Text="Не удалось открыть изображение. " + ex.Message; }
        }
        void ChoosePhoto() {
            var dlg=new OpenFileDialog { Title="Прикрепить изображение", Filter="Изображения|*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.tif;*.tiff|Все файлы|*.*" };
            if (dlg.ShowDialog(Window)==true) LoadPhoto(dlg.FileName);
        }
        string SaveAttachment() {
            if (attached==null) return null;
            Directory.CreateDirectory(Store.Images);
            bool jpeg=Regex.IsMatch(attachmentName ?? "", @"\.jpe?g$", RegexOptions.IgnoreCase);
            string path=Path.Combine(Store.Images,Guid.NewGuid().ToString("N")+(jpeg?".jpg":".png"));
            BitmapEncoder enc=jpeg ? (BitmapEncoder)new JpegBitmapEncoder { QualityLevel=91 } : new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(attached)); using (var output=File.Create(path)) enc.Save(output);
            return path;
        }
        void SetBusy(bool busy) {
            Find<Button>("ChatActions").IsEnabled=!busy&&CanManageHistory&&history.Contains(active);Find<Button>("ArchiveSwitch").IsEnabled=!busy&&WorkPolicy.History;
            foreach (string name in new [] {"Models","NewChat","ChatMode","AgentMode","ArtMode","QuickDocument","QuickAgents","QuickPhoto","QuickArt","Projects","PreviewToggle","UseFileContext","ClosePreview","RemoveAttach"})
                Find<FrameworkElement>(name).IsEnabled=!busy;
            list.IsEnabled=!busy; Find<Button>("Attach").IsEnabled=!busy;Find<CheckBox>("WebSearch").IsEnabled=!busy&&WorkPolicy.Network;
            Find<Button>("Microphone").IsEnabled=Find<Button>("MicDevices").IsEnabled=Find<Button>("Toolbox").IsEnabled=!busy;
            var b=Find<Button>("Send"); b.Content=busy ? "Стоп" : (art ? "Нарисовать" : "Отправить"); b.Background=Color(busy ? "#91494C" : "#304B3B");
            b.ToolTip=busy?"Остановить ответ · Esc":"Enter — отправить, Shift+Enter — новая строка";UpdateSendEnabled();
        }
        void Persist() {
            Store.Save(active);
            if (!history.Contains(active)) history.Add(active);
            RefreshList(); Labels();
        }
        System.Media.SoundPlayer audioPlayer;
        void StopAudio(){if(audioPlayer!=null){audioPlayer.Stop();audioPlayer.Dispose();audioPlayer=null;}}
        bool CanAnswerWithoutWeights(string request){
            if(!art&&attached==null&&document==null&&LocalMusic.Requested(request))return true;
            if(Orchestrator.DesktopIntent(request)&&!Orchestrator.WantsScreen(request))return true;
            if(art||agents||attached!=null)return false;
            if(DocumentComparison.Requested(request))return true;
            return document==null&&(Calculator.Direct(request)!=null||ReplyFormat.LiteralField(request)!=null);
        }
        async Task Send() {
            if (work != null || reading || speechWork!=null || voice.Recording||PaneBusy) return;
            if(OpenChatUtility(input.Text))return;
            if(nextHiddenTurn>=0)ShowChat();
            string typed=input.Text.Trim(),request=KeyboardLayout.Decode(typed);
            bool projectTurn=FileTurn(request);
            if(!projectTurn)try{WorkPolicy.RequireAI();}catch(UnauthorizedAccessException ex){status.Text=ex.Message;return;}
            if (request.Length==0 && attached==null && document==null) { input.Focus(); return; }
            if (request.Length==0) request=document!=null ? "Кратко изложи содержание документа." : art ? "Создай иллюстрацию по этому изображению, сохрани композицию и цвета." : "Опиши это изображение.";
            if (!projectTurn&&!Store.Ready(art?Store.Art(prefs):Store.Chat(prefs),prefs.Models)&&!CanAnswerWithoutWeights(request)) {
                Settings();
                if (!Store.Ready(art?Store.Art(prefs):Store.Chat(prefs),prefs.Models)) return;
            }
            var cts=work=new CancellationTokenSource();
            idle.Stop(); prepareDraft.Stop();voice.StopSpeech();
            var buffer=new StringBuilder();
            var reply=new Turn { Role="assistant", Text="", ProjectOnly=projectTurn };
            TextBlock live=null; StackPanel livePanel=null; bool userSaved=false;
            var timer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(65) };
            timer.Tick+=(s,e) => {
                if (live==null) return;
                string now; lock(buffer){if(buffer.Length==live.Text.Length)return;now=buffer.ToString();}
                bool bottom=NearEnd;live.Text=now;if(bottom)QueueEnd(false);
            };
            try {
                StartProgress();
                if(!projectTurn&&WorkPolicy.CaptureAllowed&&attached==null&&document==null&&!ReplyFormat.Field(request)&&Orchestrator.WantsScreen(request))await CaptureIntoDraft(System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(Window).Handle));
                cts.Token.ThrowIfCancellationRequested();
                var user=new Turn { Role="user", Text=request, ProjectOnly=projectTurn, OriginalText=typed.Length>0&&typed!=request?typed:null, Image=SaveAttachment() };
                if(document!=null){Documents.Save(document);user.DocumentId=document.Id;user.DocumentName=document.Name;}
                string previousTitle=active.Title;var previousTime=active.Updated;
                if(projectTurn&&active.Turns.Count==0&&chatProject!=null)active.ProjectKey=chatProject.CurrentProjectKey;
                active.Turns.Add(user);
                if (active.Turns.Count==1) { var title=projectTurn?"Работа с файлами":Regex.Replace(request,@"\s+"," "); active.Title=title.Length>56 ? title.Substring(0,55)+"…" : title; }
                try{Persist();}
                catch{
                    active.Turns.Remove(user);active.Title=previousTitle;active.Updated=previousTime;
                    throw;
                }
                userSaved=true; AddTurn(user); input.Clear(); SetAttachment(null,null);
                Find<StackPanel>("Welcome").Visibility=Visibility.Collapsed;
                StopAudio();SetBusy(true); ScrollEnd();
                livePanel=new StackPanel { Margin=new Thickness(0,4,0,10) };
                livePanel.Children.Add(Author("Zarum")); BeginWorkers(livePanel); live=new TextBlock { FontSize=15,TextWrapping=TextWrapping.Wrap };
                livePanel.Children.Add(live); transcript.Children.Add(livePanel); timer.Start();
                Action<string> activity = txt => Window.Dispatcher.BeginInvoke(new Action(() => { if (work==cts) Progress(txt); }));
                Action<string> append=t=>{lock(buffer)buffer.Append(t);};
                Action<WorkerEvent> eventSink=w=>Window.Dispatcher.Invoke(new Action(()=>{if(!closing&&work==cts)WorkerUpdate(w);}));
                await RunRequest(projectTurn?new List<Turn>{user}:active.Turns.Where(t=>!t.ProjectOnly).ToList(),reply,append,activity,eventSink,cts.Token);
                status.Text="";
                if(prefs.ConversationVoice&&!projectTurn&&!cts.IsCancellationRequested){
                    try{voice.Speak(reply.Text);}catch(Exception ex){status.Text="Озвучивание. "+ex.Message;}
                }
            } catch(Exception ex) {
                if (closing) return;
                bool stopped=cts.IsCancellationRequested;
                if (stopped) engine.Cancel();
                string partial; lock(buffer) partial=buffer.ToString().Trim();
                reply.Text=partial + (partial.Length==0 ? "" : "\n\n") + (stopped ? "Ответ остановлен." : "Не удалось завершить запрос. " + ex.Message);
                status.Text=userSaved ? "" : reply.Text;
            } finally {
                timer.Stop();
                if (!closing) {
                    bool follow=NearEnd;
                    var recentWorkers=FinishWorkers(reply);
                    if (livePanel!=null) transcript.Children.Remove(livePanel);
                    if (userSaved) {
                        active.Turns.Add(reply); AddTurn(reply,recentWorkers);
                        try { Persist(); } catch(Exception ex) { status.Text="Не удалось сохранить историю. " + ex.Message; }
                    }
                    EndProgress();work=null;SetBusy(false);
                    if(follow){TrimRecentTurns();QueueEnd(false);}
                }
                work=null; cts.Dispose(); if (!closing) idle.Start();
            }
        }
        static TextBlock Author(string name) {
            return new TextBlock { Text=name, FontSize=12, FontWeight=FontWeights.SemiBold, Foreground=Color("#43674D"), Margin=new Thickness(0,0,0,4) };
        }
        static RichTextBox Formatted(string text) {
            var doc=new FlowDocument { PagePadding=new Thickness(0), FontFamily=new FontFamily("Inter, Segoe UI"), FontSize=15, Foreground=Color("#29332D"), TextAlignment=TextAlignment.Left };
            bool code=false;
            foreach (string line in (text ?? "").Replace("\r","").Split('\n')) {
                if (line.StartsWith("\x60\x60\x60")) { code=!code; continue; }
                var p=new Paragraph { Margin=new Thickness(0,0,0,code?0:3) };
                if (code) { p.FontFamily=new FontFamily("Consolas"); p.FontSize=13; p.Background=Color("#E6EAE4"); p.Inlines.Add(new Run(line)); }
                else {
                    string value=line;
                    if (Regex.IsMatch(value,@"^#{1,4} ")) { value=Regex.Replace(value,@"^#{1,4} ",""); p.FontWeight=FontWeights.SemiBold; p.FontSize=17; }
                    if(value.StartsWith("- ") || value.StartsWith("* ")) value="•  "+value.Substring(2);
                    foreach (string part in Regex.Split(value,@"(\*\*[^*]+\*\*|\x60[^\x60]+\x60)")) {
                        if(part.StartsWith("**") && part.EndsWith("**") && part.Length>4) p.Inlines.Add(new Bold(new Run(part.Substring(2,part.Length-4))));
                        else if(part.Length>2 && part[0]=='\x60' && part[part.Length-1]=='\x60') p.Inlines.Add(new Run(part.Substring(1,part.Length-2)){FontFamily=new FontFamily("Consolas"),Background=Color("#E2E7DE")});
                        else p.Inlines.Add(new Run(part));
                    }
                }
                doc.Blocks.Add(p);
            }
            return new RichTextBox(doc) { IsReadOnly=true, IsDocumentEnabled=false, BorderThickness=new Thickness(0), Padding=new Thickness(0), Background=Brushes.Transparent,
                VerticalScrollBarVisibility=ScrollBarVisibility.Disabled, HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled, FocusVisualStyle=null };
        }
        void AddTurn(Turn t,WrapPanel recentWorkers=null) {
            transcript.Children.Add(TurnCard(t,recentWorkers));
        }
        Border TurnCard(Turn t,WrapPanel recentWorkers) {
            bool user=t.Role=="user";
            var shell=new Border { HorizontalAlignment=user ? HorizontalAlignment.Right : HorizontalAlignment.Stretch,
                MaxWidth=user?650:820, Margin=new Thickness(user?36:0,4,user?0:18,12), CornerRadius=user?new CornerRadius(17,17,4,17):new CornerRadius(4,18,18,18),
                BorderBrush=Color(user?"#CEDDCB":"#DDE4DA"),BorderThickness=new Thickness(1),
                Background=user?Color("#DEECD9"):Brushes.White, Padding=new Thickness(17,11,17,11) };
            var block=new StackPanel(); shell.Child=block;
            block.Children.Add(Author((user?"Вы":"Zarum")+(t.ProjectOnly?" · рабочая папка · только в этом сеансе":"")));
            if(recentWorkers!=null)block.Children.Add(recentWorkers);
            if(!string.IsNullOrEmpty(t.DocumentName)){
                var show=new Button{Content=t.DocumentName,ContentTemplate=OstumActionIcons.Tab("file"),HorizontalAlignment=HorizontalAlignment.Left,ToolTip="Посмотреть текст вложения справа",Margin=new Thickness(0,2,0,5)};
                show.Click+=(s,e)=>{if(work==null)ReadOnlyPreview(Documents.Load(t.DocumentId));};block.Children.Add(show);
            }
            if (!string.IsNullOrEmpty(t.Image) && File.Exists(t.Image)) {
                try {
                    var img=new Image { Source=OpenImage(t.Image,t.Generated?640:320), MaxWidth=t.Generated?420:220, MaxHeight=t.Generated?390:160, Stretch=Stretch.Uniform, HorizontalAlignment=HorizontalAlignment.Left, Margin=new Thickness(0,2,0,8) };
                    block.Children.Add(img);
                } catch (Exception) { block.Children.Add(new TextBlock {Text="Не удалось открыть сохранённое изображение."}); }
            }
            if (!t.Generated || t.Text!="Изображение готово.") block.Children.Add(Formatted(t.Text));
            if(user&&!string.IsNullOrEmpty(t.OriginalText))block.Children.Add(new Expander{Header="Исправлена раскладка · исходный ввод",FontSize=11,Foreground=Color("#536557"),Content=Formatted(t.OriginalText)});
            if (!user) {
                WorkerHistory(block,t); AddResearchDetails(block,t);
                var actions=new WrapPanel { Margin=new Thickness(0,3,0,0) }; block.Children.Add(actions);
                if (!t.Generated&&(!t.ProjectOnly||WorkPolicy.CaptureAllowed)) Small(actions,"Копировать",() => Clipboard.SetText(t.Text??""));
                if(!t.Generated&&!t.ProjectOnly)Small(actions,"Сохранить…",()=>SaveAnswer(t));
                if(!t.Generated)Small(actions,"Озвучить",()=>voice.Speak(t.Text));
                if(!string.IsNullOrEmpty(t.Audio)&&File.Exists(t.Audio)){
                    Small(actions,"Слушать",()=>{try{StopAudio();audioPlayer=new System.Media.SoundPlayer(t.Audio);audioPlayer.Play();}catch(Exception ex){status.Text=ex.Message;}});
                    Small(actions,"Стоп",StopAudio);
                    Small(actions,"Сохранить WAV…",()=>{var save=new SaveFileDialog{Filter="Музыка WAV|*.wav",FileName="OSTUM — музыка.wav"};if(save.ShowDialog(Window)==true&&!string.Equals(Path.GetFullPath(save.FileName),t.Audio,StringComparison.OrdinalIgnoreCase))try{WorkPolicy.LocalPath(save.FileName);File.Copy(t.Audio,save.FileName,true);}catch(Exception ex){status.Text=ex.Message;}});
                }
                if (t.Generated && File.Exists(t.Image)) {
                    Small(actions,"Сохранить PNG…",() => {
                        var dlg=new SaveFileDialog { Filter="Изображение PNG|*.png", FileName="Ostum-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".png" };
                        if(dlg.ShowDialog(Window)==true && !string.Equals(Path.GetFullPath(dlg.FileName),Path.GetFullPath(t.Image),StringComparison.OrdinalIgnoreCase)){WorkPolicy.LocalPath(dlg.FileName);File.Copy(t.Image,dlg.FileName,true);}
                    });
                    Small(actions,"Обсудить",() => { if (work==null) { SetMode(false); LoadPhoto(t.Image); } });
                    Small(actions,"Вариация",() => { if (work==null) { SetMode(true); LoadPhoto(t.Image); } });
                }
            }
            return shell;
        }
        void Small(Panel row,string title,Action action) {
            var b=new Button { Content=title,ContentTemplate=OstumActionIcons.For(title),FontSize=11,Padding=new Thickness(7,3,7,3),Background=Color("#F0F3ED"),Foreground=Color("#526455"),Margin=new Thickness(0,2,6,0) };
            b.Click+=(s,e) => { try { action(); } catch (Exception ex) { status.Text=ex.Message; } }; row.Children.Add(b);
        }
    }
}
