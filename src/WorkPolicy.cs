using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Principal;

namespace Sfera {
    public sealed class CompanyRules {
        public bool Managed {get;set;}
        public bool LocalAI {get;set;}
        public bool Training {get;set;}
        public bool ChatHistory {get;set;}
        public bool InternalShares {get;set;}
        public bool ImportedModels {get;set;}
        public ProjectRule[] Projects {get;set;}
        public string[] ModelFolders {get;set;}
    }
    public sealed class ProjectRule {
        public string Root {get;set;}
        public bool Analysis {get;set;}
        public bool Training {get;set;}
        public string[] Groups {get;set;}
        public override string ToString(){return System.IO.Path.GetFileName(Root.TrimEnd('\\'))+"  ·  "+(Analysis?"локальный анализ":"только редактор");}
    }
    public static class WorkPolicy {
        public static string PolicyPath {get{return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"OSTUM","policy.json");}}
        public static CompanyRules Rules {
            get{
                try{return Store.Json().Deserialize<CompanyRules>(File.ReadAllText(PolicyPath))??new CompanyRules{Managed=true};}
                catch(FileNotFoundException){return new CompanyRules();}
                catch(DirectoryNotFoundException){return new CompanyRules();}
                catch(Exception){return new CompanyRules{Managed=true};}
            }
        }
        public static bool Network {get{return !Rules.Managed;}}
        public static bool History {get{var r=Rules;return !r.Managed||r.ChatHistory;}}
        public static void RequireAI(){var r=Rules;if(r.Managed&&!r.LocalAI)throw new UnauthorizedAccessException("В этой организации разрешён только редактор.");}
        public static void RequireNetwork(){if(!Network)throw new UnauthorizedAccessException("Внешние соединения отключены политикой организации.");}
        public static string Full(string path){return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);}
        public static bool Within(string path,string root){string a=Full(path),b=Full(root);return a.Equals(b,StringComparison.OrdinalIgnoreCase)||a.StartsWith(b+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);}
        public static void LocalPath(string path){
            var full=Path.GetFullPath(path);var rules=Rules;
            if(full.StartsWith(@"\\?\")||full.StartsWith(@"\\.\")||full.IndexOf(':',2)>=0)throw new UnauthorizedAccessException("Специальные пути Windows недоступны.");
            bool remote=full.StartsWith(@"\\")||new DriveInfo(Path.GetPathRoot(full)).DriveType==DriveType.Network;
            if(remote){
                bool approved=rules.Managed&&rules.InternalShares&&((rules.Projects??new ProjectRule[0]).Any(p=>p!=null&&Within(full,p.Root)&&Member(p))||(rules.ModelFolders??new string[0]).Any(r=>Within(full,r)));
                if(!approved)throw new UnauthorizedAccessException("Сетевая папка отсутствует в корпоративном списке.");
            }
            for(string cur=full;!string.IsNullOrEmpty(cur);cur=Path.GetDirectoryName(cur)){
                try{if((File.GetAttributes(cur)&FileAttributes.ReparsePoint)!=0)throw new UnauthorizedAccessException("Переход по ссылкам и точкам подключения отключён.");}
                catch(FileNotFoundException){}catch(DirectoryNotFoundException){}
            }
        }
        static bool Member(ProjectRule p){
            if(p.Groups==null||p.Groups.Length==0)return true;
            using(var id=WindowsIdentity.GetCurrent()){var user=new WindowsPrincipal(id);return p.Groups.Any(g=>user.IsInRole(g));}
        }
        public static ProjectRule[] Projects(){
            var r=Rules;if(r.Managed)return (r.Projects??new ProjectRule[0]).Where(p=>p!=null&&!string.IsNullOrWhiteSpace(p.Root)&&Member(p)).ToArray();
            var file=Path.Combine(Store.Home,"pro","projects.json");
            try{return Store.Json().Deserialize<ProjectRule[]>(File.ReadAllText(file))??new ProjectRule[0];}
            catch(FileNotFoundException){return new ProjectRule[0];}catch(DirectoryNotFoundException){return new ProjectRule[0];}
        }
        public static void Register(string root,bool analyze,bool train){
            if(Rules.Managed)throw new UnauthorizedAccessException("Список проектов задаёт администратор.");
            LocalPath(root);if(!Directory.Exists(root))throw new DirectoryNotFoundException();
            var all=Projects().Where(p=>!string.Equals(Full(root),Full(p.Root),StringComparison.OrdinalIgnoreCase)).ToList();
            all.Add(new ProjectRule{Root=Path.GetFullPath(root),Analysis=analyze,Training=train});
            Store.Write(Path.Combine(Store.Home,"pro","projects.json"),all);
        }
        static ProjectRule[] Matching(string file){
            LocalPath(file);var rules=Rules;
            if(rules.Managed){
                var matches=(rules.Projects??new ProjectRule[0]).Where(p=>p!=null&&Within(file,p.Root)).ToArray();
                if(matches.Length==0||matches.Any(p=>!Member(p)))throw new UnauthorizedAccessException("Нет доступа к этому проекту.");
                return matches;
            }
            return Projects().Where(p=>Within(file,p.Root)).ToArray();
        }
        public static void CheckFile(string file,bool analysis=false,bool training=false){
            var matches=Matching(file);var r=Rules;
            if(analysis){RequireAI();if(matches.Any(p=>!p.Analysis))throw new UnauthorizedAccessException("Для этого файла разрешён только редактор.");}
            if(training){RequireAI();if((r.Managed&&!r.Training)||matches.Any(p=>!p.Analysis||!p.Training))throw new UnauthorizedAccessException("Обучение на этом файле запрещено.");}
        }
        public static void CheckImport(string file){
            LocalPath(file);var r=Rules;
            if(r.Managed&&(!r.ImportedModels||!(r.ModelFolders??new string[0]).Any(dir=>Within(file,dir))))throw new UnauthorizedAccessException("Модель находится вне одобренной библиотеки.");
        }
        public static bool GlobalFile(string path){return !Rules.Managed&&!Projects().Any(p=>Within(path,p.Root));}
        public static bool CaptureAllowed {get{return !Rules.Managed&&!Projects().Any(p=>!p.Analysis);}}
        public static void Audit(string operation,string resource){
            string folder=Path.Combine(Store.Home,"pro");Directory.CreateDirectory(folder);
            string identity;using(var id=WindowsIdentity.GetCurrent())identity=id.User.Value;
            string key;using(var sha=System.Security.Cryptography.SHA256.Create())key=BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(resource))).Replace("-","");
            lock(auditLock)File.AppendAllText(Path.Combine(folder,"operations.log"),DateTime.UtcNow.ToString("o")+"\t"+identity+"\t"+operation+"\t"+key+Environment.NewLine);
        }
        static readonly object auditLock=new object();
    }
}
