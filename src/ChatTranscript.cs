using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace Sfera {
    public sealed partial class Desktop {
        const int TranscriptPageSize=24;
        int firstVisibleTurn;
        int nextHiddenTurn=-1;
        bool expandedHistory,scrollPending,explicitEnd,readingEarlier;
        double requestedOffset;
        int navigation,requestedNavigation;
        bool NearEnd {get{return scroll.ScrollableHeight-scroll.VerticalOffset<72;}}

        void SetupTranscript(){
            Find<Button>("EarlierTurns").Click+=(s,e)=>ShowEarlierTurns();
            Find<Button>("LaterTurns").Click+=(s,e)=>ShowLaterTurns();
            Find<Button>("JumpLatest").Click+=(s,e)=>{if(nextHiddenTurn>=0)ShowChat();else ScrollEnd();input.Focus();};
            scroll.PreviewMouseWheel+=(s,e)=>{navigation++;if(e.Delta>0)readingEarlier=true;};
            scroll.AddHandler(ScrollBar.ScrollEvent,new ScrollEventHandler((s,e)=>{
                var bar=e.OriginalSource as ScrollBar;
                if(bar!=null&&bar.Orientation==Orientation.Vertical&&bar.TemplatedParent==scroll){navigation++;readingEarlier=e.NewValue<bar.Maximum-1;}
            }));
            scroll.PreviewKeyDown+=(s,e)=>{
                if(e.Key==Key.Up||e.Key==Key.PageUp||e.Key==Key.Home){navigation++;readingEarlier=true;}
                else if(e.Key==Key.Down||e.Key==Key.PageDown||e.Key==Key.End)navigation++;
            };
            scroll.ScrollChanged+=(s,e)=>{
                if(e.OriginalSource!=scroll)return;
                if(e.VerticalChange<0&&!NearEnd&&e.ExtentHeightChange==0)readingEarlier=true;
                else if(e.VerticalChange>0&&NearEnd)readingEarlier=false;
                Find<Button>("JumpLatest").Visibility=NearEnd&&nextHiddenTurn<0?Visibility.Collapsed:Visibility.Visible;
            };
        }
        void ShowChat(){
            Labels();transcript.Children.Clear();expandedHistory=false;nextHiddenTurn=-1;
            firstVisibleTurn=Math.Max(0,active.Turns.Count-TranscriptPageSize);
            for(int i=firstVisibleTurn;i<active.Turns.Count;i++)AddTurn(active.Turns[i]);
            UpdateEarlierButton();
            Find<StackPanel>("Welcome").Visibility=active.Turns.Count==0?Visibility.Visible:Visibility.Collapsed;
            ScrollEnd();
        }
        void UpdateEarlierButton(){
            Find<Button>("LaterTurns").Visibility=nextHiddenTurn>=0?Visibility.Visible:Visibility.Collapsed;
            var button=Find<Button>("EarlierTurns");
            button.Visibility=firstVisibleTurn==0?Visibility.Collapsed:Visibility.Visible;
            button.Content="Показать предыдущие сообщения · "+Math.Min(firstVisibleTurn,TranscriptPageSize);
        }
        void ShowFoundTurn(int index){
            navigation++;readingEarlier=true;expandedHistory=true;Labels();transcript.Children.Clear();
            Find<StackPanel>("Welcome").Visibility=Visibility.Collapsed;
            firstVisibleTurn=Math.Max(0,index-3);int end=Math.Min(active.Turns.Count,firstVisibleTurn+TranscriptPageSize);
            for(int i=firstVisibleTurn;i<end;i++)transcript.Children.Add(TurnCard(active.Turns[i],null));
            nextHiddenTurn=end<active.Turns.Count?end:-1;UpdateEarlierButton();
            var card=(FrameworkElement)transcript.Children[index-firstVisibleTurn];
            var border=new Border{Child=null,BorderBrush=Color("#CB7848"),BorderThickness=new Thickness(3,0,0,0),Padding=new Thickness(9,0,0,0)};
            transcript.Children.Remove(card);border.Child=card;transcript.Children.Insert(index-firstVisibleTurn,border);
            scroll.UpdateLayout();scroll.ScrollToVerticalOffset(scroll.VerticalOffset+border.TranslatePoint(new Point(),scroll).Y);
            Find<Button>("JumpLatest").Visibility=nextHiddenTurn>=0||!NearEnd?Visibility.Visible:Visibility.Collapsed;
            status.Text="Найденное сообщение · "+(index+1)+" из "+active.Turns.Count;
        }
        void ShowLaterTurns(){
            if(nextHiddenTurn<0)return;
            int start=nextHiddenTurn,end=Math.Min(active.Turns.Count,start+TranscriptPageSize);
            for(int i=start;i<end;i++)transcript.Children.Add(TurnCard(active.Turns[i],null));
            nextHiddenTurn=end<active.Turns.Count?end:-1;UpdateEarlierButton();
        }
        void ShowEarlierTurns(){
            if(firstVisibleTurn==0||transcript.Children.Count==0)return;
            expandedHistory=true;
            var anchor=(FrameworkElement)transcript.Children[0];
            double before=anchor.TranslatePoint(new Point(),scroll).Y;
            int start=Math.Max(0,firstVisibleTurn-TranscriptPageSize);
            for(int i=firstVisibleTurn-1;i>=start;i--)transcript.Children.Insert(0,TurnCard(active.Turns[i],null));
            firstVisibleTurn=start;UpdateEarlierButton();
            scroll.UpdateLayout();
            scroll.ScrollToVerticalOffset(scroll.VerticalOffset+anchor.TranslatePoint(new Point(),scroll).Y-before);
        }
        void TrimRecentTurns(){
            if(expandedHistory)return;
            while(transcript.Children.Count>TranscriptPageSize){transcript.Children.RemoveAt(0);firstVisibleTurn++;}
            UpdateEarlierButton();
        }
        void ScrollEnd(){readingEarlier=false;QueueEnd(true);}
        void QueueEnd(bool requested){
            if(!requested&&readingEarlier)return;
            if(requested||!scrollPending){explicitEnd=requested;requestedOffset=scroll.VerticalOffset;requestedNavigation=navigation;}
            if(scrollPending)return;
            scrollPending=true;
            Window.Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>{
                scrollPending=false;
                if(closing||navigation!=requestedNavigation||(!explicitEnd&&(readingEarlier||scroll.VerticalOffset+1<Math.Min(requestedOffset,scroll.ScrollableHeight))))return;
                var presenter=scroll.Template==null?null:scroll.Template.FindName("PART_ScrollContentPresenter",scroll) as IScrollInfo;
                if(presenter!=null&&!scroll.CanContentScroll)presenter.SetVerticalOffset(presenter.ExtentHeight);
                else scroll.ScrollToEnd();
            }));
        }
    }
}
