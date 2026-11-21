using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;

namespace Sfera {
    public sealed class InvoiceDraft {
        public bool Selected{get;set;}public string Number{get;set;}public string Date{get;set;}public string Amount{get;set;}
        public string Currency{get;set;}public string Source{get;set;}public string Location{get;set;}public string Hash{get;set;}public string Note{get;set;}
        internal static string Digest(string path){using(var input=File.OpenRead(path))using(var hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(input)).Replace("-","").ToLowerInvariant();}
        internal static InvoiceDraft Extract(ResearchDocument doc,string hash){
            var draft=new InvoiceDraft{Source=doc.Name,Hash=hash,Date="",Number="",Amount="",Currency="",Location="",Note=""};
            var numbers=new HashSet<string>();var amounts=new HashSet<string>();var dates=new HashSet<string>();var currencies=new HashSet<string>();
            foreach(var part in doc.Parts){
                foreach(Match m in Regex.Matches(part.Text,@"\b(?:накладная|сч[её]т(?:-фактура)?)\s*(?:на оплату\s*)?(?:№|номер|N)?\s*([А-ЯA-Z0-9][\w/-]*\d[\w/-]*|\d+)\b",RegexOptions.IgnoreCase))numbers.Add(m.Groups[1].Value);
                foreach(Match m in Regex.Matches(part.Text,@"(?:\bот\s+|\bдата\s*[: ]\s*)(\d{2}[./]\d{2}[./]\d{4}|\d{4}-\d{2}-\d{2})",RegexOptions.IgnoreCase)){
                    DateTime d;if(DateTime.TryParseExact(m.Groups[1].Value,new[]{"dd.MM.yyyy","dd/MM/yyyy","yyyy-MM-dd"},CultureInfo.InvariantCulture,DateTimeStyles.None,out d))dates.Add(d.ToString("yyyy-MM-dd"));
                }
                foreach(Match m in Regex.Matches(part.Text,@"(?:\b(?:всего к оплате|итого(?: к оплате)?|к оплате|сумма(?: сч[её]та)?)\s*[:—-]?\s*)([0-9]+(?:[ \u00a0]\d{3})*(?:[.,]\d{1,2})?)\s*(₽|руб\w*|RUB|EUR|евро|€|USD|доллар\w*|\$)?",RegexOptions.IgnoreCase)){
                    amounts.Add(m.Groups[1].Value.Replace(" ","").Replace("\u00a0","").Replace(',','.'));
                    var cur=m.Groups[2].Value.ToUpperInvariant();
                    if(cur.Length>0)currencies.Add(cur=="€"||cur=="EUR"||cur=="ЕВРО"?"EUR":cur=="USD"||cur=="$"||cur.StartsWith("ДОЛЛАР")?"USD":"RUB");
                    if(draft.Location.Length==0)draft.Location=part.Label;
                }
            }
            if(numbers.Count==1)draft.Number=numbers.First();if(dates.Count==1)draft.Date=dates.First();
            if(amounts.Count==1)draft.Amount=amounts.First();if(currencies.Count==1)draft.Currency=currencies.First();
            draft.Selected=draft.Number.Length>0&&draft.Amount.Length>0&&draft.Currency.Length>0&&dates.Count<=1&&!doc.Truncated&&doc.ScanNotice==null;
            draft.Note=draft.Selected?(dates.Count==0?"Проверьте дату":"Проверьте по источнику"):"Проверьте неоднозначные или пропущенные поля";
            return draft;
        }
    }
    public sealed class DueItem {public string title{get;set;}public string owner{get;set;}public string due{get;set;}public string source{get;set;}public string location{get;set;}public int row{get;set;}}
    public sealed partial class ProjectWindow {
        const string InvoiceFile="Реестр счетов OSTUM.xlsx";
        string AnalyzePath(string path){
            path=ProTools.UnderProject(project,path,true);
            if(!WorkPolicy.CanAnalyzeDocument(path))throw new UnauthorizedAccessException("Для чтения всего файла подтвердите локальный анализ в настройках доступа к этой папке.");
            return path;
        }
        async Task<string> InvoiceImport(){
            if(!Discard())return "Импорт отменён.";
            var paths=Entries(folder).Where(e=>!e.Folder&&Regex.IsMatch(e.Name,@"\.(pdf|docx|txt)$",RegexOptions.IgnoreCase)).OrderBy(e=>e.Name).Select(e=>e.Path).Take(101).ToArray();
            if(paths.Length>100)return "В папке больше 100 документов. Выберите папку с меньшей партией.";
            if(paths.Length==0)return "В выбранной папке нет PDF, DOCX или TXT для реестра.";
            string dest=ProTools.UnderProject(project,Path.Combine(folder,InvoiceFile));var known=new HashSet<string>();
            var previousNumbers=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if(File.Exists(dest)){
                var prior=await ProTools.Office(project,AnalyzePath(dest),"read",new Dictionary<string,object>{{"sheet","Счета"}},OperationToken);
                foreach(var cell in prior.rows.Where(c=>Regex.IsMatch(c.key,@"^G\d+$")))known.Add(cell.value);
                var values=prior.rows.ToDictionary(c=>c.key,c=>c.value);
                foreach(var cell in prior.rows.Where(c=>c.key!="A1"&&Regex.IsMatch(c.key,@"^A\d+$"))){
                    string date;values.TryGetValue("B"+cell.key.Substring(1),out date);previousNumbers.Add(cell.value+"|"+date);
                }
            }
            var docs=new List<InvoiceDraft>();var ct=OperationToken;
            foreach(string path in paths){
                ct.ThrowIfCancellationRequested();AnalyzePath(path);status.Text="Читаю "+Path.GetFileName(path);
                var draft=await Task.Run(()=>{
                    string hash=InvoiceDraft.Digest(path);
                    if(known.Contains(hash))return new InvoiceDraft{Source=Path.GetFileName(path),Hash=hash,Note="Уже в реестре или в этой партии"};
                    try{
                        var parsed=InvoiceDraft.Extract(Documents.Read(path,false,ct),hash);
                        if(hash!=InvoiceDraft.Digest(path))throw new IOException("Файл изменился во время чтения.");
                        return parsed;
                    }catch(IOException ex){return new InvoiceDraft{Source=Path.GetFileName(path),Hash=hash,Note=ex.Message};}
                },ct);
                if(!string.IsNullOrEmpty(draft.Number)&&previousNumbers.Contains(draft.Number+"|"+draft.Date)){
                    draft.Selected=false;draft.Note="В реестре уже есть этот номер и дата. Сравните редакции";
                }
                AnalyzePath(path);docs.Add(draft);known.Add(draft.Hash);
            }
            foreach(var duplicate in docs.Where(d=>!string.IsNullOrEmpty(d.Number)).GroupBy(d=>d.Number+"|"+d.Date).Where(g=>g.Count()>1))
                foreach(var d in duplicate){d.Selected=false;d.Note="Совпадают номер и дата. Сверьте источники";}
            var win=WorkspaceUi.Dialog(DialogHost,"Проверка счетов перед записью",960,560);
            var layout=new DockPanel{Margin=new Thickness(16)};win.Content=layout;
            var buttons=new WrapPanel();DockPanel.SetDock(buttons,Dock.Bottom);layout.Children.Add(buttons);
            var table=new DataGrid{ItemsSource=docs,AutoGenerateColumns=false,CanUserAddRows=false,CanUserDeleteRows=false};
            table.Columns.Add(new DataGridCheckBoxColumn{Header="Включить",Binding=new Binding("Selected"){Mode=BindingMode.TwoWay}});
            foreach(var c in new[]{new[]{"Номер","Number"},new[]{"Дата ГГГГ-ММ-ДД","Date"},new[]{"Сумма","Amount"},new[]{"Валюта","Currency"},new[]{"Файл","Source"},new[]{"Место","Location"},new[]{"Проверка","Note"}})
                table.Columns.Add(new DataGridTextColumn{Header=c[0],Binding=new Binding(c[1]),IsReadOnly=c[1]=="Source"||c[1]=="Location"||c[1]=="Note"});
            layout.Children.Add(table);
            buttons.Children.Add(WorkspaceUi.Button(win,"Посмотреть источник",async()=>{var row=table.SelectedItem as InvoiceDraft;if(row!=null)try{await ShowInvoiceSource(win,row.Source,row.Location,row.Hash);}catch(Exception ex){MessageBox.Show(win,ex.Message);}}));
            buttons.Children.Add(WorkspaceUi.Button(win,"Сохранить выбранные",()=>{table.CommitEdit(DataGridEditingUnit.Cell,true);table.CommitEdit(DataGridEditingUnit.Row,true);win.DialogResult=true;},"Primary"));
            buttons.Children.Add(WorkspaceUi.Button(win,"Отмена",()=>win.DialogResult=false,"Flat"));
            if(win.ShowDialog()!=true)return "Импорт отменён. Реестр сохранён в прежнем виде.";
            var picked=docs.Where(d=>d.Selected).ToArray();if(picked.Length==0)return "Строки для записи не выбраны.";
            string[] sources=picked.Select(d=>AnalyzePath(Path.Combine(folder,d.Source))).ToArray();
            var result=await ProTools.Office(project,dest,"invoices",new Dictionary<string,object>{{"sources",sources},{"records",picked}},ct);
            ListFiles();await Open(dest,"Счета");return "В реестр добавлено строк: "+result.added+". Пропущено повторов: "+result.skipped+".";
        }
        async Task ShowInvoiceSource(Window owner,string name,string location,string expectedHash){
            if(string.IsNullOrEmpty(name)||Path.GetFileName(name)!=name||name.Contains(":"))throw new IOException("Проверьте имя источника.");
            string path=AnalyzePath(Path.Combine(folder,name));var ct=OperationToken;
            var doc=await Task.Run(()=>{
                if(!string.Equals(InvoiceDraft.Digest(path),expectedHash,StringComparison.OrdinalIgnoreCase))throw new IOException("Источник изменился с момента составления реестра. Проверьте новую редакцию.");
                var read=Documents.Read(path,true,ct);
                if(!string.Equals(InvoiceDraft.Digest(path),expectedHash,StringComparison.OrdinalIgnoreCase))throw new IOException("Источник изменился во время чтения.");
                return read;
            },ct);AnalyzePath(path);
            var parts=doc.Parts.Where(p=>p.Label==location).ToList();if(parts.Count==0)parts=doc.Parts;
            var win=WorkspaceUi.Dialog(owner,name+" · "+location,720,640);var stack=new StackPanel{Margin=new Thickness(18)};
            var page=parts.FirstOrDefault();string image;
            if(page!=null&&doc.ScanPictures!=null&&doc.ScanPictures.TryGetValue(page.ScanNumber.ToString(),out image)){
                var bmp=new System.Windows.Media.Imaging.BitmapImage();using(var input=new MemoryStream(Convert.FromBase64String(image))){bmp.BeginInit();bmp.CacheOption=System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;bmp.StreamSource=input;bmp.EndInit();}bmp.Freeze();
                stack.Children.Add(new Image{Source=bmp,MaxHeight=350});
            }
            foreach(var part in parts)stack.Children.Add(new TextBlock{Text=part.Label+"   "+part.Text,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,4,0,4)});
            win.Content=new ScrollViewer{Content=stack};win.ShowDialog();
        }
        async Task InvoiceSource(){
            var row=SelectedRow();if(row==null||opened==null||Path.GetFileName(file)!=InvoiceFile)throw new IOException("Откройте реестр и выберите ячейку в строке счёта.");
            string index=Regex.Match(row.key,@"\d+$").Value;if(index=="1")throw new IOException("Выберите строку под заголовком.");
            Func<string,string> get=c=>{var v=opened.rows.FirstOrDefault(r=>r.key==c+index);return v==null?"":v.value;};
            await ShowInvoiceSource(DialogHost,get("E"),get("F"),get("G"));
        }
        async Task<string> Deadlines(string mode){
            string register=ProTools.UnderProject(project,Path.Combine(folder,TaskFile));
            if(!File.Exists(register))return "В этой папке ещё нет реестра поручений. Создайте поручение из ячейки или абзаца.";
            AnalyzePath(register);var ct=OperationToken;
            var result=await ProTools.Office(project,register,"deadlines",new Dictionary<string,object>{{"period",mode},{"today",DateTime.Today.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)},{"calendar",mode=="calendar"}},ct);
            AnalyzePath(register);
            if(result.tasks.Length==0)return "Открытых поручений с таким сроком в реестре нет.";
            if(mode=="calendar"){
                var picker=new SaveFileDialog{Title="Экспорт сроков в локальный календарь",InitialDirectory=folder,Filter="Календарь iCalendar|*.ics",FileName="Сроки OSTUM.ics"};
                if(picker.ShowDialog(DialogHost)!=true)return "Экспорт отменён.";
                string path=ProTools.UnderProject(project,picker.FileName);
                if(!string.Equals(Path.GetDirectoryName(path),folder,StringComparison.OrdinalIgnoreCase)||Reserved(path)||!path.EndsWith(".ics",StringComparison.OrdinalIgnoreCase))throw new IOException("Выберите файл .ics в папке реестра.");
                string tmp=Path.Combine(folder,".ostum-"+Guid.NewGuid().ToString("N")+".tmp");
                try{File.WriteAllText(tmp,result.calendar,new UTF8Encoding(false));if(File.Exists(path))File.Replace(tmp,path,null);else File.Move(tmp,path);}
                finally{if(File.Exists(tmp))File.Delete(tmp);}
                return "Календарь сохранён: "+Path.GetFileName(path)+". Поручений: "+result.tasks.Length+".";
            }
            return string.Join("\n",result.tasks.Select(t=>t.due+" · "+t.title+(string.IsNullOrEmpty(t.owner)?"":" · "+t.owner)+"\n   "+t.source+" · "+t.location+" · строка реестра "+t.row));
        }
    }
}
