using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace Sfera {
    sealed partial class ModelSettings {
        readonly Window window;
        readonly CheckBox art, voice;
        readonly CheckBox thinking;
        readonly TextBox folder;
        readonly Button browse, apply, dismiss;
        readonly TextBlock notice;
        readonly ProgressBar progress;
        readonly Preferences prefs;
        CancellationTokenSource transfer;
        T Find<T>(string id) where T:FrameworkElement { return (T)window.FindName(id); }
        public ModelSettings(Window owner, Preferences p,bool wantDrawing=false,bool wantVoice=false) {
            prefs = p;
            window = (Window)XamlReader.Parse(Store.Resource("ModelsWindow.xaml"));
            window.Resources.MergedDictionaries.Add(owner.Resources); window.Owner = owner;
            Find<Button>("DeveloperChannel").Click+=(s,e)=>Brand.OpenChannel(window);
            art = Find<CheckBox>("ArtChoice"); voice=Find<CheckBox>("VoiceChoice");
            thinking = Find<CheckBox>("Thinking"); folder = Find<TextBox>("Folder");
            browse = Find<Button>("Browse"); apply = Find<Button>("Apply"); dismiss = Find<Button>("Dismiss");
            notice = Find<TextBlock>("Notice"); progress = Find<ProgressBar>("Progress");
            art.IsChecked=wantDrawing||Store.DrawingOn(p);voice.IsChecked=wantVoice||Store.DictationOn(p);
            folder.Text = p.Models; thinking.IsChecked = p.Think;
            SetupLibrary();
            art.Click+=(s,e)=>Refresh();voice.Click+=(s,e)=>Refresh();
            browse.Click += (s,e) => {
                using (var dlg = new Forms.FolderBrowserDialog { Description="Папка для Zarum и дополнительных модулей", SelectedPath=folder.Text }) {
                    if (dlg.ShowDialog() == Forms.DialogResult.OK) { folder.Text = dlg.SelectedPath; Refresh(); }
                }
            };
            apply.Click += Apply;
            dismiss.Click += (s,e) => { if (transfer != null) transfer.Cancel(); else window.Close(); };
            window.Closing += (s,e) => { if (transfer != null) { transfer.Cancel(); e.Cancel=true; } };
            Refresh();
        }
        ModelFile[] Selected() {
            var ids=Chosen.Files.AsEnumerable();
            if(art.IsChecked==true)ids=ids.Concat(Store.Art(prefs).Files);
            if(voice.IsChecked==true)ids=ids.Concat(new[]{"voice"});
            return ids.Select(id=>Store.Files[id]).ToArray();
        }
        void Refresh() {
            Find<TextBlock>("ChatInfo").Text = Chosen.Description;
            var files = Selected();
            var missing=files.Where(f=>!f.Present(folder.Text)).ToArray();
            long online=missing.Sum(f=>f.bytes);
            Find<TextBlock>("SizeInfo").Text=online==0?"Все выбранные файлы есть на диске.":"Загрузка из Hugging Face · "+Store.Size(online);
            apply.Content=online>0?"Скачать и применить":"Применить";
            notice.Text = "";
        }
        async void Apply(object sender, RoutedEventArgs e) {
            if (transfer != null) return;
            var cts = transfer = new CancellationTokenSource();
            BusyLibrary(true);
            voice.IsEnabled = art.IsEnabled = folder.IsEnabled = browse.IsEnabled = thinking.IsEnabled = apply.IsEnabled = false;
            progress.Visibility = Visibility.Visible; dismiss.Content = "Остановить";
            var throttle = Stopwatch.StartNew(); string last = null;
            bool success = false;
            try {
                await Downloads.Prepare(Selected(), folder.Text, (label,value) => {
                    if (last == label && throttle.ElapsedMilliseconds < 150 && value < 1) return;
                    last=label; throttle.Restart();
                    window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => {
                        if (transfer == cts) { notice.Text = label; progress.Value = value; }
                    }));
                }, cts.Token);
                var next = Store.Load();
                if(Chosen.Imported!=null&&(changedImport||!Chosen.Imported.Available()||next.Chat!=Chosen.Id))await CheckCandidate(next,Chosen.Imported,cts.Token);
                next.ImportedModels=library.Where(m=>m.Imported!=null).Select(m=>m.Imported).ToList();
                next.Chat=Chosen.Id;
                next.Models=folder.Text;next.Think=thinking.IsChecked==true;next.Drawing=art.IsChecked==true;next.Dictation=voice.IsChecked==true;
                Store.Save(next);
                prefs.Chat=next.Chat;prefs.ImportedModels=next.ImportedModels;prefs.Art=next.Art;prefs.Models=next.Models;prefs.Think=next.Think;prefs.Drawing=next.Drawing;prefs.Dictation=next.Dictation;
                success = true;
            } catch (Exception ex) {
                notice.Text = cts.IsCancellationRequested ? "Загрузка остановлена. Её можно продолжить из этой папки." : ex.Message;
            } finally {
                transfer = null; cts.Dispose();
                BusyLibrary(false);
                voice.IsEnabled = art.IsEnabled = folder.IsEnabled = browse.IsEnabled = thinking.IsEnabled = apply.IsEnabled = true;
                progress.Visibility = Visibility.Collapsed; dismiss.Content = "Закрыть";
            }
            if (success) window.DialogResult = true;
        }
        public bool Show() { return window.ShowDialog() == true; }
    }
}
