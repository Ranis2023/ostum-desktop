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
        readonly ScaleTransform flame=new ScaleTransform(1,1);
        readonly TranslateTransform drift=new TranslateTransform();
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
                case "source":return 10;case "docs":return 5;case "diff":return 8;case "music":return 6;default:return 0;
            }
        }
        Rect Frame(int n) {
            return new Rect(n%Columns*FrameWidth,(palette*(Frames/Columns)+n/Columns)*FrameHeight,FrameWidth,FrameHeight);
        }
        public AgentLight(string id,bool running) {
            palette=Palette(id);active=running;
            Width=20;Height=23;VerticalAlignment=VerticalAlignment.Center;
            var motion=new TransformGroup();motion.Children.Add(flame);motion.Children.Add(drift);
            RenderTransformOrigin=new Point(.5,.86);RenderTransform=motion;
            ink=new ImageBrush(Artwork.Value){ViewboxUnits=BrushMappingMode.Absolute,Viewbox=Frame(palette*7%Frames),Stretch=Stretch.Fill};
            Background=ink;RenderOptions.SetBitmapScalingMode(this,BitmapScalingMode.HighQuality);
            IsVisibleChanged+=(s,e)=>{if(IsVisible)Play();else Pause();};
            Unloaded+=(s,e)=>Pause();
        }
        void Play() {
            if(!active||!SystemParameters.ClientAreaAnimation)return;
            var cycle=TimeSpan.FromMilliseconds(1640+palette*27);
            var loop=new RectAnimationUsingKeyFrames{Duration=cycle,RepeatBehavior=RepeatBehavior.Forever};
            for(int i=0;i<Frames;i++)loop.KeyFrames.Add(new DiscreteRectKeyFrame(Frame((i+palette*7)%Frames),KeyTime.FromTimeSpan(TimeSpan.FromTicks(i*cycle.Ticks/Frames))));
            Timeline.SetDesiredFrameRate(loop,28);ink.BeginAnimation(TileBrush.ViewboxProperty,loop);
            var breathe=new DoubleAnimation(.93,1.08,TimeSpan.FromMilliseconds(540+palette*21)) {AutoReverse=true,RepeatBehavior=RepeatBehavior.Forever,EasingFunction=new SineEase{EasingMode=EasingMode.EaseInOut}};
            Timeline.SetDesiredFrameRate(breathe,24);flame.BeginAnimation(ScaleTransform.ScaleYProperty,breathe);
            var flicker=new DoubleAnimation(-.65,.65,TimeSpan.FromMilliseconds(380+palette*13)){AutoReverse=true,RepeatBehavior=RepeatBehavior.Forever,EasingFunction=new SineEase()};
            Timeline.SetDesiredFrameRate(flicker,24);drift.BeginAnimation(TranslateTransform.XProperty,flicker);
        }
        void Pause() {
            var last=ink.Viewbox;ink.BeginAnimation(TileBrush.ViewboxProperty,null);ink.Viewbox=last;
            flame.BeginAnimation(ScaleTransform.ScaleYProperty,null);
            drift.BeginAnimation(TranslateTransform.XProperty,null);
        }
        public void Rest() {
            active=false;Pause();
        }
        public void Extinguish(TimeSpan length) {
            Rest();
            if(!IsVisible||!SystemParameters.ClientAreaAnimation)return;
            var ease=new QuadraticEase{EasingMode=EasingMode.EaseIn};
            flame.BeginAnimation(ScaleTransform.ScaleXProperty,new DoubleAnimation(1,.32,length){EasingFunction=ease});
            flame.BeginAnimation(ScaleTransform.ScaleYProperty,new DoubleAnimation(1,.13,length){EasingFunction=ease});
            drift.BeginAnimation(TranslateTransform.YProperty,new DoubleAnimation(0,-2,length));
            BeginAnimation(OpacityProperty,new DoubleAnimation(1,0,length));
        }
    }
}
