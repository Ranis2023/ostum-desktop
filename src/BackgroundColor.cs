using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Sfera {
    internal static class BackgroundColor {
        static readonly string[] Names={"оранжев|orange","красн|red","син|blue","зел[её]н|green","бел|white","ч[её]рн|black","ж[её]лт|yellow","фиолетов|purple","розов|pink","сер|gr[ae]y"};
        static readonly Color[] Colors={System.Windows.Media.Colors.Orange,System.Windows.Media.Colors.Red,System.Windows.Media.Colors.Blue,System.Windows.Media.Colors.Green,System.Windows.Media.Colors.White,System.Windows.Media.Colors.Black,System.Windows.Media.Colors.Yellow,System.Windows.Media.Colors.Purple,System.Windows.Media.Colors.Pink,System.Windows.Media.Colors.Gray};
        internal static bool TryEdit(string source,string request,string output,int width,int height,CancellationToken ct){
            if(source==null||!Regex.IsMatch(request,@"\b(?:фон\w*|background)\b",RegexOptions.IgnoreCase))return false;
            // Only accept a literal solid-background operation. Complex visual
            // instructions remain the responsibility of the image model.
            var words=Regex.Matches(request.ToLowerInvariant(),@"\p{L}+").Cast<Match>().Select(m=>m.Value).ToArray();
            var allowed=@"^(?:измени|изображение|картинк\w*|снимок|фотографи\w*|это|этой|этот|на|у|сделай|замени|закрась|залей|перекрась|поменяй|цвет\w*|однотонн\w*|сплошн\w*|пожалуйста|фон\w*|а|в|the|a|an|this|background|solid|color|colour|bright|plain|to|make|set|change|replace|image|with)$";
            int[] matches=Enumerable.Range(0,Names.Length).Where(i=>words.Any(word=>Regex.IsMatch(word,@"^(?:"+Names[i]+@")(?:ым|им|ый|ой|ий|юю|ее|ею|ую)?$"))).ToArray();
            if(matches.Length!=1||words.Any(word=>!Regex.IsMatch(word,allowed)&&!Regex.IsMatch(word,@"^(?:"+Names[matches[0]]+@")(?:ым|им|ый|ой|ий|юю|ее|ею|ую)?$")))return false;
            var original=new BitmapImage();
            using(var input=File.OpenRead(source)){original.BeginInit();original.CacheOption=BitmapCacheOption.OnLoad;original.DecodePixelWidth=width;original.StreamSource=input;original.EndInit();}original.Freeze();
            var scaled=new TransformedBitmap(original,new ScaleTransform((double)width/original.PixelWidth,(double)height/original.PixelHeight));
            var bmp=new FormatConvertedBitmap(scaled,PixelFormats.Bgra32,null,0);
            int w=bmp.PixelWidth,h=bmp.PixelHeight,n=w*h;var pixels=new byte[n*4];bmp.CopyPixels(pixels,w*4,0);
            int[] corners={0,w-1,n-w,n-1};
            Func<int,int,bool> similar=(a,b)=>{for(int ch=0;ch<4;ch++)if(Math.Abs(pixels[a*4+ch]-pixels[b*4+ch])>16)return false;return true;};
            int anchor=corners.OrderByDescending(c=>corners.Count(k=>similar(c,k))).First();
            if(corners.Count(k=>similar(anchor,k))<3||pixels[anchor*4+3]<245)return false;
            var seen=new bool[n];var todo=new Queue<int>();
            Action<int> add=k=>{if(!seen[k]&&similar(anchor,k)){seen[k]=true;todo.Enqueue(k);}};
            for(int x=0;x<w;x++){add(x);add(n-w+x);}
            for(int y=1;y<h-1;y++){add(y*w);add(y*w+w-1);}
            int count=0;
            while(todo.Count>0){int k=todo.Dequeue();if((++count&8191)==0)ct.ThrowIfCancellationRequested();
                if(k%w>0)add(k-1);if(k%w<w-1)add(k+1);if(k>=w)add(k-w);if(k<n-w)add(k+w);}
            if(count<n/12)return false;
            var target=Colors[matches[0]];for(int i=0;i<n;i++)if(seen[i]){pixels[i*4]=target.B;pixels[i*4+1]=target.G;pixels[i*4+2]=target.R;}
            ct.ThrowIfCancellationRequested();var image=BitmapSource.Create(w,h,96,96,PixelFormats.Bgra32,null,pixels,w*4);
            var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(image));using(var file=File.Create(output))png.Save(file);return true;
        }
    }
}
