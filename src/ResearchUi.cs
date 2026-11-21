using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace Sfera {
    public sealed partial class Desktop {
        ResearchDocument document;
        bool reading;
        int attachmentVersion;
        System.Threading.CancellationTokenSource attachmentRead;
        void ChooseFile(bool documentsOnly=false) {
            var dlg=new OpenFileDialog {Title="Прикрепить файл",Filter="Поддерживаемые файлы|*.pdf;*.docx;*.xlsx;*.txt;*.md;*.csv;*.tsv;*.json;*.log;*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.tif;*.tiff|Документы|*.pdf;*.docx;*.xlsx;*.txt;*.md;*.csv;*.tsv;*.json;*.log|Изображения|*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.tif;*.tiff"};
            if(documentsOnly){dlg.Title="Выберите документ для разбора";dlg.FilterIndex=2;}
            if(dlg.ShowDialog(Window)==true)LoadFile(dlg.FileName);
        }
        async void LoadFile(string path) {
            bool office=Path.GetExtension(path).Equals(".docx",StringComparison.OrdinalIgnoreCase)||Path.GetExtension(path).Equals(".xlsx",StringComparison.OrdinalIgnoreCase);
            if(office&&(Path.GetExtension(path).Equals(".xlsx",StringComparison.OrdinalIgnoreCase)||!WorkPolicy.GlobalFile(path))){await OpenInChat(path);return;}
            try{WorkPolicy.CheckFile(path,true);if(!WorkPolicy.GlobalFile(path)){status.Text="Откройте документ в рабочем пространстве и явно выберите фрагмент для анализа.";return;}}
            catch(Exception ex){status.Text=ex.Message;return;}
            if(!Documents.Supported(path)){LoadPhoto(path);return;}
            if(art)SetMode(false);
            SetAttachment(null,null);int ticket=attachmentVersion;reading=true;
            var read=attachmentRead=new System.Threading.CancellationTokenSource();
            Find<StackPanel>("AttachmentBar").Visibility=Find<ContentControl>("DocIcon").Visibility=Visibility.Visible;
            Find<TextBlock>("AttachName").Text=Path.GetFileName(path);UpdateSendEnabled();
            status.Text="Читаю "+Path.GetFileName(path)+"…";
            try {
                var result=await Task.Run(()=>Documents.Read(path,true,read.Token));
                if(closing||ticket!=attachmentVersion)return;
                WorkPolicy.CheckFile(path,true);if(!WorkPolicy.GlobalFile(path))return;
                document=result;ReadOnlyPreview(result,path);
                Find<StackPanel>("AttachmentBar").Visibility=Visibility.Visible;
                Find<ContentControl>("DocIcon").Visibility=Visibility.Visible;
                Find<TextBlock>("AttachName").Text=result.Name;
                status.Text=result.ScanNotice??(result.Truncated?"Вложена начальная часть большого документа.":result.ScanPictures!=null?"Скан распознан на этом ПК. Выберите строку справа, чтобы увидеть её на изображении страницы.":"");
                PromptHint();input.Focus();
            } catch(Exception ex) {if(ticket==attachmentVersion&&!closing)status.Text="Не удалось прочитать файл. "+ex.Message;}
            finally {
                if(attachmentRead==read)attachmentRead=null;read.Dispose();
                if(ticket==attachmentVersion&&!closing){
                    reading=false;if(document==null)Find<StackPanel>("AttachmentBar").Visibility=Visibility.Collapsed;
                    UpdateSendEnabled();
                }
            }
        }
        void AddResearchDetails(Panel host,Turn turn) {
            if(turn.Sources!=null&&turn.Sources.Count>0){
                var entries=new StackPanel();
                for(int i=0;i<turn.Sources.Count;i++){
                    var source=turn.Sources[i];Uri link;
                    string label="["+(i+1)+"] "+source.Title;
                    if(WebResearch.PublicUrl(source.Url,out link)){
                        var u=link;
                        var b=new Button{Content=label,ContentTemplate=OstumActionIcons.Tab("external"),ToolTip=u.AbsoluteUri,HorizontalAlignment=HorizontalAlignment.Left,
                            HorizontalContentAlignment=HorizontalAlignment.Left,Padding=new Thickness(6,3,6,3),FontSize=12,Foreground=Color("#285843"),Background=Color("#EDF2EA")};
                        b.Click+=(s,e)=>{try{WorkPolicy.RequireNetwork();Process.Start(new ProcessStartInfo(u.AbsoluteUri){UseShellExecute=true});}catch(Exception ex){status.Text=ex.Message;}};
                        entries.Children.Add(b);
                    } else entries.Children.Add(new TextBlock{Text=label,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap});
                    entries.Children.Add(new TextBlock{Text=(link==null?"":link.Host+" · ")+source.Location,FontSize=11,Foreground=Color("#57665C"),Margin=new Thickness(5,1,0,2)});
                    entries.Children.Add(new TextBlock{Text=ResearchText.Clip(source.Excerpt,350),TextWrapping=TextWrapping.Wrap,FontSize=12,Margin=new Thickness(5,0,8,8)});
                }
                host.Children.Add(new Expander{Header="Источники · "+turn.Sources.Count,Content=entries,Margin=new Thickness(0,5,0,3)});
            }
            if(turn.Steps!=null&&turn.Steps.Count>0){
                host.Children.Add(new Expander{Header="Ход работы",Margin=new Thickness(0,2,0,3),Content=new TextBlock{Text=string.Join("\n",turn.Steps),TextWrapping=TextWrapping.Wrap,FontSize=12,Margin=new Thickness(8,5,2,5)}});
            }
        }
        void SaveAnswer(Turn turn) {
            var dlg=new SaveFileDialog{Title="Сохранить ответ с источниками",Filter="Markdown|*.md|Текст|*.txt",FileName="Ответ Zarum",AddExtension=true};
            if(dlg.ShowDialog(Window)==true){WorkPolicy.LocalPath(dlg.FileName);File.WriteAllText(dlg.FileName,Agents.Export(turn),new UTF8Encoding(true));}
        }
    }
}
