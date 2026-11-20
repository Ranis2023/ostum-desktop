using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Xml;
using System.Xml.Linq;

namespace Sfera {
    public static class WebResearch {
        const int ByteLimit=2400000;
        static readonly TimeSpan MatchTime=TimeSpan.FromSeconds(2);
        const RegexOptions HtmlOptions=RegexOptions.IgnoreCase|RegexOptions.Singleline;
        const string Agent="Mozilla/5.0 (Windows NT 10.0; Win64; x64) Sfera/2.0";
        public static bool PublicUrl(string text,out Uri uri) {
            return Uri.TryCreate(text,UriKind.Absolute,out uri) && (uri.Scheme=="https" || uri.Scheme=="http") &&
                uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) && uri.HostNameType==UriHostNameType.Dns &&
                uri.Host.Contains(".") && !uri.Host.EndsWith(".") && !Regex.IsMatch(uri.DnsSafeHost,@"(^|\.)(localhost|local|internal|lan|home|test|invalid)$",RegexOptions.IgnoreCase);
        }
        static bool Routable(IPAddress ip) {
            if(ip.IsIPv4MappedToIPv6)ip=ip.MapToIPv4();
            var b=ip.GetAddressBytes();
            if(b.Length==16)return (b[0]&0xe0)==0x20 && !(b[0]==0x20 && b[1]==1 && (b[2]==0 || (b[2]==0x0d && b[3]==0xb8))) && !(b[0]==0x20&&b[1]==2);
            return b[0]!=0&&b[0]!=10&&b[0]!=127&&b[0]<224 && !(b[0]==100&&b[1]>=64&&b[1]<=127) &&
                !(b[0]==169&&b[1]==254) && !(b[0]==172&&b[1]>=16&&b[1]<=31) &&
                !(b[0]==192&&(b[1]==168||(b[1]==0&&(b[2]==0||b[2]==2))||(b[1]==88&&b[2]==99))) &&
                !(b[0]==198&&(b[1]==18||b[1]==19||(b[1]==51&&b[2]==100))) && !(b[0]==203&&b[1]==0&&b[2]==113);
        }
        sealed class Page {public string Body,Type,Url;}
        static async Task<byte[]> Transfer(Uri uri,IPAddress ip,CancellationToken ct) {
            string address=ip.AddressFamily==AddressFamily.InterNetworkV6 ? "["+ip+"]" : ip.ToString();
            var args=new[]{"--disable","--silent","--show-error","--http1.1","--noproxy","*","--compressed","--include","--proto","=http,https",
                "--connect-timeout","5","--max-time","13","--max-filesize",ByteLimit.ToString(),"--user-agent",Agent,
                "--resolve",uri.IdnHost+":"+uri.Port+":"+address,"--",uri.AbsoluteUri};
            var info=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"curl.exe"),string.Join(" ",args.Select(Engine.Quote))) {
                UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true };
            using(var p=Process.Start(info)) using(ct.Register(()=>{try{if(!p.HasExited)p.Kill();}catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){}})) {
                try {
                    var error=p.StandardError.ReadToEndAsync();
                    using(var data=new MemoryStream()) {
                        var buf=new byte[16384];int n;
                        while((n=await p.StandardOutput.BaseStream.ReadAsync(buf,0,buf.Length,ct).ConfigureAwait(false))>0){
                            if(data.Length+n>ByteLimit+20000)throw new IOException("Страница превышает предел чтения.");
                            data.Write(buf,0,n);
                        }
                        p.WaitForExit();await error.ConfigureAwait(false);ct.ThrowIfCancellationRequested();
                        if(p.ExitCode!=0)throw new IOException("Сайт не ответил или ограничил чтение страницы.");
                        return data.ToArray();
                    }
                } finally {if(!p.HasExited){try{p.Kill();}catch(InvalidOperationException){}}}
            }
        }
        static async Task<Page> Get(string address,CancellationToken ct) {
            Uri uri;
            for(int r=0;r<4;r++) {
                ct.ThrowIfCancellationRequested();
                if(!PublicUrl(address,out uri))throw new IOException("Для поиска нужен публичный адрес HTTP или HTTPS.");
                var resolving=Dns.GetHostAddressesAsync(uri.DnsSafeHost);
                if(await Task.WhenAny(resolving,Task.Delay(4500,ct)).ConfigureAwait(false)!=resolving){ct.ThrowIfCancellationRequested();throw new IOException("Сайт долго отвечает.");}
                var ips=await resolving.ConfigureAwait(false);
                if(ips.Length==0||ips.Any(ip=>!Routable(ip)))throw new IOException("Этот адрес недоступен для веб-поиска.");
                var ipChoice=ips.OrderBy(ip=>ip.AddressFamily!=AddressFamily.InterNetwork).First();
                var bytes=await Transfer(uri,ipChoice,ct).ConfigureAwait(false);
                int end=-1;
                for(int i=0;i+3<Math.Min(bytes.Length,20000);i++)if(bytes[i]==13&&bytes[i+1]==10&&bytes[i+2]==13&&bytes[i+3]==10){end=i;break;}
                if(end<0)throw new IOException("Получен неполный ответ сайта.");
                string headers=Encoding.ASCII.GetString(bytes,0,end);
                var http=Regex.Match(headers,@"^HTTP/\S+\s+(\d{3})");int code=http.Success?int.Parse(http.Groups[1].Value):0;
                if(code>=300&&code<400){
                    var next=Regex.Match(headers,@"(?im)^location:\s*([^\r\n]+)");Uri target;
                    if(!next.Success||!Uri.TryCreate(uri,next.Groups[1].Value.Trim(),out target))break;
                    address=target.AbsoluteUri;continue;
                }
                if(code<200||code>=300)throw new IOException("Сайт ответил HTTP "+code+".");
                string type=Regex.Match(headers,@"(?im)^content-type:\s*([^\r\n]+)").Groups[1].Value;
                if(!Regex.IsMatch(type,"text|html|json|xml|rss",RegexOptions.IgnoreCase))throw new IOException("Сайт вернул файл без доступного текста.");
                var charset=Regex.Match(type,@"charset\s*=\s*[""']?([A-Za-z0-9._-]+)",RegexOptions.IgnoreCase);
                Encoding enc=Encoding.UTF8;if(charset.Success)try{enc=Encoding.GetEncoding(charset.Groups[1].Value);}catch(ArgumentException){}
                return new Page{Body=enc.GetString(bytes,end+4,bytes.Length-end-4),Type=type,Url=uri.AbsoluteUri};
            }
            throw new IOException("Сайт перенаправил запрос слишком много раз.");
        }
        static string Clean(string html) {
            return ResearchText.Plain(WebUtility.HtmlDecode(Regex.Replace(html??"","<[^>]{0,3000}>"," ",HtmlOptions,MatchTime)));
        }
        static string Attr(string tag,string attr) {
            var m=Regex.Match(tag,@"\b"+attr+@"\s*=\s*([""'])(.*?)\1",HtmlOptions,MatchTime);
            return WebUtility.HtmlDecode(m.Groups[2].Value);
        }
        static List<Source> Duck(string html) {
            var rows=new List<Source>();
            var anchors=Regex.Matches(html,@"<a\b([^>]{0,1800}\bclass\s*=\s*[""']result__(a|snippet)[""'][^>]{0,1800})>(.*?)</a>",HtmlOptions,MatchTime);
            Source current=null;
            foreach(Match a in anchors) {
                if(a.Groups[2].Value=="snippet"){if(current!=null)current.Excerpt=ResearchText.Clip(Clean(a.Groups[3].Value),740);continue;}
                string link=Attr(a.Groups[1].Value,"href");if(link.StartsWith("//"))link="https:"+link;
                Uri url;if(!PublicUrl(link,out url)){current=null;continue;}
                if(url.Host=="duckduckgo.com" || url.Host.EndsWith(".duckduckgo.com",StringComparison.OrdinalIgnoreCase))link=HttpUtility.ParseQueryString(url.Query)["uddg"];
                if(!PublicUrl(link,out url)){current=null;continue;}
                current=new Source {Title=ResearchText.Clip(Clean(a.Groups[3].Value),160),Url=url.AbsoluteUri,Location="выдача DuckDuckGo"};
                rows.Add(current);
            }
            return rows;
        }
        static List<Source> Bing(string xml) {
            XDocument doc;
            using(var r=XmlReader.Create(new StringReader(xml),new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null}))doc=XDocument.Load(r);
            return doc.Descendants("item").Select(i=>new Source {Title=Clean((string)i.Element("title")),Url=(string)i.Element("link"),Excerpt=ResearchText.Clip(Clean((string)i.Element("description")),740),Location="выдача Bing"}).ToList();
        }
        static List<Source> Brave(string html) {
            var rows=new List<Source>();
            foreach(string chunk in Regex.Split(html,@"\bdata-type=[""']web[""']",HtmlOptions,MatchTime).Skip(1).Take(12)){
                string head=chunk.Substring(0,Math.Min(chunk.Length,14500));
                var anchor=Regex.Match(head,@"<a\b([^>]+)>",HtmlOptions,MatchTime);
                var title=Regex.Match(head,@"<div\b[^>]*\bsearch-snippet-title\b[^>]*>(.*?)</div>",HtmlOptions,MatchTime);
                var snippet=Regex.Match(head,@"\bgeneric-snippet\b[^>]*>\s*<div\b[^>]*>(.*?)</div>",HtmlOptions,MatchTime);
                if(!anchor.Success||!title.Success)continue;
                rows.Add(new Source{Title=Clean(title.Groups[1].Value),Url=Attr(anchor.Groups[1].Value,"href"),Excerpt=ResearchText.Clip(Clean(snippet.Groups[1].Value),740),Location="выдача Brave"});
            }
            return rows;
        }
        sealed class WikiResult {public WikiQuery query{get;set;}}
        sealed class WikiQuery {public WikiPage[] search{get;set;}}
        sealed class WikiPage {public string title{get;set;} public string snippet{get;set;} public int pageid{get;set;}}
        static async Task<List<Source>> Lookup(string q,CancellationToken ct) {
            string encoded=Uri.EscapeDataString(q);
            var rows=new List<Source>();
            try {rows=Duck((await Get("https://html.duckduckgo.com/html/?q="+encoded,ct).ConfigureAwait(false)).Body);}
            catch(Exception ex){if(!Recover(ex,ct))throw;}
            if(rows.Count==0)try{rows=Brave((await Get("https://search.brave.com/search?q="+encoded,ct).ConfigureAwait(false)).Body);}
            catch(Exception ex){if(!Recover(ex,ct))throw;}
            if(rows.Count==0)try{
                rows=Bing((await Get("https://www.bing.com/search?format=rss&q="+encoded,ct).ConfigureAwait(false)).Body);
                var terms=ResearchText.Words(q);
                rows=rows.Where(s=>ResearchText.Words(s.Title+" "+s.Excerpt).Count(terms.Contains)>=Math.Min(2,Math.Max(1,terms.Count))).ToList();
            }catch(Exception ex){if(!Recover(ex,ct))throw;}
            if(rows.Count==0)try{
                string host=Regex.IsMatch(q,@"\p{IsCyrillic}")?"ru.wikipedia.org":"en.wikipedia.org";
                var json=(await Get("https://"+host+"/w/api.php?action=query&list=search&format=json&srlimit=4&srsearch="+encoded,ct).ConfigureAwait(false)).Body;
                var pages=Store.Json().Deserialize<WikiResult>(json);
                if(pages!=null&&pages.query!=null&&pages.query.search!=null)rows=pages.query.search.Select(s=>new Source{Title=s.title,Url="https://"+host+"/?curid="+s.pageid,Excerpt=Clean(s.snippet),Location="выдача Википедии"}).ToList();
            }catch(Exception ex){if(!Recover(ex,ct))throw;}
            return rows;
        }
        static bool Recover(Exception ex,CancellationToken ct) {
            ct.ThrowIfCancellationRequested();
            return ex is IOException||ex is SocketException||ex is XmlException||ex is RegexMatchTimeoutException||ex is ArgumentException||ex is InvalidOperationException||ex is System.ComponentModel.Win32Exception;
        }
        static async Task Enrich(Source s,string query,CancellationToken ct) {
            try {
                var page=await Get(s.Url,ct).ConfigureAwait(false);
                string cleaned=Regex.Replace(page.Body,@"<(script|style|nav|footer|header|aside)\b[^>]*>.*?</\1\s*>|<!--.*?-->"," ",HtmlOptions,MatchTime);
                var title=Regex.Match(cleaned,@"<title\b[^>]*>(.*?)</title>",HtmlOptions,MatchTime);
                var main=Regex.Match(cleaned,@"<(main|article)\b[^>]*>(.*?)</\1\s*>",HtmlOptions,MatchTime);
                var terms=ResearchText.Words(query);
                var paragraphs=Regex.Matches(main.Success?main.Groups[2].Value:cleaned,@"<(p|h1|h2|li)\b[^>]*>(.*?)</\1\s*>",HtmlOptions,MatchTime)
                    .Cast<Match>().Select(m=>Clean(m.Groups[2].Value)).Where(t=>t.Length>=45).Distinct().Take(280).Select((t,i)=>new {t,i,Score=ResearchText.Words(t).Count(terms.Contains)+(i<2?1:0)});
                string excerpt=string.Join(" ",paragraphs.OrderByDescending(p=>p.Score).ThenBy(p=>p.i).Take(3).OrderBy(p=>p.i).Select(p=>ResearchText.Clip(p.t,650)));
                if(excerpt.Length<60 && page.Type.StartsWith("text/plain"))excerpt=ResearchText.Clip(ResearchText.Plain(page.Body),900);
                if(excerpt.Length<60)return;
                if(title.Success)s.Title=ResearchText.Clip(Clean(title.Groups[1].Value),150);
                s.Excerpt=ResearchText.Clip(excerpt,1250);s.Url=page.Url;s.Location="текст страницы";
            } catch(Exception ex){if(!Recover(ex,ct))throw;}
        }
        public static async Task<List<Source>> Search(string query,CancellationToken ct) {
            query=ResearchText.Clip(ResearchText.Plain(query),240);if(query.Length==0)return new List<Source>();
            Uri named;
            var direct=Regex.Match(query,@"https?://[^\s<>""']+",RegexOptions.IgnoreCase);
            List<Source> rows;
            if(direct.Success && PublicUrl(direct.Value.TrimEnd('.',',',')',']','?'),out named))rows=new List<Source>{new Source{Url=named.AbsoluteUri,Title=named.Host}};
            else if(direct.Success)return new List<Source>();
            else rows=await Lookup(query,ct).ConfigureAwait(false);
            var chosen=new List<Source>();var domains=new List<string>();
            foreach(var s in rows) {
                Uri u;if(!PublicUrl(s.Url,out u)||chosen.Any(x=>x.Url==s.Url)||domains.Count(d=>d==u.Host)>=2)continue;
                chosen.Add(s);domains.Add(u.Host);if(chosen.Count>=3)break;
            }
            await Task.WhenAll(chosen.Select(s=>Enrich(s,query,ct))).ConfigureAwait(false);
            return chosen.Where(s=>!string.IsNullOrWhiteSpace(s.Excerpt)).ToList();
        }
    }
}
