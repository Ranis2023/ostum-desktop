using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Speech.Synthesis;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Sfera {
    public sealed class Voice : IDisposable {
        readonly string binaries;
        SpeechSynthesizer speaker;
        Process fallback;
        readonly object speechGate=new object();
        System.Media.SoundPlayer playback;
        MicrophoneCapture mic;
        public bool Recording {get{return mic!=null;}}
        public string InputName {get{return mic==null?"":mic.Name;}}
        public double Level {get{return mic==null?0:mic.Level;}}
        public Exception RecordingError {get{return mic==null?null:mic.Failure;}}
        public Voice(string engineDir){binaries=engineDir;}
        public void Start(){Start(null);}
        public void Start(string inputId) {
            WorkPolicy.RequireAI();
            Discard();StopSpeech();mic=MicrophoneCapture.Open(inputId);
        }
        public void Discard(){var open=mic;mic=null;if(open!=null)open.Dispose();}
        public string Finish() {
            if(mic==null)throw new InvalidOperationException("Сначала включите микрофон.");
            try{return mic.Finish();}finally{Discard();}
        }
        public async Task<string> Recognize(string wav,string models,CancellationToken token) {
            WorkPolicy.RequireAI();
            string model=Store.Files["voice"].PathIn(models);
            if(!File.Exists(model))throw new FileNotFoundException("Модель диктовки отсутствует. Откройте «Скачать голосовую модель» в меню инструментов.");
            string dir=Path.Combine(binaries,"whisper-b5454"), exe=Path.Combine(dir,"whisper-cli.exe");
            string output=Path.Combine(Path.GetTempPath(),"sfera-"+Guid.NewGuid().ToString("N"));
            var args=new[]{"-m",model,"-f",wav,"-l","ru","-otxt","-of",output,"-nt","-t","5","--no-gpu"};
            var si=new ProcessStartInfo(exe,string.Join(" ",args.Select(Engine.Quote))){WorkingDirectory=dir,UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true};
            try {
                using(var process=Process.Start(si)){
                    var log=process.StandardError.ReadToEndAsync();var std=process.StandardOutput.ReadToEndAsync();
                    using(token.Register(()=>Engine.Kill(process)))await Task.Run(()=>process.WaitForExit()).ConfigureAwait(false);
                    await Task.WhenAll(log,std).ConfigureAwait(false);token.ThrowIfCancellationRequested();
                    if(process.ExitCode!=0||!File.Exists(output+".txt"))throw new IOException("Whisper не смог распознать запись. "+ResearchText.Clip(log.Result,180));
                    string words=Regex.Replace(File.ReadAllText(output+".txt",Encoding.UTF8),@"\s+"," ").Trim();
                    return Regex.IsMatch(words,@"^(?:\s*[\[(](?:blank_audio|silence|тишина|музыка|music|шум)[\])]\s*)+$",RegexOptions.IgnoreCase)?"":words;
                }
            } catch(Win32Exception ex){throw new IOException("Не удалось открыть локальный движок Whisper. "+ex.Message,ex);}
            finally{if(File.Exists(output+".txt"))File.Delete(output+".txt");}
        }
        public static SpeechSynthesizer RussianSpeaker() {
            var synth=new SpeechSynthesizer();
            var ru=synth.GetInstalledVoices().FirstOrDefault(v=>v.Enabled&&v.VoiceInfo.Culture.TwoLetterISOLanguageName=="ru");
            if(ru!=null)synth.SelectVoice(ru.VoiceInfo.Name);
            return synth;
        }
        public void Speak(string text) {
            StopSpeech();if(speaker==null)speaker=RussianSpeaker();
            string value=Regex.Replace(text??"", @"\x60{3}[\s\S]*?\x60{3}|\*\*|#{1,5} |\[\d+\]", " ");
            if(string.IsNullOrWhiteSpace(value))return;
            if(speaker.GetInstalledVoices().Any(v=>v.Enabled&&v.VoiceInfo.Culture.TwoLetterISOLanguageName=="ru")){
                speaker.SetOutputToDefaultAudioDevice();speaker.SpeakAsync(value);return;
            }
            string root=Path.Combine(ProTools.Root(),"speech");
            var info=new ProcessStartInfo(Path.Combine(root,"espeak-ng.exe"),"--path=. -b 1 -v ru --stdout --stdin"){WorkingDirectory=root,UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true};
            info.EnvironmentVariables["ESPEAK_DATA_PATH"]="espeak-ng-data";
            var process=Process.Start(info);lock(speechGate)fallback=process;
            Task.Run(async()=>{
                try{using(var wave=new MemoryStream()){
                    var read=process.StandardOutput.BaseStream.CopyToAsync(wave);var b=Encoding.UTF8.GetBytes(value);
                    await process.StandardInput.BaseStream.WriteAsync(b,0,b.Length);process.StandardInput.Close();await read;
                    if(wave.Length<44)return;
                    // eSpeak streams a PCM header with placeholder lengths when stdout is a pipe.
                    byte[] header=wave.GetBuffer();
                    if(Encoding.ASCII.GetString(header,0,4)!="RIFF"||Encoding.ASCII.GetString(header,36,4)!="data")return;
                    Buffer.BlockCopy(BitConverter.GetBytes(checked((int)wave.Length-8)),0,header,4,4);
                    Buffer.BlockCopy(BitConverter.GetBytes(checked((int)wave.Length-44)),0,header,40,4);wave.Position=0;
                    using(var sound=new System.Media.SoundPlayer(wave)){
                        lock(speechGate){if(fallback!=process)return;playback=sound;}
                        try{sound.PlaySync();}finally{lock(speechGate){if(playback==sound)playback=null;}}
                    }
                }}
                catch(IOException){}catch(InvalidOperationException){}
                finally{lock(speechGate){if(fallback==process)fallback=null;}process.Dispose();}
            });
        }
        public void StopSpeech(){if(speaker!=null)speaker.SpeakAsyncCancelAll();lock(speechGate){if(playback!=null)playback.Stop();if(fallback!=null){Engine.Kill(fallback);fallback=null;}}}
        public void Dispose(){Discard();StopSpeech();if(speaker!=null){speaker.Dispose();speaker=null;}}
    }
}
