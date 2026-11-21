using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Sfera {
    public sealed class ScanLine {public string Text{get;set;}public double[] Box{get;set;}}
    public sealed class ScanPage {public int Number{get;set;}public string Language{get;set;}public string Picture{get;set;}public ScanLine[] Lines{get;set;}}
    internal static class LocalOcr {
        internal static ScanPage[] Read(string file,List<int> pages,bool preview,CancellationToken ct){
            string script=Convert.ToBase64String(Encoding.Unicode.GetBytes(Store.Resource("ScanPages.ps1")));
            var start=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,@"WindowsPowerShell\v1.0\powershell.exe"),"-NoProfile -NonInteractive -EncodedCommand "+script){
                UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,StandardOutputEncoding=Encoding.UTF8,RedirectStandardError=true,StandardErrorEncoding=Encoding.UTF8};
            start.EnvironmentVariables["OSTUM_SCAN_SOURCE"]=Path.GetFullPath(file);
            start.EnvironmentVariables["OSTUM_SCAN_PAGES"]=string.Join(",",pages);
            start.EnvironmentVariables["OSTUM_SCAN_PREVIEW"]=preview?"1":"0";
            ct.ThrowIfCancellationRequested();
            using(var process=Process.Start(start))using(ct.Register(()=>Engine.Kill(process))){
                var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();
                if(!process.WaitForExit(45000+8000*pages.Count)){Engine.Kill(process);throw new IOException("Windows не успела распознать страницы. Откройте меньшую часть PDF.");}
                Task.WaitAll(output,error);ct.ThrowIfCancellationRequested();
                if(process.ExitCode!=0)throw new IOException("Распознавание скана Windows: "+ResearchText.Clip(error.Result,320));
                return Store.Json().Deserialize<ScanPage[]>(output.Result);
            }
        }
    }
}
