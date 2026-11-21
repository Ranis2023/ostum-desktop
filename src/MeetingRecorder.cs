using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Sfera {
    // Write PCM directly to disk so a long meeting does not accumulate in RAM.
    internal sealed class MeetingRecorder : IDisposable {
        readonly object gate=new object();
        readonly ManualResetEventSlim stopped=new ManualResetEventSlim();
        string output,temporary;
        MMDevice endpoint;
        WasapiCapture capture;
        WaveFileWriter writer;
        bool started,preserveRecording;
        Exception failure;
        internal Exception Error {get{lock(gate)return failure;}}
        internal static MeetingRecorder Start(string output,string input){
            MeetingAudio.CheckSource(output);
            if(File.Exists(output)||!string.Equals(Path.GetExtension(output),".wav",StringComparison.OrdinalIgnoreCase))throw new IOException("Выберите новое имя WAV.");
            var recorder=new MeetingRecorder{output=output,temporary=Path.Combine(Path.GetDirectoryName(output),".ostum-record-"+Guid.NewGuid().ToString("N")+".wav")};
            try{Task.Run(()=>recorder.Connect(input)).GetAwaiter().GetResult();return recorder;}catch{recorder.Dispose();throw;}
        }
        void Connect(string input){
            using(var devices=new MMDeviceEnumerator())endpoint=string.IsNullOrEmpty(input)?devices.GetDefaultAudioEndpoint(DataFlow.Capture,Role.Multimedia):devices.GetDevice(input);
            if(endpoint.State!=DeviceState.Active||endpoint.AudioEndpointVolume.Mute)throw new IOException("Выбранный микрофон отключён или заглушён в Windows.");
            var format=new WaveFormat(16000,16,1);
            writer=new WaveFileWriter(new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None),format);
            capture=new WasapiCapture(endpoint,false,80){WaveFormat=format};
            capture.DataAvailable+=(s,e)=>{lock(gate){if(writer==null||failure!=null)return;try{
                if(writer.Length+e.BytesRecorded>4L*3600*32000)throw new IOException("Записано 4 часа. Завершите эту запись.");
                writer.Write(e.Buffer,0,e.BytesRecorded);
            }catch(Exception ex){failure=ex;}}};
            capture.RecordingStopped+=(s,e)=>{lock(gate){if(e.Exception!=null)failure=e.Exception;}stopped.Set();};
            capture.StartRecording();started=true;
        }
        void StopDevice(){
            if(capture!=null){
                if(started){capture.StopRecording();stopped.Wait(TimeSpan.FromSeconds(4));started=false;}
                capture.Dispose();capture=null;
            }
            lock(gate){if(writer!=null){writer.Dispose();writer=null;}}
            if(endpoint!=null){endpoint.Dispose();endpoint=null;}
        }
        internal string Finish(){
            preserveRecording=true;
            try{
                StopDevice();MeetingAudio.CheckSource(output);
                if(!File.Exists(temporary)||new FileInfo(temporary).Length<=46)throw new IOException("Микрофон не передал звук.");
                string target=output;
                if(File.Exists(target))target=Path.Combine(Path.GetDirectoryName(output),Path.GetFileNameWithoutExtension(output)+"-"+Guid.NewGuid().ToString("N").Substring(0,8)+".wav");
                File.Move(temporary,target);return target;
            }catch(Exception ex){throw new IOException(ex.Message+"\nЗапись можно восстановить из файла "+temporary,ex);}
        }
        public void Dispose(){
            try{StopDevice();}finally{stopped.Dispose();if(!preserveRecording&&temporary!=null&&File.Exists(temporary))File.Delete(temporary);}
        }
    }
}
