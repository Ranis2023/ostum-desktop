using System;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace Sfera {
    // Each light uses a moving view into the same locally rendered 3D artwork.
    sealed class AgentLight : Border {
        const int Frames=48, Columns=12, FrameWidth=78, FrameHeight=87;
        static readonly Lazy<BitmapSource> Artwork=new Lazy<BitmapSource>(LoadArtwork);
        readonly ImageBrush ink;
        readonly int palette;
        bool active;
        static BitmapSource LoadArtwork() {
            using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("AgentLights.png")) {
                var bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;
                bitmap.StreamSource=stream;bitmap.EndInit();bitmap.Freeze();return bitmap;
            }
        }
        static int Palette(string id) {
            switch(id) {
                case "analyst":return 1;case "facts":return 2;case "review":return 3;
                case "edit":return 4;case "web":return 5;case "paint":return 6;
                case "chat":return 7;case "calc":return 8;case "plan":return 9;
                case "source":return 10;default:return 0;
            }
        }
        Rect Frame(int n) {
            return new Rect(n%Columns*FrameWidth,(palette*(Frames/Columns)+n/Columns)*FrameHeight,FrameWidth,FrameHeight);
        }
        public AgentLight(string id,bool running) {
            palette=Palette(id);active=running;
            Width=26;Height=29;VerticalAlignment=VerticalAlignment.Center;
            ink=new ImageBrush(Artwork.Value){ViewboxUnits=BrushMappingMode.Absolute,Viewbox=Frame(palette*7%Frames),Stretch=Stretch.Fill};
            Background=ink;RenderOptions.SetBitmapScalingMode(this,BitmapScalingMode.HighQuality);
            Loaded+=(s,e)=>Play();Unloaded+=(s,e)=>ink.BeginAnimation(TileBrush.ViewboxProperty,null);
        }
        void Play() {
            if(!active||!SystemParameters.ClientAreaAnimation)return;
            var cycle=TimeSpan.FromSeconds(2);
            var loop=new RectAnimationUsingKeyFrames{Duration=cycle,RepeatBehavior=RepeatBehavior.Forever};
            for(int i=0;i<Frames;i++)loop.KeyFrames.Add(new DiscreteRectKeyFrame(Frame((i+palette*7)%Frames),KeyTime.FromTimeSpan(TimeSpan.FromTicks(i*cycle.Ticks/Frames))));
            Timeline.SetDesiredFrameRate(loop,24);ink.BeginAnimation(TileBrush.ViewboxProperty,loop);
        }
        public void Rest() {
            active=false;var last=ink.Viewbox;ink.BeginAnimation(TileBrush.ViewboxProperty,null);ink.Viewbox=last;
        }
    }
}
