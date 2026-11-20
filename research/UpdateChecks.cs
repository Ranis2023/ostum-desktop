using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Sfera;

class UpdateChecks {
    static void Need(bool ok,string message){if(!ok)throw new InvalidDataException(message);}
    static Updates.Release Example(){
        return new Updates.Release{tag_name="v8.1.2",assets=new[]{new Updates.Asset{name="Ostum-Desktop-Setup.exe",size=4000,digest="sha256:"+new string('a',64),browser_download_url=Brand.Repository+"/releases/download/v8.1.2/Ostum-Desktop-Setup.exe"}}};
    }
    static void Reject(Action<Updates.Release> edit){
        var r=Example();edit(r);try{Updates.Parse(r);}catch(InvalidDataException){return;}throw new Exception("Unexpected package accepted");
    }
    static void Metadata(){
        Need(Updates.Parse(Example()).Version==new Version(8,1,2),"stable package");
        Reject(r=>r.prerelease=true);Reject(r=>r.draft=true);Reject(r=>r.tag_name="v8.1.2-rc1");
        Reject(r=>r.tag_name="v08.1.2");Reject(r=>r.assets[0].digest="unknown");Reject(r=>r.assets[0].size=0);
        Reject(r=>r.assets[0].browser_download_url=r.assets[0].browser_download_url.Replace("github.com","example.org"));
        Reject(r=>r.assets[0].browser_download_url=r.assets[0].browser_download_url.Replace("v8.1.2","latest"));
        Reject(r=>r.assets=new[]{r.assets[0],r.assets[0]});Console.WriteLine("PASS stable version, origin, tag, digest, size and unique asset");
    }
    static T Field<T>(Desktop d,string key){return (T)typeof(Desktop).GetField(key,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(d);}
    static async Task Wait(Func<bool> ok,TimeSpan limit,string operation){
        var t=Stopwatch.StartNew();while(!ok()&&t.Elapsed<limit)await Task.Delay(120);Need(ok(),operation);
    }
    static void Photo(Window w,string path){
        w.UpdateLayout();var png=new PngBitmapEncoder();
        var b=new RenderTargetBitmap((int)w.ActualWidth,(int)w.ActualHeight,96,96,PixelFormats.Pbgra32);b.Render(w);png.Frames.Add(BitmapFrame.Create(b));
        using(var f=File.Create(path))png.Save(f);
    }
    static async Task Live(Desktop d,string root,string expected,string mode){
        var notice=(Button)d.Window.FindName("UpdateNotice");
        Need(notice.Visibility==Visibility.Collapsed,"notice before check");
        if(mode=="managed"||mode=="off"){
            Need(!Field<DispatcherTimer>(d,"releaseTimer").IsEnabled,"automatic check scheduled while disabled");
            if(mode=="managed"){
                bool blocked=false;
                try{await Updates.Latest(System.Threading.CancellationToken.None);}catch(UnauthorizedAccessException){blocked=true;}
                Need(blocked,"managed check reached network");
            }
            Console.WriteLine("PASS update policy "+mode);return;
        }
        await Wait(()=>notice.Visibility==Visibility.Visible,TimeSpan.FromSeconds(45),"startup notification");
        Need(notice.Content.ToString().Contains(expected),"notice names current release: "+notice.Content);
        var package=Field<Updates.Package>(d,"available");
        Photo(d.Window,Path.Combine(root,"notice.png"));
        if(mode=="windows"){
            d.Window.WindowState=WindowState.Minimized;
            string mark=Path.Combine(root,"updates","notified.txt");
            await Wait(()=>File.Exists(mark)&&File.ReadAllText(mark)==expected,TimeSpan.FromSeconds(5),"Windows notification");
            Need(Field<object>(d,"updateIcon")!=null,"Shell notification object");
            Console.WriteLine("PASS Windows notification submitted for "+expected);
            d.Window.WindowState=WindowState.Normal;
        }
        if(mode!="auto"){
            Need(!package.Exists&&Directory.GetFiles(root,"*.exe",SearchOption.AllDirectories).Length==0,"notify only performed a download");
            Console.WriteLine("PASS startup notice "+package.Version+" without automatic download");return;
        }
        await Wait(()=>Field<object>(d,"checkingRelease")==null,TimeSpan.FromMinutes(9),"automatic download timed out");
        Need(Field<bool>(d,"installerReady"),Field<string>(d,"updateStatus"));Need(package.Exists,"cache");
        Updates.Verify(package,System.Threading.CancellationToken.None);
        ((TextBox)d.Window.FindName("Input")).Text="я";
        var fakeDialog=new Window();
        await (Task)typeof(Desktop).GetMethod("ApplyUpdate",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(d,new object[]{fakeDialog});
        fakeDialog.Close();
        Need(Field<string>(d,"updateStatus").Contains("черновик"),"draft protection");
        var file=Directory.GetFiles(root,"Ostum-Desktop-Setup.exe",SearchOption.AllDirectories)[0];
        byte original;
        using(var f=new FileStream(file,FileMode.Open,FileAccess.ReadWrite)){original=(byte)f.ReadByte();f.Position=0;f.WriteByte((byte)(original^1));}
        bool rejected=false;
        try{Updates.Verify(package,System.Threading.CancellationToken.None);}catch(InvalidDataException){rejected=true;}
        finally{using(var f=new FileStream(file,FileMode.Open,FileAccess.Write))f.WriteByte(original);}
        Need(rejected,"changed installer was accepted");
        Console.WriteLine("PASS startup notice, unattended download "+package.Version+", SHA-256, PE version, draft protection and tamper detection");
    }
    [STAThread]static int Main(string[] args){
        try{
            Metadata();if(args.Length==0)return 0;
            string root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);
            typeof(Store).GetField("Home").SetValue(null,root);
            if(args[2]=="managed"){
                string path=WorkPolicy.PolicyPath;
                Need(WorkPolicy.Within(path,root),"managed test requires an isolated policy path");
                Directory.CreateDirectory(Path.GetDirectoryName(path));File.WriteAllText(path,"{\"Managed\":true}");
            }
            Store.Save(new Preferences{Models=Path.Combine(root,"no-models"),CheckForUpdates=args[2]!="off",DownloadUpdates=args[2]=="auto"});
            var app=new Application{ShutdownMode=ShutdownMode.OnMainWindowClose};var desktop=new Desktop(Path.Combine(root,"engines"));var w=desktop.Window;
            w.ShowActivated=false;w.ShowInTaskbar=false;w.WindowStartupLocation=WindowStartupLocation.Manual;w.Left=-4800;w.Top=5;int code=1;
            w.ContentRendered+=async(s,e)=>{try{await Live(desktop,root,args[1],args[2]);code=0;}catch(Exception ex){Console.Error.WriteLine(ex);}finally{w.Close();}};
            app.Run(w);return code;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
