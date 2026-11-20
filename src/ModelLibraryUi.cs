using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace Sfera {
    public static class MachineProfile {
        [StructLayout(LayoutKind.Sequential)]sealed class Memory {public uint length=64,load;public ulong physical,available,page,freePage,virtualBytes,freeVirtual,extended;}
        [DllImport("kernel32.dll",SetLastError=true)]static extern bool GlobalMemoryStatusEx([In,Out]Memory m);
        public static bool LargeForRam(long size){var mem=new Memory();return GlobalMemoryStatusEx(mem)&&(double)size>mem.physical*.8;}
        public static int RamGiB(){var mem=new Memory();return GlobalMemoryStatusEx(mem)?(int)Math.Round(mem.physical/1073741824d):0;}
        static string Gib(ulong n){return (n/1073741824d).ToString("0.#")+" ГиБ";}
        internal static int NvidiaGiB(){
            string smi=Path.Combine(Environment.SystemDirectory,"nvidia-smi.exe");if(!File.Exists(smi))return 0;
            try{using(var proc=Process.Start(new ProcessStartInfo(smi,"--query-gpu=memory.total --format=csv,noheader,nounits"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true})){
                if(!proc.WaitForExit(2200)){proc.Kill();return 0;}
                int largest=0;foreach(string line in proc.StandardOutput.ReadToEnd().Split('\n')){int mb;if(int.TryParse(line.Trim(),out mb))largest=Math.Max(largest,mb);}
                return (int)Math.Round(largest/1024d);
            }}catch(System.ComponentModel.Win32Exception){return 0;}catch(InvalidOperationException){return 0;}
        }
        public static string Describe(string directory){
            var mem=new Memory();GlobalMemoryStatusEx(mem);
            string cpu=Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER")??"CPU",gpu="CPU",vram="";
            try{using(var query=new ManagementObjectSearcher("SELECT Name FROM Win32_Processor"))using(var rows=query.Get())foreach(ManagementObject o in rows){cpu=Convert.ToString(o["Name"]).Trim();break;}}catch(ManagementException){}
            try{using(var query=new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController"))using(var rows=query.Get())gpu=string.Join(", ",rows.Cast<ManagementObject>().Select(o=>Convert.ToString(o["Name"])));}catch(ManagementException){}
            string smi=Path.Combine(Environment.SystemDirectory,"nvidia-smi.exe");
            if(File.Exists(smi))try{using(var proc=Process.Start(new ProcessStartInfo(smi,"--query-gpu=memory.total,memory.free --format=csv,noheader,nounits"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true})){
                if(proc.WaitForExit(3000)){string[] values=(proc.StandardOutput.ReadLine()??"").Split(',');ulong total,free;if(values.Length==2&&ulong.TryParse(values[0].Trim(),out total)&&ulong.TryParse(values[1].Trim(),out free))vram=" · видеопамять "+Gib(total*1048576)+" (свободно "+Gib(free*1048576)+")";}else proc.Kill();
            }}catch(Exception){}
            var drive=new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directory)));
            return cpu+" · "+Gib(mem.physical)+" ОЗУ\n"+gpu+vram+"\nСвободно на диске "+Gib((ulong)drive.AvailableFreeSpace)+". SSD ускоряет первый запуск. Контекст, размер модели, видеопамять и число агентов влияют на время ответа.";
        }
    }
    sealed partial class ModelSettings {
        readonly List<Model> library=new List<Model>();
        bool changedImport;
        Model Chosen {get{return Find<ComboBox>("EngineChoice").SelectedItem as Model??Store.Chat(prefs);}}
        void BusyLibrary(bool busy){
            foreach(var id in new[]{"EngineChoice","ImportGGUF","CatalogButton"})Find<Control>(id).IsEnabled=!busy;
        }
        void SetupLibrary(){
            var choices=Find<ComboBox>("EngineChoice");library.AddRange(Store.Available(prefs));choices.ItemsSource=library;choices.SelectedItem=library.FirstOrDefault(m=>m.Id==prefs.Chat)??library[0];
            choices.SelectionChanged+=(s,e)=>Refresh();
            Find<Button>("ImportGGUF").Click+=async(s,e)=>{
                if(transfer!=null)return;
                var pick=new OpenFileDialog{Title="Открыть модель с диска",Filter="Языковая модель GGUF|*.gguf"};
                if(pick.ShowDialog(window)!=true)return;
                try{
                    WorkPolicy.CheckImport(pick.FileName);
                    var candidates=Directory.EnumerateFiles(Path.GetDirectoryName(pick.FileName),"*mmproj*.gguf").Take(2).ToArray();
                    string projection=candidates.Length==1?candidates[0]:null;
                    if(projection==null&&MessageBox.Show(window,"Подключить зрительный проектор для анализа изображений этой моделью?","Импорт GGUF",MessageBoxButton.YesNo)==MessageBoxResult.Yes){
                        var vision=new OpenFileDialog{Title="Зрительный проектор для этой версии модели",InitialDirectory=Path.GetDirectoryName(pick.FileName),Filter="Проектор GGUF|*.gguf"};
                        if(vision.ShowDialog(window)!=true)return;projection=vision.FileName;
                    }
                    if(projection!=null)WorkPolicy.CheckImport(projection);
                    BusyLibrary(true);var record=await Task.Run(()=>LocalModel.Inspect(pick.FileName,projection));
                    if(window.IsVisible){SelectLocal(record);apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));}
                }catch(Exception ex){notice.Text=ex.Message;}finally{if(transfer==null)BusyLibrary(false);}
            };
            Find<Button>("CatalogButton").Click+=async(s,e)=>await Catalog();
            var info=Find<TextBlock>("HardwareInfo");info.Text="Определяю оборудование…";
            Find<Button>("MeasureSpeed").Click+=async(s,e)=>{
                if(transfer!=null)return;
                var cts=transfer=new CancellationTokenSource();var button=(Button)s;button.IsEnabled=false;apply.IsEnabled=false;dismiss.Content="Остановить";BusyLibrary(true);
                try{
                    var p=Store.Json().Deserialize<Preferences>(Store.Json().Serialize(prefs));p.Chat=Chosen.Id;p.Models=folder.Text;p.ImportedModels=library.Where(m=>m.Imported!=null).Select(m=>m.Imported).ToList();
                    notice.Text="Измеряю загрузку, время до первого токена и генерацию 32 токенов…";
                    using(var tester=new Engine(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"engines")))notice.Text=await Task.Run(()=>tester.Measure(p,cts.Token));
                }catch(Exception ex){notice.Text=cts.IsCancellationRequested?"Замер остановлен.":ex.Message;}
                finally{transfer=null;cts.Dispose();button.IsEnabled=apply.IsEnabled=true;BusyLibrary(false);dismiss.Content="Закрыть";}
            };
            window.Loaded+=async(s,e)=>{try{info.Text=await Task.Run(()=>MachineProfile.Describe(prefs.Models));}catch(Exception ex){info.Text="Не удалось прочитать сведения об оборудовании. "+ex.Message;}};
        }
        void SelectLocal(LocalModel record){
            changedImport=true;
            library.RemoveAll(m=>m.Id==record.Id);var model=new Model(record);library.Add(model);
            var box=Find<ComboBox>("EngineChoice");box.Items.Refresh();box.SelectedItem=model;
        }
        async Task CheckCandidate(Preferences settings,LocalModel candidate,CancellationToken token){
            if(!candidate.Available())throw new IOException("Нужны все файлы модели.");
            if(MachineProfile.LargeForRam(candidate.Bytes)&&MessageBox.Show(window,"Веса занимают большую часть объёма установленной ОЗУ. Загрузка может быть длительной. Проверить модель?","Возможности компьютера",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)throw new OperationCanceledException();
            var p=Store.Json().Deserialize<Preferences>(Store.Json().Serialize(settings));
            p.ImportedModels=new List<LocalModel>{candidate};p.Chat=candidate.Id;p.Personalize=false;p.ProjectScope=null;
            notice.Text="Проверяю, что модель загружается и отвечает на этом ПК…";
            await Task.Run(async()=>{
                using(var tester=new Engine(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"engines"))){
                    string image=null;
                    try{
                        if(!string.IsNullOrEmpty(candidate.Projector)){
                            image=Path.Combine(Path.GetTempPath(),"ostum-probe-"+Guid.NewGuid().ToString("N")+".png");
                            using(var bitmap=new System.Drawing.Bitmap(64,64))bitmap.Save(image,System.Drawing.Imaging.ImageFormat.Png);
                        }
                        string reply=await tester.Role(p,"Answer briefly.",image==null?"Reply OK.":"Name one color in this image.",image,24,false,x=>{},x=>{},token);
                        if(string.IsNullOrWhiteSpace(reply))throw new IOException("Проверочный запрос не дал ответа. Выберите другую модель.");
                    }finally{if(image!=null&&File.Exists(image))File.Delete(image);}
                }
            },token);
        }
        async Task Catalog(){
            if(transfer!=null)return;
            var entry=ModelCatalogDialog.Choose(window,folder.Text);if(entry==null)return;
            if(entry.builtin){
                Find<ComboBox>("EngineChoice").SelectedItem=library.First(m=>m.Id=="quality");
                apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));return;
            }
            if(MachineProfile.LargeForRam(entry.files[0].bytes)&&MessageBox.Show(window,"Выбранная модель велика для доступной ОЗУ. Скачать "+Store.Size(entry.files.Sum(f=>f.bytes))+"?","Загрузка модели",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;
            var cts=transfer=new CancellationTokenSource();bool selected=false;
            try{
                BusyLibrary(true);apply.IsEnabled=false;dismiss.Content="Остановить";progress.Visibility=Visibility.Visible;
                string dest=entry.DirectoryIn(folder.Text);WorkPolicy.CheckImport(Path.Combine(dest,entry.files[0].name));
                await Downloads.Prepare(entry.files,dest,(label,ratio)=>window.Dispatcher.BeginInvoke(new Action(()=>{notice.Text=label;progress.Value=ratio;})),cts.Token);
                var record=await Task.Run(()=>LocalModel.Inspect(entry.files[0].PathIn(dest),entry.files.Length>1?entry.files[1].PathIn(dest):null));SelectLocal(record);selected=true;
            }catch(Exception ex){notice.Text=ex.Message;}finally{transfer=null;cts.Dispose();BusyLibrary(false);apply.IsEnabled=true;dismiss.Content="Закрыть";progress.Visibility=Visibility.Collapsed;}
            if(selected)apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
    }
}
