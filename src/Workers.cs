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
        readonly List<WorkerEvent> workerEvents=new List<WorkerEvent>();
        readonly Dictionary<WorkerEvent,LightCard> workerCards=new Dictionary<WorkerEvent,LightCard>();

        static string WorkerColor(string id) {
            switch(id) {
                case "analyst":return "#5740A6";case "facts":return "#156960";case "review":return "#90571B";
                case "edit":return "#99436F";case "web":return "#266887";case "paint":return "#A7433E";
                case "chat":return "#415E99";case "calc":return "#885128";case "plan":return "#804197";
                case "source":return "#456F3B";case "docs":return "#236D79";case "diff":return "#96612F";case "music":return "#924868";default:return "#67479A";
            }
        }
        sealed class LightCard : Border {
            readonly AgentLight light;
            readonly TextBlock marker;
            readonly SolidColorBrush halo;
            readonly Stopwatch visibleFor=new Stopwatch();
            readonly bool live;
            static string ShortAction(WorkerEvent evt){
                switch(evt.Id){
                    case "route":return "Выбирает инструменты";case "plan":return "Составляет план";
                    case "docs":return evt.Detail.StartsWith("Ищет")?"Ищет в файлах":evt.Detail.StartsWith("Читает")?"Читает файл":"Работает с файлом";
                    case "web":return "Ищет источники";case "facts":return evt.Detail.StartsWith("Извлекает")?"Извлекает поле":"Сверяет факты";
                    case "analyst":return "Разбирает задачу";case "review":return "Проверяет вывод";case "source":return "Сверяет тезис";
                    case "edit":return "Собирает ответ";case "music":return "Создаёт музыку";case "diff":return "Сравнивает текст";
                    default:return evt.Detail;
                }
            }
            public LightCard(WorkerEvent evt,bool isLive) {
                live=isLive;
                var tint=(SolidColorBrush)Color(WorkerColor(evt.Id));var c=tint.Color;
                var wash=System.Windows.Media.Color.FromArgb(17,c.R,c.G,c.B);
                Background=new LinearGradientBrush(wash,System.Windows.Media.Color.FromArgb(10,c.R,c.G,c.B),0);
                BorderBrush=new SolidColorBrush(System.Windows.Media.Color.FromArgb(55,c.R,c.G,c.B));
                CornerRadius=new CornerRadius(15);BorderThickness=new Thickness(1);Height=30;
                Padding=new Thickness(2,0,8,0);Margin=new Thickness(0,2,5,2);MaxWidth=220;
                var body=new DockPanel{VerticalAlignment=VerticalAlignment.Center};Child=body;
                var well=new Grid{Width=25,Height=25};
                halo=new SolidColorBrush(System.Windows.Media.Color.FromArgb(28,c.R,c.G,c.B));
                well.Children.Add(new Border{Width=19,Height=19,CornerRadius=new CornerRadius(10),Background=halo});
                light=new AgentLight(evt.Id,isLive);well.Children.Add(light);
                DockPanel.SetDock(well,Dock.Left);body.Children.Add(well);
                var name=new TextBlock{Text=evt.Name,FontSize=11.5,FontWeight=FontWeights.SemiBold,Foreground=tint,VerticalAlignment=VerticalAlignment.Center};
                DockPanel.SetDock(name,Dock.Left);body.Children.Add(name);
                marker=new TextBlock{Text=ShortAction(evt),FontSize=11,Foreground=Color("#505B57"),Margin=new Thickness(6,0,0,0),
                    TextTrimming=TextTrimming.CharacterEllipsis,VerticalAlignment=VerticalAlignment.Center};
                body.Children.Add(marker);ToolTip=evt.Name+" · "+evt.Detail;
                AutomationProperties.SetName(this,evt.Name+" · "+evt.Detail);
                if(isLive)Loaded+=Arrive;else SetResult(evt);
            }
            void Arrive(object sender,RoutedEventArgs args) {
                Loaded-=Arrive;visibleFor.Start();
                if(!SystemParameters.ClientAreaAnimation)return;
                var ease=new CubicEase{EasingMode=EasingMode.EaseOut};
                var length=TimeSpan.FromMilliseconds(250);
                var transform=new TranslateTransform();var scale=new ScaleTransform();
                var group=new TransformGroup();group.Children.Add(scale);group.Children.Add(transform);RenderTransform=group;RenderTransformOrigin=new Point(.16,.7);
                var grow=new DoubleAnimation(.88,1,length){EasingFunction=ease,FillBehavior=FillBehavior.Stop};
                scale.BeginAnimation(ScaleTransform.ScaleXProperty,grow);scale.BeginAnimation(ScaleTransform.ScaleYProperty,grow);
                transform.BeginAnimation(TranslateTransform.YProperty,new DoubleAnimation(5,0,length){EasingFunction=ease,FillBehavior=FillBehavior.Stop});
                BeginAnimation(OpacityProperty,new DoubleAnimation(0,1,length){EasingFunction=ease,FillBehavior=FillBehavior.Stop});
                halo.BeginAnimation(Brush.OpacityProperty,new DoubleAnimation(.22,1,length){AutoReverse=true,FillBehavior=FillBehavior.Stop});
            }
            public void SetResult(WorkerEvent evt) {
                light.Rest();
                string result=evt.State=="done"?"Готово":evt.State=="stopped"?"Остановлен":evt.State=="error"?"Ошибка":"Завершение не записано";
                marker.Text=evt.State=="done"?"✓ Готово":result;
                if(!live&&evt.Ended>evt.Started){
                    double seconds=(evt.Ended-evt.Started).TotalSeconds;
                    marker.Text+=seconds<.1?" · <0,1 с":" · "+seconds.ToString("0.#")+" с";
                }
                ToolTip=evt.Name+" · "+evt.Detail+" · "+result;
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
                    var length=TimeSpan.FromMilliseconds(310);light.Extinguish(length);
                    halo.BeginAnimation(Brush.OpacityProperty,new DoubleAnimation(1,0,length));
                    BeginAnimation(OpacityProperty,new DoubleAnimation(1,0,TimeSpan.FromMilliseconds(200)){BeginTime=TimeSpan.FromMilliseconds(115)});
                    await Task.Delay(325);
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
            LightCard card;
            if(evt.State=="running") {
                workerEvents.Add(evt);card=new LightCard(evt,true);workerCards[evt]=card;
                workerPanel.Visibility=Visibility.Visible;workerPanel.Children.Add(card);
            }else{
                int index=workerEvents.FindLastIndex(w=>w.State=="running"&&w.Id==evt.Id&&w.Started==evt.Started);
                if(index<0)return;
                var started=workerEvents[index];workerEvents[index]=evt;
                if(workerCards.TryGetValue(started,out card)){workerCards.Remove(started);card.SetResult(evt);card.Retire(workerPanel);}
            }
        }
        WrapPanel FinishWorkers(Turn turn) {
            var panel=workerPanel;if(panel==null)return null;
            turn.Workers=workerEvents.OrderBy(x=>x.Started).ToList();
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
                var rows=new StackPanel();foreach(var evt in turn.Workers){
                    var row=new DockPanel();var chip=new LightCard(evt,false);
                    DockPanel.SetDock(chip,Dock.Left);row.Children.Add(chip);
                    row.Children.Add(new TextBlock{Text=evt.Detail,FontSize=12,Foreground=Color("#505B57"),TextWrapping=TextWrapping.Wrap,VerticalAlignment=VerticalAlignment.Center});
                    rows.Children.Add(row);
                }
                history.Content=rows;
            };
            host.Children.Add(history);
        }
    }
}
