using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Sfera {
    public sealed class LocalModel {
        public string Id {get;set;}
        public string Path {get;set;}
        public string[] Parts {get;set;}
        public long[] Sizes {get;set;}
        public string Projector {get;set;}
        public string Name {get;set;}
        public string Architecture {get;set;}
        public long Bytes {get;set;}
        public bool Available(){
            if(Parts==null||Sizes==null||Parts.Length==0||Parts.Length!=Sizes.Length||Path!=Parts[0])return false;
            try{foreach(var file in Parts)WorkPolicy.CheckImport(file);if(!string.IsNullOrEmpty(Projector))WorkPolicy.CheckImport(Projector);
                return Parts.Select((file,i)=>{var f=new FileInfo(file);return f.Exists&&f.Length==Sizes[i];}).All(x=>x)&&(string.IsNullOrEmpty(Projector)||File.Exists(Projector));}
            catch(Exception e){if(e is IOException||e is UnauthorizedAccessException||e is ArgumentException)return false;throw;}
        }
        static int Width(uint type){switch(type){case 0:case 1:case 7:return 1;case 2:case 3:return 2;case 4:case 5:case 6:return 4;case 10:case 11:case 12:return 8;default:return 0;}}
        static void Skip(BinaryReader r,long n){if(n<0||n>r.BaseStream.Length-r.BaseStream.Position)throw new InvalidDataException("Повреждены метаданные GGUF.");r.BaseStream.Seek(n,SeekOrigin.Current);}
        static string Str(BinaryReader r,bool keep){
            long n=checked((long)r.ReadUInt64());if(!keep){Skip(r,n);return null;}if(n>65536||n>r.BaseStream.Length-r.BaseStream.Position)throw new InvalidDataException("Повреждён заголовок GGUF.");
            return new UTF8Encoding(false,true).GetString(r.ReadBytes((int)n));
        }
        static string Value(BinaryReader r,uint t,bool keep){
            if(t==8)return Str(r,keep);
            if(t==9){
                uint elem=r.ReadUInt32();long count=checked((long)r.ReadUInt64());int width=Width(elem);
                if(count>16000000||elem==9||(width==0&&elem!=8))throw new InvalidDataException("Неподдерживаемая структура GGUF.");
                if(width>0)Skip(r,checked(count*width));else for(long i=0;i<count;i++)Str(r,false);
                return null;
            }
            int bytes=Width(t);if(bytes==0)throw new InvalidDataException("Неизвестный тип GGUF.");Skip(r,bytes);return null;
        }
        public static Dictionary<string,string> Metadata(string file){
            var result=new Dictionary<string,string>(StringComparer.Ordinal);
            using(var s=File.Open(file,FileMode.Open,FileAccess.Read,FileShare.Read))using(var r=new BinaryReader(s)){
                if(r.ReadUInt32()!=0x46554747)throw new InvalidDataException("Ожидается файл модели GGUF.");
                uint v=r.ReadUInt32();if(v<2||v>3||r.ReadUInt64()==0)throw new InvalidDataException("Движок поддерживает GGUF v2/v3 с тензорами.");
                ulong n=r.ReadUInt64();if(n>65536)throw new InvalidDataException("Повреждён каталог GGUF.");
                for(ulong i=0;i<n;i++){
                    string key=Str(r,true);uint type=r.ReadUInt32();bool wanted=key=="general.name"||key=="general.type"||key=="general.architecture"||key=="adapter.type";
                    string value=Value(r,type,wanted);if(wanted&&value!=null)result[key]=value;
                }
            }return result;
        }
        public static LocalModel Inspect(string input,string projector=null){
            string file=System.IO.Path.GetFullPath(input),dir=System.IO.Path.GetDirectoryName(file);
            if(!file.EndsWith(".gguf",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Выберите модель в формате GGUF.");
            var split=Regex.Match(System.IO.Path.GetFileName(file),@"^(.*)-(\d{5})-of-(\d{5})\.gguf$",RegexOptions.IgnoreCase);
            var parts=new[]{file};
            if(split.Success){int n=int.Parse(split.Groups[3].Value);if(n<1||n>512)throw new InvalidDataException("Неверное число частей GGUF.");
                parts=Enumerable.Range(1,n).Select(i=>System.IO.Path.Combine(dir,split.Groups[1].Value+"-"+i.ToString("D5")+"-of-"+n.ToString("D5")+".gguf")).ToArray();}
            var sizes=parts.Select(p=>{var f=new FileInfo(p);if(!f.Exists||f.Length<64)throw new FileNotFoundException("Отсутствует часть модели: "+f.Name);return f.Length;}).ToArray();
            file=parts[0];var meta=Metadata(file);string architecture,name,kind;
            if(!meta.TryGetValue("general.architecture",out architecture)||architecture=="clip"||meta.ContainsKey("adapter.type")||(meta.TryGetValue("general.type",out kind)&&kind!="model"))throw new InvalidDataException("Для чата выберите основные веса языковой модели GGUF.");
            if(!meta.TryGetValue("general.name",out name)||string.IsNullOrWhiteSpace(name))name=System.IO.Path.GetFileNameWithoutExtension(file);
            if(!string.IsNullOrEmpty(projector)){
                projector=System.IO.Path.GetFullPath(projector);var pm=Metadata(projector);
                if(!(pm.TryGetValue("general.architecture",out kind)&&kind=="clip"))throw new InvalidDataException("Выбранный файл не содержит зрительный проектор.");
            }
            string id;using(var hash=SHA256.Create())id=BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(file.ToUpperInvariant()))).Replace("-","").ToLowerInvariant();
            return new LocalModel{Id="local-"+id,Name=name,Architecture=architecture,Path=file,Parts=parts,Sizes=sizes,Bytes=sizes.Sum(),Projector=projector};
        }
    }
    public sealed class CatalogEntry {
        public string title {get;set;}public string license {get;set;}public string revision {get;set;}public string source {get;set;}public ModelFile[] files {get;set;}
        public bool builtin {get;set;}
        public string upstream {get;set;} public string parameters {get;set;} public string description {get;set;} public string score {get;set;}
        public int ram {get;set;} public int vram {get;set;}
        public string Label {get{return builtin?title+" ★":title;}}
        public string Vision {get{return files.Length>1?"Да":"—";}}
        public string Size {get{return Store.Size(files.Sum(f=>f.bytes));}}
        public string BenchmarkLabel {get{return string.IsNullOrEmpty(score)?"—":score;}}
        public static CatalogEntry[] All(){
            var entries=Store.Json().Deserialize<CatalogEntry[]>(Store.Resource("model-catalog.json"));
            foreach(var e in entries.Where(x=>x.builtin))e.files=Store.ChatModels[0].Files.Select(id=>Store.Files[id]).ToArray();
            return entries;
        }
        public string DirectoryIn(string library){return builtin?library:System.IO.Path.Combine(library,"catalog",revision);}
    }
}
