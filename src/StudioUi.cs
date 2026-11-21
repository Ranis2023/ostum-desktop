using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Forms=System.Windows.Forms;

namespace Sfera {
    public sealed partial class Desktop {
        Voice voice;
        CancellationTokenSource speechWork;
        readonly DispatcherTimer recordMeter=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(70)};
        readonly Stopwatch recordTime=new Stopwatch();
        bool recordingFlow;
        string meetingEngines;
        bool meetingOpen;
        void SetupStudio(string engines) {
            meetingEngines=engines;
            voice=new Voice(engines);
            var net=Find<CheckBox>("WebSearch");net.IsChecked=prefs.AllowWeb&&WorkPolicy.Network;net.IsEnabled=WorkPolicy.Network;
            Action labelNetwork=()=>{Find<TextBlock>("HomePrivacy").Text=net.IsChecked==true&&net.IsEnabled?"Веб-поиск разрешён":"Локальный режим";};
            labelNetwork();
            RoutedEventHandler saveNet=(s,e)=>{prefs.AllowWeb=net.IsChecked==true&&WorkPolicy.Network;labelNetwork();Store.Save(prefs);};
            net.Checked+=saveNet;net.Unchecked+=saveNet;
            Find<Button>("Microphone").Click+=async(s,e)=>await Dictate();
            Find<Button>("MicDevices").Click+=(s,e)=>ShowMicDevices();
            Find<Button>("Toolbox").Click+=(s,e)=>ShowTools();
            Find<Button>("AboutApp").Click+=(s,e)=>About();
            Find<Button>("CollapseSidebar").Click+=(s,e)=>{historyAutoHidden=false;ToggleSidebar();Find<Button>("ExpandSidebar").Focus();};
            Find<Button>("ExpandSidebar").Click+=(s,e)=>{historyAutoHidden=false;ToggleSidebar();Find<Button>("CollapseSidebar").Focus();};
            recordMeter.Tick+=async(s,e)=>{
                if(!voice.Recording){recordMeter.Stop();return;}
                var elapsed=recordTime.Elapsed;
                Find<TextBlock>("MicTime").Text=elapsed.Minutes+":"+elapsed.Seconds.ToString("00");
                var level=Find<Border>("MicLevel");
                level.Width=Math.Max(voice.Level*74,level.Width*.68);
                if(elapsed>=TimeSpan.FromMinutes(3)||voice.RecordingError!=null)await Dictate();
            };
            Window.Closing+=(s,e)=>{if(e.Cancel)return;recordMeter.Stop();if(speechWork!=null)speechWork.Cancel();voice.Dispose();};
        }
        void ShowMicDevices() {
            if(voice.Recording||speechWork!=null)return;
            try {
                var devices=MicrophoneCapture.Inputs();
                var host=Find<Button>("MicDevices");
                var menu=new ContextMenu{PlacementTarget=host,Placement=PlacementMode.Top};
                var current=devices.FirstOrDefault(m=>m.IsDefault);
                AddMicChoice(menu,"Как в Windows"+(current==null?"":" · "+current.Name),null);
                menu.Items.Add(new Separator());
                foreach(var device in devices)AddMicChoice(menu,device.Name,device.Id);
                host.ContextMenu=menu;menu.IsOpen=true;
            }catch(Exception ex){status.Text="Не удалось получить список микрофонов. "+ex.Message;}
        }
        void AddMicChoice(ItemsControl menu,string label,string id) {
            var item=new MenuItem{Header=label,Icon=OstumActionIcons.Menu(label,"mic"),IsCheckable=true,IsChecked=string.Equals(id??"",prefs.MicrophoneId??"",StringComparison.OrdinalIgnoreCase)};
            item.Click+=(s,e)=>{
                string old=prefs.MicrophoneId;
                try{prefs.MicrophoneId=id;Store.Save(prefs);Find<Button>("MicDevices").ToolTip=label;status.Text="Микрофон для диктовки · "+label;}
                catch(Exception ex){prefs.MicrophoneId=old;status.Text="Не удалось сохранить микрофон. "+ex.Message;}
            };
            menu.Items.Add(item);
        }
        void ToggleSidebar(){
            var col=Find<Grid>("Layout").ColumnDefinitions[0];bool open=col.Width.Value>40;
            col.Width=new GridLength(open?0:215);Find<Button>("ExpandSidebar").Visibility=open?Visibility.Visible:Visibility.Collapsed;
            Find<Border>("SidebarRoot").Visibility=open?Visibility.Collapsed:Visibility.Visible;
        }
        static MenuItem Item(ItemsControl menu,string text,Action run,string kind=null) {
            var item=new MenuItem{Header=text,Icon=OstumActionIcons.Menu(text,kind)};item.Click+=(s,e)=>run();menu.Items.Add(item);return item;
        }
        void ShowModes(){
            var menu=new ContextMenu{PlacementTarget=Find<Button>("ChatMode"),Placement=PlacementMode.Top};
            var automatic=Item(menu,"Авто · подобрать инструменты",()=>SetMode(false));
            var team=Item(menu,"Команда · несколько агентов",()=>SetMode(false,true));
            var drawing=Item(menu,"Рисование · создать изображение",()=>SetMode(true));
            foreach(var item in new[]{automatic,team,drawing})item.IsCheckable=true;
            automatic.IsChecked=!agents&&!art;team.IsChecked=agents;drawing.IsChecked=art;
            Find<Button>("ChatMode").ContextMenu=menu;menu.IsOpen=true;
        }
        void ShowTools() {
            var menu=new ContextMenu();var host=Find<Button>("Toolbox");menu.PlacementTarget=host;
            Item(menu,"Найти сообщение…",SearchHistory,"search").IsEnabled=CanManageHistory;
            Item(menu,"Сценарии сверки Excel…",async()=>{
                if(work!=null||speechWork!=null||voice.Recording||PaneBusy||!ShowProject())return;
                try{status.Text=await chatProject.ChatAction(new ChatFileIntent{Action="recipes",Value=""},"",CancellationToken.None);}catch(Exception ex){status.Text=ex.Message;}
            },"compare");
            Item(menu,"Совещание · запись и протокол…",OpenMeeting,"record");
            Item(menu,"Место на диске…",()=>{if(work==null&&speechWork==null&&!PaneBusy&&!voice.Recording)StorageWindow.Show(Window,prefs);},"storage");
            menu.Items.Add(new Separator());
            Item(menu,"Создать инструментальную музыку…",()=>{SetMode(false);if(string.IsNullOrWhiteSpace(input.Text))input.Text="Сгенерируй музыку на 15 секунд для ";input.Focus();input.CaretIndex=input.Text.Length;});menu.Items.Add(new Separator());
            var screens=new MenuItem{Header="Приложить снимок экрана",Icon=OstumActionIcons.Menu("Приложить снимок экрана")};menu.Items.Add(screens);
            foreach(var monitor in Forms.Screen.AllScreens) {
                var chosen=monitor;string label=chosen.DeviceName.Substring(chosen.DeviceName.LastIndexOf('\\')+1)+" · "+chosen.Bounds.Width+" × "+chosen.Bounds.Height;
                Item(screens,label,async()=>await CaptureScreen(chosen),"screen");
            }
            Item(menu,"Окна и приложения…",ShowComputer).IsEnabled=WorkPolicy.CaptureAllowed;screens.IsEnabled=WorkPolicy.CaptureAllowed;
            menu.Items.Add(new Separator());
            var dialog=new MenuItem{Header="Голосовой диалог",Icon=OstumActionIcons.Menu("Голосовой диалог"),IsCheckable=true,IsChecked=prefs.ConversationVoice,ToolTip="После записи отправлять фразу и читать ответ вслух"};
            dialog.Click+=(s,e)=>{prefs.ConversationVoice=dialog.IsChecked;Store.Save(prefs);if(!dialog.IsChecked)voice.StopSpeech();};
            menu.Items.Add(dialog); Item(menu,"Остановить озвучивание",()=>voice.StopSpeech());
            if(!Store.DictationOn(prefs)||!Store.Files["voice"].Present(prefs.Models))Item(menu,"Подключить диктовку…",async()=>await PrepareVoice());
            menu.Items.Add(new Separator());Item(menu,"Мой рабочий профиль…",ShowProfile).IsEnabled=!WorkPolicy.Rules.Managed;
            Item(menu,"О программе и обновлениях…",About);
            menu.IsOpen=true;
        }
        async void OpenMeeting(){
            if(meetingOpen||work!=null||speechWork!=null||voice.Recording||PaneBusy)return;
            meetingOpen=true;prepareDraft.Stop();idle.Stop();
            try{WorkPolicy.RequireAI();if(!await PrepareVoice())return;StopAudio();voice.StopSpeech();
                new MeetingWindow(Window,prefs,engine,meetingEngines).Show();
            }catch(Exception ex){status.Text=ex.Message;}finally{meetingOpen=false;if(!closing&&work==null)idle.Start();}
        }
        bool OpenChatUtility(string request){
            if(art||attached!=null||document!=null)return false;
            string text=(request??"").Trim().TrimEnd('.','!','?').ToLowerInvariant();
            Action launch;
            switch(text){
                case "открой совещание":case "разбери запись совещания":case "запиши совещание":launch=OpenMeeting;break;
                case "открой хранилище":case "покажи место на диске":launch=()=>StorageWindow.Show(Window,prefs);break;
                case "найди сообщение":case "открой поиск по сообщениям":launch=SearchHistory;break;
                default:return false;
            }
            input.Clear();prepareDraft.Stop();launch();return true;
        }
        Task<bool> PrepareVoice() {
            if(Store.DictationOn(prefs)&&Store.Files["voice"].Present(prefs.Models))return Task.FromResult(true);
            if(work!=null||speechWork!=null)return Task.FromResult(false);
            bool applied=new ModelSettings(Window,prefs,false,true).Show();
            if(applied){engine.StopChat();Labels();}
            return Task.FromResult(applied&&Store.DictationOn(prefs)&&Store.Files["voice"].Present(prefs.Models));
        }
        void VoiceButtons(bool active,string action) {
            var micButton=Find<Button>("Microphone");
            string micAction=!active?"Записать голосовое сообщение":action=="Распознать"?"Завершить запись":"Отменить распознавание";
            micButton.Content=micButton.ToolTip=micAction;
            System.Windows.Automation.AutomationProperties.SetName(micButton,micAction);
            micButton.Background=Color(active?"#F8E2DB":"#E8EEE8");
            var send=Find<Button>("Send");send.Content=active?action:(art?"Нарисовать":"Отправить");
            send.ToolTip=active?action:"Enter — отправить, Shift+Enter — новая строка";
            Find<Button>("MicDevices").IsEnabled=!active;
            Find<Button>("NewChat").IsEnabled=Find<Button>("Models").IsEnabled=list.IsEnabled=!active;
            foreach(string id in new[]{"Attach","QuickDocument","QuickAgents","QuickPhoto","QuickArt","Projects","PreviewToggle","Toolbox","ChatMode"})
                Find<Button>(id).IsEnabled=!active;
            UpdateSendEnabled();
        }
        void MicIdle(){
            recordMeter.Stop();recordTime.Reset();Find<Border>("MicStrip").Visibility=Visibility.Collapsed;
            if(!closing&&work==null)VoiceButtons(false,null);
        }
        void CancelVoice(){
            if(speechWork!=null){speechWork.Cancel();return;}
            voice.Discard();MicIdle();status.Text="Запись отменена.";input.Focus();
        }
        async Task Dictate(){
            if(speechWork!=null){speechWork.Cancel();return;}
            if(work!=null||recordingFlow)return;recordingFlow=true;
            string wav=null;
            try {
                if(!voice.Recording){
                    if(!await PrepareVoice()||closing)return;
                    voice.Start(prefs.MicrophoneId);recordTime.Restart();
                    Find<TextBlock>("MicInputName").Text=voice.InputName;Find<TextBlock>("MicTime").Text="0:00";
                    Find<Border>("MicLevel").Width=0;Find<Border>("MicStrip").Visibility=Visibility.Visible;
                    VoiceButtons(true,"Распознать");recordMeter.Start();input.Focus();
                    status.Text="Идёт запись. Enter — распознать, Esc — отменить.";return;
                }
                recordMeter.Stop();recordTime.Stop();Find<Border>("MicStrip").Visibility=Visibility.Collapsed;
                wav=voice.Finish();var c=speechWork=new CancellationTokenSource();
                try{
                    VoiceButtons(true,"Отменить");
                    status.Text="Whisper распознаёт речь на этом ПК…";
                    string words=await voice.Recognize(wav,prefs.Models,c.Token);
                    if(closing)return;
                    if(string.IsNullOrWhiteSpace(words)){status.Text="В записи не удалось разобрать слова.";return;}
                    input.Text=(input.Text.TrimEnd()+" "+words).Trim();input.CaretIndex=input.Text.Length;input.Focus();
                    status.Text=prefs.ConversationVoice?"":"Диктовка готова. Текст можно исправить и отправить.";
                }finally{speechWork=null;c.Dispose();MicIdle();}
                if(prefs.ConversationVoice&&!closing)await Send();
            }catch(OperationCanceledException){status.Text="Распознавание остановлено.";}
            catch(Exception ex){status.Text=ex.Message;}
            finally{recordingFlow=false;if(!voice.Recording&&!closing)MicIdle();if(wav!=null&&File.Exists(wav))File.Delete(wav);}
        }
        async Task CaptureScreen(Forms.Screen monitor){
            if(work!=null||voice.Recording)return;
            try{await CaptureIntoDraft(monitor);status.Text="Снимок приложен. Можно задать вопрос или убрать снимок.";}
            catch(Exception ex){status.Text="Не удалось получить снимок. "+ex.Message;}
        }
        async Task CaptureIntoDraft(Forms.Screen monitor) {
            if(!WorkPolicy.CaptureAllowed)throw new UnauthorizedAccessException("В защищённом пространстве снимки экрана отключены.");
            var state=Window.WindowState;Window.Hide();
            try{await Task.Delay(330);var shot=await Task.Run(()=>Computer.Capture(monitor));SetAttachment(Scale(shot),"Снимок экрана");}
            finally{if(!closing){Window.Show();Window.WindowState=state;Window.Activate();input.Focus();}}
        }
        Window Sheet(string title,int width,int height,FrameworkElement body) {
            return new Window{Owner=Window,Title=title,Width=width,Height=height,MinWidth=430,MinHeight=280,WindowStartupLocation=WindowStartupLocation.CenterOwner,
                Background=Color("#F8F8FC"),FontFamily=Window.FontFamily,Content=body,Icon=Window.Icon};
        }
        static Button ButtonFor(string text,Action click){
            var b=new Button{ContentTemplate=OstumActionIcons.For(text),Content=text,Margin=new Thickness(0,3,8,3),Padding=new Thickness(12,7,12,7)};
            b.Click+=(s,e)=>click();return b;
        }
        void ShowProfile(){
            string path=Personal.ProfilePath;
            var root=new DockPanel{Margin=new Thickness(20)};var toolbar=new StackPanel();DockPanel.SetDock(toolbar,Dock.Top);root.Children.Add(toolbar);
            var enabled=new CheckBox{Content="Учитывать рабочий профиль",IsChecked=prefs.Personalize,Margin=new Thickness(0,0,0,9)};toolbar.Children.Add(enabled);
            toolbar.Children.Add(new TextBlock{Text=File.Exists(Personal.Adapter)?"Zarum 9B · личный адаптер формата работы установлен":"Задачи, стиль и требования к результату",Foreground=Color("#655D76"),Margin=new Thickness(0,0,0,10)});
            string derived=Personal.ReadText(Personal.HistoryPath);
            if(derived.Length>0)toolbar.Children.Add(new Expander{Header="Предпочтения из истории ChatGPT",Margin=new Thickness(0,0,0,8),Content=new TextBlock{Text=derived,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(8)}});
            var edit=new TextBox{Text=File.Exists(path)?File.ReadAllText(path):"",MaxLength=2400,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Padding=new Thickness(9)};
            var win=Sheet("Мой рабочий профиль",580,430,root);
            var bar=new WrapPanel{HorizontalAlignment=HorizontalAlignment.Right};DockPanel.SetDock(bar,Dock.Bottom);root.Children.Add(bar);
            bar.Children.Add(ButtonFor("Отмена",()=>win.Close()));
            bar.Children.Add(ButtonFor("Сохранить",()=>{
                try{Personal.WriteProfile(edit.Text);prefs.Personalize=enabled.IsChecked==true;Store.Save(prefs);engine.StopChat();win.Close();}
                catch(Exception ex){MessageBox.Show(win,ex.Message,"Ostum Desktop");}
            }));
            root.Children.Add(edit);win.ShowDialog();
        }
        void ShowComputer(){
            var root=new DockPanel{Margin=new Thickness(18)};var listWindows=new ListBox{DisplayMemberPath="Title",Margin=new Thickness(0,8,0,10)};
            var win=Sheet("Окна и приложения",610,420,root);var head=new WrapPanel();DockPanel.SetDock(head,Dock.Top);root.Children.Add(head);
            Action refresh=()=>{listWindows.ItemsSource=Computer.Windows(new WindowInteropHelper(Window).Handle);if(listWindows.Items.Count>0)listWindows.SelectedIndex=0;};
            foreach(var pair in new[]{new[]{"Блокнот","notepad"},new[]{"Калькулятор","calc"},new[]{"Проводник","explorer"}}){
                string command=pair[1];head.Children.Add(ButtonFor(pair[0],()=>{try{Computer.Launch(command);win.Close();}catch(Exception ex){MessageBox.Show(win,ex.Message);}}));
            }
            head.Children.Add(ButtonFor("Обновить",refresh));
            var bar=new WrapPanel();DockPanel.SetDock(bar,Dock.Bottom);root.Children.Add(bar);
            foreach(var pair in new[]{new[]{"Перейти","focus"},new[]{"Левая половина","left"},new[]{"Правая половина","right"},new[]{"Развернуть","max"},new[]{"Свернуть","min"}}){
                string action=pair[1];bar.Children.Add(ButtonFor(pair[0],()=>{var chosen=listWindows.SelectedItem as AppWindow;if(chosen==null)return;try{win.Close();Computer.Arrange(chosen,action);}catch(Exception ex){status.Text=ex.Message;}}));
            }
            root.Children.Add(listWindows);refresh();win.ShowDialog();
        }
        void StartProgress(){
            Find<WrapPanel>("StageList").Children.Clear();var panel=Find<Border>("RunPanel");panel.Visibility=Visibility.Visible;
            var dot=Find<System.Windows.Shapes.Ellipse>("RunDot");
            if(SystemParameters.ClientAreaAnimation){var pulse=new DoubleAnimation(1,.32,TimeSpan.FromMilliseconds(750)){AutoReverse=true,RepeatBehavior=RepeatBehavior.Forever};dot.BeginAnimation(UIElement.OpacityProperty,pulse);}
            Progress("Оркестратор выбирает инструменты");
        }
        void Progress(string line){
            status.Text=line;
            string name=null;
            if(line.StartsWith("Оркестратор"))name="Оркестратор";
            else if(line.StartsWith("Координатор"))name="План";
            else if(line.StartsWith("Аналитик и проверяющий"))name="Анализ и проверка";
            else if(line.StartsWith("Аналитик"))name="Аналитик";
            else if(line.StartsWith("Рецензент"))name="Рецензент";
            else if(line.StartsWith("Редактор"))name="Редактор";
            else if(line.StartsWith("Веб")||line.StartsWith("Ищу")||line.StartsWith("Поиск ·"))name="Поиск";
            else if(line.StartsWith("Генератор")||line.StartsWith("Рисую"))name="Изображение";
            else if(line.StartsWith("Рабочий профиль"))name="Профиль";
            else if(line.StartsWith("Компьютер"))name="Компьютер";
            else if(line.StartsWith("Читаю документ"))name="Документ";
            else if(line.StartsWith("Отвечаю")||line.StartsWith("Составляю ответ")||line.StartsWith("Обдумываю"))name="Ответ";
            if(name==null)return;
            var strip=Find<WrapPanel>("StageList");
            int count=strip.Children.Count;
            if(count>0){
                var previous=(ContentControl)strip.Children[count-1];
                if(object.Equals(previous.Content,name))return;
                previous.ContentTemplate=OstumActionIcons.Tab("check");
                previous.Foreground=Color("#536559");previous.FontWeight=FontWeights.Normal;
            }
            if(count==8)strip.Children.RemoveAt(0);
            strip.Children.Add(new ContentControl{Content=name,ContentTemplate=OstumActionIcons.Tab("auto"),Focusable=false,
                Foreground=Color("#35543F"),FontSize=12,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,1,12,1)});
        }
        void EndProgress(){Find<System.Windows.Shapes.Ellipse>("RunDot").BeginAnimation(UIElement.OpacityProperty,null);Find<Border>("RunPanel").Visibility=Visibility.Collapsed;}
    }
}
