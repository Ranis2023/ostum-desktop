using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Forms;
using Microsoft.Win32;
using Sfera;

[assembly: AssemblyTitle("Установить Ostum Desktop")]
[assembly: AssemblyProduct("Ostum Desktop")]
[assembly: AssemblyVersion("5.0.0.0")]

namespace SferaSetup {
    sealed class Bundle : IDisposable {
        readonly Stream source=Assembly.GetExecutingAssembly().GetManifestResourceStream("OstumDesktop.msi");
        public void Copy(Stream output,CancellationToken token) {
            if(source==null||source.Length!=Payload.MsiBytes)throw new InvalidDataException("Установщик неполный.");
            source.Position=0;var buffer=new byte[1024*1024];
            using(var hash=SHA256.Create()) {
                int read;while((read=source.Read(buffer,0,buffer.Length))>0) {
                    token.ThrowIfCancellationRequested();hash.TransformBlock(buffer,0,read,buffer,0);output.Write(buffer,0,read);
                }
                hash.TransformFinalBlock(buffer,0,0);
                if(!BitConverter.ToString(hash.Hash).Replace("-","").Equals(Payload.MsiHash,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Не совпала контрольная сумма программы.");
            }
        }
        public void Dispose(){if(source!=null)source.Dispose();}
    }
    static class Install {
        static string AppFile {get{
            var fallback=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs","Ostum Desktop");
            var dir=Registry.GetValue(@"HKEY_CURRENT_USER\Software\SferaAI\Setup","InstallPath",fallback) as string;
            return Path.Combine(string.IsNullOrWhiteSpace(dir)?fallback:dir,"OstumDesktop.exe");
        }}
        static bool MatchingVersion(string file){
            string digest=Path.Combine(Path.GetDirectoryName(file),"OstumTools.zip.sha256");
            if(!File.Exists(file)||FileVersionInfo.GetVersionInfo(file).FileVersion!=Payload.AppVersion||!File.Exists(digest)||!File.ReadAllText(digest).Trim().Equals(Payload.ToolsHash,StringComparison.OrdinalIgnoreCase))return false;
            using(var input=File.OpenRead(file))using(var hash=SHA256.Create())
                if(!BitConverter.ToString(hash.ComputeHash(input)).Replace("-","").Equals(Payload.AppHash,StringComparison.OrdinalIgnoreCase))return false;
            return
                File.Exists(Path.Combine(Path.GetDirectoryName(file),"engines","llama-b12547","llama-server.exe")) &&
                File.Exists(Path.Combine(Path.GetDirectoryName(file),"OstumTools.zip")) &&
                File.Exists(Path.Combine(Path.GetDirectoryName(file),"UglyToad.PdfPig.dll")) &&
                File.Exists(Path.Combine(Path.GetDirectoryName(file),"NAudio.Core.dll")) &&
                File.Exists(Path.Combine(Path.GetDirectoryName(file),"NAudio.Wasapi.dll")) &&
                File.Exists(Path.Combine(Path.GetDirectoryName(file),"adapters","source-check.gguf")) &&
                File.Exists(Path.Combine(Path.GetDirectoryName(file),"adapters","tool-routing.gguf")) &&
                File.Exists(Path.Combine(Path.GetDirectoryName(file),"engines","whisper-b5454","whisper-cli.exe"));
        }
        static bool IdleWindow(Process p) {
            try {
                var wnd=AutomationElement.FromHandle(p.MainWindowHandle);
                if(!wnd.Current.IsEnabled)return false;
                var input=wnd.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.AutomationIdProperty,"Input"));
                var send=wnd.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.AutomationIdProperty,"Send"));
                object val;
                if(input==null||send==null||!input.TryGetCurrentPattern(ValuePattern.Pattern,out val))return false;
                if(!string.IsNullOrWhiteSpace(((ValuePattern)val).Current.Value))return false;
                if(send.Current.Name!="Отправить"&&send.Current.Name!="Нарисовать")return false;
                var attachment=wnd.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.AutomationIdProperty,"AttachName"));
                return attachment==null||attachment.Current.IsOffscreen||string.IsNullOrEmpty(attachment.Current.Name);
            }catch(Exception){return false;}
        }
        static void ReleaseWindow(string exe,Action<string,int> report,CancellationToken token) {
            foreach(var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe))){
                using(p) {
                    if(p.HasExited)continue;
                    try{if(!string.Equals(p.MainModule.FileName,exe,StringComparison.OrdinalIgnoreCase))continue;}catch(System.ComponentModel.Win32Exception){continue;}
                    while(!p.HasExited) {
                        token.ThrowIfCancellationRequested();
                        if(p.MainWindowHandle!=IntPtr.Zero&&IdleWindow(p)){
                            report("Обновляю открытое приложение",-1);
                            if(p.CloseMainWindow()&&p.WaitForExit(5000))break;
                        }
                        report("Закройте окно Ostum Desktop, когда закончите текущую работу",-1);
                        p.WaitForExit(1500);p.Refresh();
                    }
                }
            }
        }
        public static void Maximum(string dest,bool offline,CancellationToken stop,CancellationToken skip,Action<string,int> report,Action<bool> offerBase) {
            var strongest=Store.Files.Values.OrderBy(f=>f.id=="q9"?0:f.id=="v9"?1:f.id=="voice"?2:3).ToArray();
            var missing=strongest.Where(f=>!f.Present(dest)).ToArray();
            if(missing.Length==0){
                foreach(var f in strongest){report("Проверяю "+f.name,-1);
                    try{Downloads.Verify(f.PathIn(dest),f,stop);}
                    catch(InvalidDataException){if(offline)throw;Downloads.Prepare(new[]{f},dest,(s,v)=>report(s,(int)(v*100)),stop).GetAwaiter().GetResult();}
                }
                return;
            }
            if(offline)throw new IOException("В автономном комплекте отсутствуют компоненты: "+string.Join(", ",missing.Select(f=>f.name)));
            Func<long> remaining=()=>missing.Where(f=>!f.Present(dest)).Sum(f=>{
                var part=new FileInfo(f.PathIn(dest)+".part");return f.bytes-(part.Exists&&part.Length<=f.bytes?part.Length:0);
            });
            long needed=remaining();
            if(new DriveInfo(Path.GetPathRoot(dest)).AvailableFreeSpace<needed+256L*1024*1024)throw new IOException("Для установки моделей требуется "+Store.Size(needed+256L*1024*1024)+" свободного места.");
            using(var transfer=CancellationTokenSource.CreateLinkedTokenSource(stop,skip)){
                offerBase(true);
                try{
                    report("Первый запуск · текст, фото, диктовка и рисование · "+Store.Size(needed),-1);
                    WorkPolicy.RequireNetwork();
                    for(int i=0;i<5;i++){
                        long before=remaining();int lastPercent=-2;string lastLabel=null;
                        try{
                            Downloads.Prepare(strongest,dest,(s,f)=>{
                                int n=(int)(100*f);
                                if(s!=lastLabel||n!=lastPercent){report(s,n);lastLabel=s;lastPercent=n;}
                            },transfer.Token).GetAwaiter().GetResult();return;
                        }catch(Exception ex){
                            transfer.Token.ThrowIfCancellationRequested();
                            if(!(ex is IOException||ex is HttpRequestException||ex is TaskCanceledException))throw;
                            if(remaining()>=before||i==4)throw new IOException("Загрузка прервана. Запустите этот установщик ещё раз для продолжения.",ex);
                        }
                    }
                }catch(OperationCanceledException){stop.ThrowIfCancellationRequested();report("Открываю версию с установленными моделями",-1);}
                finally{offerBase(false);}
            }
        }
        public static void Run(string self,bool check,bool launch,bool offline,CancellationToken stop,CancellationToken skip,Action<string,int> report,Action<bool> offerBase) {
            offline=offline||!WorkPolicy.Network||File.Exists(Path.Combine(Path.GetDirectoryName(self),"OSTUM-OFFLINE.txt"));
            string home=Store.Home,logs=Directory.CreateDirectory(Path.Combine(home,"logs")).FullName;
            using(var bundle=new Bundle()){

                if(check){bundle.Copy(Stream.Null,stop);return;}
                string cache=Directory.CreateDirectory(Path.Combine(home,"installer")).FullName;
                string msi=Path.Combine(cache,"Ostum-"+Payload.AppVersion+".msi");
                string preparing=msi+"."+Guid.NewGuid().ToString("N")+".tmp";
                try{
                    string app=AppFile;
                    if(!MatchingVersion(app)){
                        using(var output=File.Create(preparing))bundle.Copy(output,stop);
                        if(File.Exists(msi))File.Replace(preparing,msi,null);else File.Move(preparing,msi);
                        string legacy=Path.Combine(Path.GetDirectoryName(app),"SferaAI.exe");
                        if(File.Exists(legacy))ReleaseWindow(legacy,report,stop);
                        ReleaseWindow(app,report,stop);
                        int code=1618;
                        for(int i=0;i<16&&code==1618;i++){
                            stop.ThrowIfCancellationRequested();
                            report(i==0?"Устанавливаю приложение и локальные движки":"Жду завершения другой установки Windows",-1);
                            var si=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"msiexec.exe"),
                                "/i \""+msi+"\" /qn /norestart SFERA_MANAGED_MODELS=1 /L*v \""+Path.Combine(logs,"oneclick-msi.log")+"\""){UseShellExecute=false,CreateNoWindow=true};
                            using(var p=Process.Start(si)){p.WaitForExit();code=p.ExitCode;}if(code==1618)Thread.Sleep(2000);
                        }
                        if(code!=0&&code!=3010)throw new IOException("Windows Installer вернул "+code+". Журнал находится в "+logs);
                    }
                    app=AppFile;if(!MatchingVersion(app))throw new IOException("Windows ещё не завершила замену файлов приложения.");
                    stop.ThrowIfCancellationRequested();
                    var prefs=Store.Load();
                    string dest=prefs.Models, external=Path.Combine(Path.GetDirectoryName(self),"Models");
                    InstallModels.MigrateManagedNames(dest,stop);
                    if(Directory.Exists(external)){
                        int bad=InstallModels.FromFolder(external,dest,n=>{stop.ThrowIfCancellationRequested();report("Устанавливаю и проверяю "+n,-1);});
                        if(bad!=0)throw new IOException("Часть моделей из папки Models не установилась. Подробности в "+Path.Combine(logs,"setup-models.log"));
                    }
                    Maximum(dest,offline,stop,skip,report,offerBase);
                    if(Acceleration.Supported&&!skip.IsCancellationRequested){
                        using(var acceleration=CancellationTokenSource.CreateLinkedTokenSource(stop,skip)){
                            try{
                                if(!offline)offerBase(true);
                                Acceleration.Prepare(Path.Combine(Path.GetDirectoryName(app),"engines","llama-b12547"),Path.Combine(Path.GetDirectoryName(self),"Acceleration"),offline,report,acceleration.Token);
                            }catch(OperationCanceledException){stop.ThrowIfCancellationRequested();}
                            catch(Exception ex){
                                if(!(ex is IOException||ex is InvalidDataException||ex is HttpRequestException||ex is UnauthorizedAccessException||ex is System.ComponentModel.Win32Exception))throw;
                                File.WriteAllText(Path.Combine(logs,"acceleration.log"),ex.Message);
                                report("Доступно локальное ускорение через Vulkan",-1);
                            }finally{offerBase(false);}
                        }
                    }
                    stop.ThrowIfCancellationRequested();
                    Registry.SetValue(@"HKEY_CURRENT_USER\Software\SferaAI\Setup","ModelSource",dest);
                    if(!prefs.Drawing.HasValue)prefs.Drawing=Store.Ready(Store.Art(prefs),dest);
                    if(!prefs.Dictation.HasValue)prefs.Dictation=Store.Files["voice"].Present(dest);
                    Store.Save(prefs);
                    report(Store.Files.Values.All(f=>f.Present(dest))?"Ostum Desktop готов. Текст, фото, диктовка и рисование установлены.":"Установлена часть компонентов. Загрузку можно продолжить в настройках.",100);
                    if(launch)Process.Start(new ProcessStartInfo(app){UseShellExecute=true,WorkingDirectory=Path.GetDirectoryName(app)});
                }finally{if(File.Exists(preparing))File.Delete(preparing);}
            }
        }
    }
    static class Entry {
        [STAThread] public static int Main(string[] args) {
            bool first;
            using(var mutex=new Mutex(true,@"Local\SferaAI_OneClick_2",out first)){
                if(!first)return args.Contains("--verify")||args.Contains("--quiet") ? 1618 : 0;
                bool check=args.Contains("--verify"),silent=check||args.Contains("--quiet"),offline=args.Contains("--offline");
                string self=Assembly.GetExecutingAssembly().Location;
                Directory.CreateDirectory(Path.Combine(Store.Home,"logs"));
                string path=Path.Combine(Store.Home,"logs",check?"installer-verify.log":"oneclick.log");
                using(var stop=new CancellationTokenSource())using(var skip=new CancellationTokenSource())using(var log=new StreamWriter(path,false,new UTF8Encoding(false)){AutoFlush=true}){
                    if(silent){try{Install.Run(self,check,!check&&!args.Contains("--no-launch"),offline,stop.Token,skip.Token,(s,n)=>{if(n<=0)log.WriteLine(s);},b=>{});log.WriteLine("OK");return 0;}catch(Exception e){log.WriteLine(e);return 1;}}
                    Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
                    using(var form=new Form{Text="Установка Ostum Desktop",Size=new Size(520,254),FormBorderStyle=FormBorderStyle.FixedDialog,StartPosition=FormStartPosition.CenterScreen,MaximizeBox=false,BackColor=Color.FromArgb(248,247,244),Font=new Font("Segoe UI",10),Icon=Icon.ExtractAssociatedIcon(self)}){
                        var title=new Label{Text="Ostum Desktop",Left=24,Top=18,AutoSize=true,Font=new Font("Segoe UI",19,FontStyle.Bold)};
                        var message=new Label{Text="Подготавливаю установку…",Left=26,Top=75,Width=454,Height=40};
                        var bar=new ProgressBar{Left=26,Top=129,Width=450,Height=14,Style=ProgressBarStyle.Marquee};form.Controls.AddRange(new Control[]{title,message,bar});
                        var useBase=new Button{Text="Открыть и скачать позже",Left=25,Top=161,Width=248,Height=31,Visible=false};form.Controls.Add(useBase);
                        useBase.Click+=(s,e)=>{skip.Cancel();useBase.Enabled=false;};
                        bool busy=true;int exit=1;string last=null;
                        form.FormClosing+=(s,e)=>{if(busy){e.Cancel=true;stop.Cancel();message.Text="Завершаю текущую операцию…";}};
                        form.Shown+=async(s,e)=>{
                            try{
                                await Task.Run(()=>Install.Run(self,false,true,offline,stop.Token,skip.Token,(label,value)=>{
                                    if(label!=last){log.WriteLine(label);last=label;}
                                    form.BeginInvoke(new Action(()=>{message.Text=label;bar.Style=value<0?ProgressBarStyle.Marquee:ProgressBarStyle.Continuous;if(value>=0)bar.Value=value;}));
                                },show=>form.BeginInvoke(new Action(()=>useBase.Visible=show))));log.WriteLine("OK");exit=0;
                            }catch(OperationCanceledException){log.WriteLine("Cancelled");exit=1602;}catch(Exception ex){log.WriteLine(ex);MessageBox.Show(form,ex.Message,"Установка Ostum Desktop",MessageBoxButtons.OK,MessageBoxIcon.Error);}
                            busy=false;form.Close();
                        };
                        Application.Run(form);return exit;
                    }
                }
            }
        }
    }
}
