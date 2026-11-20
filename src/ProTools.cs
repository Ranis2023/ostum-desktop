using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Sfera {
    public static class ProTools {
        static readonly object gate=new object();
        static string ready;
        public static string Root() {
            lock(gate){
                if(ready!=null)return ready;
                string archive=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"OstumTools.zip");
                string sha=File.ReadAllText(archive+".sha256").Trim().ToUpperInvariant();
                if(!System.Text.RegularExpressions.Regex.IsMatch(sha,"^[0-9A-F]{64}$"))throw new InvalidDataException("Неверный каталог компонентов.");
                string parent=Path.Combine(Store.Home,"runtime"),dir=Path.Combine(parent,sha);
                if(File.Exists(Path.Combine(dir,"ready"))){ready=dir;return ready;}
                Directory.CreateDirectory(parent);
                string temp=Directory.CreateDirectory(Path.Combine(parent,Guid.NewGuid().ToString("N"))).FullName;
                try{
                    using(var stream=File.OpenRead(archive))using(var h=SHA256.Create()){
                        if(BitConverter.ToString(h.ComputeHash(stream)).Replace("-","")!=sha)throw new InvalidDataException("Переустановите компоненты OSTUM. Контрольная сумма не совпала.");
                        stream.Position=0;
                        using(var zip=new ZipArchive(stream,ZipArchiveMode.Read)){
                            foreach(var entry in zip.Entries){
                                string target=Path.GetFullPath(Path.Combine(temp,entry.FullName));
                                if(!WorkPolicy.Within(target,temp))throw new InvalidDataException("Неверный путь в комплекте.");
                                if(entry.Name.Length==0){Directory.CreateDirectory(target);continue;}
                                Directory.CreateDirectory(Path.GetDirectoryName(target));
                                using(var src=entry.Open())using(var dst=File.Create(target))src.CopyTo(dst);
                            }
                        }
                    }
                    File.WriteAllText(Path.Combine(temp,"ready"),sha);
                    try{Directory.Move(temp,dir);}catch(IOException){if(!File.Exists(Path.Combine(dir,"ready")))throw;}
                }finally{if(Directory.Exists(temp))Directory.Delete(temp,true);}
                return ready=dir;
            }
        }
        public static string UnderProject(ProjectRule project,string path,bool analysis=false,bool training=false){
            if(project==null||!WorkPolicy.Projects().Any(p=>string.Equals(WorkPolicy.Full(p.Root),WorkPolicy.Full(project.Root),StringComparison.OrdinalIgnoreCase))||!WorkPolicy.Within(path,project.Root))throw new UnauthorizedAccessException("Выберите файл активного рабочего пространства.");
            WorkPolicy.CheckFile(path,analysis,training);return Path.GetFullPath(path);
        }
        public static async Task<string> Python(string script,string[] args,string input,CancellationToken ct,Action<string> progress=null){
            string root=await Task.Run((Func<string>)Root,ct).ConfigureAwait(false);
            string exe=Path.Combine(root,@"python\python.exe");
            var options=new[]{"-B","-X","utf8",Path.Combine(root,script)}.Concat(args??new string[0]);
            var info=new ProcessStartInfo(exe,string.Join(" ",options.Select(Engine.Quote))){WorkingDirectory=root,CreateNoWindow=true,UseShellExecute=false,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
            using(var job=new ProcessJob())using(var child=new Process{StartInfo=info}){
                var output=new StringBuilder();
                child.OutputDataReceived+=(s,e)=>{if(e.Data!=null){if(progress!=null)progress(e.Data);else lock(output)output.AppendLine(e.Data);}};
                child.Start();job.Attach(child);child.BeginOutputReadLine();
                var error=child.StandardError.ReadToEndAsync();
                using(ct.Register(()=>{job.Dispose();Engine.Kill(child);})){
                    if(input!=null){var bytes=Encoding.UTF8.GetBytes(input);await child.StandardInput.BaseStream.WriteAsync(bytes,0,bytes.Length,ct).ConfigureAwait(false);}
                    child.StandardInput.Close();await Task.Run(()=>child.WaitForExit()).ConfigureAwait(false);await error.ConfigureAwait(false);ct.ThrowIfCancellationRequested();
                    if(child.ExitCode!=0&&output.Length==0)throw new IOException(ResearchText.Clip(error.Result,1200));
                    return output.ToString();
                }
            }
        }
        public static async Task<OfficeResult> Office(ProjectRule project,string path,string action,Dictionary<string,object> args,CancellationToken ct){
            UnderProject(project,path);WorkPolicy.LocalPath(Path.Combine(Path.GetDirectoryName(path),".ostum-versions"));
            var req=args??new Dictionary<string,object>();req["action"]=action;req["path"]=path;
            if(req.ContainsKey("other"))UnderProject(project,(string)req["other"]);
            if(req.ContainsKey("sources"))foreach(var s in (string[])req["sources"])UnderProject(project,s);
            var result=Store.Json().Deserialize<OfficeResult>(await Python("office.py",null,Store.Json().Serialize(req),ct).ConfigureAwait(false));
            if(result==null||result.error!=null)throw new IOException(result==null?"Редактор не ответил.":result.error);
            if(action!="read"&&action!="versions")WorkPolicy.Audit(action,path);
            return result;
        }
    }
    public sealed class OfficeRow {
        public string key{get;set;} public string value{get;set;} public string display{get;set;}
        public string style{get;set;} public string comment{get;set;}
        public int table{get;set;}public int tableRow{get;set;}public int tableColumn{get;set;}
        public bool bold{get;set;}public bool italic{get;set;}
        public InlineText[] runs{get;set;}
    }
    public sealed class InlineText{public string value{get;set;}public bool bold{get;set;}public bool italic{get;set;}}
    public sealed class OfficePatch {
        public bool Selected{get;set;} public string key{get;set;} public string old{get;set;}
        public string value{get;set;} public string operation{get;set;} public string Source{get;set;}
    }
    public sealed class OfficeResult {
        public string error{get;set;} public string kind{get;set;} public string sheet{get;set;} public string[] sheets{get;set;}
        public string revision{get;set;} public bool readOnly{get;set;} public OfficeRow[] rows{get;set;}
        public OfficePatch[] patches{get;set;} public string[] versions{get;set;}
    }
}
