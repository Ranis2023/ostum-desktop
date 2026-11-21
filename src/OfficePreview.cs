using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;

namespace Sfera {
    internal static class OfficePreview {
        static readonly Brush ink=new SolidColorBrush(Color.FromRgb(42,37,53)),lavender=new SolidColorBrush(Color.FromRgb(225,220,236));
        static Paragraph ParagraphFor(OfficeRow row,Action<OfficeRow,Hyperlink> select){
            var span=new Hyperlink{Foreground=ink,TextDecorations=null,ToolTip="Выбрать абзац "+row.key};
            if(row.runs!=null&&row.runs.Any(t=>!string.IsNullOrEmpty(t.value))){
                foreach(var t in row.runs){var run=new Run(t.value);if(t.bold)run.FontWeight=FontWeights.Bold;if(t.italic)run.FontStyle=FontStyles.Italic;span.Inlines.Add(run);}
            }else span.Inlines.Add(new Run(string.IsNullOrEmpty(row.value)?" ":row.value));
            span.Click+=(s,e)=>select(row,span);
            var p=new Paragraph(span){Margin=new Thickness(0,1,0,8),LineHeight=22};
            if(Regex.IsMatch(row.style??"","Heading|Заголовок|Title",RegexOptions.IgnoreCase)){p.FontSize=20;p.FontWeight=FontWeights.SemiBold;p.Margin=new Thickness(0,14,0,8);}
            if(!string.IsNullOrWhiteSpace(row.comment))p.ToolTip=row.comment;
            return p;
        }
        public static FrameworkElement Word(OfficeRow[] rows,Action<OfficeRow> select){
            var doc=new FlowDocument{FontFamily=new FontFamily("Segoe UI"),FontSize=15,PagePadding=new Thickness(38,26,38,26),Background=Brushes.White,Foreground=ink};
            Hyperlink previous=null;var highlight=new SolidColorBrush(Color.FromRgb(255,232,209));
            Action<OfficeRow,Hyperlink> pick=(row,link)=>{if(previous!=null)previous.ClearValue(TextElement.BackgroundProperty);link.Background=highlight;previous=link;select(row);};
            Table table=null;TableRow tr=null;TableCell cell=null;int oldTable=0,oldRow=0,oldColumn=0;
            foreach(var r in rows){
                if(r.table==0){doc.Blocks.Add(ParagraphFor(r,pick));oldTable=0;continue;}
                if(oldTable!=r.table){
                    table=new Table{CellSpacing=0,Margin=new Thickness(0,8,0,14)};table.RowGroups.Add(new TableRowGroup());doc.Blocks.Add(table);
                    oldTable=r.table;oldRow=oldColumn=0;
                }
                if(oldRow!=r.tableRow){tr=new TableRow();table.RowGroups[0].Rows.Add(tr);oldRow=r.tableRow;oldColumn=0;}
                if(oldColumn!=r.tableColumn){cell=new TableCell{BorderBrush=lavender,BorderThickness=new Thickness(.5),Padding=new Thickness(7,4,7,2)};tr.Cells.Add(cell);oldColumn=r.tableColumn;}
                cell.Blocks.Add(ParagraphFor(r,pick));
            }
            return new FlowDocumentScrollViewer{Document=doc,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,IsToolBarVisible=false};
        }
        sealed class SheetLine{public int Number{get;set;}public string[] Values{get;set;}}
        static string Column(int n){string s="";while(n>0){n--;s=(char)('A'+n%26)+s;n/=26;}return s;}
        static int Number(string s){int n=0;foreach(char c in s)n=n*26+c-'A'+1;return n;}
        public static FrameworkElement Sheet(OfficeRow[] rows,Action<OfficeRow> select){
            var wrap=new DockPanel();var controls=new WrapPanel();DockPanel.SetDock(controls,Dock.Top);wrap.Children.Add(controls);
            var grid=ProjectWindow.GridView();grid.SelectionUnit=DataGridSelectionUnit.Cell;grid.GridLinesVisibility=DataGridGridLinesVisibility.All;grid.HorizontalGridLinesBrush=grid.VerticalGridLinesBrush=lavender;grid.CanUserReorderColumns=false;wrap.Children.Add(grid);
            var lookup=rows.GroupBy(r=>r.key).ToDictionary(g=>g.Key,g=>g.First());int from=1,column=1,maxRow=20,maxCol=8;
            foreach(var key in lookup.Keys){var m=Regex.Match(key,@"^([A-Z]+)(\d+)$");if(!m.Success)continue;maxRow=Math.Max(maxRow,int.Parse(m.Groups[2].Value));maxCol=Math.Max(maxCol,Number(m.Groups[1].Value));}
            var label=new TextBlock{Margin=new Thickness(8,5,8,4)};Action refresh=()=>{
                grid.ItemsSource=null;grid.Columns.Clear();grid.Columns.Add(new DataGridTextColumn{Header="",Binding=new Binding("Number"),Width=60,CanUserSort=false});
                int size=Math.Min(32,maxCol-column+1);for(int i=0;i<size;i++)grid.Columns.Add(new DataGridTextColumn{Header=Column(column+i),Binding=new Binding("Values["+i+"]"),MinWidth=85,Width=115,CanUserSort=false});
                var lines=new List<SheetLine>();for(int r=from;r<=Math.Min(maxRow,from+149);r++){var v=new string[size];for(int c=0;c<size;c++){OfficeRow data;if(lookup.TryGetValue(Column(column+c)+r,out data))v[c]=data.display;}lines.Add(new SheetLine{Number=r,Values=v});}
                grid.ItemsSource=lines;label.Text=Column(column)+from+" – "+Column(column+size-1)+Math.Min(maxRow,from+149);
            };
            Action<string,string,Action> button=(kind,hint,fn)=>{
                var b=new Button{Content=hint,ContentTemplate=OstumActionIcons.Only,Tag="only:"+kind,ToolTip=hint,MinWidth=32,Padding=new Thickness(5,2,5,2),Margin=new Thickness(2)};
                System.Windows.Automation.AutomationProperties.SetName(b,hint);
                b.Click+=(a,e)=>{fn();refresh();};controls.Children.Add(b);
            };
            if(maxRow>150){button("up","Предыдущие 150 строк",()=>from=Math.Max(1,from-150));button("down","Следующие 150 строк",()=>from=Math.Min(1+(maxRow-1)/150*150,from+150));}
            if(maxCol>32){button("back","Предыдущие 32 столбца",()=>column=Math.Max(1,column-32));button("forward","Следующие 32 столбца",()=>column=Math.Min(1+(maxCol-1)/32*32,column+32));}
            controls.Children.Add(label);grid.SelectedCellsChanged+=(s,e)=>{
                if(grid.SelectedCells.Count==0)return;var cell=grid.SelectedCells[0];var line=cell.Item as SheetLine;if(line==null||cell.Column.DisplayIndex==0)return;
                string key=cell.Column.Header.ToString()+line.Number;OfficeRow data;select(lookup.TryGetValue(key,out data)?data:new OfficeRow{key=key,value="",display=""});
            };refresh();return wrap;
        }
    }
}
