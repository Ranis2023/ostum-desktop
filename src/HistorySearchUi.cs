using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Sfera {
    public sealed partial class Desktop {
        void AssignChatProject(Conversation chat){
            var roots=WorkPolicy.Projects();
            int choice=ProjectWindow.Choose(Window,"К какому проекту относится диалог?",new[]{"Без проекта"}.Concat(roots.Select(p=>p.Root)).ToArray());
            if(choice<0)return;
            try{Store.AssignProject(chat,choice==0?null:Store.ProjectId(roots[choice-1].Root));status.Text="Проект диалога сохранён.";}
            catch(Exception ex){status.Text=ex.Message;}
        }
        void SearchHistory(){
            if(!CanManageHistory||PaneBusy)return;
            var w=WorkspaceUi.Dialog(Window,"Найти сообщение",815,505);
            var root=new DockPanel{Margin=new Thickness(22)};w.Content=root;
            var top=ReviewTable.Top(root);
            top.Children.Add(WorkspaceUi.Text(w,"Поиск по сообщениям","Title"));
            top.Children.Add(WorkspaceUi.Text(w,"Введите слова из переписки. Откройте строку, чтобы перейти к сообщению.","Hint"));
            var query=new TextBox{MaxLength=240,Padding=new Thickness(8),ToolTip="Слова для поиска"};top.Children.Add(query);
            var options=new WrapPanel{Margin=new Thickness(0,8,0,0)};top.Children.Add(options);
            var projects=WorkPolicy.Projects();
            var scope=new ComboBox{MinWidth=165,MaxWidth=360,ItemsSource=new[]{"Все проекты","Без проекта"}.Concat(projects.Select(p=>p.Root)).ToArray(),SelectedIndex=0};options.Children.Add(scope);
            var archived=new CheckBox{Content="Включая архив",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(12,0,0,0)};options.Children.Add(archived);
            var note=WorkspaceUi.Text(w,"","Hint");top.Children.Add(note);
            var bar=ReviewTable.Bottom(root);var list=ReviewTable.New();root.Children.Add(list);
            ReviewTable.Column(list,"Диалог и автор","Caption",1,true);ReviewTable.Column(list,"Сообщение","Excerpt",2,true);
            Store.ChatHit hit=null;CancellationTokenSource pending=null;
            Action open=()=>{hit=list.SelectedItem as Store.ChatHit;if(hit!=null)w.DialogResult=true;};
            var back=WorkspaceUi.Button(w,"Закрыть",()=>w.DialogResult=false,"Flat");back.IsCancel=true;bar.Children.Add(back);
            var select=WorkspaceUi.Button(w,"К сообщению",open,"Primary");select.IsEnabled=false;bar.Children.Add(select);
            Action refresh=async()=>{
                if(pending!=null)pending.Cancel();
                list.ItemsSource=null;select.IsEnabled=false;
                string words=query.Text.Trim();if(words.Length==0){note.Text="Поиск выполняется на этом компьютере.";return;}
                var job=new CancellationTokenSource();pending=job;
                string key=scope.SelectedIndex<1?null:scope.SelectedIndex==1?"":Store.ProjectId(projects[scope.SelectedIndex-2].Root);
                bool withArchive=archived.IsChecked==true;note.Text="Ищу…";
                try{await Task.Delay(180,job.Token);var found=await Task.Run(()=>Store.SearchMessages(words,key,withArchive,job.Token),job.Token);
                    if(job.IsCancellationRequested)return;list.ItemsSource=found;list.SelectedIndex=found.Count>0?0:-1;
                    note.Text=found.Count==0?"Совпадений нет. Попробуйте другое слово.":found.Count==100?"Первые 100 совпадений. Уточните запрос для остальных.":"Совпадений · "+found.Count;
                }catch(OperationCanceledException){}catch(Exception ex){if(!job.IsCancellationRequested)note.Text=ex.Message;}
                finally{if(pending==job)pending=null;job.Dispose();}
            };
            list.SelectionChanged+=(s,e)=>select.IsEnabled=list.SelectedItem!=null;
            list.MouseDoubleClick+=(s,e)=>{if(ItemsControl.ContainerFromElement(list,e.OriginalSource as DependencyObject) is DataGridRow)open();};
            w.PreviewKeyDown+=(s,e)=>{if(e.Key==Key.Enter&&list.SelectedItem!=null){e.Handled=true;open();}};
            query.TextChanged+=(s,e)=>refresh();scope.SelectionChanged+=(s,e)=>refresh();archived.Click+=(s,e)=>refresh();
            w.Closed+=(s,e)=>{if(pending!=null)pending.Cancel();};w.Loaded+=(s,e)=>{query.Focus();refresh();};
            if(w.ShowDialog()!=true||hit==null||!CanManageHistory)return;
            var chat=history.FirstOrDefault(c=>c.Id==hit.Id);
            if(chat==null||!TryLoadChat(chat))return;
            int turn=Store.LocateHit(chat,hit);if(turn<0){status.Text="Сообщение изменилось. Повторите поиск.";return;}
            if(chat!=active){
                if((input.Text.Length>0||attached!=null||document!=null)&&MessageBox.Show(Window,"Оставить черновик и перейти к другому диалогу? Текст черновика будет очищен.","Перейти к сообщению",MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)!=MessageBoxResult.Yes)return;
                if(!ClosePreview())return;active=chat;ClearDraft();
            }
            archiveView=active.Archived;RefreshList();ShowFoundTurn(turn);
        }
    }
}
