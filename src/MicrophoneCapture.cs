using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Sfera {
    public sealed class MicrophoneInput {
        public string Id { get; set; }
        public string Name { get; set; }
        public bool IsDefault { get; set; }
    }

    public sealed class MicrophoneCapture : IDisposable {
        const int Rate=16000;
        const int MaxBytes=Rate*2*181;
        readonly object gate=new object();
        readonly MemoryStream pcm=new MemoryStream();
        MMDevice endpoint;
        WasapiCapture capture;
        int peak, audibleSamples;
        double currentLevel;
        Exception failure;

        public string Name { get; private set; }
        public double Level {get {lock(gate)return currentLevel;}}
        public Exception Failure {get {lock(gate)return failure;}}

        public static List<MicrophoneInput> Inputs() {
            var list=new List<MicrophoneInput>();
            using(var devices=new MMDeviceEnumerator()) {
                string defaultId=null;
                if(devices.HasDefaultAudioEndpoint(DataFlow.Capture,Role.Multimedia))
                    using(var current=devices.GetDefaultAudioEndpoint(DataFlow.Capture,Role.Multimedia))defaultId=current.ID;
                foreach(var item in devices.EnumerateAudioEndPoints(DataFlow.Capture,DeviceState.Active))
                    using(item)list.Add(new MicrophoneInput{Id=item.ID,Name=item.FriendlyName,IsDefault=item.ID==defaultId});
            }
            return list;
        }

        public static MicrophoneCapture Open(string id) {
            var mic=new MicrophoneCapture();
            // WASAPI raises its stop notification on the context that opened it.
            // Keep hardware notifications independent of the WPF dispatcher.
            try {Task.Run(()=>mic.Connect(id)).GetAwaiter().GetResult();return mic;}
            catch {mic.Dispose();throw;}
        }
        void Connect(string id) {
            using(var devices=new MMDeviceEnumerator()) {
                try {endpoint=string.IsNullOrEmpty(id)?devices.GetDefaultAudioEndpoint(DataFlow.Capture,Role.Multimedia):devices.GetDevice(id);}
                catch(Exception ex){throw new IOException("Микрофон недоступен. Подключите его или выберите другой вход рядом с кнопкой записи.",ex);}
            }
            if(endpoint.State!=DeviceState.Active)throw new IOException("Выбранный микрофон отключён. Подключите его или выберите другой вход.");
            Name=endpoint.FriendlyName;
            if(endpoint.AudioEndpointVolume.Mute)throw new IOException("В Windows выключен звук микрофона «"+Name+"». Включите его в параметрах звука.");
            try {
                capture=new WasapiCapture(endpoint,false,60);
                capture.WaveFormat=new WaveFormat(Rate,16,1);
                capture.DataAvailable+=(s,e)=>Append(e.Buffer,e.BytesRecorded);
                capture.RecordingStopped+=(s,e)=>{lock(gate)failure=e.Exception;};
                capture.StartRecording();
            } catch(UnauthorizedAccessException ex) {
                throw new IOException("Windows запретила доступ к микрофону. Разрешите его для классических приложений в разделе «Конфиденциальность и безопасность → Микрофон».",ex);
            }
        }

        void Append(byte[] bytes,int count) {
            if(count<2)return;
            lock(gate) {
                count=Math.Min(count&~1,MaxBytes-(int)pcm.Length);
                if(count<=0)return;
                pcm.Write(bytes,0,count);
                double energy=0;
                for(int i=0;i<count;i+=2) {
                    int sample=(short)(bytes[i]|(bytes[i+1]<<8));
                    peak=Math.Max(peak,Math.Abs(sample));energy+=(double)sample*sample;
                }
                double rms=Math.Sqrt(energy/(count/2));
                if(rms>=42)audibleSamples+=count/2;
                double db=20*Math.Log10(Math.Max(1,rms)/32768);
                currentLevel=Math.Max(0,Math.Min(1,(db+62)/54));
            }
        }
        void CloseDevice() {
            try {if(capture!=null){capture.Dispose();capture=null;}}
            finally {if(endpoint!=null){endpoint.Dispose();endpoint=null;}}
        }
        public string Finish() {
            CloseDevice();
            lock(gate) {
                if(failure!=null)throw new IOException("Прервалась запись с микрофона «"+Name+"». Проверьте подключение. "+failure.Message,failure);
                if(pcm.Length<Rate*2/5)throw new IOException("Запись слишком короткая. Нажмите микрофон, произнесите фразу и нажмите «Распознать».");
                if(peak<125||audibleSamples<Rate*.06)throw new IOException("Сигнал от «"+Name+"» почти не слышен. Проверьте уровень, кнопку выключения на микрофоне и выбранный вход.");
                var bytes=pcm.GetBuffer();int count=(int)pcm.Length;
                double gain=Math.Min(9,14000.0/peak);
                if(gain>1.05)for(int i=0;i<count;i+=2) {
                    int sample=(short)(bytes[i]|(bytes[i+1]<<8));
                    int value=(int)Math.Round(sample*gain);
                    bytes[i]=(byte)value;bytes[i+1]=(byte)(value>>8);
                }
                string path=Path.Combine(Path.GetTempPath(),"sfera-"+Guid.NewGuid().ToString("N")+".wav");
                try {
                    using(var wav=new WaveFileWriter(path,new WaveFormat(Rate,16,1))) {
                        wav.Write(bytes,0,count);
                        int tail=Math.Max(0,Rate*2*12/10-count);
                        if(tail>0)wav.Write(new byte[tail],0,tail);
                    }
                    return path;
                } catch {if(File.Exists(path))File.Delete(path);throw;}
            }
        }
        public void Dispose() {try{CloseDevice();}finally{pcm.Dispose();}}
    }
}
