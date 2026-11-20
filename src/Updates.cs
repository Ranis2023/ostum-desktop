using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Sfera {
    public static class Updates {
        const string Endpoint="https://api.github.com/repos/Ranis2023/ostum-desktop/releases/latest";
        const string SetupName="Ostum-Desktop-Setup.exe";
        static readonly HttpClient api=new HttpClient {Timeout=TimeSpan.FromSeconds(15),MaxResponseContentBufferSize=2*1024*1024};
        public sealed class Asset {
            public string name {get;set;}
            public string browser_download_url {get;set;}
            public string digest {get;set;}
            public long size {get;set;}
        }
        public sealed class Release {
            public string tag_name {get;set;}
            public bool draft {get;set;}
            public bool prerelease {get;set;}
            public Asset[] assets {get;set;}
        }
        public sealed class Package {
            public readonly Version Version;
            internal readonly ModelFile File;
            internal Package(Version version,ModelFile file){ Version=version;File=file; }
            internal string DirectoryName {get{return Path.Combine(Store.Home,"updates",Version.ToString());}}
            internal string PathName {get{return File.PathIn(DirectoryName);}}
            public bool Exists {get{return File.Present(DirectoryName);}}
        }
        public static Package Parse(Release release){
            Version version;
            if(release==null||release.draft||release.prerelease||!Regex.IsMatch(release.tag_name??"", @"^v[0-9]+\.[0-9]+\.[0-9]+$")||
                !Version.TryParse(release.tag_name.Substring(1),out version)||"v"+version!=release.tag_name)
                throw new InvalidDataException("GitHub вернул некорректную версию стабильного выпуска.");
            var assets=(release.assets??new Asset[0]).Where(a=>a!=null&&a.name==SetupName).ToArray();
            var url=Brand.Repository+"/releases/download/"+release.tag_name+"/"+SetupName;
            if(assets.Length!=1||assets[0].browser_download_url!=url||assets[0].size<=0||assets[0].size>=2L*1024*1024*1024||
                !Regex.IsMatch(assets[0].digest??"", @"\Asha256:[0-9a-fA-F]{64}\z"))
                throw new InvalidDataException("В выпуске нет проверяемого установщика Windows.");
            return new Package(version,new ModelFile{name=SetupName,url=url,bytes=assets[0].size,sha256=assets[0].digest.Substring(7)});
        }
        public static async Task<Package> Latest(CancellationToken ct){
            WorkPolicy.RequireNetwork();
            using(var request=new HttpRequestMessage(HttpMethod.Get,Endpoint)){
                request.Headers.UserAgent.ParseAdd("OstumDesktop/"+Brand.Version);
                using(var response=await api.SendAsync(request,ct).ConfigureAwait(false)){
                    response.EnsureSuccessStatusCode();
                    var release=Store.Json().Deserialize<Release>(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                    var package=Parse(release);
                    return package;
                }
            }
        }
        public static Task Download(Package package,Action<string,double> progress,CancellationToken ct){
            WorkPolicy.RequireNetwork();
            return Downloads.Prepare(new[]{package.File},package.DirectoryName,progress,ct);
        }
        public static void Verify(Package package,CancellationToken ct){
            if(!package.Exists)throw new IOException("Локальный установщик отсутствует или загружен не полностью.");
            Downloads.Verify(package.PathName,package.File,ct);
            var actual=FileVersionInfo.GetVersionInfo(package.PathName).FileVersion;
            if(actual!=package.Version.ToString()+".0")throw new InvalidDataException("Версия установщика отличается от опубликованной.");
        }
        public static void Install(Package package,CancellationToken ct){
            WorkPolicy.RequireNetwork();
            if(package.Version<=new Version(Brand.Version))throw new InvalidOperationException("Установлена эта или более новая версия.");
            // Keep the verified executable read-only until Windows has created the installer process.
            using(var held=new FileStream(package.PathName,FileMode.Open,FileAccess.Read,FileShare.Read)){
                Verify(package,ct);ct.ThrowIfCancellationRequested();WorkPolicy.RequireNetwork();
                using(var process=Process.Start(new ProcessStartInfo(package.PathName,"--update-app"){UseShellExecute=false,WorkingDirectory=package.DirectoryName})){
                    if(process==null)throw new IOException("Windows не запустила установщик.");
                }
            }
        }
    }
}
