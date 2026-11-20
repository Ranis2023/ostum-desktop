using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Sfera {
    internal static class LocalMusic {
        internal static ModelFile[] Files {get{return Store.MusicFiles;}}
        static readonly string[] binaries={"ace-synth.exe","ggml.dll","ggml-base.dll","ggml-cpu-x64.dll","ggml-cpu-haswell.dll","ggml-vulkan.dll"};
        static readonly Regex Intent=new Regex(@"^\s*(?:пожалуйста[, ]+)?(?:сгенерируй|создай|сочини|напиши)\s+(?:мне\s+)?(?:инструментальн\w*\s+|фонов\w*\s+|коротк\w*\s+)?(?:музыку|музыкальный трек|мелодию|инструментал|саундтрек|джингл|трек)\b",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
        internal static bool Requested(string text){return Intent.IsMatch(text??"");}
        internal static int Duration(string text){var m=Regex.Match(text,@"(?<!\d)(\d{1,4})\s*(?:сек(?:унд\w*)?|с)\b",RegexOptions.IgnoreCase);return m.Success?Math.Max(10,Math.Min(60,int.Parse(m.Groups[1].Value))):15;}
        internal static string Library(string models){return Path.Combine(models,Store.MusicDirectory);}
        internal static string Output {get{return Store.Audio;}}
        internal static async Task<string> Create(string models,string caption,int seconds,Action<string> status,CancellationToken ct){
            WorkPolicy.RequireAI();string library=Library(models);WorkPolicy.CheckImport(Path.Combine(library,Files[1].name));
            string previous=null;int last=-10;
            await Downloads.Prepare(Files,library,(s,p)=>{int n=(int)(p*100);if(s!=previous||Math.Abs(n-last)>=2){previous=s;last=n;status(s+" · "+n+"%");}},ct).ConfigureAwait(false);
            string id=Guid.NewGuid().ToString("N"),job=Path.Combine(library,"jobs",id);
            WorkPolicy.LocalPath(job);Directory.CreateDirectory(job);var wav=Path.Combine(job,"track0.wav");
            try{
                using(var stream=File.OpenRead(Files[0].PathIn(library)))using(var zip=new ZipArchive(stream,ZipArchiveMode.Read)){
                    foreach(string filename in binaries){
                        ct.ThrowIfCancellationRequested();var entry=zip.GetEntry(filename);
                        if(entry==null)throw new InvalidDataException("В пакете музыки отсутствует "+filename);
                        using(var source=entry.Open())using(var target=File.Create(Path.Combine(job,filename)))source.CopyTo(target);
                    }
                }
                var request=new {caption=Regex.Replace(caption??"","[\r\n]+"," "),lyrics="[Instrumental]",duration=seconds,seed=BitConverter.ToInt32(Guid.NewGuid().ToByteArray(),0)&int.MaxValue,inference_steps=8,shift=3.0};
                File.WriteAllText(Path.Combine(job,"track.json"),Store.Json().Serialize(request),new UTF8Encoding(false));
                string args="--request track.json --dit ../../"+Files[1].name+" --embedding ../../"+Files[2].name+" --vae ../../"+Files[3].name+" --wav --vae-chunk 64 --vae-overlap 16";
                status("Синтезирую "+seconds+" с музыки на этом компьютере");
                using(var group=new ProcessJob())using(var child=new Process{StartInfo=new ProcessStartInfo(Path.Combine(job,binaries[0]),args){WorkingDirectory=job,UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true}}){
                    ct.ThrowIfCancellationRequested();child.Start();group.Attach(child);var output=child.StandardOutput.ReadToEndAsync();var error=child.StandardError.ReadToEndAsync();
                    using(ct.Register(()=>{group.Dispose();Engine.Kill(child);})){await Task.Run(()=>child.WaitForExit()).ConfigureAwait(false);await Task.WhenAll(output,error).ConfigureAwait(false);}
                    ct.ThrowIfCancellationRequested();
                    if(child.ExitCode!=0||!File.Exists(wav))throw new IOException("Музыкальный движок завершился без записи WAV. "+ResearchText.Clip(error.Result,280));
                }
                using(var f=File.OpenRead(wav))using(var b=new BinaryReader(f)){
                    if(f.Length<1024||new string(b.ReadChars(4))!="RIFF")throw new InvalidDataException("Музыкальный движок вернул неполный WAV.");
                }
                ct.ThrowIfCancellationRequested();Directory.CreateDirectory(Output);string result=Path.Combine(Output,id+".wav");File.Move(wav,result);return result;
            }finally{
                foreach(var f in binaries.Concat(new[]{"track.json","track0.wav"}))try{File.Delete(Path.Combine(job,f));}catch(IOException){}
                try{Directory.Delete(job,false);}catch(IOException){}
            }
        }
    }
}
