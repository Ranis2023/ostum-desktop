using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using Sfera;

class WorkdayInstall {
    static string Digest(byte[] bytes){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","").ToLowerInvariant();}
    static int Main(string[] args){
        try{
            var root=Directory.CreateDirectory(Path.GetFullPath(args[0])).FullName;
            typeof(Store).GetField("Home").SetValue(null,root);
            if(!WorkPolicy.Within(Store.Chats,root))throw new Exception("Unexpected production profile");
            var results=new List<object>();
            string target=Path.Combine(root,"tmp-model-library");
            int code=InstallModels.FromFolder(args[1],target);
            results.Add(new{id="U01",passed=code==0&&Directory.GetFiles(target,"*",SearchOption.AllDirectories).Length==10,measure="Offline base and music files"});
            code=InstallModels.FromFolder(args[1],target);
            int reused=File.ReadAllLines(Path.Combine(root,"logs","setup-models.log")).Count(s=>s.StartsWith("Verified "));
            results.Add(new{id="U02",passed=code==0&&reused==10,measure=reused+" existing files verified"});
            Store.Write(Path.Combine(root,"install.json"),results);
            const string url="https://huggingface.co/Qwen/Qwen3.5-0.8B/resolve/2fc06364715b967f1860aea9cf38778875588b17/LICENSE";
            ServicePointManager.SecurityProtocol=SecurityProtocolType.Tls12;
            byte[] raw;using(var web=new WebClient()){web.Headers["User-Agent"]="OSTUM-QA";raw=web.DownloadData(url);}
            if(raw.Length<514||raw.Length>200000)throw new Exception("Unexpected upstream fixture");
            var file=new ModelFile{id="license-qa",name="fixture.txt",bytes=raw.Length,sha256=Digest(raw),url=url};
            string directory=Directory.CreateDirectory(Path.Combine(root,"download")).FullName;
            string final=file.PathIn(directory);
            using(var partial=File.Create(final+".part"))partial.Write(raw,0,513);
            Downloads.Prepare(new[]{file},directory,(s,p)=>{},CancellationToken.None).GetAwaiter().GetResult();
            results.Add(new{id="U03",passed=File.ReadAllBytes(final).SequenceEqual(raw)&&!File.Exists(final+".part"),measure="Resume from byte 513"});
            File.Delete(final);File.WriteAllBytes(final+".part",new byte[raw.Length]);
            Downloads.Prepare(new[]{file},directory,(s,p)=>{},CancellationToken.None).GetAwaiter().GetResult();
            results.Add(new{id="U04",passed=File.ReadAllBytes(final).SequenceEqual(raw),measure="Corrupt complete partial replaced with verified bytes"});
            File.WriteAllBytes(final,new byte[raw.Length]);
            Downloads.Prepare(new[]{file},directory,(s,p)=>{},CancellationToken.None).GetAwaiter().GetResult();
            results.Add(new{id="U05",passed=File.ReadAllBytes(final).SequenceEqual(raw),measure="Existing file with the right size but bad digest repaired"});
            Store.Write(Path.Combine(root,"install.json"),results);return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
