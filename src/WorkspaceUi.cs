using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using Microsoft.Win32;

namespace Sfera {
    internal static class WorkspaceUi {
        internal static Grid View(string name) {
            return (Grid)XamlReader.Parse(Store.Resource(name).Replace("<!--theme-->",Store.Resource("WorkspaceTheme.xaml")));
        }
        internal static void Skin(Window w) {
            w.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(Store.Resource("WorkspaceTheme.xaml")));
            w.FontFamily = new FontFamily("Inter, Segoe UI Variable Text, Segoe UI"); w.FontSize=14;
            w.Background=(Brush)new BrushConverter().ConvertFromString("#F4F5F1"); w.UseLayoutRounding=true;
        }
        internal static Window Dialog(Window owner,string title,int width,int height) {
            var w=new Window {Owner=owner,Title=title,Icon=owner.Icon,WindowStartupLocation=WindowStartupLocation.CenterOwner,Width=Math.Min(width,SystemParameters.WorkArea.Width),Height=Math.Min(height,SystemParameters.WorkArea.Height),MinWidth=355,MinHeight=170};
            Skin(w); return w;
        }
        internal static TextBlock Text(FrameworkElement host,string text,string style) {
            return new TextBlock {Text=text,Style=(Style)host.FindResource(style), Margin=new Thickness(0,3,0,5)};
        }
        internal static Button Button(FrameworkElement host,string label,Action click,string style=null) {
            var b=new Button {Content=label}; if(style!=null)b.Style=(Style)host.FindResource(style); b.Click+=(s,e)=>click();return b;
        }
        internal static int Permission(Window owner, string directory,int current=0) {
            var w=Dialog(owner,"Подключить папку",555,500);var root=new DockPanel{Margin=new Thickness(22)};w.Content=root;
            var buttons=new WrapPanel{HorizontalAlignment=HorizontalAlignment.Right};DockPanel.SetDock(buttons,Dock.Bottom);root.Children.Add(buttons);
            var close=Button(w,"Отмена",()=>w.DialogResult=false,"Flat");close.IsCancel=true;buttons.Children.Add(close);
            var ok=Button(w,"Подключить папку",()=>w.DialogResult=true,"Primary");ok.IsDefault=true;buttons.Children.Add(ok);
            var panel=new StackPanel();root.Children.Add(new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
            panel.Children.Add(Text(w,"Что разрешить в этой папке?","Title"));panel.Children.Add(Text(w,directory,"Hint"));
            var names=new[]{"Только редактор","Локальный анализ","Анализ и обучение"};
            var help=new[]{"Открывайте Word и Excel и вносите правки вручную. Содержимое доступно только редактору.","Можно задать Zarum вопрос о выбранном абзаце или ячейке. Модель работает на вашем ПК.","Вы сможете дополнительно обучать Zarum на отдельно проверенных вопросах и ответах."};
            int choice=current;
            for(int i=0;i<names.Length;i++){
                int index=i;var body=new StackPanel();body.Children.Add(Text(w,names[i],"Subhead"));body.Children.Add(Text(w,help[i],"Hint"));
                var radio=new RadioButton{GroupName="Access",Content=body,IsChecked=i==current,Margin=new Thickness(1,5,0,2),VerticalContentAlignment=VerticalAlignment.Center};radio.Checked+=(s,e)=>choice=index;panel.Children.Add(radio);
            }
            return w.ShowDialog()==true?choice:-1;
        }
        internal static void Guide(Window owner) {
            var w=Dialog(owner,"Как работать в OSTUM Pro",565,466);var root=new DockPanel{Margin=new Thickness(22)};w.Content=root;
            var done=Button(w,"Понятно",()=>w.DialogResult=true,"Primary");done.HorizontalAlignment=HorizontalAlignment.Right;done.IsDefault=true;DockPanel.SetDock(done,Dock.Bottom);root.Children.Add(done);
            var p=new StackPanel();root.Children.Add(new ScrollViewer{Content=p,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});p.Children.Add(Text(w,"От папки до первой правки","Title"));
            string[][] steps={new[]{"1. Откройте рабочую папку","Перетащите папку или DOCX/XLSX из Проводника. Если папка новая, выберите доступ. В организации доступные папки назначает администратор."},new[]{"2. Выберите то, что хотите изменить","Дважды щёлкните файл слева. Нажмите на абзац или ячейку, затем «Предложить правку». В режиме локального анализа кнопка «Спросить Zarum» помогает переформулировать выбранный текст."},new[]{"3. Проверьте и сохраните","Откройте вкладку «Правки», оставьте нужные галочки и нажмите «Сохранить выбранные». Предыдущую редакцию можно вернуть через «Ещё → Версии файла»."}};
            foreach(var step in steps){p.Children.Add(Text(w,step[0],"Subhead"));p.Children.Add(Text(w,step[1],"Hint"));}w.ShowDialog();
        }
    }

    internal sealed class ProAccessWindow : Window {
        readonly Grid view; readonly TextBlock feedback; readonly Button check;
        ProAccessWindow(Window owner) {
            Owner=owner;Icon=owner.Icon;Title="Доступ к OSTUM Pro";WindowStartupLocation=WindowStartupLocation.CenterOwner;
            Width=Math.Min(655,SystemParameters.WorkArea.Width);Height=Math.Min(690,SystemParameters.WorkArea.Height);MinWidth=375;MinHeight=410;WorkspaceUi.Skin(this);
            view=WorkspaceUi.View("ProAccessView.xaml");Content=view;feedback=Get<TextBlock>("Feedback");check=Get<Button>("Verify");
            string request="p_"+ProAccess.Device;Get<TextBox>("Request").Text=request;
            Get<Button>("Channel").Click+=(s,e)=>Brand.OpenChannel(this);check.Click+=(s,e)=>OpenBot(request);
            Get<Button>("Copy").Click+=(s,e)=>{try{Clipboard.SetText(request);feedback.Text="Запрос скопирован. Отправьте его проверяющему боту в Telegram.";}catch{feedback.Text="Выделите код и нажмите Ctrl+C.";}};
            Get<Button>("Import").Click+=(s,e)=>{var pick=new OpenFileDialog{Filter="Подтверждение OSTUM|*.ostumpro",Title="Выберите файл из ответа бота"};if(pick.ShowDialog(this)==true)LoadFile(pick.FileName);};
            Get<Button>("CloseGate").Click+=(s,e)=>DialogResult=false;
            if(!WorkPolicy.Network){Get<Button>("Channel").Visibility=check.Visibility=Visibility.Collapsed;Get<TextBlock>("NetHint").Text="На другом устройстве подпишитесь на @ranisgaliaskarov. Отправьте код ниже боту @"+Brand.SubscriptionBot+" и перенесите файл ответа на рабочий ПК.";}
            AllowDrop=true;PreviewDragOver+=(s,e)=>{if(!e.Data.GetDataPresent(DataFormats.FileDrop))return;e.Effects=IsGrant(e.Data)?DragDropEffects.Copy:DragDropEffects.None;e.Handled=true;};
            PreviewDrop+=(s,e)=>{if(!e.Data.GetDataPresent(DataFormats.FileDrop))return;e.Handled=true;if(IsGrant(e.Data))LoadFile(((string[])e.Data.GetData(DataFormats.FileDrop))[0]);else feedback.Text="Перетащите один файл OSTUM-Pro.ostumpro, полученный от бота.";};
        }
        T Get<T>(string name) where T:FrameworkElement{return (T)view.FindName(name);}
        static bool IsGrant(IDataObject data){var files=data.GetData(DataFormats.FileDrop) as string[];return files!=null&&files.Length==1&&string.Equals(Path.GetExtension(files[0]),".ostumpro",StringComparison.OrdinalIgnoreCase);}
        void LoadFile(string path) {
            try { WorkPolicy.LocalPath(path);using(var f=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)) {
                if(f.Length>2048||f.Length==0)throw new InvalidDataException("Выберите файл .ostumpro из сообщения бота.");
                using(var reader=new StreamReader(f))ProAccess.Import(reader.ReadToEnd());
            } DialogResult=true; } catch(Exception ex) {feedback.Text=ex.Message;}
        }
        void OpenBot(string request) {
            try{WorkPolicy.RequireNetwork();Process.Start(new ProcessStartInfo("https://t.me/"+Brand.SubscriptionBot+"?start="+request){UseShellExecute=true});feedback.Text="В Telegram нажмите «Запустить». Сохраните файл из ответа бота и перетащите его сюда.";}
            catch(Exception){feedback.Text="Откройте @"+Brand.SubscriptionBot+" в Telegram на другом устройстве и отправьте ему код, показанный выше.";}
        }
        internal static bool Ensure(Window owner) {
            try{return ProAccess.Active || (new ProAccessWindow(owner).ShowDialog()==true && ProAccess.Active);}
            catch(Exception ex){MessageBox.Show(owner,ex.Message,"Доступ к OSTUM Pro",MessageBoxButton.OK,MessageBoxImage.Information);return false;}
        }
        internal static void Manage(Window owner) {
            try{var w=new ProAccessWindow(owner);w.Get<Button>("CloseGate").Content="Закрыть";if(ProAccess.Active)w.feedback.Text="На этом компьютере доступ действует до "+ProAccess.Expires.ToString("dd.MM.yyyy HH:mm")+". Его можно продлить заранее.";w.ShowDialog();}
            catch(Exception ex){MessageBox.Show(owner,ex.Message,"Подписка Pro",MessageBoxButton.OK,MessageBoxImage.Information);}
        }
    }
}
