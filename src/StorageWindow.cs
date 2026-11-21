using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Sfera {
    internal static class StorageWindow {
        internal static void Show(Window owner,Preferences preferences){
            var w=WorkspaceUi.Dialog(owner,"Место на диске",870,515);var root=new DockPanel{Margin=new Thickness(20)};w.Content=root;
            var top=ReviewTable.Top(root);top.Children.Add(WorkspaceUi.Text(w,"Хранилище OSTUM","Title"));
            var free=WorkspaceUi.Text(w,"","Subhead");top.Children.Add(free);
            var hint=WorkspaceUi.Text(w,"Сканирую библиотеку и кэш…","Hint");top.Children.Add(hint);
            var bar=ReviewTable.Bottom(root);var grid=ReviewTable.New(true);root.Children.Add(grid);
            var check=new FrameworkElementFactory(typeof(CheckBox));check.SetBinding(CheckBox.IsCheckedProperty,new Binding("Selected"){Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged});
            check.SetBinding(CheckBox.IsEnabledProperty,new Binding("Removable"));check.SetValue(FrameworkElement.HorizontalAlignmentProperty,HorizontalAlignment.Center);
            grid.Columns.Add(new DataGridTemplateColumn{Header="Очистить",CellTemplate=new DataTemplate{VisualTree=check}});
            ReviewTable.Column(grid,"Раздел","Group",115);ReviewTable.Column(grid,"Содержимое","Name",1.6,true);ReviewTable.Column(grid,"Размер","Size",90);ReviewTable.Column(grid,"Состояние","Status",1,true);
            var source=new CancellationTokenSource();var token=source.Token;var rows=new List<StorageItem>();bool working=false,mutating=false;Button refresh=null,remove=null;
            Func<Task> load=async()=>{
                rows=await Task.Run(()=>StorageManager.Scan(preferences,token),token);if(token.IsCancellationRequested)return;grid.ItemsSource=rows;
                var volumes=new[]{Store.Home,preferences.Models}.Select(Path.GetPathRoot).Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(path=>{var drive=new DriveInfo(path);return drive.Name+"  "+StorageManager.Size(drive.AvailableFreeSpace)+" свободно";});
                free.Text=string.Join("     ·     ",volumes);hint.Text="Выберите файлы для очистки. Подключённые модели и личные данные защищены.";
            };
            Action<bool> run=async clean=>{
                if(working)return;var selected=rows.Where(x=>x.Removable&&x.Selected).ToList();
                if(clean){if(selected.Count==0){hint.Text="Отметьте файлы в столбце «Очистить».";return;}
                    string prompt="Удалить выбранные файлы ("+StorageManager.Size(selected.Sum(x=>x.Bytes))+")?\n\n"+string.Join("\n",selected.Take(8).Select(x=>x.Name))+(selected.Count>8?"\n…":"");
                    if(MessageBox.Show(w,prompt,"Очистка хранилища",MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No)!=MessageBoxResult.Yes)return;}
                working=true;mutating=clean;refresh.IsEnabled=remove.IsEnabled=grid.IsEnabled=false;
                try{var errors=new List<string>();int done=0;
                    if(clean)await Task.Run(()=>{foreach(var x in selected){
                        token.ThrowIfCancellationRequested();
                        try{StorageManager.Remove(x,Store.Load(),token);done++;}catch(IOException ex){errors.Add(x.Name+" · "+ex.Message);}catch(UnauthorizedAccessException ex){errors.Add(x.Name+" · "+ex.Message);}
                    }},token);
                    await load();if(clean&&!token.IsCancellationRequested)hint.Text="Удалено позиций · "+done+(errors.Count>0?". "+string.Join("\n",errors):"");
                }catch(OperationCanceledException){}catch(Exception ex){if(!token.IsCancellationRequested)hint.Text=ex.Message;}
                finally{working=false;mutating=false;if(!token.IsCancellationRequested){grid.IsEnabled=refresh.IsEnabled=true;remove.IsEnabled=rows.Any(x=>x.Removable);}}
            };
            refresh=WorkspaceUi.Button(w,"Обновить список",()=>run(false),"Flat");bar.Children.Add(refresh);
            remove=WorkspaceUi.Button(w,"Очистить выбранное…",()=>run(true),"Primary");bar.Children.Add(remove);
            w.Loaded+=(s,e)=>run(false);w.Closing+=(s,e)=>{if(mutating){e.Cancel=true;hint.Text="Завершаю очистку…";}else source.Cancel();};w.ShowDialog();source.Dispose();
        }
    }
}
