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
        MicrophoneCapture mic;
        public bool Recording {get{return mic!=null;}}
        public string InputName {get{return mic==null?"":mic.Name;}}
        public double Level {get{return mic==null?0:mic.Level;}}
        public Exception RecordingError {get{return mic==null?null:mic.Failure;}}
        public Voice(string engineDir){binaries=engineDir;}
        public void Start(){Start(null);}
        public void Start(string inputId) {
            Discard();StopSpeech();mic=MicrophoneCapture.Open(inputId);
        }
        public void Discard(){var open=mic;mic=null;if(open!=null)open.Dispose();}
        public string Finish() {
            if(mic==null)throw new InvalidOperationException("Сначала включите микрофон.");
            try{return mic.Finish();}finally{Discard();}
        }
        public async Task<string> Recognize(string wav,string models,CancellationToken token) {
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
            StopSpeech(); if(speaker==null)speaker=RussianSpeaker();
            string value=Regex.Replace(text??"", @"\x60{3}[\s\S]*?\x60{3}|\*\*|#{1,5} |\[\d+\]", " ");
            if(!string.IsNullOrWhiteSpace(value)){speaker.SetOutputToDefaultAudioDevice();speaker.SpeakAsync(value);}
        }
        public void StopSpeech(){if(speaker!=null)speaker.SpeakAsyncCancelAll();}
        public void Dispose(){Discard();if(speaker!=null){StopSpeech();speaker.Dispose();speaker=null;}}
    }
}
