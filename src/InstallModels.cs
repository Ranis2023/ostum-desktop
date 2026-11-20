using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace Sfera {
    public static class InstallModels {
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        static extern bool CreateHardLink(string newFileName, string existingFileName, IntPtr securityAttributes);
        internal static bool TryLink(string source,string target){return CreateHardLink(target,source,IntPtr.Zero);}
        internal static void LinkOrCopy(string source,string target,long bytes){
            if(TryLink(source,target))return;
            if(new DriveInfo(Path.GetPathRoot(target)).AvailableFreeSpace<bytes+128L*1024*1024)throw new IOException("Недостаточно свободного места.");
            File.Copy(source,target);
        }

        // Models are installed as per-user data so reinstalls and application updates can reuse them.
        public static void MigrateManagedNames(string directory,CancellationToken token) {
            if(!string.Equals(Path.GetFullPath(directory),Path.GetFullPath(new Preferences().Models),StringComparison.OrdinalIgnoreCase))return;
            foreach(var f in Store.Files.Values.Where(file=>file.legacyName!=null)){
                string current=Path.Combine(directory,f.name),old=Path.Combine(directory,f.legacyName);
                if(!File.Exists(old)||File.Exists(current))continue;
                Downloads.Verify(old,f,token);
                File.Move(old,current);
            }
        }
        public static int FromFolder(string source, string destination) {
            return FromFolder(source,destination,null);
        }
        public static int FromFolder(string source, string destination, Action<string> status) {
            if (!Directory.Exists(source)) return 0;
            var included = Store.ChatModels.Concat(Store.ArtModels).Where(m => Store.Ready(m,source)).SelectMany(m => m.Files).Distinct().Select(id => Store.Files[id]).ToList();
            if (Store.Files["voice"].Present(source)) included.Add(Store.Files["voice"]);
            string musicSource=Path.Combine(source,Store.MusicDirectory);
            if(Directory.Exists(musicSource))included.AddRange(Store.MusicFiles);
            if (included.Count == 0) return 0;
            Directory.CreateDirectory(destination);
            Directory.CreateDirectory(Path.Combine(Store.Home,"logs"));
            int failed=0;
            using (var log = new StreamWriter(Path.Combine(Store.Home,"logs","setup-models.log"),false)) {
                foreach (var f in included) {
                    if(status!=null)status(f.name);
                    bool music=Store.MusicFiles.Contains(f);
                    string target=music?Path.Combine(destination,Store.MusicDirectory):destination;
                    Directory.CreateDirectory(target);
                    var src=f.PathIn(music?musicSource:source); var dst=f.PathIn(target);
                    var tmp=dst+"."+Guid.NewGuid().ToString("N")+".importing";
                    try {
                        if (f.Present(target)) {
                            try { Downloads.Verify(dst,f,CancellationToken.None); log.WriteLine("Verified " + f.name); continue; }
                            catch (InvalidDataException) { log.WriteLine("Repairing " + f.name); }
                        }
                        LinkOrCopy(src,tmp,f.bytes);
                        Downloads.Verify(tmp,f,CancellationToken.None);
                        if (File.Exists(dst)) File.Replace(tmp,dst,null); else File.Move(tmp,dst);
                        log.WriteLine("Installed " + f.name);
                    } catch (Exception ex) {
                        failed++;
                        log.WriteLine("Skipped " + f.name + " — " + ex.Message);
                    } finally { if (File.Exists(tmp)) File.Delete(tmp); }
                }
            }
            return failed == 0 ? 0 : 1;
        }
    }
}
