using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Sfera {
    // One immutable index per open attachment. Closing the process drops all text from memory.
    internal sealed class DocumentSearch {
        static readonly Regex tokens=new Regex(@"[\p{L}\d]+(?:[-‑–][\p{L}\d]+)*",RegexOptions.Compiled);
        static readonly HashSet<string> stop=new HashSet<string>("и в во к на о с по у из для от или как где какой какая какие что это мне пожалуйста укажи найди покажи документ документа файле the an of in for is find".Split(' '));
        static readonly Regex ending=new Regex("(иями|ами|ями|ого|его|ому|ему|ыми|ими|ах|ях|ой|ей|ам|ям|ых|их|ов|ев|ая|ое|ые|ую|ию|ия|ии|ие|а|я|ы|и|у|ю|е)$",RegexOptions.Compiled);
        static readonly LinkedList<DocumentSearch> cache=new LinkedList<DocumentSearch>();
        readonly ResearchDocument document;
        readonly string path;
        readonly long length;
        readonly DateTime modified;
        readonly List<Dictionary<string,int>> positions=new List<Dictionary<string,int>>();
        readonly Dictionary<string,int> frequency=new Dictionary<string,int>();
        static bool Identifier(string word){return word.Any(char.IsDigit)&&word.Any(char.IsLetter);}
        static string Key(string raw){
            string term=raw.ToLowerInvariant().Replace('ё','е').Replace('‑','-').Replace('–','-');
            if(stop.Contains(term))return "";
            if(term.Length>=5&&term.All(c=>c>='а'&&c<='я')){
                var suffix=ending.Match(term);
                if(suffix.Success&&suffix.Index>=3)return term.Substring(0,suffix.Index);
            }
            return term;
        }
        static Dictionary<string,int> Terms(string text,Dictionary<string,string> vocabulary=null){
            var result=new Dictionary<string,int>();
            foreach(Match token in tokens.Matches(text??"")){
                string word=token.Value,key;
                if(vocabulary==null||!vocabulary.TryGetValue(word,out key)){
                    key=Key(word);if(vocabulary!=null&&vocabulary.Count<8192)vocabulary[word]=key;
                }
                if(key.Length>0&&!result.ContainsKey(key))result.Add(key,token.Index);
            }
            return result;
        }
        DocumentSearch(ResearchDocument doc,FileInfo file){
            document=doc;path=file.FullName;length=file.Length;modified=file.LastWriteTimeUtc;
            var vocabulary=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            foreach(var p in doc.Parts){
                var words=Terms(p.Text,vocabulary);positions.Add(words);
                foreach(string key in words.Keys){int count;frequency.TryGetValue(key,out count);frequency[key]=count+1;}
            }
        }
        public static DocumentSearch Open(string file,Func<ResearchDocument> read){
            var info=new FileInfo(file);if(!info.Exists)return null;
            lock(cache){
                var node=cache.First;
                while(node!=null){
                    if(string.Equals(node.Value.path,info.FullName,StringComparison.OrdinalIgnoreCase)){
                        var old=node.Value;cache.Remove(node);
                        if(old.length==info.Length&&old.modified==info.LastWriteTimeUtc){cache.AddFirst(old);return old;}
                        break;
                    }
                    node=node.Next;
                }
                var doc=read();if(doc==null)return null;
                var index=new DocumentSearch(doc,info);cache.AddFirst(index);
                if(cache.Count>4)cache.RemoveLast();
                return index;
            }
        }
        sealed class Hit {public int index,focus;public double score;public bool identifier;}
        public List<Source> Find(string query,int count){
            var weights=new Dictionary<string,double>();
            foreach(var term in Terms(query).Keys){
                int n;if(frequency.TryGetValue(term,out n))weights[term]=Math.Log(1+(positions.Count+.5)/(n+.5))*(Identifier(term)?4:1);
            }
            var hits=new List<Hit>();
            for(int i=0;i<positions.Count;i++){
                var hit=new Hit{index=i};double best=0;
                foreach(var word in weights){
                    int at;if(!positions[i].TryGetValue(word.Key,out at))continue;
                    hit.score+=word.Value;hit.identifier|=Identifier(word.Key);
                    if(word.Value>best){best=word.Value;hit.focus=at;}
                }
                if(hit.score>0)hits.Add(hit);
            }
            IEnumerable<Hit> selected;
            if(hits.Count==0){
                int n=Math.Min(count,positions.Count);
                selected=Enumerable.Range(0,n).Select(i=>new Hit{index=n<=1?0:i*(positions.Count-1)/(n-1)});
            }else{
                bool exact=hits.Any(h=>h.identifier);
                selected=hits.Where(h=>!exact||h.identifier).OrderByDescending(h=>h.score).ThenBy(h=>h.index).Take(count);
            }
            return selected.Select(h=>{
                var part=document.Parts[h.index];string excerpt=part.Text;
                if(excerpt.Length>660&&h.focus>350){
                    int start=Math.Min(excerpt.Length-660,Math.Max(0,h.focus-200));
                    int space=excerpt.LastIndexOf(' ',start,Math.Min(35,start+1));if(space>=0)start=space+1;
                    excerpt=(start==0?"":"…")+excerpt.Substring(start);
                }
                return new Source{Title=document.Name,Location=part.Label+(document.Truncated?" · извлечена часть файла":""),Excerpt=excerpt};
            }).ToList();
        }
    }
}
