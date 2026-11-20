using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Sfera {
    public static class Workers {
        public static async Task<T> Track<T>(Action<WorkerEvent> notify,string id,string name,string detail,Func<Task<T>> act){
            var started=new WorkerEvent{Id=id,Name=name,Detail=detail,State="running",Started=DateTime.UtcNow};
            if(notify!=null)notify(started);string state="done";
            try{return await act().ConfigureAwait(false);}
            catch(OperationCanceledException){state="stopped";throw;}
            catch{state="error";throw;}
            finally{if(notify!=null)notify(started.Finish(state));}
        }
    }

    public sealed partial class Desktop {
        WrapPanel workerPanel;
        readonly Dictionary<string,WorkerEvent> workerEvents=new Dictionary<string,WorkerEvent>();
        readonly Dictionary<string,LightCard> workerCards=new Dictionary<string,LightCard>();

        static string WorkerColor(string id) {
            switch(id) {
                case "analyst":return "#5740A6";case "facts":return "#156960";case "review":return "#90571B";
                case "edit":return "#99436F";case "web":return "#266887";case "paint":return "#A7433E";
                case "chat":return "#415E99";case "calc":return "#885128";case "plan":return "#804197";
                case "source":return "#456F3B";default:return "#67479A";
            }
        }
        sealed class LightCard : Border {
            readonly AgentLight light;
            readonly TextBlock marker;
            readonly Stopwatch visibleFor=new Stopwatch();
            readonly bool live;
            public LightCard(WorkerEvent evt,bool isLive) {
                live=isLive;
                var tint=(SolidColorBrush)Color(WorkerColor(evt.Id));var c=tint.Color;
                var wash=System.Windows.Media.Color.FromArgb(16,c.R,c.G,c.B);
                Background=new LinearGradientBrush(wash,Colors.White,0);
                BorderBrush=new SolidColorBrush(System.Windows.Media.Color.FromArgb(51,c.R,c.G,c.B));
                CornerRadius=new CornerRadius(13);BorderThickness=new Thickness(1);
                Padding=new Thickness(5,5,10,5);Margin=new Thickness(0,3,6,3);MaxWidth=220;
                var body=new DockPanel();Child=body;
                var well=new Grid{Width=31,Height=31,VerticalAlignment=VerticalAlignment.Center};
                var glow=new RadialGradientBrush(System.Windows.Media.Color.FromArgb(65,c.R,c.G,c.B),Colors.Transparent);
                well.Background=glow;light=new AgentLight(evt.Id,isLive);well.Children.Add(light);
                DockPanel.SetDock(well,Dock.Left);body.Children.Add(well);
                var labels=new StackPanel{Margin=new Thickness(5,0,0,0),VerticalAlignment=VerticalAlignment.Center};
                body.Children.Add(labels);
                var heading=new StackPanel{Orientation=Orientation.Horizontal};labels.Children.Add(heading);
                heading.Children.Add(new TextBlock{Text=evt.Name,FontSize=12,FontWeight=FontWeights.SemiBold,Foreground=tint});
                marker=new TextBlock{Text=isLive?"в работе":"",FontSize=10,Foreground=tint,Margin=new Thickness(5,1,0,0)};heading.Children.Add(marker);
                labels.Children.Add(new TextBlock{Text=evt.Detail,TextWrapping=TextWrapping.Wrap,FontSize=11.5,Foreground=Color("#565268")});
                AutomationProperties.SetName(this,evt.Name+" · "+evt.Detail);
                if(isLive)Loaded+=Arrive;else SetResult(evt);
            }
            void Arrive(object sender,RoutedEventArgs args) {
                Loaded-=Arrive;visibleFor.Start();
                if(!SystemParameters.ClientAreaAnimation)return;
                var ease=new CubicEase{EasingMode=EasingMode.EaseOut};
                var length=TimeSpan.FromMilliseconds(210);
                var transform=new TranslateTransform();var scale=new ScaleTransform();
                var group=new TransformGroup();group.Children.Add(scale);group.Children.Add(transform);RenderTransform=group;RenderTransformOrigin=new Point(.16,.7);
                var grow=new DoubleAnimation(.93,1,length){EasingFunction=ease,FillBehavior=FillBehavior.Stop};
                scale.BeginAnimation(ScaleTransform.ScaleXProperty,grow);scale.BeginAnimation(ScaleTransform.ScaleYProperty,grow);
                transform.BeginAnimation(TranslateTransform.YProperty,new DoubleAnimation(7,0,length){EasingFunction=ease,FillBehavior=FillBehavior.Stop});
                BeginAnimation(OpacityProperty,new DoubleAnimation(.32,1,length){EasingFunction=ease,FillBehavior=FillBehavior.Stop});
            }
            public void SetResult(WorkerEvent evt) {
                light.Rest();
                string result=evt.State=="done"?"Готово":evt.State=="stopped"?"Остановлен":evt.State=="error"?"Ошибка":"Завершение не записано";
                marker.Text=evt.State=="done"?"✓":result;
                if(!live&&evt.Ended>evt.Started)marker.Text+="  "+Math.Max(1,(int)(evt.Ended-evt.Started).TotalSeconds)+" с";
                ToolTip=evt.Name+" · "+result;
                AutomationProperties.SetName(this,evt.Name+" · "+evt.Detail+" · "+result);
            }
            public async void Retire(WrapPanel owner) {
                // A local calculation can finish on the same dispatcher turn as it started.
                // Keep its completion visible while the final reply is already on screen.
                await Dispatcher.Yield(DispatcherPriority.Background);
                int hold=Math.Max(0,720-(int)visibleFor.ElapsedMilliseconds);
                if(hold>0)await Task.Delay(hold);
                if(!owner.Children.Contains(this))return;
                if(SystemParameters.ClientAreaAnimation&&IsVisible) {
                    BeginAnimation(OpacityProperty,new DoubleAnimation(1,0,TimeSpan.FromMilliseconds(145)));
                    await Task.Delay(150);
                }
                owner.Children.Remove(this);
                if(owner.Children.Count==0)owner.Visibility=Visibility.Collapsed;
            }
        }
        void BeginWorkers(Panel host) {
            workerEvents.Clear();workerCards.Clear();
            workerPanel=new WrapPanel{Margin=new Thickness(0,0,0,5),Visibility=Visibility.Collapsed};
            host.Children.Add(workerPanel);
        }
        void WorkerUpdate(WorkerEvent evt) {
            if(workerPanel==null)return;
            workerEvents[evt.Id]=evt;LightCard card;
            if(evt.State=="running") {
                if(workerCards.TryGetValue(evt.Id,out card))workerPanel.Children.Remove(card);
                card=new LightCard(evt,true);workerCards[evt.Id]=card;
                workerPanel.Visibility=Visibility.Visible;workerPanel.Children.Add(card);
            }else if(workerCards.TryGetValue(evt.Id,out card)){
                workerCards.Remove(evt.Id);card.SetResult(evt);card.Retire(workerPanel);
            }
        }
        WrapPanel FinishWorkers(Turn turn) {
            var panel=workerPanel;if(panel==null)return null;
            turn.Workers=workerEvents.Values.OrderBy(x=>x.Started).ToList();
            var parent=panel.Parent as Panel;if(parent!=null)parent.Children.Remove(panel);
            workerPanel=null;workerCards.Clear();workerEvents.Clear();
            return panel.Children.Count==0?null:panel;
        }
        void WorkerHistory(Panel host,Turn turn) {
            if(turn.Workers==null||turn.Workers.Count==0)return;
            string title=turn.Workers.Count==1?"Работа помощника":"Работа команды · "+turn.Workers.Count;
            var history=new Expander{Header=title,Margin=new Thickness(0,4,0,4)};
            history.Expanded+=(s,e)=>{
                if(history.Content!=null)return;
                var rows=new WrapPanel();foreach(var evt in turn.Workers)rows.Children.Add(new LightCard(evt,false));
                history.Content=rows;
            };
            host.Children.Add(history);
        }
    }
}
