using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;

namespace Sfera {
    public sealed partial class Desktop {
        ProjectWindow chatProject;
        bool restoreHistoryWidth;
        double savedPreviewWidth;
        bool PreviewOpen {get{return Find<Border>("PreviewPane").Visibility==Visibility.Visible;}}
        bool ProjectContext {get{return PreviewOpen&&chatProject!=null&&chatProject.HasDocument&&Find<CheckBox>("UseFileContext").IsChecked==true;}}
        bool FileTurn(string request){
            if(art||attached!=null||document!=null||LocalMusic.Requested(request))return false;
            if(ChatFileIntent.Read(request)!=null)return true;
            return ProjectContext&&!Orchestrator.DesktopIntent(request);
        }
        bool PaneBusy {get{return chatProject!=null&&chatProject.Working;}}
        void SetupPreview(){
            Window.PreviewKeyDown+=(s,e)=>{
                if(Keyboard.Modifiers!=ModifierKeys.Control||work!=null||PaneBusy)return;
                if(e.Key==Key.O){e.Handled=true;ChooseFile(true);}
            };
            Find<Button>("PreviewToggle").Click+=(s,e)=>{if(work!=null||voice.Recording||speechWork!=null)return;if(PreviewOpen)ClosePreview();else ShowProject();};
            Find<Button>("ClosePreview").Click+=(s,e)=>{if(work==null)ClosePreview();};
            Window.SizeChanged+=(s,e)=>{if(PreviewOpen)LimitPreview();};
            Find<Grid>("ChatCanvas").SizeChanged+=(s,e)=>{
                double width=Find<Grid>("ChatCanvas").ActualWidth;
                Find<Grid>("WelcomeArt").Visibility=width<625?Visibility.Collapsed:Visibility.Visible;
                Find<TextBlock>("ModelLabel").Visibility=width<525?Visibility.Collapsed:Visibility.Visible;
            };
        }
        void LimitPreview(){
            var cols=Find<Grid>("Layout").ColumnDefinitions;
            if(cols[0].ActualWidth>0&&Window.ActualWidth-cols[0].ActualWidth<810){ToggleSidebar();restoreHistoryWidth=true;}
            cols[1].MinWidth=310;
            cols[3].MaxWidth=Math.Max(280,Window.ActualWidth-cols[0].Width.Value-345);
            cols[3].MinWidth=Math.Min(300,cols[3].MaxWidth);
        }
        void DisplayPane(UIElement view,bool office) {
            var cols=Find<Grid>("Layout").ColumnDefinitions;bool wasOpen=PreviewOpen;
            Find<ContentControl>("PreviewHost").Content=view;
            Find<CheckBox>("UseFileContext").Visibility=office?Visibility.Visible:Visibility.Collapsed;
            Find<Border>("PreviewPane").Visibility=Find<GridSplitter>("PreviewSplitter").Visibility=Visibility.Visible;
            if(!wasOpen){
                if(cols[0].Width.Value>0){ToggleSidebar();restoreHistoryWidth=true;}
                LimitPreview();cols[3].Width=new GridLength(Math.Min(cols[3].MaxWidth,savedPreviewWidth>0?savedPreviewWidth:Math.Max(320,Window.ActualWidth*.44)));
            }
        }
        bool ClosePreview(){
            if(!PreviewOpen)return true;
            if(chatProject!=null&&!chatProject.CanLeave())return false;
            if(chatProject!=null){chatProject.EndDock();chatProject=null;}
            var cols=Find<Grid>("Layout").ColumnDefinitions;savedPreviewWidth=cols[3].ActualWidth;
            Find<ContentControl>("PreviewHost").Content=null;
            Find<Border>("PreviewPane").Visibility=Find<GridSplitter>("PreviewSplitter").Visibility=Visibility.Collapsed;
            cols[3].MinWidth=0;cols[3].Width=new GridLength(0);cols[1].MinWidth=0;
            if(restoreHistoryWidth&&cols[0].Width.Value==0)ToggleSidebar();restoreHistoryWidth=false;return true;
        }
        bool ShowProject(){
            if(chatProject!=null)return true;
            if(!ProAccessWindow.Ensure(Window))return false;
            chatProject=new ProjectWindow(Window,prefs,engine);
            var content=chatProject.DockInChat();Find<CheckBox>("UseFileContext").IsChecked=true;DisplayPane(content,true);
            return true;
        }
        async Task OpenInChat(string path){
            if(work!=null||PaneBusy||!ShowProject())return;
            try{
                string action=Directory.Exists(path)?"folder":"open";
                status.Text=await chatProject.ChatAction(new ChatFileIntent{Action=action,Value=path},"",System.Threading.CancellationToken.None);
            }catch(Exception ex){status.Text=ex.Message;}
        }
        void ReadOnlyPreview(ResearchDocument doc,string original=null){
            if(doc==null||!ClosePreview())return;
            var panel=new DockPanel{Margin=new Thickness(13)};
            var head=new StackPanel{Margin=new Thickness(0,0,0,8)};DockPanel.SetDock(head,Dock.Top);panel.Children.Add(head);
            head.Children.Add(new TextBlock{Text=doc.Name,FontSize=17,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap});
            head.Children.Add(new TextBlock{Text=doc.Truncated?"Извлечённый текст · загружена часть документа":"Извлечённый текст вложения",FontSize=12,Foreground=Color("#54645B")});
            if(original!=null&&Path.GetExtension(original).Equals(".docx",StringComparison.OrdinalIgnoreCase)){
                var edit=new Button{Content="Открыть для правок",HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,6,0,0)};
                edit.Click+=async(s,e)=>{if(work==null){await OpenInChat(original);if(chatProject!=null&&chatProject.HasDocument)SetAttachment(null,null);}};head.Children.Add(edit);
            }
            var list=new ListView{ItemsSource=doc.Parts,Background=Brushes.White,BorderBrush=Color("#DBE3D9"),HorizontalContentAlignment=HorizontalAlignment.Stretch};
            // The chat's history-item template is specific to conversations, so use a separate item container style.
            list.ItemContainerStyle=new Style(typeof(ListViewItem));
            var layout=new FrameworkElementFactory(typeof(StackPanel));layout.SetValue(FrameworkElement.MarginProperty,new Thickness(4));
            var label=new FrameworkElementFactory(typeof(TextBlock));label.SetBinding(TextBlock.TextProperty,new System.Windows.Data.Binding("Label"));label.SetValue(TextBlock.FontWeightProperty,FontWeights.SemiBold);layout.AppendChild(label);
            var value=new FrameworkElementFactory(typeof(TextBlock));value.SetBinding(TextBlock.TextProperty,new System.Windows.Data.Binding("Text"));value.SetValue(TextBlock.TextWrappingProperty,TextWrapping.Wrap);layout.AppendChild(value);
            list.ItemTemplate=new DataTemplate{VisualTree=layout};ScrollViewer.SetHorizontalScrollBarVisibility(list,ScrollBarVisibility.Disabled);
            panel.Children.Add(list);DisplayPane(panel,false);
        }
    }
}
