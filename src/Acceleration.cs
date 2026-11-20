using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace Sfera {
    public static class Acceleration {
        public const string Id="llama-b12547-cuda12";
        public static string Folder {get{return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"SferaAI","acceleration",Id);}}
        static string Marker {get{return Path.Combine(Folder,"verified-b12547.txt");}}
        static ModelFile[] libraries {get{return Store.Cuda.libraries;}}
        static ModelFile[] archives {get{return Store.Cuda.archives;}}
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32),DllImport("nvcuda.dll",ExactSpelling=true)]static extern int cuInit(uint flags);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32),DllImport("nvcuda.dll",ExactSpelling=true)]static extern int cuDriverGetVersion(out int value);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32),DllImport("nvcuda.dll",ExactSpelling=true)]static extern int cuDeviceGetCount(out int count);
        static readonly Lazy<bool> driver=new Lazy<bool>(DetectDriver);
        static bool DetectDriver(){
            try{int version,count;return cuInit(0)==0&&cuDriverGetVersion(out version)==0&&version>=12000&&cuDeviceGetCount(out count)==0&&count>0;}
            catch(DllNotFoundException){return false;}catch(EntryPointNotFoundException){return false;}catch(BadImageFormatException){return false;}
        }
        public static bool Supported {get{return driver.Value;}}
        public static bool Ready {
            get{return Supported&&File.Exists(Marker)&&File.Exists(Path.Combine(Folder,"llama-server.exe"))&&libraries.All(f=>f.Present(Folder));}
        }
        static void Publish(string temp,string target){
            if(File.Exists(target))File.Replace(temp,target,null);else File.Move(temp,target);
        }
        static void InstallLibrary(ModelFile f,Action<string> write,CancellationToken ct){
            string target=f.PathIn(Folder),temp=target+"."+Guid.NewGuid().ToString("N")+".part";
            try{write(temp);Downloads.Verify(temp,f,ct);Publish(temp,target);}
            finally{if(File.Exists(temp))File.Delete(temp);}
        }
        static bool Verified(ModelFile f,CancellationToken ct){
            if(!f.Present(Folder))return false;
            try{Downloads.Verify(f.PathIn(Folder),f,ct);return true;}catch(InvalidDataException){return false;}
        }
        static void CopyRuntime(string stock,CancellationToken ct){
            var core=new[]{"llama-server.exe","llama-server-impl.dll","llama-common.dll","llama.dll","ggml.dll","ggml-base.dll","mtmd.dll","libomp.dll","vcruntime140.dll","vcruntime140_1.dll","msvcp140.dll"};
            var names=core.Concat(Directory.EnumerateFiles(stock,"ggml-cpu-*.dll").Select(Path.GetFileName));
            foreach(string name in names){
                ct.ThrowIfCancellationRequested();
                string source=Path.Combine(stock,name),target=Path.Combine(Folder,name);
                var original=new FileInfo(source);var copy=new FileInfo(target);
                if(copy.Exists&&copy.Length==original.Length&&copy.LastWriteTimeUtc==original.LastWriteTimeUtc)continue;
                string temp=target+"."+Guid.NewGuid().ToString("N")+".part";
                try{InstallModels.LinkOrCopy(source,temp,original.Length);Publish(temp,target);}
                finally{if(File.Exists(temp))File.Delete(temp);}
            }
        }
        static void FromArchive(ModelFile archive,List<ModelFile> missing,string cache,Action<string,int> status,CancellationToken ct) {
            if(missing.Count==0)return;
            long required=missing.Sum(f=>f.bytes)+archive.bytes+160L*1024*1024;
            if(new DriveInfo(Path.GetPathRoot(Folder)).AvailableFreeSpace<required)throw new IOException("Недостаточно места для загрузки ускорителя NVIDIA.");
            Downloads.Prepare(new[]{archive},cache,(name,p)=>status("Загружаю ускорение NVIDIA · "+name,(int)(p*100)),ct).GetAwaiter().GetResult();
            string file=archive.PathIn(cache);
            try{
                using(var stream=File.OpenRead(file))using(var zip=new ZipArchive(stream,ZipArchiveMode.Read)){
                    foreach(var lib in missing){
                        ct.ThrowIfCancellationRequested();var entry=zip.GetEntry(lib.name);
                        if(entry==null||entry.Length!=lib.bytes)throw new InvalidDataException("В сборке NVIDIA отсутствует "+lib.name);
                        status("Проверяю "+lib.name,-1);
                        InstallLibrary(lib,temp=>{using(var input=entry.Open())using(var output=File.Create(temp)){
                            var buffer=new byte[1024*1024];int n;
                            while((n=input.Read(buffer,0,buffer.Length))!=0){ct.ThrowIfCancellationRequested();output.Write(buffer,0,n);}
                        }},ct);
                    }
                }
            }finally{if(File.Exists(file))File.Delete(file);}
        }
        public static bool Prepare(string stock,string media,bool offline,Action<string,int> status,CancellationToken ct){
            if(!Supported)return false;
            if(Ready&&libraries.All(f=>Verified(f,ct))){CopyRuntime(stock,ct);return true;}
            Directory.CreateDirectory(Folder);
            if(File.Exists(Marker))File.Delete(Marker);
            var missing=new List<ModelFile>();
            foreach(var lib in libraries){
                ct.ThrowIfCancellationRequested();
                if(Verified(lib,ct))continue;
                if(!lib.Present(media)){missing.Add(lib);continue;}
                status("Подключаю ускорение NVIDIA · "+lib.name,-1);
                InstallLibrary(lib,temp=>InstallModels.LinkOrCopy(lib.PathIn(media),temp,lib.bytes),ct);
            }
            if(missing.Count>0){
                if(offline)return false;
                string cache=Directory.CreateDirectory(Path.Combine(Folder,"downloads")).FullName;
                FromArchive(archives[0],missing.Where(f=>f.name=="ggml-cuda.dll").ToList(),cache,status,ct);
                FromArchive(archives[1],missing.Where(f=>f.name!="ggml-cuda.dll").ToList(),cache,status,ct);
            }
            CopyRuntime(stock,ct);
            string mark=Marker+"."+Guid.NewGuid().ToString("N")+".part";
            try{File.WriteAllText(mark,Id);Publish(mark,Marker);}finally{if(File.Exists(mark))File.Delete(mark);}
            return Ready;
        }
    }
}
