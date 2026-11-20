using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace Sfera {
    internal static class ModelCatalogDialog {
        internal static CatalogEntry Choose(Window owner,string directory) {
            var dialog=WorkspaceUi.Dialog(owner,"Модели для этого компьютера",700,615);
            var body=new DockPanel();dialog.Content=new Border{Padding=new Thickness(20),Background=dialog.Background,Child=body};
            var head=new StackPanel();DockPanel.SetDock(head,Dock.Top);body.Children.Add(head);
            head.Children.Add(WorkspaceUi.Text(dialog,"Какую модель выбрать?","Title"));
            head.Children.Add(WorkspaceUi.Text(dialog,"★ Zarum 9B входит в OSTUM. Остальные модели можно добавить по желанию.","Hint"));
            var list=ProjectWindow.GridView();list.RowHeight=31;list.Height=232;list.Margin=new Thickness(0,9,0,9);
            list.SelectionUnit=DataGridSelectionUnit.FullRow;
            foreach(var col in new[]{new[]{"Модель","Label","144"},new[]{"Млрд параметров","parameters","118"},new[]{"Фото","Vision","54"},new[]{"На диске","Size","93"},new[]{"MMLU-Pro","BenchmarkLabel","84"}})
                list.Columns.Add(new DataGridTextColumn{Header=col[0],Binding=new Binding(col[1]),Width=int.Parse(col[2]),IsReadOnly=true});
            list.ItemsSource=CatalogEntry.All();head.Children.Add(list);
            var buttons=new WrapPanel{Margin=new Thickness(0,7,0,0)};DockPanel.SetDock(buttons,Dock.Bottom);body.Children.Add(buttons);
            var load=WorkspaceUi.Button(dialog,"Выбрать",()=>dialog.DialogResult=true,"Primary");buttons.Children.Add(load);
            var source=WorkspaceUi.Button(dialog,"Тесты разработчика",()=>{
                var selection=(CatalogEntry)list.SelectedItem;
                try{WorkPolicy.RequireNetwork();Process.Start(new ProcessStartInfo("https://huggingface.co/Qwen/"+selection.upstream){UseShellExecute=true});}
                catch(Exception ex){MessageBox.Show(dialog,ex.Message,"Каталог моделей");}
            },"Flat");source.IsEnabled=WorkPolicy.Network;buttons.Children.Add(source);
            var close=WorkspaceUi.Button(dialog,"Закрыть",()=>dialog.DialogResult=false,"Flat");close.IsCancel=true;buttons.Children.Add(close);
            var notes=new StackPanel();body.Children.Add(new ScrollViewer{Content=notes,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
            var title=WorkspaceUi.Text(dialog,"","Subhead");var about=WorkspaceUi.Text(dialog,"","Hint");var fit=WorkspaceUi.Text(dialog,"","Hint");
            notes.Children.Add(title);notes.Children.Add(about);notes.Children.Add(fit);
            notes.Children.Add(WorkspaceUi.Text(dialog,"MMLU-Pro оценивает ответы на сложные вопросы. Выше — больше верных ответов. Здесь приведены результаты исходных моделей Qwen. Сжатые веса Q4_K_M и ваш режим могут дать другой результат. Прочерк означает отсутствие сопоставимого значения.","Hint"));
            int ram=MachineProfile.RamGiB(),gpu=0;
            Action refresh=()=>{
                var model=list.SelectedItem as CatalogEntry;if(model==null){load.IsEnabled=false;return;}
                string dest=model.DirectoryIn(directory);bool installed=model.files.All(f=>f.Present(dest));
                title.Text=model.title+(model.builtin?" · рекомендуется OSTUM":"");about.Text=model.description;
                fit.Text="Ориентир для подбора: ОЗУ "+model.ram+" ГиБ, видеопамять "+model.vram+" ГиБ для преимущественной работы на GPU. "+
                    (ram==0?"":("На этом ПК "+ram+" ГиБ ОЗУ"+(gpu>0?" и "+gpu+" ГиБ видеопамяти":"")+". "+(ram<model.ram?"Запас ОЗУ ниже ориентира. ":"")))+
                    (gpu>0&&gpu<model.vram?"Часть работы может перейти на CPU и замедлить ответ. ":"")+
                    (installed?"Файлы уже в библиотеке.":"Объём загрузки "+Store.Size(model.files.Where(f=>!f.Present(dest)).Sum(f=>f.bytes))+".")+" Лицензия "+model.license+".";
                bool allowed=true;if(!model.builtin)try{WorkPolicy.CheckImport(Path.Combine(dest,model.files[0].name));}catch(UnauthorizedAccessException){allowed=false;}
                load.IsEnabled=allowed&&(installed||WorkPolicy.Network);
                load.Content=installed?"Проверить и выбрать":WorkPolicy.Network?"Скачать и выбрать":"Требуются файлы на диске";
            };
            list.SelectionChanged+=(s,e)=>refresh();
            dialog.Loaded+=async(s,e)=>{gpu=await System.Threading.Tasks.Task.Run(()=>MachineProfile.NvidiaGiB());if(dialog.IsVisible)refresh();};
            list.SelectedIndex=0;return dialog.ShowDialog()==true?(CatalogEntry)list.SelectedItem:null;
        }
    }
}
