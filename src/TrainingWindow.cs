using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;

namespace Sfera {
    public sealed class TrainingWindow:Window{
        readonly ProjectRule project;readonly Preferences prefs;
        readonly DataGrid pairs=ProjectWindow.GridView(),examples=ProjectWindow.GridView();
        readonly ObservableCollection<TrainingPair> approved=new ObservableCollection<TrainingPair>();
        readonly ComboBox versions=new ComboBox{MinWidth=165};
        readonly TextBlock status=new TextBlock{TextWrapping=TextWrapping.Wrap,Margin=new Thickness(4)},report=new TextBlock{TextWrapping=TextWrapping.Wrap,Margin=new Thickness(4)};
        readonly CheckBox reviewed=new CheckBox{Content="Я проверил выбранные пары и разрешаю локальное обучение",Margin=new Thickness(4)};
        readonly Button train=new Button{ContentTemplate=OstumActionIcons.Label,Content="Обучить Zarum",Padding=new Thickness(9,5,9,5),Margin=new Thickness(4)};
        readonly TabControl tabs=new TabControl();CancellationTokenSource work;string source;
        public TrainingWindow(Window owner,ProjectRule p,Preferences current){
            ProTools.UnderProject(p,p.Root,false,true);
            project=p;prefs=current;Owner=owner;Icon=owner.Icon;Title="Zarum · Локальное дообучение";Width=900;Height=615;MinWidth=660;MinHeight=430;WindowStartupLocation=WindowStartupLocation.CenterOwner;
            var root=new DockPanel{Margin=new Thickness(14)};Content=root;DockPanel.SetDock(status,Dock.Bottom);root.Children.Add(status);root.Children.Add(tabs);
            var data=new DockPanel();var top=new StackPanel();DockPanel.SetDock(top,Dock.Top);data.Children.Add(top);
            top.Children.Add(new TextBlock{Text="5–200 пар «вопрос — ожидаемый ответ». Оставьте одну группу для записей из одного источника. Отдельные группы будут отложены для проверки.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(4)});
            var bar=new WrapPanel();top.Children.Add(bar);Button(bar,"Импорт JSON",Load);Button(bar,"Обезличить контакты",Redact);bar.Children.Add(train);
            top.Children.Add(reviewed);train.Click+=async(s,e)=>{if(work!=null){work.Cancel();return;}await Start();};
            pairs.IsReadOnly=false;pairs.CanUserAddRows=true;pairs.CanUserDeleteRows=true;pairs.ItemsSource=approved;
            foreach(var col in new[]{new[]{"Вопрос","prompt"},new[]{"Ожидаемый ответ","answer"},new[]{"Группа / источник","group"}})
                pairs.Columns.Add(new DataGridTextColumn{Header=col[0],Binding=new Binding(col[1]){Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged},Width=new DataGridLength(col[1]=="group"?1:2,DataGridLengthUnitType.Star)});
            pairs.BeginningEdit+=(s,e)=>reviewed.IsChecked=false;approved.CollectionChanged+=(s,e)=>reviewed.IsChecked=false;
            data.Children.Add(pairs);tabs.Items.Add(new TabItem{Header="Учебные примеры",HeaderTemplate=OstumActionIcons.Tab("learn"),Content=data});
            var validation=new DockPanel();var controls=new StackPanel();DockPanel.SetDock(controls,Dock.Top);validation.Children.Add(controls);var toolbar=new WrapPanel();controls.Children.Add(toolbar);
            toolbar.Children.Add(versions);Button(toolbar,"Применить в проекте",UseVersion);Button(toolbar,"Вернуться к базе",()=>{ProjectTraining.Select(project,null);ShowReport();});Button(toolbar,"Отозвать версию",Revoke);
            controls.Children.Add(report);validation.Children.Add(examples);
            ProjectWindow.Col(examples,"Контрольный вопрос","prompt",2,true);ProjectWindow.Col(examples,"Эталон","expected",1,true);ProjectWindow.Col(examples,"Базовая модель","base",2,true);ProjectWindow.Col(examples,"С адаптером","trained",2,true);
            tabs.Items.Add(new TabItem{Header="Проверка и версии",HeaderTemplate=OstumActionIcons.Tab("history"),Content=validation});versions.SelectionChanged+=(s,e)=>Safe(ShowReport);LoadVersions();
            status.Text="Данные, отчёт и версии относятся к проекту "+p.Root;
            Closing+=(s,e)=>{if(work!=null){work.Cancel();e.Cancel=true;status.Text="Завершаю локальный процесс…";}};
        }
        void Safe(Action fn){try{fn();}catch(Exception ex){status.Text=ex.Message;}}
        void Button(Panel panel,string name,Action fn){var b=new Button{ContentTemplate=OstumActionIcons.Label,Content=name,Margin=new Thickness(4),Padding=new Thickness(9,5,9,5)};b.Click+=(s,e)=>{if(work==null)Safe(fn);};panel.Children.Add(b);}
        void LoadVersions(){var all=ProjectTraining.Versions(project);versions.ItemsSource=all;versions.SelectedItem=all.FirstOrDefault();}
        AdapterVersion Selection(){var v=versions.SelectedItem as AdapterVersion;if(v==null)throw new IOException("Выберите версию адаптера.");return v;}
        void ShowReport(){
            var v=versions.SelectedItem as AdapterVersion;if(v==null){report.Text="Проверенные версии появятся после обучения.";examples.ItemsSource=null;return;}
            var r=ProjectTraining.Report(project,v);
            report.Text=string.Format("Обучение {0} · отложено {1} · ранг {2} · F1 базы {3:P1} · с адаптером {4:P1} · ухудшений {5} · совпадений с обучающими ответами {6}\n{7}",
                r.training,r.held_out,r.rank,r.baseline,r.adapted,r.regressions,r.verbatim,ProjectTraining.Active(project)==v.Id?"Эта версия активна в проекте.":"Эта версия выключена.");
            if(r.held_out<5)report.Text+="\nМало контрольных примеров для вывода о качестве. Добавьте независимые примеры из других групп.";
            if(r.adapted<r.baseline)report.Text+="\nНа контрольных данных базовая модель точнее. Сравните ответы ниже перед применением.";
            examples.ItemsSource=r.cases;
        }
        void Load(){
            var dlg=new OpenFileDialog{Title="Утверждаемые пары · JSON",InitialDirectory=project.Root,Filter="Массив JSON · prompt, answer, group|*.json"};
            if(dlg.ShowDialog(this)!=true)return;string path=ProTools.UnderProject(project,dlg.FileName,false,true);
            if(new FileInfo(path).Length>2*1024*1024)throw new IOException("Лимит учебного набора — 2 МиБ.");
            var rows=Store.Json().Deserialize<TrainingPair[]>(File.ReadAllText(path));if(rows==null||rows.Length>200||rows.Any(r=>r==null))throw new IOException("Проверьте структуру массива и количество примеров.");
            approved.Clear();foreach(var r in rows)approved.Add(r);source=path;status.Text="Проверьте пары. Можно отредактировать или удалить отдельные строки.";
        }
        void Redact(){
            Func<string,string> mask=value=>Regex.Replace(Regex.Replace(value??"",@"[\w.%+\-]+@[\w.\-]+\.\w{2,}","[почта]"),@"(?<!\d)(?:\+7|8)[\s()\-]*(?:\d[\s()\-]*){9}\d(?!\d)","[телефон]");
            foreach(var r in approved){r.prompt=mask(r.prompt);r.answer=mask(r.answer);r.group=mask(r.group);}pairs.Items.Refresh();reviewed.IsChecked=false;status.Text="Скрыты распознанные почта и телефоны. Проверьте имена, реквизиты и другие идентификаторы вручную.";
        }
        async Task Start(){
            try{
                pairs.CommitEdit(DataGridEditingUnit.Cell,true);pairs.CommitEdit(DataGridEditingUnit.Row,true);
                if(reviewed.IsChecked!=true)throw new InvalidOperationException("Сначала подтвердите проверку учебных примеров.");
                var data=approved.ToList();work=new CancellationTokenSource();pairs.IsEnabled=reviewed.IsEnabled=false;train.Content="Остановить";status.Text="Проверяю базовые веса Zarum…";
                await ProjectTraining.Train(project,prefs,data,source,line=>Dispatcher.BeginInvoke(new Action(()=>status.Text=line)),work.Token);
                LoadVersions();tabs.SelectedIndex=1;status.Text="Проверьте реальные ответы на отложенных примерах перед применением.";
            }catch(OperationCanceledException){status.Text="Обучение остановлено. Активная версия сохранена.";}catch(Exception ex){status.Text=ex.Message;}
            finally{if(work!=null)work.Dispose();work=null;pairs.IsEnabled=reviewed.IsEnabled=true;train.Content="Обучить Zarum";}
        }
        void UseVersion(){var v=Selection();var r=ProjectTraining.Report(project,v);if(r.adapted<=r.baseline||r.regressions>0||r.verbatim>0)
            if(MessageBox.Show(this,string.Format("На {0} контрольных примерах F1 базы {1:P1}, с адаптером {2:P1}. Сравните ответы во вкладке проверки. Применить эту версию в проекте?",r.held_out,r.baseline,r.adapted),"Результаты обучения",MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)!=MessageBoxResult.Yes)return;
            ProjectTraining.Select(project,v);ShowReport();}
        void Revoke(){var v=Selection();if(MessageBox.Show(this,"Отозвать эту версию для текущего проекта?","Доступ к адаптеру",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;ProjectTraining.Revoke(project,v);LoadVersions();}
    }
}
