using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Sfera {
    public static class ResearchText {
        public static string Clip(string text, int limit) {
            text=(text ?? "").Trim();
            return text.Length<=limit ? text : text.Substring(0,limit-1)+"…";
        }
        public static string Plain(string text) { return Regex.Replace(text ?? "",@"\s+"," ").Trim(); }
        public static HashSet<string> Words(string value) {
            return new HashSet<string>(Regex.Matches((value??"").ToLowerInvariant(),@"[\p{L}\p{Nd}]{2,}")
                .Cast<Match>().Select(m=>m.Value.Length>7 ? m.Value.Substring(0,7) : m.Value));
        }
    }
    public sealed class DocumentPart {
        public int ScanNumber{get;set;}
        public double[] Box{get;set;}
        public string Label { get; set; }
        public string Text { get; set; }
    }
    public sealed class ResearchDocument {
        public Dictionary<string,string> ScanPictures{get;set;}
        public string ScanNotice{get;set;}
        public string Id { get; set; }
        public string Name { get; set; }
        public List<DocumentPart> Parts { get; set; }
        public bool Truncated { get; set; }
    }
    public static class Documents {
        const int MaxChars=600000;
        static readonly string[] extensions={".pdf",".docx",".txt",".md",".csv",".tsv",".json",".log"};
        public static bool Supported(string path) { return extensions.Contains(Path.GetExtension(path),StringComparer.OrdinalIgnoreCase); }
        static string Local(string id) { return Path.Combine(Store.Home,"documents",id+".json"); }
        public static void Save(ResearchDocument d) { Store.Write(Local(d.Id),d); }
        public static ResearchDocument Load(string id) {
            if (id==null || !Regex.IsMatch(id,"^[a-f0-9]{32}$")) return null;
            var file=Local(id);
            return File.Exists(file) ? Store.Json().Deserialize<ResearchDocument>(File.ReadAllText(file)) : null;
        }
        public static ResearchDocument Read(string file){return Read(file,true,System.Threading.CancellationToken.None);}
        internal static ResearchDocument Read(string file,bool preview,System.Threading.CancellationToken ct) {
            if (!Supported(file)) throw new IOException("Поддерживаются PDF, DOCX и текстовые файлы.");
            var info=new FileInfo(file);
            if (info.Length>32L*1024*1024) throw new IOException("Для вложения выберите файл до 32 МБ.");
            var doc=new ResearchDocument { Id=Guid.NewGuid().ToString("N"), Name=info.Name, Parts=new List<DocumentPart>() };
            int total=0;
            foreach (var section in Sections(info,doc,preview,ct)) {
                ct.ThrowIfCancellationRequested();
                string text=ResearchText.Plain(section.Text);
                for (int start=0;start<text.Length;) {
                    int size=Math.Min(1150,text.Length-start);
                    if (size<text.Length-start) { int cut=text.LastIndexOf(' ',start+size-1,size); if(cut>start+650)size=cut-start; }
                    if (total+size>MaxChars) {doc.Truncated=true;break;}
                    if (size>0) {doc.Parts.Add(new DocumentPart {Label=section.Label, Text=text.Substring(start,size),Box=section.Box,ScanNumber=section.ScanNumber});total+=size;}
                    start+=size; while(start<text.Length && text[start]==' ')start++;
                }
                if(doc.Truncated) break;
            }
            if (doc.Parts.Count==0) throw new IOException(info.Extension.Equals(".pdf",StringComparison.OrdinalIgnoreCase)?"В PDF не найден читаемый текст. "+(doc.ScanNotice??"Попробуйте более чёткий скан."):"Файл пуст или в нём нет текста.");
            return doc;
        }
        static IEnumerable<DocumentPart> Sections(FileInfo info,ResearchDocument result,bool preview,System.Threading.CancellationToken ct) {
            string ext=info.Extension.ToLowerInvariant();
            if (ext==".pdf") {
                var content=new SortedDictionary<int,List<DocumentPart>>();
                var scans=new List<int>();int length=0;
                using (var pdf=PdfDocument.Open(info.FullName))
                    foreach (var page in pdf.GetPages()){
                        ct.ThrowIfCancellationRequested();
                        string value=ContentOrderTextExtractor.GetText(page);
                        if(string.IsNullOrWhiteSpace(value)){
                            if(scans.Count<30)scans.Add(page.Number);else result.ScanNotice="Распознаны первые 30 страниц скана.";
                        }else{content[page.Number]=new List<DocumentPart>{new DocumentPart{Label="стр. "+page.Number,Text=value}};length+=value.Length;}
                        if(length>=MaxChars&&page.Number<pdf.NumberOfPages){result.ScanNotice="Извлечена начальная часть PDF.";break;}
                    }
                if(scans.Count>0){
                    ScanPage[] pages;
                    try{pages=LocalOcr.Read(info.FullName,scans,preview,ct);}
                    catch(IOException ex){if(content.Count==0)throw;result.ScanNotice=ex.Message;pages=new ScanPage[0];}
                    foreach(var page in pages){
                        if(!string.IsNullOrEmpty(page.Picture)){
                            if(result.ScanPictures==null)result.ScanPictures=new Dictionary<string,string>();
                            result.ScanPictures[page.Number.ToString(CultureInfo.InvariantCulture)]=page.Picture;
                        }
                        content[page.Number]=page.Lines.Select(line=>new DocumentPart{Label="стр. "+page.Number+" · OCR "+page.Language,ScanNumber=page.Number,Box=line.Box,Text=line.Text}).ToList();
                    }
                }
                foreach(var parts in content.Values)foreach(var part in parts)yield return part;
            } else if (ext==".docx") {
                using (var file=info.OpenRead()) using(var zip=new ZipArchive(file,ZipArchiveMode.Read)) {
                    var entry=zip.GetEntry("word/document.xml");
                    if (entry==null || entry.Length>24L*1024*1024) throw new IOException("Не удалось прочитать основной текст DOCX.");
                    XDocument xml;
                    using(var stream=entry.Open()) using(var reader=XmlReader.Create(stream,new XmlReaderSettings { DtdProcessing=DtdProcessing.Prohibit, XmlResolver=null, MaxCharactersInDocument=24000000 })) xml=XDocument.Load(reader);
                    XNamespace w="http://schemas.openxmlformats.org/wordprocessingml/2006/main";
                    int para=0;
                    foreach(var p in xml.Descendants(w+"p")) yield return new DocumentPart { Label="абз. "+(++para), Text=string.Concat(p.Descendants(w+"t").Select(n=>n.Value)) };
                }
            } else {
                var bytes=File.ReadAllBytes(info.FullName);
                string raw;
                using(var stream=new MemoryStream(bytes)) using(var reader=new StreamReader(stream,new UTF8Encoding(false,true),true)) {
                    try {raw=reader.ReadToEnd();} catch(DecoderFallbackException) { raw=Encoding.GetEncoding(1251).GetString(bytes); }
                }
                if(raw.IndexOf('\0')>=0)throw new IOException("Этот файл содержит двоичные данные.");
                using(var reader=new StringReader(raw)) {
                    string line; int n=0,start=1; var block=new StringBuilder();
                    while((line=reader.ReadLine())!=null) {
                        n++;block.AppendLine(line);
                        if(block.Length<900)continue;
                        yield return new DocumentPart {Label="строки "+start+"–"+n,Text=block.ToString()};block.Clear();start=n+1;
                    }
                    if(block.Length>0)yield return new DocumentPart {Label="строки "+start+"–"+n,Text=block.ToString()};
                }
            }
        }
        public static List<Source> Find(List<Turn> history,string query) {
            var docs=history.AsEnumerable().Reverse().Select(t=>t.DocumentId).Where(id=>id!=null&&Regex.IsMatch(id,"^[a-f0-9]{32}$")).Distinct().Take(4)
                .Select(id=>DocumentSearch.Open(Local(id),()=>Load(id))).Where(d=>d!=null).ToList();
            var perFile=docs.Select(d=>d.Find(query,docs.Count==1?5:3)).ToList();
            var result=new List<Source>();
            for(int rank=0;rank<5&&result.Count<6;rank++)
                foreach(var file in perFile)if(rank<file.Count&&result.Count<6)result.Add(file[rank]);
            return result;
        }
    }
    public static class Calculator {
        sealed class Parser {
            readonly string text;
            int at;
            public Parser(string expression) {text=Regex.Replace(expression,@"\s+","").Replace('×','*').Replace('÷','/').Replace('−','-').Replace(',','.');}
            bool Eat(char c){if(at>=text.Length || text[at]!=c)return false;at++;return true;}
            decimal Sum() { decimal v=Product();while(true){if(Eat('+'))v+=Product();else if(Eat('-'))v-=Product();else return v;} }
            decimal Product() { decimal v=Unary();while(true){if(Eat('*'))v*=Unary();else if(Eat('/'))v/=Unary();else return v;} }
            decimal Unary(){if(Eat('-'))return -Unary();if(Eat('+'))return Unary();return Power();}
            decimal Power() {
                decimal v;
                if(Eat('(')){v=Sum();if(!Eat(')'))throw new FormatException();}
                else {int begin=at;while(at<text.Length && ("0123456789.".IndexOf(text[at])>=0))at++;
                    if(!decimal.TryParse(text.Substring(begin,at-begin),NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out v))throw new FormatException();}
                if(Eat('%'))v/=100;
                if(Eat('^')){decimal exp=Unary(); if(exp!=decimal.Truncate(exp)||Math.Abs(exp)>32)throw new FormatException();decimal pow=1;for(int i=0;i<(int)Math.Abs(exp);i++)pow*=v;v=exp<0?1/pow:pow;}
                return v;
            }
            public decimal Evaluate(){var result=Sum(); if(at!=text.Length)throw new FormatException();return result;}
        }
        public static string Calculate(string expression) {
            if(string.IsNullOrWhiteSpace(expression)||expression.Length>160||!Regex.IsMatch(expression,@"^[0-9\s+*/×÷−().,%^\-]+$"))return null;
            try { return new Parser(expression).Evaluate().ToString("0.############################",CultureInfo.InvariantCulture); }
            catch(FormatException){return null;}catch(ArithmeticException){return null;}
        }
        static string IncludedFee(string request){
            const RegexOptions options=RegexOptions.IgnoreCase|RegexOptions.CultureInvariant;
            const string rate=@"(?<rate>\d{1,3}(?:[.,]\d{1,3})?)\s*%";
            const string money=@"(?<![\d.,+\-−])(?<total>(?:\d{1,3}(?:[ \u00a0\u202f]\d{3}){1,3}|\d{1,12})(?:[.,]\d{1,2})?)\s*(?:₽|руб(?:лей|ля|ль)?\b\.?)";
            if(!Regex.IsMatch(request,@"\b(?:сколько\s+(?:рублей\s+)?(?:(?:составляет|составит)\s+)?(?:именно\s+)?(?:сбор|комиссия)|какова\s+сумма\s+(?:сбора|комиссии))\s*[?.!]?\s*(?:только число[.!]?)?\s*$",options))return null;
            if(!Regex.IsMatch(request,@"\b(?:сбор(?:а)?|комисси[ия])\s*"+rate+@"\s+(?:уже\s+)?включ[её]н",options)||
               Regex.IsMatch(request,@"[-−]\s*\d|\b(?:без|скидк\w*|переведи|сравни|объясни|распиши|json)\b|\bот\s+(?:общей|полной|итоговой)\s+суммы",options))return null;
            var totalMatch=Regex.Match(request,money,options);var rateMatch=Regex.Match(request,rate,options);
            if(!totalMatch.Success||Regex.IsMatch(Regex.Replace(Regex.Replace(request,money,"",options),rate,"",options),@"\d")||
                totalMatch.NextMatch().Success||rateMatch.NextMatch().Success)return null;
            decimal total,percent;
            if(!decimal.TryParse(Regex.Replace(totalMatch.Groups["total"].Value,@"\s","").Replace(',','.'),NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out total)||
               !decimal.TryParse(rateMatch.Groups["rate"].Value.Replace(',','.'),NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out percent))return null;
            decimal fee=decimal.Round(total*percent/(100m+percent),2,MidpointRounding.AwayFromZero);
            return fee.ToString("0.##",CultureInfo.GetCultureInfo("ru-RU"))+(Regex.IsMatch(request,@"только число",options)?"":" руб.");
        }
        public static string Direct(string request) {
            if(string.IsNullOrWhiteSpace(request)||request.Length>200)return null;
            const RegexOptions ignore=RegexOptions.IgnoreCase|RegexOptions.CultureInvariant;
            if(request.IndexOf('%')>=0){
                string fee=IncludedFee(request);if(fee!=null)return fee;
            }
            if(Regex.IsMatch(request,@"(?:сколько\s+минут|(?:посчитай|вычисли|рассчитай)\s+(?:длительность|продолжительность|разниц\w*)\b.*\bминут)",ignore)
                &&!Regex.IsMatch(request,@"перерыв|обед|вычет|часов\w*\s+пояс|местно\w*\s+врем|переведи|объясни|формул|таблиц|json|utc|gmt|мск",ignore)){
                const string clock=@"([01]?\d|2[0-3]):([0-5]\d)";
                var interval=Regex.Match(request,@"\b(?:с|от)\s+"+clock+@"\s+(?:до|по)\s+"+clock+@"(?![:\d])(?:\s+(следующего\s+(?:дня|утра)|на следующий день))?",ignore);
                if(interval.Success&&!Regex.IsMatch(request.Remove(interval.Index,interval.Length),@"\d|\b(?:дней|суток|через день)\b",ignore)){
                    int start=60*int.Parse(interval.Groups[1].Value)+int.Parse(interval.Groups[2].Value);
                    int end=60*int.Parse(interval.Groups[3].Value)+int.Parse(interval.Groups[4].Value);
                    if(interval.Groups[5].Success)end+=1440;
                    if(end<start)return "Окончание раньше начала. Уточните, заканчивается ли интервал на следующий день.";
                    return (end-start).ToString(CultureInfo.InvariantCulture)+(Regex.IsMatch(request,@"только числ|одним числом",ignore)?"":" мин.");
                }
            }
            var prefix=Regex.Match(request,@"^\s*(?:сколько(?:\s+будет)?|посчитай(?:те)?|рассчитай(?:те)?|вычисли(?:те)?|calculate|what is)\b\s*:?\s*",RegexOptions.IgnoreCase);
            string expression=request.Substring(prefix.Length).Trim().TrimEnd('?','=').Trim();
            if(expression.Length>160||!Regex.IsMatch(expression,@"[+*/×÷−%^\-]"))return null;
            if(!prefix.Success&&Regex.IsMatch(expression,@"^(?:\d{4}-\d\d-\d\d|\d{1,2}/\d{1,2}/\d{2,4}|\+?\d{1,3}[- ]\d{3}[- ]\d{3}[- ]\d{2}[- ]\d{2})$"))return null;
            string result=Calculate(expression);
            if(result!=null)return result;
            if(Regex.IsMatch(expression,@"^\s*[+−-]?\d+(?:[.,]\d+)?\s*[/÷]\s*[+−-]?0+(?:[.,]0+)?\s*$"))return "Деление на ноль не определено.";
            return null;
        }
    }
}
