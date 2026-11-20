using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;

namespace Sfera {
    public sealed partial class ProjectWindow {
        const string TaskFile="Поручения OSTUM.xlsx";
        bool InRegister {get{return opened!=null&&string.Equals(Path.GetFileName(file),TaskFile,StringComparison.OrdinalIgnoreCase)&&opened.sheet=="Поручения";}}

        void CompanyTools(){
            var host=Get<Button>("Templates");
            var menu=new ContextMenu{PlacementTarget=host,Placement=PlacementMode.Top};
            Action<string,Action> add=(label,run)=>{var item=new MenuItem{Header=label};item.Click+=(s,e)=>Try(run);menu.Items.Add(item);};
            add("Открыть реестр поручений этой папки",()=>Run(OpenRegister));
            add("Сверить две таблицы Excel…",()=>Run(Reconcile));
            menu.Items.Add(new Separator());
            add("Шаблоны и закупочные предложения…",()=>Run(Demos));
            host.ContextMenu=menu;menu.IsOpen=true;
        }
        async Task OpenRegister(){
            string dir=opened==null?folder:Path.GetDirectoryName(file);
            string path=ProTools.UnderProject(project,Path.Combine(dir,TaskFile));
            if(!File.Exists(path))throw new IOException("В этой папке ещё нет поручений. Выберите абзац или ячейку в документе, затем «Ещё → Создать поручение».");
            if(!Discard())return;
            folder=dir;ListFiles();await Open(path,"Поручения");
        }
        async Task AddTask(){
            var row=SelectedRow();if(row==null||opened==null||InRegister)return;
            var w=WorkspaceUi.Dialog(this,"Поручение из документа",530,465);
            var root=new DockPanel{Margin=new Thickness(21)};w.Content=root;
            var actions=new WrapPanel{HorizontalAlignment=HorizontalAlignment.Right};DockPanel.SetDock(actions,Dock.Bottom);root.Children.Add(actions);
            var cancel=WorkspaceUi.Button(w,"Отмена",()=>w.DialogResult=false,"Flat");cancel.IsCancel=true;actions.Children.Add(cancel);
            var accept=WorkspaceUi.Button(w,"Создать поручение",()=>w.DialogResult=true,"Primary");actions.Children.Add(accept);
            var form=new StackPanel();root.Children.Add(new ScrollViewer{Content=form,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
            form.Children.Add(WorkspaceUi.Text(w,Path.GetFileName(file)+" · "+(opened.sheet??"")+" "+row.key,"Subhead"));
            form.Children.Add(WorkspaceUi.Text(w,"В реестр рядом с документом попадут выбранный фрагмент и ссылка на его место.","Hint"));
            form.Children.Add(WorkspaceUi.Text(w,"Что сделать","Subhead"));
            var title=new TextBox{Text=ResearchText.Clip(row.display??row.value,200),MaxLength=500,MinHeight=42,TextWrapping=TextWrapping.Wrap};form.Children.Add(title);
            form.Children.Add(WorkspaceUi.Text(w,"Ответственный","Subhead"));var owner=new TextBox{MaxLength=160};form.Children.Add(owner);
            form.Children.Add(WorkspaceUi.Text(w,"Срок","Subhead"));var due=new DatePicker();form.Children.Add(due);
            title.TextChanged+=(s,e)=>accept.IsEnabled=!string.IsNullOrWhiteSpace(title.Text);accept.IsEnabled=!string.IsNullOrWhiteSpace(title.Text);
            w.Loaded+=(s,e)=>{title.Focus();title.SelectAll();};
            if(w.ShowDialog()!=true)return;
            var args=new Dictionary<string,object>{{"other",file},{"revision",opened.revision},{"sheet",opened.sheet??""},{"key",row.key},{"title",title.Text},{"owner",owner.Text},{"due",due.SelectedDate.HasValue?due.SelectedDate.Value.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture):""}};
            string dest=Path.Combine(Path.GetDirectoryName(file),TaskFile);
            await ProTools.Office(project,dest,"task",args,lifetime.Token);ListFiles();status.Text="Поручение сохранено. Откройте «Инструменты → Реестр поручений» в этой папке.";
        }
        string RegisterRow(){
            var selected=SelectedRow();if(!InRegister||selected==null)throw new IOException("Выберите ячейку в строке поручения.");
            var m=Regex.Match(selected.key,@"^[A-Z]+([0-9]+)$");
            if(!m.Success||m.Groups[1].Value=="1")throw new IOException("Выберите строку с поручением под заголовками.");
            return m.Groups[1].Value;
        }
        async Task TaskSource(){
            string n=RegisterRow();
            Func<string,string> val=col=>{var c=opened.rows.FirstOrDefault(v=>v.key==col+n);return c==null?"":c.value;};
            string name=val("E"),sheet=val("F"),key=val("G"),saved=val("H");
            if(name.Length==0||Path.GetFileName(name)!=name||!OfficeFile(name)||name.IndexOf(':')>=0)throw new IOException("Проверьте имя файла в столбце «Файл».");
            string path=ProTools.UnderProject(project,Path.Combine(Path.GetDirectoryName(file),name));
            if(!File.Exists(path))throw new IOException("Исходный файл перемещён. Проверьте имя в реестре.");
            if(!Discard())return;
            filter.Clear();await Open(path,sheet);
            var source=opened.rows.FirstOrDefault(r=>r.key==key);
            if(source==null){status.Text="Документ открыт. Исходное место изменилось, найдите фрагмент по тексту в реестре.";return;}
            tabs.SelectedIndex=2;grid.SelectedItem=source;grid.ScrollIntoView(source);
            status.Text=(source.value??"").StartsWith(saved,StringComparison.Ordinal)?"Открыт источник поручения · "+key:"Содержимое источника изменилось. Сопоставьте его с текстом в реестре.";
        }
        void CompleteTask(){
            string k="D"+RegisterRow();var cell=opened.rows.FirstOrDefault(v=>v.key==k);
            AddPatch(k,cell==null?"":cell.value,"Готово","replace","Статус поручения");
            status.Text="Проверьте изменение статуса и нажмите «Сохранить выбранные».";
        }
        async Task Reconcile(){
            if(!Discard())return;
            var sources=new string[2];var sheetsToRead=new string[2];var revisions=new string[2];var keys=new string[2];
            for(int i=0;i<2;i++){
                string side=i==0?"Исходная книга":"Новая книга";
                var picker=new OpenFileDialog{InitialDirectory=folder,Filter="Книги Excel|*.xlsx",Title=side+" для сверки"};
                if(picker.ShowDialog(this)!=true)return;
                sources[i]=ProTools.UnderProject(project,picker.FileName);
                if(i==1&&string.Equals(sources[0],sources[1],StringComparison.OrdinalIgnoreCase))throw new IOException("Выберите две разные книги.");
                var book=await ProTools.Office(project,sources[i],"read",null,lifetime.Token);
                if(book.sheets.Length==0)throw new IOException("В книге нет доступных листов.");
                int n=book.sheets.Length==1?0:Choose(this,side+" · выберите лист",book.sheets);if(n<0)return;
                sheetsToRead[i]=book.sheets[n];
                if(book.sheet!=sheetsToRead[i])book=await ProTools.Office(project,sources[i],"read",new Dictionary<string,object>{{"sheet",sheetsToRead[i]}},lifetime.Token);
                revisions[i]=book.revision;
                var headers=book.rows.Where(r=>Regex.IsMatch(r.key,@"^[A-Z]+1$")&&!string.IsNullOrWhiteSpace(r.display)).ToArray();
                if(headers.Length==0)throw new IOException("В первой строке книги нужны названия столбцов.");
                int k=Choose(this,side+" · по какому столбцу сопоставить строки?",headers.Select(r=>r.display+"   ("+r.key+")").ToArray());
                if(k<0)return;keys[i]=headers[k].display;
            }
            var save=new SaveFileDialog{InitialDirectory=folder,FileName="Сверка "+DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss")+".xlsx",Title="Сохранить отдельный отчёт",Filter="Отчёт Excel|*.xlsx"};
            if(save.ShowDialog(this)!=true)return;
            if(File.Exists(save.FileName))throw new IOException("Выберите новое имя. Отчёт сохраняется отдельно.");
            await ProTools.Office(project,save.FileName,"reconcile",new Dictionary<string,object>{{"sources",sources},{"sheets",sheetsToRead},{"keys",keys},{"revisions",revisions}},lifetime.Token);
            folder=Path.GetDirectoryName(save.FileName);ListFiles();await Open(save.FileName,"Сверка");
        }
    }
}
