using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace Sfera {
    public sealed class TrainingPair {
        public string prompt{get;set;} public string answer{get;set;} public string group{get;set;}
    }
    public sealed class AdapterVersion{
        public string Id{get;set;} public string Created{get;set;} public string Owner{get;set;} public string BaseHash{get;set;}
        public string Source{get;set;} public string Root{get;set;} public bool Revoked{get;set;}
        public override string ToString(){return Created+(Revoked?" · отозван":"");}
    }
    public sealed class TrainingReport{
        public int training{get;set;}public int held_out{get;set;}public int rank{get;set;}
        public double baseline{get;set;}public double adapted{get;set;}public int regressions{get;set;}public int verbatim{get;set;}
        public EvaluationCase[] cases{get;set;}
    }
    public sealed class EvaluationCase{
        public string prompt{get;set;} public string expected{get;set;} public string @base{get;set;} public string trained{get;set;}
    }
    public static class ProjectTraining{
        static string Sid {get{using(var id=WindowsIdentity.GetCurrent())return id.User.Value;}}
        static string Home(ProjectRule p){return ProTools.UnderProject(p,Path.Combine(p.Root,".ostum-training",Sid),false,true);}
        static string Dir(ProjectRule p,string id){
            Guid guid;if(!Guid.TryParseExact(id,"N",out guid))throw new IOException("Неверная версия.");
            return ProTools.UnderProject(p,Path.Combine(Home(p),id),false,true);
        }
        static string Manifest(ProjectRule p,string id){return ProTools.UnderProject(p,Path.Combine(Dir(p,id),"version.json"),false,true);}
        public static List<AdapterVersion> Versions(ProjectRule p){
            string dir=Home(p);var list=new List<AdapterVersion>();if(!Directory.Exists(dir))return list;
            foreach(var path in Directory.EnumerateDirectories(dir)){
                Guid n;if(!Guid.TryParseExact(Path.GetFileName(path),"N",out n))continue;
                string meta=Manifest(p,n.ToString("N"));if(!File.Exists(meta))continue;
                var v=Store.Json().Deserialize<AdapterVersion>(File.ReadAllText(meta));
                if(v!=null&&v.Id==n.ToString("N")&&v.Owner==Sid&&string.Equals(v.Root,p.Root,StringComparison.OrdinalIgnoreCase))list.Add(v);
            }
            return list.OrderByDescending(v=>v.Created).ToList();
        }
        public static TrainingReport Report(ProjectRule p,AdapterVersion v){return Store.Json().Deserialize<TrainingReport>(File.ReadAllText(ProTools.UnderProject(p,Path.Combine(Dir(p,v.Id),"evaluation.json"),false,true)));}
        public static string Active(ProjectRule p){
            string pointer=ProTools.UnderProject(p,Path.Combine(Home(p),"active.json"),false,true);
            return File.Exists(pointer)?Store.Json().Deserialize<string>(File.ReadAllText(pointer)):null;
        }
        public static string Resolve(Preferences prefs){
            if(string.IsNullOrEmpty(prefs.ProjectScope)||Store.Chat(prefs).Id!="quality")return null;
            var p=WorkPolicy.Projects().FirstOrDefault(row=>string.Equals(row.Root,prefs.ProjectScope,StringComparison.OrdinalIgnoreCase));
            if(p==null||!p.Training)return null;
            try{
                var selected=Active(p);if(string.IsNullOrEmpty(selected))return null;
                var file=Manifest(p,selected);if(!File.Exists(file))return null;
                var v=Store.Json().Deserialize<AdapterVersion>(File.ReadAllText(file));
                if(v==null||v.Revoked||v.Owner!=Sid||v.Id!=selected||!string.Equals(p.Root,v.Root,StringComparison.OrdinalIgnoreCase)||v.BaseHash!=Store.Files["q9"].sha256)return null;
                if(!string.IsNullOrEmpty(v.Source))ProTools.UnderProject(p,v.Source,false,true);
                string adapter=ProTools.UnderProject(p,Path.Combine(Dir(p,selected),"adapter.gguf"),true,true);
                return File.Exists(adapter)?adapter:null;
            }catch(UnauthorizedAccessException){return null;}
        }
        public static void Select(ProjectRule p,AdapterVersion v){
            if(v!=null){
                var current=Versions(p).Single(x=>x.Id==v.Id);
                if(current.Revoked||current.BaseHash!=Store.Files["q9"].sha256)throw new IOException("Версия отозвана или относится к другой модели.");
                if(!string.IsNullOrEmpty(current.Source))ProTools.UnderProject(p,current.Source,false,true);
                Report(p,current);
            }
            Store.Write(ProTools.UnderProject(p,Path.Combine(Home(p),"active.json"),false,true),v==null?"":v.Id);
            WorkPolicy.Audit(v==null?"adapter.disable":"adapter.activate",p.Root);
        }
        public static void Revoke(ProjectRule p,AdapterVersion v){
            var current=Versions(p).Single(x=>x.Id==v.Id);current.Revoked=true;
            Store.Write(Manifest(p,v.Id),current);if(Active(p)==v.Id)Select(p,null);WorkPolicy.Audit("adapter.revoke",p.Root);
        }
        public static async Task Train(ProjectRule p,Preferences prefs,List<TrainingPair> rows,string source,Action<string> progress,CancellationToken ct){
            if(rows.Count<5||rows.Count>200||rows.Any(r=>string.IsNullOrWhiteSpace(r.prompt)||string.IsNullOrWhiteSpace(r.answer)||r.prompt.Length>3500||r.answer.Length>2500))
                throw new InvalidDataException("Подготовьте от 5 до 200 заполненных пар. Пределы — 3500 знаков для вопроса и 2500 для ответа.");
            if(!string.IsNullOrEmpty(source))ProTools.UnderProject(p,source,false,true);
            string id=Guid.NewGuid().ToString("N"),directory=Dir(p,id);
            string weight=Store.Files["q9"].PathIn(prefs.Models);
            await Task.Run(()=>Downloads.Verify(weight,Store.Files["q9"],ct),ct).ConfigureAwait(false);
            Directory.CreateDirectory(directory);
            string data=Path.Combine(directory,"approved.json");
            try{
                Store.Write(data,rows);
                var args=new[]{"--model",weight,"--engine",Path.Combine(AppDomain.CurrentDomain.BaseDirectory,@"engines\llama-b12547"),"--data",data,"--out",directory};
                await ProTools.Python(@"training\studio.py",args,null,ct,progress).ConfigureAwait(false);
                ProTools.UnderProject(p,directory,false,true);ct.ThrowIfCancellationRequested();
                if(!File.Exists(Path.Combine(directory,"evaluation.json"))||!File.Exists(Path.Combine(directory,"adapter.gguf")))throw new IOException("Обучение не завершилось.");
                Store.Write(Manifest(p,id),new AdapterVersion{Id=id,Created=DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),Owner=Sid,Root=p.Root,Source=source,BaseHash=Store.Files["q9"].sha256});
                WorkPolicy.Audit("training.complete",p.Root);
            }finally{
                foreach(string name in new[]{"approved.json","features.log","evaluate.log","candidate.gguf"}){
                    string tmp=Path.Combine(directory,name);if(File.Exists(tmp))File.Delete(tmp);
                }
                if(!File.Exists(Path.Combine(directory,"version.json"))){foreach(string name in new[]{"adapter.gguf","evaluation.json"}){string tmp=Path.Combine(directory,name);if(File.Exists(tmp))File.Delete(tmp);}if(!Directory.EnumerateFileSystemEntries(directory).Any())Directory.Delete(directory);}
            }
        }
    }
}
