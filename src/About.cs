using System;
using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;

namespace Sfera {
    public static class Brand {
        public const string DeveloperChannel="https://t.me/ranisgaliaskarov";
        public static void OpenChannel(Window owner){
            try{WorkPolicy.RequireNetwork();Process.Start(new ProcessStartInfo(DeveloperChannel){UseShellExecute=true});}
            catch(Exception){MessageBox.Show(owner,DeveloperChannel,"Канал разработчика",MessageBoxButton.OK,MessageBoxImage.Information);}
        }
        public const string Repository="https://github.com/Ranis2023/ostum-desktop";
        public static string Version {get{return typeof(Brand).Assembly.GetName().Version.ToString(3);}}
        public sealed class Release {public string tag_name {get;set;}}
        public static async System.Threading.Tasks.Task<System.Version> Latest() {
            WorkPolicy.RequireNetwork();
            using(var client=new HttpClient{Timeout=TimeSpan.FromSeconds(12)}) {
                client.DefaultRequestHeaders.UserAgent.ParseAdd("OstumDesktop/"+Version);
                string json=await client.GetStringAsync("https://api.github.com/repos/Ranis2023/ostum-desktop/releases/latest");
                var release=Store.Json().Deserialize<Release>(json);System.Version value;
                if(release==null||!System.Version.TryParse((release.tag_name??"").TrimStart('v','V'),out value))throw new FormatException("Версия выпуска указана неверно.");
                return value;
            }
        }
    }
    public sealed partial class Desktop {
        void About() {
            var body=new StackPanel{Margin=new Thickness(24)};
            var header=new DockPanel();body.Children.Add(header);
            var mark=new Image{Source=Window.Icon,Width=54,Height=54,Margin=new Thickness(0,0,13,0)};
            DockPanel.SetDock(mark,Dock.Left);header.Children.Add(mark);
            var titles=new StackPanel{VerticalAlignment=VerticalAlignment.Center};header.Children.Add(titles);
            titles.Children.Add(new TextBlock{Text="Ostum Desktop",FontSize=23,FontWeight=FontWeights.SemiBold});
            titles.Children.Add(new TextBlock{Text="OSTUM   /   Версия "+Brand.Version,Foreground=Color("#66596E"),FontSize=12});
            body.Children.Add(new TextBlock{Text="Локальный ИИ для работы, когда сеть недоступна. Zarum 9B отвечает на вопросы, разбирает документы и фотографии. Агенты могут проверять выводы параллельно. Полная установка включает диктовку, озвучивание и генерацию изображений. В рабочих пространствах можно редактировать DOCX и XLSX и обучать локальный адаптер Zarum.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,17,0,8)});
            body.Children.Add(new TextBlock{Text="Основа Zarum — Qwen3.5-9B. Собственные адаптеры LoRA дополняют модель. Код приложения распространяется по лицензии MIT.",TextWrapping=TextWrapping.Wrap,FontSize=12,Foreground=Color("#66596E")});
            var row=new WrapPanel{Margin=new Thickness(0,12,0,0)};body.Children.Add(row);
            var statusLine=new TextBlock{TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,7,0,0)};
            var check=new Button{Content="Проверить обновления",Margin=new Thickness(0,3,7,3)};row.Children.Add(check);
            var download=ButtonFor("Открыть выпуск",()=>Process.Start(Brand.Repository+"/releases/latest"));row.Children.Add(download);
            row.Children.Add(ButtonFor("Исходный код",()=>Process.Start(Brand.Repository)));
            row.IsEnabled=WorkPolicy.Network;
            body.Children.Add(statusLine);
            check.Click+=async(s,e)=>{
                check.IsEnabled=false;statusLine.Text="Проверяю выпуски на GitHub…";
                try{var latest=await Brand.Latest();statusLine.Text=latest>new Version(Brand.Version)?"Доступна версия "+latest+". Откройте выпуск и запустите установщик.":"Установлена актуальная версия "+Brand.Version+".";}
                catch(Exception){statusLine.Text="Не удалось проверить выпуски на GitHub. Для проверки нужна сеть.";}
                finally{check.IsEnabled=true;}
            };
            var win=Sheet("О программе и обновлениях",555,360,new ScrollViewer{Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
            win.ShowDialog();
        }
    }
}
