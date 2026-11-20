using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms=System.Windows.Forms;

namespace Sfera {
    public sealed partial class ProjectWindow {
        bool drawerOpen;
        T Get<T>(string name) where T:FrameworkElement {return (T)view.FindName(name);}
        void Click(string name,Action fn){Get<Button>(name).Click+=(s,e)=>{if(!busy)Try(fn);};}
        void InitializeWorkspace(){
            PreviewKeyDown+=(s,e)=>{if(e.Key==System.Windows.Input.Key.F1){e.Handled=true;WorkspaceUi.Help(this,"projects");}};
            Click("Help",()=>WorkspaceUi.Help(this,"projects"));Click("AddFolder",PickFolder);Click("PickFolder",PickFolder);Click("PickFile",PickOffice);
            Click("FolderAccess",()=>{RequireProject();if(WorkPolicy.Rules.Managed)return;int mode=WorkspaceUi.Permission(this,project.Root,project.Training?2:project.Analysis?1:0);if(mode<0||!Discard())return;WorkPolicy.Register(project.Root,mode>=1,mode==2);RefreshProjects(project.Root);status.Text="Доступ к папке обновлён.";});
            Click("ToggleFiles",()=>{drawerOpen=!drawerOpen;LayoutPanels();});Get<Border>("Shade").MouseLeftButtonDown+=(s,e)=>CloseDrawer();
            PreviewKeyDown+=(s,e)=>{if(drawerOpen&&e.Key==System.Windows.Input.Key.Escape){CloseDrawer();e.Handled=true;}};
            Click("Up",()=>{RequireProject();var parent=Directory.GetParent(folder);if(parent!=null&&WorkPolicy.Within(parent.FullName,project.Root)&&Discard()){folder=parent.FullName;Clear();ListFiles();}});
            Click("FileMenu",FileActions);Click("SearchProject",()=>Run(SearchProject));Click("Templates",CompanyTools);
            Click("Train",()=>{RequireProject();engine.StopChat();new TrainingWindow(this,project,prefs).ShowDialog();engine.StopChat();});
            Click("Sample",()=>Run(Example));Click("Home",()=>{if(Discard()){Clear();status.Text="Выберите другой файл или перетащите его из Проводника.";}});
            Click("Edit",()=>Edit(false));Click("Ask",()=>Run(Analyze));Click("More",More);Click("Save",()=>Run(Save));
            Click("Uncheck",()=>{foreach(var p in patches)p.Selected=false;changes.Items.Refresh();});
            patches.CollectionChanged+=(s,e)=>RefreshView();grid.SelectionChanged+=(s,e)=>RefreshView();tabs.SelectionChanged+=(s,e)=>{if(e.OriginalSource==tabs)RefreshView();};
            AllowDrop=true;PreviewDragOver+=DragOverFile;PreviewDrop+=DropFiles;
            PreviewDragLeave+=(s,e)=>{var position=e.GetPosition(this);if(position.X<=1||position.Y<=1||position.X>=ActualWidth-1||position.Y>=ActualHeight-1)Get<Border>("DropOverlay").Visibility=Visibility.Collapsed;};
            SizeChanged+=(s,e)=>LayoutPanels();
        }
        void CloseDrawer(){drawerOpen=false;LayoutPanels();}
        void LayoutPanels(){
            bool compact=ActualWidth>0&&ActualWidth<850;if(!compact)drawerOpen=false;
            var side=Get<Border>("Sidebar");((ColumnDefinition)view.FindName("SideColumn")).Width=new GridLength(compact?0:255);
            Grid.SetColumnSpan(side,compact?2:1);Panel.SetZIndex(side,compact?3:0);side.Width=compact?255:double.NaN;
            side.HorizontalAlignment=compact?HorizontalAlignment.Left:HorizontalAlignment.Stretch;side.Visibility=compact&&!drawerOpen?Visibility.Collapsed:Visibility.Visible;
            Get<Border>("Shade").Visibility=compact&&drawerOpen?Visibility.Visible:Visibility.Collapsed;
            var toggle=Get<Button>("ToggleFiles");toggle.Visibility=compact?Visibility.Visible:Visibility.Collapsed;toggle.Content=drawerOpen?"Закрыть список":"Папки и файлы";
            Get<Grid>("DocumentArt").Visibility=ActualWidth<725?Visibility.Collapsed:Visibility.Visible;
        }
        void RefreshView(){
            bool has=project!=null,doc=opened!=null,word=doc&&opened.kind=="word",editable=doc&&!opened.readOnly;
            var rules=WorkPolicy.Rules;var selected=doc?SelectedRow():null;bool analysis=has&&project.Analysis&&(!rules.Managed||rules.LocalAI);
            bool training=analysis&&project.Training&&(!rules.Managed||rules.Training);
            Get<TextBlock>("HeadHint").Text=has?(project.Analysis?(analysis?"Выбранные фрагменты можно обсуждать с локальной Zarum.":"Для этой папки доступен редактор документов."):"Только редактор · документы доступны для ручной работы."):"Документы и таблицы на вашем компьютере";
            Get<TextBlock>("FolderName").Text=has?FolderLabel(folder):"Файлы";Get<TextBlock>("FolderName").ToolTip=folder;
            var empty=Get<TextBlock>("EmptyFiles");empty.Text=has?"В этой папке пока нет файлов. Через «Файлы…» можно создать документ.":"Здесь появятся файлы подключённой папки.";empty.Visibility=files.Items.Count==0?Visibility.Visible:Visibility.Collapsed;
            Get<TextBlock>("WelcomeTitle").Text=has?"Откройте первый файл":"С чего начать?";
            Get<TextBlock>("WelcomeHint").Text=has?"Дважды щёлкните документ в списке слева или перетащите его из Проводника.":rules.Managed?"Выберите разрешённую папку в списке слева. Доступ к папкам назначает администратор.":"Подключите папку, чтобы открыть документы и таблицы прямо в OSTUM.";
            Get<TextBlock>("HeadHint").ToolTip="Бессрочный Pro активен на этом компьютере";
            projects.IsEnabled=has&&!busy;sheets.IsEnabled=!busy;Get<Button>("AddFolder").Visibility=rules.Managed?Visibility.Collapsed:Visibility.Visible;
            projects.Visibility=has?Visibility.Visible:Visibility.Collapsed;
            Get<Button>("FolderAccess").Visibility=has&&!rules.Managed?Visibility.Visible:Visibility.Collapsed;
            Get<Button>("PickFolder").IsEnabled=!busy&&(!rules.Managed||has);Get<Button>("PickFile").IsEnabled=!busy&&(!rules.Managed||has);
            Get<Button>("Sample").IsEnabled=!busy&&(!rules.Managed||has);Get<Button>("Up").IsEnabled=has&&!busy&&!string.Equals(WorkPolicy.Full(folder),WorkPolicy.Full(project.Root),StringComparison.OrdinalIgnoreCase);
            foreach(string name in new[]{"FileMenu","SearchProject","Templates"})Get<Button>(name).IsEnabled=has&&!busy;
            var train=Get<Button>("Train");train.IsEnabled=training&&!busy;train.ToolTip=training?"Обучить локальный адаптер на проверенных парах вопрос–ответ":"Для обучения нужна папка с разрешением «Анализ и обучение».";
            Get<ScrollViewer>("Landing").Visibility=doc?Visibility.Collapsed:Visibility.Visible;Get<Border>("Editor").Visibility=doc?Visibility.Visible:Visibility.Collapsed;
            Get<TextBlock>("DocTitle").Text=doc?Path.GetFileName(file):"";Get<TextBlock>("DocTitle").ToolTip=file;
            Get<TextBlock>("DocHint").Text=!doc?"":opened.readOnly?"Этот файл открыт для чтения.":selected!=null?"Выбрано «"+selected.key+"». Предложите правку и проверьте её перед сохранением.":word?"Нажмите на абзац, затем на «Предложить правку». Оригинал обновится после сохранения.":"Выберите ячейку в таблице. Для новой ячейки нажмите «Предложить правку» и введите адрес.";
            Get<Button>("Edit").IsEnabled=!busy&&editable&&(!word||selected!=null);Get<Button>("Edit").ToolTip=word?"Сначала выберите абзац в тексте":"Изменить выбранную ячейку или добавить новую";
            Get<Button>("Ask").IsEnabled=!busy&&analysis&&selected!=null;Get<Button>("Ask").ToolTip=analysis?"Выберите абзац или ячейку. Zarum получит только этот фрагмент.":"Для вопросов к Zarum подключите папку в режиме локального анализа.";
            sheets.Visibility=doc&&!word?Visibility.Visible:Visibility.Collapsed;Get<TabItem>("PatchTab").Header=patches.Count==0?"Правки":"Правки ("+patches.Count+")";
            Get<Button>("Save").IsEnabled=Get<Button>("Uncheck").IsEnabled=!busy&&patches.Count>0;Get<Button>("More").IsEnabled=!busy&&doc;
        }
        static string FolderLabel(string path){if(string.IsNullOrEmpty(path))return "";return new DirectoryInfo(path).Name;}
        void More(){
            var host=Get<Button>("More");var menu=new ContextMenu {PlacementTarget=host,Placement=PlacementMode.Bottom};
            Action<string,bool,Action> item=(name,enabled,fn)=>{var mi=new MenuItem{Header=name,IsEnabled=enabled};mi.Click+=(s,e)=>Try(fn);menu.Items.Add(mi);};
            bool edit=opened!=null&&!opened.readOnly,word=opened!=null&&opened.kind=="word";
            item("Создать поручение из фрагмента…",!InRegister&&SelectedRow()!=null,()=>Run(AddTask));
            if(InRegister){item("Открыть источник поручения",SelectedRow()!=null,()=>Run(TaskSource));item("Отметить выполненным",edit&&SelectedRow()!=null,CompleteTask);}
            item("Добавить комментарий",edit&&word&&SelectedRow()!=null,()=>Edit(true));item("Изменить формат",edit&&SelectedRow()!=null,Format);
            item("Сравнить с другим DOCX",edit&&word,()=>Run(Compare));item("Версии файла",edit,()=>Run(Restore));host.ContextMenu=menu;menu.IsOpen=true;
        }
        static bool Reserved(string path){return path.Split(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar).Any(x=>x.StartsWith(".ostum-",StringComparison.OrdinalIgnoreCase));}
        bool SelectLocation(string dir,bool registerExact){
            ProAccess.Require();WorkPolicy.LocalPath(dir);if(Reserved(dir))throw new IOException("Выберите рабочую папку с документами.");
            if(!Directory.Exists(dir))throw new DirectoryNotFoundException("Папка перемещена или недоступна.");
            var known=WorkPolicy.Projects().Where(p=>WorkPolicy.Within(dir,p.Root)).OrderByDescending(p=>WorkPolicy.Full(p.Root).Length).FirstOrDefault();
            if(known==null||registerExact&&!WorkPolicy.Rules.Managed&&!string.Equals(WorkPolicy.Full(dir),WorkPolicy.Full(known.Root),StringComparison.OrdinalIgnoreCase)){
                if(WorkPolicy.Rules.Managed)throw new UnauthorizedAccessException("Эта папка отсутствует в списке организации. Выберите папку, выданную администратором.");
                int mode=WorkspaceUi.Permission(this,dir);if(mode<0)return false;
                WorkPolicy.Register(dir,mode>=1,mode==2);RefreshProjects(dir);
            }else{
                ProTools.UnderProject(known,dir);
                if(project==null||!string.Equals(WorkPolicy.Full(project.Root),WorkPolicy.Full(known.Root),StringComparison.OrdinalIgnoreCase))RefreshProjects(known.Root);
            }
            ProTools.UnderProject(project,dir);folder=dir;Clear();ListFiles();return true;
        }
        internal void OpenOnLoad(string path){ContentRendered+=(s,e)=>Run(()=>OpenPaths(new[]{path},false));}
        void PickFolder(){
            using(var dialog=new Forms.FolderBrowserDialog{Description="Выберите папку с рабочими документами",SelectedPath=folder??""}){
                if(dialog.ShowDialog()==Forms.DialogResult.OK)Run(()=>OpenPaths(new[]{dialog.SelectedPath},true));
            }
        }
        void PickOffice(){var dialog=new OpenFileDialog{Filter="Документы Word и Excel|*.docx;*.xlsx",Title="Открыть документ в рабочем пространстве",InitialDirectory=folder};if(dialog.ShowDialog(this)==true)Run(()=>OpenPaths(dialog.FileNames,false));}
        static string[] FileDrop(IDataObject data){return data.GetDataPresent(DataFormats.FileDrop)?data.GetData(DataFormats.FileDrop) as string[]:null;}
        void DragOverFile(object sender,DragEventArgs e){
            var paths=FileDrop(e.Data);if(paths==null)return;bool accept=!busy&&paths.Length>0;
            // The operation opens items in place. Never advertise Move to Explorer.
            var effect=(e.AllowedEffects&DragDropEffects.Link)!=0?DragDropEffects.Link:e.AllowedEffects&DragDropEffects.Copy;
            e.Effects=accept?effect:DragDropEffects.None;Get<Border>("DropOverlay").Visibility=e.Effects!=DragDropEffects.None?Visibility.Visible:Visibility.Collapsed;e.Handled=true;
        }
        void DropFiles(object sender,DragEventArgs e){var paths=FileDrop(e.Data);if(paths==null)return;e.Handled=true;Get<Border>("DropOverlay").Visibility=Visibility.Collapsed;e.Effects=busy?DragDropEffects.None:(e.AllowedEffects&DragDropEffects.Link)!=0?DragDropEffects.Link:e.AllowedEffects&DragDropEffects.Copy;if(e.Effects!=DragDropEffects.None)Run(()=>OpenPaths(paths,false));}
        async Task OpenPaths(string[] paths,bool registerExact){
            ProAccess.Require();if(paths==null||paths.Length==0)return;
            var candidates=new List<string>();foreach(string path in paths){WorkPolicy.LocalPath(path);if(Reserved(path))continue;if(Directory.Exists(path)||(File.Exists(path)&&OfficeFile(path)))candidates.Add(path);}
            if(candidates.Count==0)throw new IOException("Выберите папку, документ Word (.docx) или таблицу Excel (.xlsx).");
            int index=candidates.Count==1?0:Choose(this,"Вы перетащили несколько элементов. Что открыть?",candidates.ToArray());if(index<0)return;
            string picked=Path.GetFullPath(candidates[index]);bool directory=Directory.Exists(picked);
            if(!Discard()||!SelectLocation(directory?picked:Path.GetDirectoryName(picked),registerExact))return;
            if(directory){status.Text="Папка открыта. Дважды щёлкните документ в списке слева.";return;}
            ProTools.UnderProject(project,picked);files.SelectedItem=files.Items.Cast<Entry>().FirstOrDefault(v=>string.Equals(v.Path,picked,StringComparison.OrdinalIgnoreCase));
            await Open(picked,null);
        }
        async Task Example(){
            if(!Discard())return;
            if(project==null){
                if(WorkPolicy.Rules.Managed)throw new UnauthorizedAccessException("Попросите администратора назначить рабочую папку.");
                string parent=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),"OSTUM");string demo=Path.Combine(parent,"Учебный проект");
                WorkPolicy.LocalPath(demo);Directory.CreateDirectory(demo);
                if(!WorkPolicy.Projects().Any(p=>WorkPolicy.Within(demo,p.Root)))WorkPolicy.Register(demo,false,false);
                if(!SelectLocation(demo,false))return;
            }
            string sample=ProTools.UnderProject(project,Path.Combine(folder,"Заметка о встрече.docx"));
            for(int n=2;File.Exists(sample);n++)sample=ProTools.UnderProject(project,Path.Combine(folder,"Заметка о встрече ("+n+").docx"));
            await Create(sample,new[]{"Заметка о встрече","Команда встретится в четверг в 10:00.","Обсудим сроки поставки и подтвердим стоимость заказа."});ListFiles();await Open(sample,null);
            status.Text="Учебный файл. Нажмите на строку о встрече и попробуйте изменить время.";
        }
    }
}
