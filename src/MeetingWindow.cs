using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Sfera {
    internal sealed class MeetingWindow {
        readonly Window window;
        readonly Preferences prefs;
        readonly Engine engine;
        readonly string engines;
        readonly DataGrid speech=ReviewTable.New(true),actions=ReviewTable.New(true);
        readonly TabControl tabs=new TabControl();
        readonly ComboBox participants=new ComboBox{MinWidth=136};
        readonly CheckBox includeText=new CheckBox{Content="Включить стенограмму",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(6)};
        readonly MediaPlayer player=new MediaPlayer();
        readonly TextBlock hint;
        readonly Button choose,record,decode,analyze,save,cancel,listen,manual;
        readonly List<MeetingLine> lines=new List<MeetingLine>();
        readonly ObservableCollection<MeetingItem> items=new ObservableCollection<MeetingItem>();
        readonly DispatcherTimer recorderClock=new DispatcherTimer{Interval=TimeSpan.FromSeconds(1)};
        readonly DispatcherTimer playbackClock=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(100)};
        MeetingLine playing;
        readonly Stopwatch recorded=new Stopwatch();
        CancellationTokenSource running;
        MeetingRecorder recorder;
        string source,recordPath;
        bool changed,closing;
        internal MeetingWindow(Window owner,Preferences preferences,Engine local,string binaries){
            prefs=preferences;engine=local;engines=binaries;window=WorkspaceUi.Dialog(owner,"Совещание",1000,670);
            var root=new DockPanel{Margin=new Thickness(20)};window.Content=root;
            var top=ReviewTable.Top(root);top.Children.Add(WorkspaceUi.Text(window,"Совещание","Title"));
            top.Children.Add(WorkspaceUi.Text(window,"Откройте аудиозапись или запишите встречу. Проверьте реплики, затем выберите решения и поручения для протокола.","Hint"));
            var tools=new WrapPanel();top.Children.Add(tools);
            choose=Add(tools,"Открыть запись…",Choose);
            record=Add(tools,"Записать с микрофона…",Record);
            participants.ItemsSource=new[]{"Голоса · авто","Один участник","Два участника","Три участника","Четыре участника","Пять участников","Шесть участников"};participants.SelectedIndex=0;
            participants.ToolTip="Число голосов помогает черновой группировке моно. Для отдельных стереоканалов OSTUM использует каналы.";tools.Children.Add(participants);
            decode=Add(tools,"Распознать",()=>Run(Transcribe),"Primary");
            hint=WorkspaceUi.Text(window,"Аудио и результаты обрабатываются на этом компьютере.","Hint");top.Children.Add(hint);
            var bar=ReviewTable.Bottom(root);listen=Add(bar,"Прослушать реплику",Listen);manual=Add(bar,"Добавить поручение",Manual);
            analyze=Add(bar,"Найти решения и поручения",()=>Run(Analyze));bar.Children.Add(includeText);
            save=Add(bar,"Сохранить протокол…",Save,"Primary");cancel=Add(bar,"Остановить",()=>{if(running!=null)running.Cancel();});
            root.Children.Add(tabs);tabs.Items.Add(new TabItem{Header="Реплики",Content=speech});tabs.Items.Add(new TabItem{Header="Решения и поручения",Content=actions});
            ReviewTable.Column(speech,"Время","Time",78);ReviewTable.Column(speech,"Участник","Speaker",135,false,true);ReviewTable.Column(speech,"Реплика","Text",1,true,true);
            actions.Columns.Add(new DataGridCheckBoxColumn{Header="В протокол",Binding=new Binding("Selected"){Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged}});
            ReviewTable.Column(actions,"Тип","Type",95);ReviewTable.Column(actions,"Что решили / сделать","Title",1.4,true,true);
            ReviewTable.Column(actions,"Ответственный","Owner",106,false,true);ReviewTable.Column(actions,"Срок","Due",90,false,true);ReviewTable.Column(actions,"Основание","Evidence",1,true);
            actions.ItemsSource=items;speech.ItemsSource=lines;speech.CellEditEnding+=(s,e)=>{if(e.EditAction==DataGridEditAction.Commit){items.Clear();changed=true;Update();}};
            actions.CellEditEnding+=(s,e)=>{if(e.EditAction==DataGridEditAction.Commit)changed=true;};
            actions.MouseDoubleClick+=(s,e)=>{if(actions.CurrentColumn!=null&&Equals(actions.CurrentColumn.Header,"Основание"))Listen();};
            includeText.Click+=(s,e)=>Update();tabs.SelectionChanged+=(s,e)=>Update();
            recorderClock.Tick+=(s,e)=>{if(recorder==null)return;hint.Text="Идёт запись · "+recorded.Elapsed.ToString(@"hh\:mm\:ss");
                if(recorder.Error!=null||recorded.Elapsed.TotalHours>=4)Guard(Record);};
            player.MediaOpened+=(s,e)=>{if(playing!=null){player.Position=TimeSpan.FromMilliseconds(playing.Start);player.Play();playbackClock.Start();}};
            playbackClock.Tick+=(s,e)=>{if(playing==null||player.Position.TotalMilliseconds>=playing.End)StopPlayback();};
            player.MediaEnded+=(s,e)=>StopPlayback();
            player.MediaFailed+=(s,e)=>{StopPlayback();hint.Text="Windows не смогла воспроизвести этот файл.";};
            window.Closing+=(s,e)=>{
                if(running!=null){closing=true;running.Cancel();e.Cancel=true;return;}
                if(recorder!=null){e.Cancel=true;hint.Text="Нажмите «Завершить запись», затем закройте окно.";return;}
                if(!Discard()){closing=false;e.Cancel=true;}
            };
            window.Closed+=(s,e)=>{recorderClock.Stop();StopPlayback();};
            Update();
        }
        internal void Show(){window.ShowDialog();}
        Button Add(Panel panel,string label,Action action,string style="Flat"){
            var button=WorkspaceUi.Button(window,label,()=>Guard(action),style);button.Margin=new Thickness(2);panel.Children.Add(button);return button;
        }
        void Guard(Action action){try{action();}catch(Exception ex){hint.Text=ex.Message;}}
        bool Discard(){return !changed||MessageBox.Show(window,"Закрыть результаты без сохранения протокола?","Совещание",MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)==MessageBoxResult.Yes;}
        void StopPlayback(){playing=null;playbackClock.Stop();player.Close();}
        void Reset(string file){StopPlayback();source=file;lines.Clear();speech.Items.Refresh();items.Clear();changed=false;tabs.SelectedIndex=0;hint.Text=Path.GetFileName(file)+" · нажмите «Распознать».";Update();}
        void Choose(){
            var picker=new OpenFileDialog{Title="Открыть запись совещания",Filter="Аудиозаписи|*.wav;*.mp3;*.m4a;*.aac;*.wma;*.mp4;*.flac|Все файлы|*.*"};
            if(picker.ShowDialog(window)!=true)return;MeetingAudio.CheckSource(picker.FileName);if(Discard())Reset(picker.FileName);
        }
        void Record(){
            if(recorder!=null){
                string error=recorder.Error==null?null:recorder.Error.Message;string file;
                try{file=recorder.Finish();}finally{recorder.Dispose();recorder=null;recorderClock.Stop();recorded.Stop();Update();}
                Reset(file);if(error!=null)hint.Text="Сохранена доступная часть записи. "+error;return;
            }
            var picker=new SaveFileDialog{Title="Куда записать совещание",Filter="Запись WAV|*.wav",FileName="Совещание "+DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss")+".wav"};
            if(picker.ShowDialog(window)!=true||!Discard())return;
            recordPath=picker.FileName;StopPlayback();recorder=MeetingRecorder.Start(recordPath,prefs.MicrophoneId);changed=false;
            recorded.Restart();recorderClock.Start();hint.Text="Идёт запись с выбранного в OSTUM микрофона.";Update();
        }
        async void Run(Func<CancellationToken,Task> action){
            if(running!=null)return;StopPlayback();var cts=running=new CancellationTokenSource();Update();
            try{await action(cts.Token);}catch(OperationCanceledException){hint.Text="Операция остановлена.";}catch(Exception ex){hint.Text=ex.Message;}
            finally{running=null;cts.Dispose();Update();if(closing)window.Close();}
        }
        void Progress(string message){window.Dispatcher.BeginInvoke(new Action(()=>{if(running!=null)hint.Text=message;}));}
        async Task Transcribe(CancellationToken token){
            if(!Discard())return;int count=participants.SelectedIndex;string path=source;
            var found=await Task.Run(()=>MeetingAudio.Transcribe(path,engines,prefs.Models,count,Progress,token),token);
            token.ThrowIfCancellationRequested();MeetingAudio.CheckSource(path);items.Clear();lines.Clear();lines.AddRange(found);
            speech.Items.Refresh();speech.SelectedIndex=lines.Count>0?0:-1;tabs.SelectedIndex=0;changed=lines.Count>0;
            hint.Text=lines.Count==0?"Речь не обнаружена.":"Реплик · "+lines.Count+". Проверьте текст и подписи голосов. Дважды нажмите ячейку для исправления.";
        }
        async Task Analyze(CancellationToken token){
            speech.CommitEdit(DataGridEditingUnit.Cell,true);speech.CommitEdit(DataGridEditingUnit.Row,true);
            if(items.Count>0&&MessageBox.Show(window,"Заменить текущие строки новым разбором?","Решения и поручения",MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)!=MessageBoxResult.Yes)return;
            if(!Store.Ready(Store.Chat(prefs),prefs.Models))throw new IOException("Для разбора подключите текстовую модель в настройках OSTUM.");
            var found=await MeetingMinutes.Analyze(source,lines,prefs,engine,Progress,token);token.ThrowIfCancellationRequested();
            items.Clear();foreach(var item in found)items.Add(item);tabs.SelectedIndex=1;changed=true;
            hint.Text=items.Count==0?"Подтверждённых цитатами решений и поручений не найдено. Можно добавить поручение из выбранной реплики.":"Проверьте цитаты, ответственных и сроки. Отметьте строки для протокола.";
        }
        void Manual(){
            var l=speech.SelectedItem as MeetingLine;if(l==null){tabs.SelectedIndex=0;hint.Text="Выберите исходную реплику.";return;}
            string title=ProjectWindow.Ask(window,"Поручение из реплики",l.Time+" · "+l.Text,"");if(string.IsNullOrWhiteSpace(title))return;
            items.Add(new MeetingItem{Title=title,Type="Поручение",Line=l.Id,Evidence=l.Time+" · "+l.Text,Selected=true});changed=true;tabs.SelectedIndex=1;actions.SelectedIndex=items.Count-1;
        }
        void Listen(){
            MeetingAudio.CheckSource(source);var l=speech.SelectedItem as MeetingLine;
            if(tabs.SelectedIndex==1){var row=actions.SelectedItem as MeetingItem;l=row==null?null:lines.FirstOrDefault(i=>i.Id==row.Line);}
            if(l==null){hint.Text="Выберите строку для прослушивания.";return;}
            StopPlayback();playing=l;player.Open(new Uri(source));hint.Text="Воспроизведение с "+l.Time+" · "+l.Speaker;
        }
        void Save(){
            actions.CommitEdit(DataGridEditingUnit.Cell,true);actions.CommitEdit(DataGridEditingUnit.Row,true);
            if(!items.Any(i=>i.Selected)&&includeText.IsChecked!=true){hint.Text="Отметьте строки в столбце «В протокол» или включите стенограмму.";return;}
            var picker=new SaveFileDialog{InitialDirectory=Path.GetDirectoryName(source),Title="Сохранить новый протокол",Filter="Текстовый протокол|*.txt",FileName="Протокол "+Path.GetFileNameWithoutExtension(source)+".txt"};
            if(picker.ShowDialog(window)!=true)return;MeetingMinutes.Save(source,picker.FileName,lines,items,includeText.IsChecked==true);changed=false;hint.Text="Протокол сохранён · "+picker.FileName;
        }
        void Update(){
            if(save==null)return;bool idle=running==null&&recorder==null,has=lines.Count>0;
            choose.IsEnabled=idle;record.IsEnabled=running==null;record.Content=recorder==null?"Записать с микрофона…":"Завершить запись";
            decode.IsEnabled=idle&&source!=null;participants.IsEnabled=idle;cancel.Visibility=running==null?Visibility.Collapsed:Visibility.Visible;
            analyze.IsEnabled=save.IsEnabled=listen.IsEnabled=manual.IsEnabled=idle&&has;speech.IsEnabled=actions.IsEnabled=idle;
        }
    }
}
