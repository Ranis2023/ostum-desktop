using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Sfera {
    public sealed partial class Desktop {
        bool archiveView;
        bool TryLoadChat(Conversation chat){
            try{if(!Store.AccessibleChat(chat.ProjectKey))throw new UnauthorizedAccessException("Проект недоступен.");Store.LoadTurns(chat);return true;}
            catch(Exception ex){status.Text="Не удалось открыть диалог. "+ex.Message;return false;}
        }
        Conversation NextChat(){
            return history.Where(c=>c.Archived==archiveView).OrderByDescending(c=>c.Updated).FirstOrDefault(TryLoadChat)??new Conversation();
        }
        bool CanManageHistory {get{return WorkPolicy.History&&work==null&&speechWork==null&&!voice.Recording;}}
        void SetupHistory() {
            var search=Find<Button>("HistorySearch");search.IsEnabled=WorkPolicy.History;search.Click+=(s,e)=>SearchHistory();
            Window.PreviewKeyDown+=(s,e)=>{if(e.Key==Key.F&&Keyboard.Modifiers==(ModifierKeys.Control|ModifierKeys.Shift)){e.Handled=true;SearchHistory();}};
            var switcher=Find<Button>("ArchiveSwitch");
            switcher.Click+=(s,e)=>{if(!CanManageHistory)return;archiveView=!archiveView;RefreshList();};
            var actions=Find<Button>("ChatActions");
            actions.Click+=(s,e)=>HistoryMenu(active,actions,false);
            list.PreviewMouseRightButtonUp+=(s,e)=>{
                var row=ItemsControl.ContainerFromElement(list,e.OriginalSource as DependencyObject) as ListBoxItem;
                if(row==null)return;e.Handled=true;HistoryMenu(row.DataContext as Conversation,list,true);
            };
            list.PreviewKeyDown+=(s,e)=>{
                if(e.Key==Key.Apps||(e.Key==Key.F10&&Keyboard.Modifiers==ModifierKeys.Shift)){
                    e.Handled=true;HistoryMenu(list.SelectedItem as Conversation,list,false);
                }
            };
        }
        void HistoryMenu(Conversation chat,FrameworkElement anchor,bool atPointer) {
            if(!CanManageHistory||chat==null||!history.Contains(chat))return;
            var menu=new ContextMenu{PlacementTarget=anchor,Placement=atPointer?PlacementMode.MousePoint:PlacementMode.Bottom};
            Item(menu,chat.Archived?"Вернуть из архива":"Архивировать диалог",()=>ChangeHistory(chat,false));
            Item(menu,"Назначить проект…",()=>AssignChatProject(chat));
            menu.Items.Add(new Separator());Item(menu,"Удалить диалог…",()=>ChangeHistory(chat,true));
            menu.IsOpen=true;
        }
        void ChangeHistory(Conversation chat,bool delete) {
            if(!CanManageHistory)return;
            try{
                bool clean=true;
                if(delete){
                    var confirm=MessageBox.Show(Window,"Удалить «"+chat.Title+"» и копии вложений, которые нужны только этому диалогу?","Удаление диалога",MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No);
                    if(confirm!=MessageBoxResult.Yes||(chat==active&&!ClosePreview()))return;
                    clean=Store.DeleteConversation(chat);history.Remove(chat);
                }else{if(chat==active&&!ClosePreview())return;Store.SetArchived(chat,!chat.Archived);}
                if(chat==active&&(delete||(input.Text.Length==0&&attached==null&&document==null))) {
                    active=NextChat();
                    ShowChat();
                }
                RefreshList();Labels();
                status.Text=delete?(clean?"Диалог удалён.":"Диалог удалён. Некоторые файлы вложений остались на диске."):(chat.Archived?"Диалог перемещён в архив.":"Диалог возвращён в историю.");
            }catch(Exception ex){status.Text="Не удалось изменить диалог. "+ex.Message;}
        }
    }
}
