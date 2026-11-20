using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Sfera {
    public sealed class ModelFile {
        public string id { get; set; }
        public string name { get; set; }
        public string legacyName { get; set; }
        public long bytes { get; set; }
        public string sha256 { get; set; }
        public string url { get; set; }
        public string PathIn(string directory) {
            string path=Path.Combine(directory,name);
            if(string.IsNullOrEmpty(legacyName)||File.Exists(path))return path;
            string old=Path.Combine(directory,legacyName);
            return File.Exists(old)?old:path;
        }
        public bool Present(string directory) {
            var info = new FileInfo(PathIn(directory));
            return info.Exists && info.Length == bytes;
        }
    }

    public sealed class CudaCatalog {public ModelFile[] libraries {get;set;} public ModelFile[] archives {get;set;}}
    public sealed class Catalog { public List<ModelFile> files { get; set; } public CudaCatalog acceleration {get;set;} }

    public sealed class Model {
        public readonly string Id, Name, Description;
        public readonly string[] Files;
        public LocalModel Imported {get;private set;}
        public Model(LocalModel item):this(item.Id,item.Name,item.Architecture+" · GGUF · "+Store.Size(item.Bytes)){Imported=item;}
        public string Weights(string folder){return Imported==null?Store.Files[Files[0]].PathIn(folder):Imported.Path;}
        public string Projector(string folder){return Imported==null?(Files.Length>1?Store.Files[Files[1]].PathIn(folder):null):Imported.Projector;}
        public Model(string id, string name, string description, params string[] files) {
            Id = id; Name = name; Description = description; Files = files;
        }
        public override string ToString() { return Name; }
    }

    public sealed class Preferences {
        public string Models { get; set; }
        public string Chat { get; set; }
        public string Art { get; set; }
        public bool Think { get; set; }
        public bool Personalize { get; set; }
        public bool AllowWeb { get; set; }
        public bool ConversationVoice { get; set; }
        public string MicrophoneId { get; set; }
        public bool? Drawing { get; set; }
        public bool? Dictation { get; set; }
        public List<LocalModel> ImportedModels {get;set;}
        [System.Web.Script.Serialization.ScriptIgnore] public string ProjectScope {get;set;}
        public Preferences() {
            Models = Path.Combine(Store.Home, "library", "models");
            Chat = "quality"; Art = "studio"; Personalize=true; AllowWeb=false;
        }
    }

    public sealed class WorkerEvent {
        public string Id {get;set;}
        public string Name {get;set;}
        public string Detail {get;set;}
        public string State {get;set;}
        public DateTime Started {get;set;}
        public DateTime Ended {get;set;}
        public WorkerEvent Finish(string state){return new WorkerEvent{Id=Id,Name=Name,Detail=Detail,State=state,Started=Started,Ended=DateTime.UtcNow};}
    }
    public sealed class Turn {
        public string Role { get; set; }
        public string Text { get; set; }
        public string OriginalText { get; set; }
        public string Image { get; set; }
        public bool Generated { get; set; }
        public string DocumentId { get; set; }
        public string DocumentName { get; set; }
        public List<Source> Sources { get; set; }
        public List<string> Steps { get; set; }
        public List<WorkerEvent> Workers { get; set; }
    }

    public sealed class Source {
        public string Title { get; set; }
        public string Url { get; set; }
        public string Location { get; set; }
        public string Excerpt { get; set; }
    }

    public sealed class Conversation {
        public string Id { get; set; }
        public bool Archived { get; set; }
        public string Title { get; set; }
        public DateTime Updated { get; set; }
        public List<Turn> Turns { get; set; }
        public Conversation() {
            Id = Guid.NewGuid().ToString("N"); Title = "Новый чат";
            Updated = DateTime.Now; Turns = new List<Turn>();
        }
        public override string ToString() { return Title; }
    }

    public static class Store {
        public static readonly string Home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SferaAI");
        public static readonly string Chats = Path.Combine(Home, "chats");
        public static readonly string Images = Path.Combine(Home, "images");
        public static readonly Model[] ChatModels = {
            new Model("quality", "Zarum 9B", "Мультимодальная модель OSTUM на основе Qwen3.5-9B. Работает с текстом и фотографиями, поддерживает дообучение через адаптеры LoRA.", "q9", "v9")
        };
        public static readonly Model[] ArtModels = {
            new Model("studio", "Z-Image-Turbo", "Детальные изображения до 1024 × 1024 за 8 шагов. Требует больше памяти и времени.", "zimage", "zte", "zvae")
        };
        static readonly Catalog catalog=Json().Deserialize<Catalog>(Resource("catalog.json"));
        public static readonly Dictionary<string,ModelFile> Files = catalog.files.ToDictionary(f => f.id);
        public static CudaCatalog Cuda {get{return catalog.acceleration;}}
        public static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 }; }
        public static string Resource(string name) {
            using (var reader = new StreamReader(Assembly.GetExecutingAssembly().GetManifestResourceStream(name), Encoding.UTF8)) return reader.ReadToEnd();
        }
        public static IEnumerable<Model> Available(Preferences p){return ChatModels.Concat((p.ImportedModels??new List<LocalModel>()).Where(x=>x!=null&&x.Id!=null).Select(x=>new Model(x)));}
        public static Model Chat(Preferences p) { return Available(p).FirstOrDefault(m => m.Id == p.Chat) ?? ChatModels[0]; }
        public static Model Art(Preferences p) { return ArtModels.FirstOrDefault(m => m.Id == p.Art) ?? ArtModels[0]; }
        public static bool DrawingOn(Preferences p) {return p.Drawing??Ready(Art(p),p.Models);}
        public static bool DictationOn(Preferences p) {return p.Dictation??Files["voice"].Present(p.Models);}
        public static bool Ready(Model m, string dir) { return m.Imported!=null?m.Imported.Available():m.Files.All(id => Files[id].Present(dir)); }
        public static string Size(long bytes) { return (bytes / 1000000000.0).ToString("0.00", System.Globalization.CultureInfo.GetCultureInfo("ru-RU")) + " ГБ"; }
        public static void Write(string path, object value) {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                File.WriteAllText(tmp, Json().Serialize(value), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
            } finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }
        public static Preferences Load() {
            string path = Path.Combine(Home, "preferences.json");
            if (File.Exists(path)) {
                var existing=Json().Deserialize<Preferences>(File.ReadAllText(path));
                existing.Chat=Chat(existing).Id;existing.Art=ArtModels[0].Id;
                return existing;
            }
            var p = new Preferences();
            string source = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\SferaAI\Setup", "ModelSource", null) as string;
            foreach (string dir in new [] { p.Models, source }) {
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;
                var chat = ChatModels.LastOrDefault(m => Ready(m,dir));
                var art = ArtModels.LastOrDefault(m => Ready(m,dir));
                if (chat != null) { p.Models=dir; p.Chat=chat.Id; if(art!=null)p.Art=art.Id; break; }
            }
            return p;
        }
        public static void Save(Preferences p) { Write(Path.Combine(Home, "preferences.json"), p); }
        public static void Save(Conversation c) { c.Updated = DateTime.Now; if(WorkPolicy.History)Write(Path.Combine(Chats, c.Id + ".json"), c); }
        static string SavedChatPath(Conversation c) {
            Guid id;
            if(c==null||!Guid.TryParseExact(c.Id,"N",out id))throw new InvalidDataException("Неизвестный идентификатор диалога.");
            return Path.Combine(Chats,c.Id+".json");
        }
        public static void SetArchived(Conversation c,bool value) {
            var path=SavedChatPath(c);bool previous=c.Archived;
            if(!File.Exists(path))throw new FileNotFoundException("Диалог уже отсутствует.");
            c.Archived=value;
            try{Write(path,c);}catch{c.Archived=previous;throw;}
        }
        static IEnumerable<string> OwnedAttachments(Conversation c) {
            foreach(var t in c.Turns) {
                Guid id;
                if(Guid.TryParseExact(t.DocumentId,"N",out id))yield return Path.Combine(Home,"documents",id.ToString("N")+".json");
                if(string.IsNullOrEmpty(t.Image)||!Guid.TryParseExact(Path.GetFileNameWithoutExtension(t.Image),"N",out id))continue;
                var ext=Path.GetExtension(t.Image).ToLowerInvariant();
                if(ext!=".png"&&ext!=".jpg"&&ext!=".jpeg")continue;
                string owned=Path.Combine(Images,id.ToString("N")+ext);
                if(string.Equals(owned,t.Image,StringComparison.OrdinalIgnoreCase))yield return owned;
            }
        }
        public static bool DeleteConversation(Conversation c) {
            string path=SavedChatPath(c);
            var candidates=new HashSet<string>(OwnedAttachments(c),StringComparer.OrdinalIgnoreCase);
            var remaining=History();
            bool complete=remaining.Count==Directory.GetFiles(Chats,"*.json").Length;
            foreach(var other in remaining.Where(x=>x.Id!=c.Id))candidates.ExceptWith(OwnedAttachments(other));
            File.Delete(path);
            if(!complete)return candidates.Count==0;
            foreach(string file in candidates) {
                try{File.Delete(file);}catch(IOException){complete=false;}catch(UnauthorizedAccessException){complete=false;}
            }
            return complete;
        }
        public static List<Conversation> History() {
            if(!WorkPolicy.History)return new List<Conversation>();
            Directory.CreateDirectory(Chats);
            var result = new List<Conversation>();
            foreach (string path in Directory.EnumerateFiles(Chats, "*.json")) {
                try {
                    var c = Json().Deserialize<Conversation>(File.ReadAllText(path));
                    if (c != null && c.Turns != null && c.Id == Path.GetFileNameWithoutExtension(path)) result.Add(c);
                } catch (IOException) {} catch (ArgumentException) {} catch (InvalidOperationException) {}
            }
            return result.OrderByDescending(c => c.Updated).ToList();
        }
    }

    public static class Downloads {
        static readonly HttpClient Client = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        static async Task Fetch(ModelFile f, string partial, Action<long> progress, CancellationToken token) {
            WorkPolicy.RequireNetwork();
            using (var target = new FileStream(partial, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, 131072, true)) {
                long start = target.Length;
                if (start > f.bytes) { target.SetLength(0); start = 0; }
                if (start == f.bytes) return;
                using (var request = new HttpRequestMessage(HttpMethod.Get, f.url + "?download=true")) {
                    if (start > 0) request.Headers.Range = new RangeHeaderValue(start, null);
                    using (var res = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false)) {
                        res.EnsureSuccessStatusCode();
                        if (res.StatusCode == HttpStatusCode.PartialContent) {
                            var range = res.Content.Headers.ContentRange;
                            if (range == null || range.From != start || range.Length != f.bytes) throw new IOException("Сервер вернул неверный диапазон файла.");
                            target.Position = start;
                        } else { target.SetLength(0); target.Position = 0; }
                        using (var input = await res.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (token.Register(() => input.Dispose())) {
                            var data = new byte[131072];
                            while (true) {
                                int count;
                                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token)) {
                                    timeout.CancelAfter(TimeSpan.FromSeconds(90));
                                    using (timeout.Token.Register(() => input.Dispose()))
                                        count = await input.ReadAsync(data, 0, data.Length, timeout.Token).ConfigureAwait(false);
                                }
                                if (count == 0) break;
                                if (target.Position + count > f.bytes) throw new IOException("Размер загрузки превышает размер в каталоге.");
                                await target.WriteAsync(data, 0, count, token).ConfigureAwait(false);
                                progress(target.Position);
                            }
                        }
                    }
                }
                if (target.Length != f.bytes) throw new EndOfStreamException("Соединение прервалось до конца файла.");
            }
        }
        internal static void Verify(string path, ModelFile f, CancellationToken token) {
            using (var stream = File.OpenRead(path))
            using (var hash = SHA256.Create()) {
                var buffer = new byte[1024*1024];
                int n;
                while ((n = stream.Read(buffer, 0, buffer.Length)) > 0) {
                    token.ThrowIfCancellationRequested();
                    hash.TransformBlock(buffer, 0, n, buffer, 0);
                }
                hash.TransformFinalBlock(buffer, 0, 0);
                var actual = BitConverter.ToString(hash.Hash).Replace("-", "");
                if (!actual.Equals(f.sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Контрольная сумма файла " + f.name + " не совпала.");
            }
        }
        public static async Task Prepare(IEnumerable<ModelFile> selection, string directory, Action<string,double> update, CancellationToken token) {
            Directory.CreateDirectory(directory);
            var files = selection.Distinct().ToList();
            long total = files.Sum(f => f.bytes), done = 0;
            long missing = files.Where(f => !f.Present(directory)).Sum(f => {
                var part = new FileInfo(f.PathIn(directory) + ".part");
                return f.bytes - (part.Exists && part.Length <= f.bytes ? part.Length : 0);
            });
            var root = Path.GetPathRoot(Path.GetFullPath(directory));
            const long reserve=256L*1024*1024;
            if (missing > 0 && !root.StartsWith(@"\\") && new DriveInfo(root).AvailableFreeSpace < missing + reserve)
                throw new IOException("Для выбранных моделей и рабочего запаса нужно " + Store.Size(missing + reserve) + " свободного места. Можно выбрать папку на другом диске.");
            foreach (var file in files) {
                token.ThrowIfCancellationRequested();
                string final = file.PathIn(directory), partial = final + ".part";
                bool existing = file.Present(directory);
                if(existing){
                    update("Проверка "+file.name,(double)done/total);
                    try{await Task.Run(()=>Verify(final,file,token),token).ConfigureAwait(false);done+=file.bytes;continue;}
                    catch(InvalidDataException){
                        WorkPolicy.RequireNetwork();
                        if(!root.StartsWith(@"\\")&&new DriveInfo(root).AvailableFreeSpace<file.bytes+reserve)throw new IOException("Для восстановления модели нужно "+Store.Size(file.bytes+reserve)+" свободного места.");
                        existing=false;
                    }
                }
                if (!existing) {
                    for (int attempt = 0; ; attempt++) {
                        try {
                            update("Загрузка " + file.name, (double)done / total);
                            await Fetch(file, partial, n => update("Загрузка " + file.name, (double)(done + n) / total), token).ConfigureAwait(false);
                            break;
                        } catch (Exception ex) {
                            token.ThrowIfCancellationRequested();
                            if (attempt >= 2 || !(ex is IOException || ex is HttpRequestException || ex is TaskCanceledException || ex is ObjectDisposedException)) throw;
                        }
                        await Task.Delay(1200 * (attempt + 1), token).ConfigureAwait(false);
                    }
                }
                update("Проверка " + file.name, (double)(done + file.bytes) / total);
                try { await Task.Run(() => Verify(partial, file, token), token).ConfigureAwait(false); }
                catch (InvalidDataException) {
                    File.Delete(partial);
                    throw;
                }
                if (!existing) { if (File.Exists(final)) File.Replace(partial, final, null); else File.Move(partial, final); }
                done += file.bytes;
            }
        }
    }
}
