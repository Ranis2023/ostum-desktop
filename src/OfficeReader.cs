using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Sfera {
    // Keep one interpreter warm for interactive reads. A simultaneous reader keeps its own process.
    internal sealed class OfficeReader : IDisposable {
        static readonly object sync=new object();
        static OfficeReader idle;
        static bool leased;
        static Timer expiry;
        static int generation;
        readonly string projectRoot;
        readonly Process child;
        readonly ProcessJob job=new ProcessJob();

        static OfficeReader(){AppDomain.CurrentDomain.ProcessExit+=(s,e)=>ReleaseIdle(null);}
        OfficeReader(string root,string project){
            projectRoot=project;
            var info=new ProcessStartInfo(Path.Combine(root,@"python\python.exe"),
                "-B -X utf8 -u "+Engine.Quote(Path.Combine(root,"office.py"))+" --reader"){
                WorkingDirectory=root,CreateNoWindow=true,UseShellExecute=false,
                RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,
                StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
            child=new Process{StartInfo=info};
            try{child.Start();job.Attach(child);child.BeginErrorReadLine();}
            catch{Dispose();throw;}
        }
        static void CancelExpiry(){
            generation++;
            if(expiry!=null){expiry.Dispose();expiry=null;}
        }
        static void ReleaseIdle(int? ticket){
            OfficeReader retired;
            lock(sync){
                if(ticket.HasValue&&ticket.Value!=generation)return;
                CancelExpiry();retired=idle;idle=null;
            }
            if(retired!=null)retired.Dispose();
        }
        async Task<string> Exchange(string request,CancellationToken ct){
            using(ct.Register(()=>Engine.Kill(child))){
                var bytes=Encoding.UTF8.GetBytes(request+"\n");
                var pipe=child.StandardInput.BaseStream;
                await pipe.WriteAsync(bytes,0,bytes.Length,ct).ConfigureAwait(false);
                await pipe.FlushAsync(ct).ConfigureAwait(false);
                string line=await child.StandardOutput.ReadLineAsync().ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if(line==null)throw new EndOfStreamException("Редактор завершил работу до ответа.");
                return line;
            }
        }
        internal static async Task<string> Read(ProjectRule project,string request,CancellationToken ct){
            ct.ThrowIfCancellationRequested();
            string key=WorkPolicy.Full(project.Root);
            OfficeReader reader=null;bool owns;
            lock(sync){
                owns=!leased;
                if(owns){
                    leased=true;CancelExpiry();reader=idle;idle=null;
                }
            }
            if(owns){
                bool healthy=false;
                try{
                    if(reader!=null&&!string.Equals(reader.projectRoot,key,StringComparison.OrdinalIgnoreCase)){reader.Dispose();reader=null;}
                    if(reader==null){
                        var root=await Task.Run((Func<string>)ProTools.Root,ct).ConfigureAwait(false);
                        reader=new OfficeReader(root,key);
                    }
                    string line=await reader.Exchange(request,ct).ConfigureAwait(false);
                    healthy=true;return line;
                }catch(IOException){ct.ThrowIfCancellationRequested();} // A closed pipe is safe to retry for these reads.
                finally{
                    lock(sync){
                        leased=false;
                        if(healthy){
                            idle=reader;int ticket=++generation;
                            expiry=new Timer(_=>ReleaseIdle(ticket),null,45000,Timeout.Infinite);
                        }
                    }
                    if(!healthy&&reader!=null)reader.Dispose();
                }
            }
            return await ProTools.Python("office.py",null,request,ct).ConfigureAwait(false);
        }
        public void Dispose(){
            Engine.Kill(child);job.Dispose();child.Dispose();
        }
    }
}
