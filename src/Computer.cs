using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using System.Windows.Media.Imaging;

namespace Sfera {
    public sealed class AppWindow {
        public IntPtr Handle;
        public uint Process;
        public string Title;
        public override string ToString(){return Title;}
    }
    public static class Computer {
        delegate bool EnumProc(IntPtr window,IntPtr state);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback,IntPtr state);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr window,StringBuilder title,int cap);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr window,int mode);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int w,int h,uint flags);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr window,int attr,out int value,int size);
        public static List<AppWindow> Windows(IntPtr self) {
            var result=new List<AppWindow>();
            uint ownId;using(var current=Process.GetCurrentProcess())ownId=(uint)current.Id;
            EnumWindows((hw,st)=>{
                uint pid;GetWindowThreadProcessId(hw,out pid);int cloaked;
                if(hw==self||!IsWindowVisible(hw)||pid==ownId||DwmGetWindowAttribute(hw,14,out cloaked,4)==0&&cloaked!=0)return true;
                var str=new StringBuilder(512);GetWindowText(hw,str,str.Capacity);
                if(str.Length>0&&str.ToString()!="Program Manager")result.Add(new AppWindow{Handle=hw,Process=pid,Title=str.ToString()});
                return true;
            },IntPtr.Zero);
            return result;
        }
        public static void Arrange(AppWindow app,string position) {
            uint pid; GetWindowThreadProcessId(app.Handle,out pid);
            if(pid==0||pid!=app.Process||!IsWindowVisible(app.Handle))throw new IOException("Это окно уже закрыто.");
            if(position=="min"){ShowWindowAsync(app.Handle,6);return;}
            if(position=="max"){ShowWindowAsync(app.Handle,3);SetForegroundWindow(app.Handle);return;}
            if(position=="focus"){if(IsIconic(app.Handle))ShowWindowAsync(app.Handle,9);SetForegroundWindow(app.Handle);return;}
            if(position!="left"&&position!="right")throw new ArgumentException("Неизвестное действие.");
            ShowWindowAsync(app.Handle,9);var area=Screen.FromHandle(app.Handle).WorkingArea;
            int half=area.Width/2;
            if(!SetWindowPos(app.Handle,IntPtr.Zero,area.X+(position=="right"?half:0),area.Y,position=="right"?area.Width-half:half,area.Height,0x0040))throw new IOException("Windows не разрешила переместить это окно.");
        }
        public static BitmapSource Capture(Screen screen) {
            var bounds=screen.Bounds;
            using(var bmp=new Bitmap(bounds.Width,bounds.Height,PixelFormat.Format24bppRgb)){
                using(var g=Graphics.FromImage(bmp))g.CopyFromScreen(bounds.Left,bounds.Top,0,0,bounds.Size,CopyPixelOperation.SourceCopy);
                using(var buffer=new MemoryStream()){bmp.Save(buffer,ImageFormat.Png);buffer.Position=0;var img=BitmapFrame.Create(buffer,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);img.Freeze();return img;}
            }
        }
        public static string WindowRequest(string text,IntPtr self){
            string t=(text??"").Trim();
            if(Regex.IsMatch(t,@"^(?:покажи (?:список )?(?:открытых )?окон|какие окна открыты|список открытых окон)[?.! ]*$",RegexOptions.IgnoreCase)){
                var titles=Windows(self).ConvertAll(w=>"• "+w.Title);
                return titles.Count==0?"Нет доступных окон.":string.Join("\n",titles);
            }
            var move=Regex.Match(t,@"^(?:поставь|перемести|расположи) окно\s+(.+?)\s+(влево|вправо|слева|справа|на левую половину|на правую половину)[.! ]*$",RegexOptions.IgnoreCase);
            var state=Regex.Match(t,@"^(сверни|разверни|активируй|покажи|переключись на) окно\s+(.+?)[.! ]*$",RegexOptions.IgnoreCase);
            if(!move.Success&&!state.Success)return null;
            string query=(move.Success?move.Groups[1].Value:state.Groups[2].Value).Trim(' ','«','»','"');
            if(query.Length==0)return null;
            var windows=Windows(self);var exact=windows.FindAll(w=>w.Title.Equals(query,StringComparison.CurrentCultureIgnoreCase));
            var matches=exact.Count>0?exact:windows.FindAll(w=>w.Title.IndexOf(query,StringComparison.CurrentCultureIgnoreCase)>=0);
            if(matches.Count==0)return "Не нашла открытое окно «"+query+"».";
            if(matches.Count>1)return "Уточните заголовок окна.\n"+string.Join("\n",matches.ConvertAll(w=>"• "+w.Title));
            string verb=state.Groups[1].Value.ToLowerInvariant();
            string action=move.Success?(Regex.IsMatch(move.Groups[2].Value,"прав",RegexOptions.IgnoreCase)?"right":"left"):verb=="сверни"?"min":verb=="разверни"?"max":"focus";
            Arrange(matches[0],action);return "Готово. Окно «"+matches[0].Title+"».";
        }
        public static string LaunchIntent(string text) {
            var m=Regex.Match(text??"", @"^\s*(?:пожалуйста[, ]+)?(?:открой|запусти|open|launch)\s+(?:приложение\s+)?(блокнот|калькулятор|проводник|notepad|calculator|explorer)[.! ]*$",RegexOptions.IgnoreCase);
            if(!m.Success)return null;string n=m.Groups[1].Value.ToLowerInvariant();
            if(n=="блокнот"||n=="notepad")return "notepad";
            if(n=="калькулятор"||n=="calculator")return "calc";return "explorer";
        }
        public static string Launch(string name) {
            string file,label;
            switch(name){case "notepad":file=Path.Combine(Environment.SystemDirectory,"notepad.exe");label="Блокнот";break;case "calc":file=Path.Combine(Environment.SystemDirectory,"calc.exe");label="Калькулятор";break;case "explorer":file=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"explorer.exe");label="Проводник";break;default:throw new ArgumentException("Неизвестное приложение.");}
            using(Process.Start(new ProcessStartInfo(file){UseShellExecute=true})){}return "Открываю "+label+".";
        }
    }
}
