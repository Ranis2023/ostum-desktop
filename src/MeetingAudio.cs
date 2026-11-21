using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Dsp;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Sfera {
    internal sealed class MeetingLine {
        public int Id {get;set;}
        public int Start {get;set;}
        public int End {get;set;}
        public string Time {get{return TimeSpan.FromMilliseconds(Start).ToString(@"hh\:mm\:ss");}}
        public string Speaker {get;set;}
        public string Text {get;set;}
        internal double[] Voice;
        internal int Channel;
    }
    internal static class MeetingAudio {
        const int Rate=16000;
        internal const string Unknown="Уточните участника";
        sealed class Output {public Segment[] transcription {get;set;}}
        sealed class Segment {public Times offsets {get;set;} public string text {get;set;}}
        sealed class Times {public int from {get;set;} public int to {get;set;}}
        internal static void CheckSource(string file){
            WorkPolicy.CheckFile(file,true);
            if(!WorkPolicy.GlobalFile(file)&&!WorkPolicy.CanAnalyzeDocument(file))throw new UnauthorizedAccessException("Для записи требуется разрешение на локальный анализ этой папки.");
        }
        internal static void CheckDestination(string source,string dest){
            CheckSource(source);WorkPolicy.CheckFile(dest);
            if(WorkPolicy.Projects().Any(p=>WorkPolicy.Within(source,p.Root)!=WorkPolicy.Within(dest,p.Root)))
                throw new UnauthorizedAccessException("Сохраните протокол в папке с теми же правами доступа, что и запись.");
            if(string.Equals(Path.GetFullPath(source),Path.GetFullPath(dest),StringComparison.OrdinalIgnoreCase))throw new IOException("Выберите новое имя протокола.");
        }
        static WaveStream Open(string file){
            if(string.Equals(Path.GetExtension(file),".wav",StringComparison.OrdinalIgnoreCase)){
                var wav=new WaveFileReader(file);
                if(wav.WaveFormat.Encoding==WaveFormatEncoding.Pcm||wav.WaveFormat.Encoding==WaveFormatEncoding.IeeeFloat)return wav;
                wav.Dispose();
            }
            return new MediaFoundationReader(file);
        }
        // Choose a quiet edge near the end of a long block. Carry the remaining samples into the next block.
        static int QuietEnd(float[] buffer,int channels){
            int step=Rate/25,best=buffer.Length/channels;double lowest=double.MaxValue;
            for(int frame=best-Rate*8;frame+step<=buffer.Length/channels;frame+=step){
                double power=0;for(int i=frame*channels;i<(frame+step)*channels;i+=4)power+=buffer[i]*buffer[i];
                if(power<lowest){lowest=power;best=frame+step/2;}
            }
            return best*channels;
        }
        static bool IndependentChannels(float[] data,int count){
            int left=0,right=0;
            for(int i=0;i+640<=count;i+=640){
                double a=0,b=0;for(int j=i;j<i+640;j+=2){a+=data[j]*data[j];b+=data[j+1]*data[j+1];}
                if(a>.012&&a>8*b)left++;if(b>.012&&b>8*a)right++;
            }
            return left>=15&&right>=15;
        }
        static void WriteBlock(string path,float[] data,int count,int channels,int track){
            int stride=track==0?1:channels,first=track==0?0:track-1;
            using(var writer=new WaveFileWriter(path,new WaveFormat(Rate,16,track==0?channels:1))){
                byte[] bytes=new byte[16384];
                for(int pos=0;pos<count/stride;){
                    int take=Math.Min(bytes.Length/2,count/stride-pos);
                    for(int j=0;j<take;j++){int value=(int)(Math.Max(-1,Math.Min(1,data[(pos+j)*stride+first]))*32767);bytes[2*j]=(byte)value;bytes[2*j+1]=(byte)(value>>8);}
                    writer.Write(bytes,0,take*2);pos+=take;
                }
            }
        }
        static bool SpeechBounds(float[] data,int track,ref int begin,ref int end){
            int first=-1,last=0;
            for(int i=begin;i<end;i+=320){
                int stop=Math.Min(end,i+320);double energy=0;
                for(int j=i;j<stop;j++){float v=data[2*j+track-1];energy+=v*v;}
                if(energy/Math.Max(1,stop-i)<.000015)continue;
                if(first<0)first=i;last=stop;
            }
            if(first<0)return false;begin=Math.Max(begin,first-800);end=Math.Min(end,last+800);return true;
        }
        static async Task<Segment[]> Decode(string engineDir,string model,string file,string prefix,CancellationToken token){
            string dir=Path.Combine(engineDir,"whisper-b5454");
            var args=new[]{"-m",model,"-f",file,"-l","ru","-oj","-of",prefix,"-ml","90","-sow","-t","5","--no-gpu"};
            var start=new ProcessStartInfo(Path.Combine(dir,"whisper-cli.exe"),string.Join(" ",args.Select(Engine.Quote))){
                WorkingDirectory=dir,UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true};
            using(var job=new ProcessJob())using(var child=new Process{StartInfo=start}){
                child.Start();job.Attach(child);child.BeginErrorReadLine();child.BeginOutputReadLine();
                using(token.Register(()=>Engine.Kill(child)))await Task.Run(()=>child.WaitForExit()).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if(child.ExitCode!=0||!File.Exists(prefix+".json"))throw new IOException("Локальный Whisper не смог обработать аудиозапись.");
            }
            var decoded=Store.Json().Deserialize<Output>(File.ReadAllText(prefix+".json",Encoding.UTF8));
            if(decoded==null||decoded.transcription==null)throw new InvalidDataException("Не удалось прочитать таймкоды Whisper.");
            return decoded.transcription;
        }
        internal static List<MeetingLine> Transcribe(string source,string engines,string models,int speakers,Action<string> progress,CancellationToken token){
            CheckSource(source);string model=Store.Files["voice"].PathIn(models);
            if(!File.Exists(model))throw new IOException("Подключите диктовку в настройках модели.");
            string temp=Path.Combine(Path.GetTempPath(),"ostum-meeting-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);string wave=Path.Combine(temp,"block.wav"),prefix=Path.Combine(temp,"words");
            var lines=new List<MeetingLine>();
            try{using(var stream=Open(source)){
                int channels=stream.WaveFormat.Channels;if(channels<1||channels>2)throw new IOException("Выберите моно- или стереозапись.");
                if(stream.TotalTime.TotalHours>4)throw new IOException("Длительность одной записи до 4 часов. Выберите часть совещания.");
                ISampleProvider samples=stream.ToSampleProvider();
                if(samples.WaveFormat.SampleRate!=Rate)samples=new WdlResamplingSampleProvider(samples,Rate);
                var block=new float[Rate*channels*120];int carry=0;long frames=0;
                while(true){
                    token.ThrowIfCancellationRequested();CheckSource(source);int size=carry;
                    while(size<block.Length){token.ThrowIfCancellationRequested();int n=samples.Read(block,size,block.Length-size);if(n==0)break;size+=n;}
                    if(size==0)break;
                    int used=size==block.Length?QuietEnd(block,channels):size;
                    if(frames+(used/channels)>4L*3600*Rate)throw new IOException("Достигнут предел записи в 4 часа.");
                    int offset=(int)(frames*1000/Rate);progress("Распознаю с "+TimeSpan.FromMilliseconds(offset).ToString(@"hh\:mm\:ss")+" из "+stream.TotalTime.ToString(@"hh\:mm\:ss"));
                    var tracks=channels==2&&IndependentChannels(block,used)?new[]{1,2}:new[]{0};
                    foreach(int track in tracks){
                        WriteBlock(wave,block,used,channels,track);
                        var segments=Decode(engines,model,wave,prefix,token).GetAwaiter().GetResult();
                        foreach(var seg in segments){
                            string text=(seg.text??"").Trim();
                            if(seg.offsets==null||text.Length==0||Regex.IsMatch(text,@"^[\[\(](?:blank_audio|тишина|silence|музыка|music|шум)[\]\)]$",RegexOptions.IgnoreCase))continue;
                            int begin=Math.Max(0,Math.Min(used/channels,seg.offsets.from*16)),end=Math.Max(begin,Math.Min(used/channels,seg.offsets.to*16));
                            if(end<=begin||(track!=0&&!SpeechBounds(block,track,ref begin,ref end)))continue;
                            var line=new MeetingLine{Start=offset+begin/16,End=offset+end/16,Text=text,Speaker=Unknown};
                            line.Voice=VoicePrint(block,channels,begin,end,out line.Channel);if(track!=0)line.Channel=track;lines.Add(line);
                        }
                        File.Delete(prefix+".json");File.Delete(wave);
                    }
                    frames+=used/channels;carry=size-used;if(carry>0)Array.Copy(block,used,block,0,carry);
                }
            }
            lines=lines.OrderBy(l=>l.Start).ThenBy(l=>l.Channel).ToList();for(int i=0;i<lines.Count;i++)lines[i].Id=i+1;
            LabelSpeakers(lines,speakers);return lines;}
            finally{try{File.Delete(prefix+".json");File.Delete(wave);Directory.Delete(temp);}catch(IOException){}catch(UnauthorizedAccessException){}}
        }
        // A sparse log spectrum describes timbre. Ignore quiet/noisy windows and normalize out input gain.
        static double[] VoicePrint(float[] data,int channels,int begin,int end,out int channel){
            channel=0;double left=0,right=0;
            if(channels==2){for(int i=begin;i<end;i+=3){left+=data[i*2]*data[i*2];right+=data[i*2+1]*data[i*2+1];}
                if(Math.Max(left,right)>.001&&Math.Max(left,right)>4*Math.Min(left,right))channel=left>right?1:2;}
            int[] edges={3,6,10,15,22,32,45,63,88,121,162,213};double[] sum=new double[edges.Length-1];
            var fft=new Complex[512];var bands=new double[sum.Length];int count=0;
            for(int pos=begin;pos+512<=end;pos+=Math.Max(1600,(end-begin)/50)){
                double energy=0;
                for(int j=0;j<512;j++){float v=data[(pos+j)*channels];if(channels==2)v=(v+data[(pos+j)*channels+1])*.5f;
                    energy+=v*v;fft[j].X=(float)(v*FastFourierTransform.HammingWindow(j,512));fft[j].Y=0;}
                if(energy/512<.000025)continue;
                FastFourierTransform.FFT(true,9,fft);
                for(int k=0;k<bands.Length;k++){double power=0;for(int j=edges[k];j<edges[k+1];j++)power+=fft[j].X*fft[j].X+fft[j].Y*fft[j].Y;
                    bands[k]=Math.Log(1e-12+power/(edges[k+1]-edges[k]));}
                double mean=bands.Average();for(int k=0;k<sum.Length;k++)sum[k]+=bands[k]-mean;count++;
            }
            if(count<3)return null;for(int k=0;k<sum.Length;k++)sum[k]/=count;return sum;
        }
        static double Distance(double[] a,double[] b){double sum=0;for(int i=0;i<a.Length;i++){double d=a[i]-b[i];sum+=d*d;}return Math.Sqrt(sum/a.Length);}
        internal static void LabelSpeakers(List<MeetingLine> lines,int requested){
            foreach(var l in lines)l.Speaker=Unknown;
            if(requested==1){foreach(var l in lines)l.Speaker="Участник 1";return;}
            if(lines.Any(l=>l.Channel==1)&&lines.Any(l=>l.Channel==2)){
                foreach(var l in lines)if(l.Channel!=0)l.Speaker="Канал "+l.Channel;return;
            }
            // Mono groups are suggestions. A single microphone does not establish a person's identity.
            var valid=lines.Where(l=>l.Voice!=null).ToArray();if(valid.Length<2)return;
            var centers=new List<double[]>{(double[])valid[0].Voice.Clone()};
            int limit=requested>1?Math.Min(6,requested):Math.Min(4,valid.Length/3);
            while(centers.Count<limit){
                var next=valid.OrderByDescending(l=>centers.Min(c=>Distance(l.Voice,c))).First();
                if(centers.Min(c=>Distance(next.Voice,c))<(requested>1?.3:1.45))break;centers.Add((double[])next.Voice.Clone());
            }
            if(centers.Count<2)return;
            Func<MeetingLine,int> closest=l=>Enumerable.Range(0,centers.Count).OrderBy(i=>Distance(l.Voice,centers[i])).First();
            for(int pass=0;pass<6;pass++){
                var groups=valid.GroupBy(closest).ToArray();
                foreach(var group in groups){var members=group.ToArray();for(int k=0;k<centers[group.Key].Length;k++)centers[group.Key][k]=members.Average(l=>l.Voice[k]);}
            }
            var ordered=new List<int>();
            foreach(var l in valid){
                int i=closest(l);double d=Distance(l.Voice,centers[i]),other=centers.Where((c,j)=>j!=i).Min(c=>Distance(l.Voice,c));
                if(other-d<.18||d>2.8)continue;if(!ordered.Contains(i))ordered.Add(i);l.Speaker="Голос "+(ordered.IndexOf(i)+1)+" (?)";
            }
        }
    }
}
