using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Sfera {
    public static partial class Store {
        // The chat file remains authoritative. This index contains sidebar metadata only.
        sealed class Heading {
            public string Id {get;set;}
            public string Title {get;set;}
            public string ProjectKey {get;set;}
            public bool Archived {get;set;}
            public DateTime Updated {get;set;}
            public long Modified {get;set;}
            public long Bytes {get;set;}
        }
        static string IndexPath(string chats){return Path.Combine(Path.GetDirectoryName(chats),"chat-headings-v1.json");}
        static bool RemoveHeading(string directory,string id){
            string path=IndexPath(directory);
            try{
                if(!File.Exists(path))return true;
                var saved=ReadIndex(path);
                if(saved.Count==0)File.Delete(path);
                else if(saved.Remove(id))Write(path,saved.Values.ToList());
                return true;
            }catch(IOException){return false;}catch(UnauthorizedAccessException){return false;}
        }

        static Conversation ReadChat(string path,string id){
            var chat=Json().Deserialize<Conversation>(File.ReadAllText(path));
            if(chat==null||chat.Id!=id||chat.Turns==null)throw new InvalidDataException("Не удалось прочесть историю этого диалога.");
            return chat;
        }
        internal static void LoadTurns(Conversation chat){
            if(!chat.HeaderOnly)return;
            if(!WorkPolicy.History)throw new UnauthorizedAccessException("История отключена.");
            var full=ReadChat(SavedChatPath(chat),chat.Id);
            chat.Title=full.Title;chat.Updated=full.Updated;chat.Archived=full.Archived;chat.ProjectKey=full.ProjectKey;chat.Turns=full.Turns;
            chat.HeaderOnly=false;
        }
        static Dictionary<string,Heading> ReadIndex(string path){
            try{
                if(File.Exists(path)){
                    var entries=Json().Deserialize<List<Heading>>(File.ReadAllText(path));
                    if(entries!=null)return entries.Where(h=>h!=null&&h.Id!=null).ToDictionary(h=>h.Id);
                }
            }catch(IOException){}catch(UnauthorizedAccessException){}catch(ArgumentException){}catch(InvalidOperationException){}
            return new Dictionary<string,Heading>();
        }
        public static List<Conversation> HistoryHeadings(){
            var result=new List<Conversation>();
            if(!WorkPolicy.History)return result;
            var directory=Directory.CreateDirectory(Chats);
            string indexPath=IndexPath(directory.FullName);
            var existing=ReadIndex(indexPath);
            var current=new List<Heading>();bool changed=false;
            var scopes=new HashSet<string>(WorkPolicy.Projects().Select(p=>ProjectId(p.Root)));
            foreach(var file in directory.EnumerateFiles("*.json")){
                string id=Path.GetFileNameWithoutExtension(file.Name);Guid parsed;
                if(!Guid.TryParseExact(id,"N",out parsed))continue;
                try{
                    long modified=file.LastWriteTimeUtc.Ticks,bytes=file.Length;
                    Heading h;
                    if(!existing.TryGetValue(id,out h)||h.Modified!=modified||h.Bytes!=bytes){
                        var chat=ReadChat(file.FullName,id);
                        h=new Heading{Id=id,Title=chat.Title,ProjectKey=chat.ProjectKey,Archived=chat.Archived,Updated=chat.Updated,Modified=modified,Bytes=bytes};
                        changed=true;
                    }
                    current.Add(h);
                    if(!string.IsNullOrEmpty(h.ProjectKey)&&!scopes.Contains(h.ProjectKey))continue;
                    result.Add(new Conversation{Id=id,Title=h.Title,ProjectKey=h.ProjectKey,Archived=h.Archived,Updated=h.Updated,HeaderOnly=true});
                }catch(IOException){}catch(InvalidDataException){}catch(UnauthorizedAccessException){}catch(ArgumentException){}catch(InvalidOperationException){}
            }
            if(changed||current.Count!=existing.Count){
                try{Write(indexPath,current);}catch(IOException){}catch(UnauthorizedAccessException){}
            }
            return result.OrderByDescending(c=>c.Updated).ToList();
        }
        internal static string ProjectId(string root){
            using(var hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(WorkPolicy.Full(root).ToUpperInvariant()))).Replace("-","");
        }
        internal static bool AccessibleChat(string key){
            return string.IsNullOrEmpty(key)||WorkPolicy.Projects().Any(p=>ProjectId(p.Root)==key);
        }
        internal static void AssignProject(Conversation chat,string key){
            if(!WorkPolicy.History||!AccessibleChat(key)||!AccessibleChat(chat.ProjectKey))throw new UnauthorizedAccessException("Рабочая папка недоступна.");
            LoadTurns(chat);string old=chat.ProjectKey;chat.ProjectKey=key;
            try{Write(SavedChatPath(chat),SavedView(chat));}catch{chat.ProjectKey=old;throw;}
        }
        internal sealed class ChatHit {
            internal string Id,Project,Role,Text;
            internal int Position;
            public string Caption {get;set;}
            public string Excerpt {get;set;}
        }
        internal static List<ChatHit> SearchMessages(string query,string scope,bool archives,CancellationToken token){
            var hits=new List<ChatHit>();query=(query??"").Trim();
            if(query.Length==0||!WorkPolicy.History)return hits;
            var keys=new HashSet<string>(WorkPolicy.Projects().Select(p=>ProjectId(p.Root)));
            foreach(var heading in HistoryHeadings()){
                token.ThrowIfCancellationRequested();
                if(!WorkPolicy.History) return new List<ChatHit>();
                if((heading.Archived&&!archives)||(scope!=null&&(heading.ProjectKey??"")!=scope)||(!string.IsNullOrEmpty(heading.ProjectKey)&&!keys.Contains(heading.ProjectKey)))continue;
                Conversation chat;
                try{chat=ReadChat(SavedChatPath(heading),heading.Id);}
                catch(IOException){continue;}catch(UnauthorizedAccessException){continue;}catch(InvalidDataException){continue;}catch(InvalidOperationException){continue;}catch(ArgumentException){continue;}
                if(chat.ProjectKey!=heading.ProjectKey||(chat.Archived&&!archives))continue;
                for(int i=chat.Turns.Count-1;i>=0;i--){
                    token.ThrowIfCancellationRequested();
                    var turn=chat.Turns[i];if(turn==null||turn.ProjectOnly||string.IsNullOrEmpty(turn.Text))continue;
                    int at=turn.Text.IndexOf(query,StringComparison.OrdinalIgnoreCase);if(at<0)continue;
                    int start=Math.Max(0,at-45),end=Math.Min(turn.Text.Length,at+Math.Min(query.Length,100)+85);
                    hits.Add(new ChatHit{Id=chat.Id,Position=i,Role=turn.Role,Project=chat.ProjectKey,Text=turn.Text,
                        Caption=chat.Title+"  ·  "+(turn.Role=="user"?"Вы":"OSTUM")+(chat.Archived?"  ·  Архив":""),
                        Excerpt=(start>0?"…":"")+turn.Text.Substring(start,end-start).Replace('\r',' ').Replace('\n',' ')+(end<turn.Text.Length?"…":"")});
                    if(hits.Count==100)return hits;
                }
            }
            return hits;
        }
        internal static int LocateHit(Conversation chat,ChatHit hit){
            if(!WorkPolicy.History||chat.Id!=hit.Id||chat.ProjectKey!=hit.Project||!AccessibleChat(hit.Project))return -1;
            int persisted=0;
            for(int i=0;i<chat.Turns.Count;i++){
                var t=chat.Turns[i];if(t.ProjectOnly)continue;
                if(persisted++==hit.Position)return t.Text==hit.Text&&t.Role==hit.Role?i:-1;
            }
            return -1;
        }
    }
}
