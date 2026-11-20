$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$testRoot=Join-Path $root ('.build/pro5/policy-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($testRoot)
$code=[IO.File]::ReadAllText((Join-Path $root 'src/WorkPolicy.cs'))
# Only the test compilation directs Windows' common data directory into an isolated folder.
$anchor='Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)'
if(!$code.Contains($anchor)){throw 'Policy path changed. Update this fixture.'}
$code=$code.Replace($anchor,'Store.Home')
[IO.File]::WriteAllText((Join-Path $testRoot 'WorkPolicy.cs'),$code)
$test=@'
using System;
using System.IO;
using System.Web.Script.Serialization;
using Sfera;
namespace Sfera {
    public static class Store {
        public static string Home=AppDomain.CurrentDomain.BaseDirectory;
        public static JavaScriptSerializer Json(){return new JavaScriptSerializer();}
        public static void Write(string path,object data){Directory.CreateDirectory(Path.GetDirectoryName(path));File.WriteAllText(path,Json().Serialize(data));}
    }
}
public static class PolicyProbe {
    static void Deny(Action fn){try{fn();}catch(UnauthorizedAccessException){return;}throw new Exception("Expected denial");}
    public static void Main(){
        string root=Path.Combine(Store.Home,"workspace"),strict=Path.Combine(root,"private"),other=Path.Combine(Store.Home,"elsewhere");
        var p=new CompanyRules{Managed=true,LocalAI=true,Training=true,Projects=new[]{new ProjectRule{Root=root,Analysis=true,Training=true},new ProjectRule{Root=strict}}};
        Store.Write(WorkPolicy.PolicyPath,p);
        WorkPolicy.CheckFile(Path.Combine(root,"approved.docx"),true,true);
        string classified=Path.Combine(strict,"x.docx");WorkPolicy.CheckFile(classified);
        Deny(()=>WorkPolicy.CheckFile(classified,true));Deny(()=>WorkPolicy.CheckFile(classified,false,true));
        p.Projects[1].Training=true;Store.Write(WorkPolicy.PolicyPath,p);Deny(()=>WorkPolicy.CheckFile(classified,false,true));
        Deny(()=>WorkPolicy.CheckFile(other));Deny(WorkPolicy.RequireNetwork);Deny(()=>WorkPolicy.CheckImport(other));
        Deny(()=>WorkPolicy.LocalPath(@"\\localhost\unauthorized\probe"));
        if(WorkPolicy.CaptureAllowed||WorkPolicy.History||WorkPolicy.GlobalFile(classified))throw new Exception("Restricted surface enabled");
        p.LocalAI=false;Store.Write(WorkPolicy.PolicyPath,p);Deny(WorkPolicy.RequireAI);Deny(()=>WorkPolicy.CheckFile(root,true));
        File.WriteAllText(WorkPolicy.PolicyPath,"{ broken");Deny(WorkPolicy.RequireNetwork);Deny(WorkPolicy.RequireAI);
        Console.WriteLine("PASS: editor-only access, nested denial, training, egress, capture, local model policy, malformed policy");
    }
}
'@
[IO.File]::WriteAllText((Join-Path $testRoot 'Probe.cs'),$test)
$fx=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$exe=Join-Path $testRoot 'Probe.exe'
& (Join-Path $fx 'csc.exe') /nologo /codepage:65001 ('/out:'+$exe) ('/r:'+(Join-Path $fx 'System.Web.Extensions.dll')) (Join-Path $testRoot 'WorkPolicy.cs') (Join-Path $testRoot 'Probe.cs')
if($LASTEXITCODE){throw 'Test compilation failed'}
& $exe
if($LASTEXITCODE){throw 'Policy test failed'}
