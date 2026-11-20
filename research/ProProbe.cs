using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using Sfera;

public static class ProProbe {
    [STAThread] public static int Main(string[] args){
        try{
            if(args.Length==4&&args[0]=="--media"){
                string output=Path.GetFullPath(args[3]);Directory.CreateDirectory(output);var p=new Preferences{Models=args[2],Personalize=false};
                string picture=Path.Combine(output,"red-test.png");
                using(var b=new System.Drawing.Bitmap(96,96)){using(var g=System.Drawing.Graphics.FromImage(b))g.Clear(System.Drawing.Color.Red);b.Save(picture,System.Drawing.Imaging.ImageFormat.Png);}
                using(var e=new Engine(args[1]))using(var limit=new CancellationTokenSource(TimeSpan.FromMinutes(12))){
                    var reply=e.Role(p,"Ответь кратко по-русски.","Какого цвета это изображение?",picture,42,false,s=>{},s=>{},limit.Token).GetAwaiter().GetResult();
                    if(string.IsNullOrWhiteSpace(reply))throw new Exception("No vision response");Console.WriteLine("Vision: "+reply);
                    var art=e.Paint(p,"a blue ceramic cup on a wooden table, studio photograph",null,384,384,Console.WriteLine,limit.Token).GetAwaiter().GetResult();
                    string target=Path.Combine(output,Guid.NewGuid().ToString("N")+".png");File.Move(art,target);
                    using(var image=System.Drawing.Image.FromFile(target))Console.WriteLine("Generated "+image.Width+" x "+image.Height);
                }
                return 0;
            }
            if(args.Length==2&&args[0]=="--office"){
                string root=ProTools.Root();
                var req=new Dictionary<string,object>{{"action","read"},{"path",Path.GetFullPath(args[1])}};
                string json=ProTools.Python("office.py",null,Store.Json().Serialize(req),CancellationToken.None).GetAwaiter().GetResult();
                var result=Store.Json().Deserialize<OfficeResult>(json);if(result.rows==null||result.rows.Length==0)throw new Exception(result.error);
                Console.WriteLine("Portable editor: "+result.kind+" | "+result.rows.Length+" rows");
                var view=typeof(ProTools).Assembly.GetType("Sfera.OfficePreview").GetMethod(result.kind=="word"?"Word":"Sheet");
                var content=(FrameworkElement)view.Invoke(null,new object[]{result.rows,new Action<OfficeRow>(r=>{})});
                var panel=new Grid{Width=760,Height=400};panel.Children.Add(content);panel.Measure(new Size(760,400));panel.Arrange(new Rect(0,0,760,400));panel.UpdateLayout();
                if(content.ActualWidth<100||content.ActualHeight<100)throw new Exception("Office preview failed layout");
                Console.WriteLine("PASS · visible preview layout and packaged runtime");return 0;
            }
            var prefs=new Preferences{Personalize=false,Models=args[1]};
            var ui=(Window)XamlReader.Parse(Store.Resource("ModelsWindow.xaml"));
            if(ui.FindName("DeveloperChannel")==null||ui.FindName("MeasureSpeed")==null)throw new Exception("Settings controls missing");
            var main=(Window)XamlReader.Parse(Store.Resource("MainWindow.xaml"));
            if(main.FindName("Projects")==null||main.FindName("MicDevices")==null)throw new Exception("Main controls missing");
            using(var e=new Engine(args[0])){
                Console.WriteLine(e.Measure(prefs,CancellationToken.None).GetAwaiter().GetResult());
            }
            if(args.Length>2){
                var model=LocalModel.Inspect(args[2]);prefs.ImportedModels=new List<LocalModel>{model};prefs.Chat=model.Id;
                using(var e=new Engine(args[0])){
                    var reply=e.Role(prefs,"Answer briefly.","What is two plus two?",null,36,false,s=>{},s=>{},CancellationToken.None).GetAwaiter().GetResult();
                    if(string.IsNullOrWhiteSpace(reply))throw new Exception("No reply from local GGUF");
                    Console.WriteLine("GGUF: "+model.Name+" | "+reply);
                }
            }
            Console.WriteLine("PASS");return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
