using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Sfera {
    public static class Brand {
        public const string DeveloperChannel="https://t.me/ranisgaliaskarov";
        public const string SubscriptionBot="rangoostum_bot";
        public static void OpenChannel(Window owner){
            try{WorkPolicy.RequireNetwork();Process.Start(new ProcessStartInfo(DeveloperChannel){UseShellExecute=true});}
            catch(Exception){MessageBox.Show(owner,DeveloperChannel,"Канал разработчика",MessageBoxButton.OK,MessageBoxImage.Information);}
        }
        public const string Repository="https://github.com/Ranis2023/ostum-desktop";
        public static string Version {get{return typeof(Brand).Assembly.GetName().Version.ToString(3);}}
        public static async Task<System.Version> Latest(){return (await Updates.Latest(CancellationToken.None).ConfigureAwait(false)).Version;}
    }
    public sealed partial class Desktop {
        readonly DispatcherTimer releaseTimer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(5)};
        readonly DispatcherTimer hideUpdateIcon=new DispatcherTimer{Interval=TimeSpan.FromSeconds(30)};
        System.Windows.Forms.NotifyIcon updateIcon;
        System.Drawing.Icon updateIconImage;
        string notifiedThisSession;
        CancellationTokenSource checkingRelease;
        Updates.Package available;
        bool installerReady,automaticCheck,applyingUpdate;
        string updateStatus="";
        event Action UpdateChanged;
        void ClearUpdateIcon(){
            hideUpdateIcon.Stop();
            if(updateIcon!=null){updateIcon.Visible=false;updateIcon.Dispose();updateIcon=null;}
            if(updateIconImage!=null){updateIconImage.Dispose();updateIconImage=null;}
        }
        void NotifyMinimized(){
            if(closing||!prefs.CheckForUpdates||available==null||Window.WindowState!=WindowState.Minimized||!WorkPolicy.Network)return;
            string version=available.Version.ToString();
            if(notifiedThisSession==version)return;
            try{
                string dir=Directory.CreateDirectory(Path.Combine(Store.Home,"updates")).FullName;
                string marker=Path.Combine(dir,"notified.txt");
                if(File.Exists(marker)&&File.ReadAllText(marker)==version){notifiedThisSession=version;return;}
                ClearUpdateIcon();
                updateIconImage=System.Drawing.Icon.ExtractAssociatedIcon(typeof(Brand).Assembly.Location);
                updateIcon=new System.Windows.Forms.NotifyIcon{Icon=updateIconImage,Text="OSTUM Desktop",Visible=true};
                updateIcon.BalloonTipClicked+=(s,e)=>Window.Dispatcher.BeginInvoke(new Action(()=>{
                    if(closing)return;Window.WindowState=WindowState.Normal;Window.Activate();if(Window.IsEnabled)About();
                }));
                updateIcon.ShowBalloonTip(10000,"Доступна новая версия OSTUM "+version,"Откройте приложение, чтобы установить обновление.",System.Windows.Forms.ToolTipIcon.Info);
                hideUpdateIcon.Start();notifiedThisSession=version;File.WriteAllText(marker,version);
            }catch(System.ComponentModel.Win32Exception){ClearUpdateIcon();}
            catch(IOException){ClearUpdateIcon();}
            catch(UnauthorizedAccessException){ClearUpdateIcon();}
        }
        void ShowUpdateStatus(string text){
            updateStatus=text;
            var notice=Find<Button>("UpdateNotice");
            notice.Visibility=available!=null&&WorkPolicy.Network?Visibility.Visible:Visibility.Collapsed;
            if(available!=null)notice.Content=(installerReady?"Установить ":"Вышла версия ")+available.Version;
            if(UpdateChanged!=null)UpdateChanged();
        }
        void SetupUpdates(){
            Find<Button>("UpdateNotice").Click+=(s,e)=>About();
            hideUpdateIcon.Tick+=(s,e)=>ClearUpdateIcon();
            Window.StateChanged+=(s,e)=>NotifyMinimized();
            releaseTimer.Tick+=async(s,e)=>{
                releaseTimer.Interval=TimeSpan.FromHours(6);
                if(prefs.CheckForUpdates&&WorkPolicy.Network)await CheckUpdate(true);
            };
            Window.Loaded+=(s,e)=>{if(prefs.CheckForUpdates&&WorkPolicy.Network)releaseTimer.Start();};
            Window.Closed+=(s,e)=>{releaseTimer.Stop();ClearUpdateIcon();if(checkingRelease!=null)checkingRelease.Cancel();};
        }
        async Task CheckUpdate(bool automatic,bool downloadOnly=false){
            if(closing||checkingRelease!=null||!WorkPolicy.Network)return;
            automaticCheck=automatic;
            using(var operation=new CancellationTokenSource()){
                checkingRelease=operation;
                try{
                    if(!downloadOnly||available==null){
                        ShowUpdateStatus("Проверяю выпуски на GitHub…");
                        var latest=await Updates.Latest(operation.Token);
                        operation.Token.ThrowIfCancellationRequested();
                        if(latest.Version<=new Version(Brand.Version)){
                            available=null;installerReady=false;ShowUpdateStatus("Установлена актуальная версия "+Brand.Version+".");return;
                        }
                        available=latest;installerReady=false;
                    }
                    ShowUpdateStatus("Доступна версия "+available.Version+".");
                    NotifyMinimized();
                    bool download=downloadOnly||(prefs.CheckForUpdates&&prefs.DownloadUpdates);
                    if(download||available.Exists){
                        int previous=-1; string lastStep=null;
                        var selected=available;
                        var progress=new Progress<string>(message=>{if(!closing&&checkingRelease==operation)ShowUpdateStatus(message);});
                        if(download)await Task.Run(()=>Updates.Download(selected,(step,fraction)=>{
                            int percent=(int)(fraction*100);
                            if(percent==previous&&step==lastStep)return;
                            previous=percent;lastStep=step;
                            ((IProgress<string>)progress).Report(step.StartsWith("Проверка")?"Проверяю контрольную сумму…":"Скачиваю версию "+selected.Version+" · "+percent+"%");
                        },operation.Token));
                        await Task.Run(()=>Updates.Verify(selected,operation.Token));
                        operation.Token.ThrowIfCancellationRequested();installerReady=true;
                        ShowUpdateStatus("Версия "+selected.Version+" готова к установке.");
                    }
                }catch(OperationCanceledException){if(!closing)ShowUpdateStatus(operation.IsCancellationRequested?"Загрузка приостановлена.":"GitHub не ответил вовремя. Повторите проверку позже.");}
                catch(Exception ex){if(!closing)ShowUpdateStatus("Обновление сейчас недоступно. "+ex.GetBaseException().Message);}
                finally{checkingRelease=null;automaticCheck=false;if(!closing)ShowUpdateStatus(updateStatus);}
            }
        }
        async Task ApplyUpdate(Window dialog){
            if(!installerReady||applyingUpdate||checkingRelease!=null||available==null)return;
            if(work!=null||(chatProject!=null&&chatProject.Pending)||reading||speechWork!=null||voice.Recording||attached!=null||document!=null||!string.IsNullOrWhiteSpace(input.Text)){
                ShowUpdateStatus("Завершите текущую работу и отправьте или очистите черновик. Затем нажмите «Установить».");return;
            }
            applyingUpdate=true;dialog.IsEnabled=false;
            try{
                ShowUpdateStatus("Проверяю файл и запускаю обновление…");
                await Task.Run(()=>Updates.Install(available,draftLifetime.Token));
                applyingUpdate=false;dialog.Close();Window.Close();
            }catch(Exception ex){installerReady=false;ShowUpdateStatus("Не удалось установить обновление. "+ex.GetBaseException().Message);}
            finally{applyingUpdate=false;if(dialog.IsVisible)dialog.IsEnabled=true;}
        }
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
            var auto=new CheckBox{Content="Сообщать о новых версиях",IsChecked=prefs.CheckForUpdates,Margin=new Thickness(0,14,0,4),IsEnabled=WorkPolicy.Network};
            var preload=new CheckBox{Content="Автоматически скачивать установщик",IsChecked=prefs.DownloadUpdates,Margin=new Thickness(0,2,0,7),IsEnabled=prefs.CheckForUpdates&&WorkPolicy.Network};
            body.Children.Add(auto);body.Children.Add(preload);
            auto.Click+=(s,e)=>{
                prefs.CheckForUpdates=auto.IsChecked==true;Store.Save(prefs);
                preload.IsEnabled=prefs.CheckForUpdates&&WorkPolicy.Network;
                if(prefs.CheckForUpdates){releaseTimer.Interval=TimeSpan.FromSeconds(5);releaseTimer.Start();}
                else{releaseTimer.Stop();ClearUpdateIcon();if(automaticCheck&&checkingRelease!=null)checkingRelease.Cancel();}
            };
            preload.Click+=(s,e)=>{prefs.DownloadUpdates=preload.IsChecked==true;Store.Save(prefs);if(!prefs.DownloadUpdates&&automaticCheck&&checkingRelease!=null)checkingRelease.Cancel();};
            var row=new WrapPanel{Margin=new Thickness(0,8,0,0)};body.Children.Add(row);
            var statusLine=new TextBlock{TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,7,0,0)};
            var check=new Button{Content="Проверить обновления",Margin=new Thickness(0,3,7,3)};row.Children.Add(check);
            var installButton=new Button{Margin=new Thickness(0,3,7,3)};row.Children.Add(installButton);
            row.Children.Add(ButtonFor("Что нового",()=>{if(WorkPolicy.Network)Process.Start(Brand.Repository+"/releases/latest");}));
            row.Children.Add(ButtonFor("Исходный код",()=>{if(WorkPolicy.Network)Process.Start(Brand.Repository);}));
            row.IsEnabled=WorkPolicy.Network;
            body.Children.Add(statusLine);
            var win=Sheet("О программе и обновлениях",580,470,new ScrollViewer{Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
            win.Closing+=(s,e)=>{if(applyingUpdate)e.Cancel=true;};
            Action refresh=()=>{
                check.IsEnabled=checkingRelease==null;
                installButton.Visibility=available==null?Visibility.Collapsed:Visibility.Visible;
                installButton.Content=installerReady?"Установить и перезапустить":"Скачать обновление";
                installButton.IsEnabled=checkingRelease==null&&!applyingUpdate;
                statusLine.Text=!WorkPolicy.Network?"В организации обновления устанавливаются с локального носителя.":updateStatus;
            };
            check.Click+=async(s,e)=>await CheckUpdate(false);
            installButton.Click+=async(s,e)=>{if(installerReady)await ApplyUpdate(win);else await CheckUpdate(false,true);};
            UpdateChanged+=refresh;win.Closed+=(s,e)=>UpdateChanged-=refresh;refresh();
            win.ShowDialog();
        }
    }
}
