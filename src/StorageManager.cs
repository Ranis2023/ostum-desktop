using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;

namespace Sfera {
    internal sealed class StorageItem {
        internal string Path,Hash,Kind;
        internal long Stamp;
        public bool Selected {get;set;}
        public string Group {get;set;}
        public string Name {get;set;}
        public string Status {get;set;}
        public long Bytes {get;set;}
        public bool Removable {get;set;}
        public string Size {get{return StorageManager.Size(Bytes);}}
    }
    internal static class StorageManager {
        internal static string Size(long bytes){return bytes>=1073741824?((double)bytes/1073741824).ToString("0.00")+" ГиБ":bytes>=1048576?((double)bytes/1048576).ToString("0.0")+" МиБ":bytes>=1024?((double)bytes/1024).ToString("0")+" КиБ":bytes+" Б";}
        static string Runtime {get{return Path.Combine(Store.Home,"runtime");}}
        static bool Same(string a,string b){return string.Equals(a,b,StringComparison.OrdinalIgnoreCase);}
        static bool OwnVolume(string path){return !new Uri(Path.GetFullPath(path)).IsUnc&&new DriveInfo(Path.GetPathRoot(path)).DriveType!=DriveType.Network;}
        static IEnumerable<FileInfo> Contents(string root,CancellationToken token){
            var pending=new Stack<DirectoryInfo>();pending.Push(new DirectoryInfo(root));
            while(pending.Count>0){token.ThrowIfCancellationRequested();var dir=pending.Pop();WorkPolicy.LocalPath(dir.FullName);
                foreach(var entry in dir.EnumerateFileSystemInfos()){
                    if((entry.Attributes&FileAttributes.ReparsePoint)!=0)throw new UnauthorizedAccessException("В папке есть ссылка.");
                    if(entry is DirectoryInfo)pending.Push((DirectoryInfo)entry);else yield return (FileInfo)entry;
                }
            }
        }
        static HashSet<string> RuntimeManifest(){
            var files=new HashSet<string>(StringComparer.OrdinalIgnoreCase){"ready"};
            using(var stream=File.OpenRead(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"OstumTools.zip")))
            using(var zip=new ZipArchive(stream,ZipArchiveMode.Read)){foreach(var e in zip.Entries)if(e.Name.Length!=0)files.Add(e.FullName.Replace('/','\\'));}
            return files;
        }
        static void AddFile(List<StorageItem> rows,HashSet<string> seen,string path,string group,string name,string reason,string kind,string hash=null){
            if(!File.Exists(path)||seen.Contains(path))return;
            try{WorkPolicy.LocalPath(path);}catch(UnauthorizedAccessException){return;}var f=new FileInfo(path);bool safe=reason==null&&!WorkPolicy.Rules.Managed&&OwnVolume(path);
            rows.Add(new StorageItem{Path=path,Group=group,Name=name,Bytes=f.Length,Stamp=f.LastWriteTimeUtc.Ticks,Hash=hash,Kind=kind,
                Removable=safe,Status=reason??(safe?"Можно удалить":"Управляется организацией")});seen.Add(path);
        }
        internal static List<StorageItem> Scan(Preferences p,CancellationToken token){
            var rows=new List<StorageItem>();var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var selected=Store.Chat(p);var imported=p.ImportedModels??new List<LocalModel>();
            var keep=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var m in imported.Where(m=>m!=null)){
                foreach(string s in (m.Parts??new string[0]).Concat(new[]{m.Path,m.Projector}))if(!string.IsNullOrEmpty(s))keep.Add(s);
            }
            Action<ModelFile,string,string,bool> model=(file,dir,label,active)=>{
                token.ThrowIfCancellationRequested();string path=file.PathIn(dir);
                if(WorkPolicy.Rules.Managed&&!((WorkPolicy.Rules.ModelFolders??new string[0]).Any(r=>WorkPolicy.Within(path,r))))return;
                string reason=active?"Используется в настройках":keep.Contains(path)?"Подключена к библиотеке":null;
                if(File.Exists(path)&&new FileInfo(path).Length!=file.bytes)reason="Размер отличается от каталога";
                AddFile(rows,seen,path,"Модели",label+" · "+Path.GetFileName(path),reason,"model",file.sha256);
                AddFile(rows,seen,path+".part","Загрузки",Path.GetFileName(path),null,"partial");
            };
            foreach(var file in Store.Files.Values){
                bool active=selected.Files.Contains(file.id)||(Store.Art(p).Files.Contains(file.id)&&Store.DrawingOn(p))||(file.id=="voice"&&Store.DictationOn(p));
                model(file,p.Models,file.id=="voice"?"Диктовка":Store.Art(p).Files.Contains(file.id)?"Рисование":"Zarum",active);
            }
            foreach(var entry in CatalogEntry.All().Where(e=>!e.builtin))foreach(var f in entry.files)model(f,entry.DirectoryIn(p.Models),entry.title,false);
            foreach(var f in Store.MusicFiles)model(f,LocalMusic.Library(p.Models),"Музыка",false);
            string acceleration=Path.Combine(Store.Home,"acceleration",Acceleration.Id);
            foreach(var f in Store.Cuda.libraries)AddFile(rows,seen,f.PathIn(acceleration),"Ускорение NVIDIA",f.name,"Компонент движка","engine");
            foreach(var f in Store.Cuda.archives){
                string download=f.PathIn(Path.Combine(acceleration,"downloads"));
                AddFile(rows,seen,download,"Кэш загрузок NVIDIA",f.name,null,"model",f.sha256);
                AddFile(rows,seen,download+".part","Кэш загрузок NVIDIA",f.name,null,"partial");
            }
            foreach(string file in keep){
                try{WorkPolicy.CheckImport(file);AddFile(rows,seen,file,"Подключённый GGUF",Path.GetFileName(file),"Исходный файл пользователя","external");}catch(UnauthorizedAccessException){}
            }
            string digestPath=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"OstumTools.zip.sha256");
            string current=File.Exists(digestPath)?File.ReadAllText(digestPath).Trim():"";
            if(Directory.Exists(Runtime)){
                WorkPolicy.LocalPath(Runtime);HashSet<string> manifest=null;
                if(Regex.IsMatch(current,"^[a-fA-F0-9]{64}$")){
                    try{manifest=RuntimeManifest();}catch(IOException){}catch(InvalidDataException){}catch(UnauthorizedAccessException){}
                }
                foreach(var dir in new DirectoryInfo(Runtime).EnumerateDirectories()){
                    token.ThrowIfCancellationRequested();if(!Regex.IsMatch(dir.Name,"^[a-fA-F0-9]{64}$")||(dir.Attributes&FileAttributes.ReparsePoint)!=0)continue;
                    try{
                        var files=Contents(dir.FullName,token).ToArray();string mark=Path.Combine(dir.FullName,"ready");
                        bool valid=manifest!=null&&File.Exists(mark)&&new FileInfo(mark).Length<150&&Same(File.ReadAllText(mark).Trim(),dir.Name)
                            &&files.All(f=>manifest.Contains(f.FullName.Substring(dir.FullName.Length+1)));
                        bool removable=valid&&!Same(dir.Name,current)&&!WorkPolicy.Rules.Managed;
                        rows.Add(new StorageItem{Path=dir.FullName,Name="Компоненты "+dir.Name.Substring(0,8),Group="Кэш редактора",Kind="runtime",Bytes=files.Sum(f=>f.Length),
                            Stamp=files.Select(f=>f.LastWriteTimeUtc.Ticks).DefaultIfEmpty().Max(),Removable=removable,Status=Same(dir.Name,current)?"Текущая версия":removable?"Кэш старой версии":"Только просмотр"});
                    }catch(IOException){}catch(UnauthorizedAccessException){}
                }
            }
            string updates=Path.Combine(Store.Home,"updates");
            if(Directory.Exists(updates)){WorkPolicy.LocalPath(updates);foreach(string dir in Directory.EnumerateDirectories(updates)){
                Version v;string n=Path.GetFileName(dir);if(!Version.TryParse(n,out v)||v.ToString()!=n)continue;
                string setup=Path.Combine(dir,"Ostum-Desktop-Setup.exe"),reason=v.CompareTo(new Version(Brand.Version))>0?"Ожидает обновления":null;
                AddFile(rows,seen,setup,"Обновления","Установщик "+n,reason,"setup");AddFile(rows,seen,setup+".part","Обновления","Загрузка "+n,reason,"partial");
            }}
            foreach(var dir in new[]{Store.Chats,Store.Images,Store.Audio}){
                if(!Directory.Exists(dir)||(dir==Store.Chats&&!WorkPolicy.History))continue;
                try{rows.Add(new StorageItem{Path=dir,Kind="personal",Group="Личные данные",Name=dir==Store.Chats?"История чатов":dir==Store.Images?"Изображения":"Созданная музыка",
                    Bytes=Contents(dir,token).Sum(f=>f.Length),Status="Только просмотр"});}catch(IOException){}catch(UnauthorizedAccessException){}
            }
            return rows;
        }
        internal static void Remove(StorageItem selected,Preferences prefs,CancellationToken token){
            var fresh=Scan(prefs,token).FirstOrDefault(x=>Same(x.Path,selected.Path)&&x.Kind==selected.Kind);
            if(!selected.Removable||fresh==null||!fresh.Removable||fresh.Stamp!=selected.Stamp||fresh.Bytes!=selected.Bytes)
                throw new IOException("Состояние файла изменилось. Обновите список и выберите его ещё раз.");
            string path=fresh.Path;WorkPolicy.LocalPath(path);token.ThrowIfCancellationRequested();
            if(fresh.Kind=="runtime"){RemoveRuntime(path,token);return;}
            using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Delete)){
                if(stream.Length!=fresh.Bytes||File.GetLastWriteTimeUtc(path).Ticks!=fresh.Stamp)throw new IOException("Файл изменился.");
                if(fresh.Kind=="model"){
                    using(var sha=SHA256.Create())if(!Same(BitConverter.ToString(sha.ComputeHash(stream)).Replace("-",""),fresh.Hash))throw new IOException("Содержимое отличается от каталога. Файл сохранён.");
                }
                token.ThrowIfCancellationRequested();File.Delete(path);
            }
        }
        static void RemoveRuntime(string path,CancellationToken token){
            var allowed=RuntimeManifest();string current=File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"OstumTools.zip.sha256")).Trim();
            if(!Same(Path.GetDirectoryName(path),Runtime)||Same(Path.GetFileName(path),current))throw new UnauthorizedAccessException("Используемый кэш защищён.");
            var files=Contents(path,token).ToArray();
            if(files.Any(f=>!allowed.Contains(f.FullName.Substring(path.Length+1))))throw new IOException("В кэше есть посторонние файлы.");
            string moved=Path.Combine(Runtime,"cleanup-"+Guid.NewGuid().ToString("N"));Directory.Move(path,moved);
            try{
                // No recursive delete. Remove manifest files only; unexpected content prevents directory removal.
                foreach(var f in files){string item=Path.Combine(moved,f.FullName.Substring(path.Length+1));WorkPolicy.LocalPath(item);File.Delete(item);}
                var dirs=new Stack<string>();var empty=new List<string>();dirs.Push(moved);
                while(dirs.Count>0){string dir=dirs.Pop();WorkPolicy.LocalPath(dir);empty.Add(dir);foreach(string sub in Directory.EnumerateDirectories(dir))dirs.Push(sub);}
                for(int i=empty.Count-1;i>=0;i--)Directory.Delete(empty[i],false);
            }catch{if(Directory.Exists(moved)&&!Directory.Exists(path))Directory.Move(moved,path);throw;}
        }
    }
}
