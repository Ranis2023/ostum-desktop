using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Sfera {
    // Original 24-unit OSTUM drawings. The second stroke picks out a single detail in copper.
    public sealed class OstumIcon : FrameworkElement {
        sealed class Mark {
            internal readonly Geometry Line,Detail;
            internal Mark(string line,string detail){Line=Shape(line);Detail=Shape(detail);}
            static Geometry Shape(string data){var g=Geometry.Parse(data);g.Freeze();return g;}
        }
        static readonly Dictionary<string,Mark> marks=new Dictionary<string,Mark>{
            {"mic",new Mark("M8 10.5V7a4 4 0 0 1 8 0v4a4 4 0 0 1-8 0z M5 11a7 7 0 0 0 14 0 M12 18v3 M9 21h6","M11 6.5h2 M11 9.5h2")},
            {"record",new Mark("M3.5 9v6 M7 6v12 M17 7v10 M20.5 9v6","M10.5 8.5h3v7h-3z")},
            {"transcribe",new Mark("M3 9v6 M6.5 6v12 M10 9v6 M14 7h7 M14 12h7","M14 17h4")},
            {"attach",new Mark("M8 13.5l6.8-6.8a2.5 2.5 0 0 1 3.6 3.5l-8 8a4.5 4.5 0 0 1-6.4-6.4l8-8a5.7 5.7 0 0 1 8.1 8.1","M10 14.5l5.5-5.5")},
            {"send",new Mark("M4 4.5l16.5 6.6q1.5.9 0 1.8L4 19.5l2.8-7.4z","M7 12h7.5")},
            {"stop",new Mark("M7 4.5h10L20 7.5v9L17 19.5H7L4 16.5v-9z","M10 10h4v4h-4z")},
            {"close",new Mark("M6.5 6.5l11 11 M6.5 17.5l5.5-5.5","M14.5 9.5l3-3")},
            {"plus",new Mark("M4 5h11l4 4v9H9l-4 3v-3H3V6z","M10.8 8.5v6 M7.8 11.5h6")},
            {"tools",new Mark("M3.5 6.5l4-3 4 3-4 3z M14 15l3.5-2.5L21 15v4l-3.5 2.5L14 19z M3.5 14.5l4-2.5 4 2.5v4l-4 2.5-4-2.5z","M15 6.5h6 M18 3.5v6")},
            {"chevron",new Mark("M5.5 9l6.5 6.5","M12 15.5L18.5 9")},
            {"panel",new Mark("M4 4.5h16v15H4z M10 4.5v15","M6.5 8v3")},
            {"files",new Mark("M3 5h18v14H3z M14 5v14","M16.5 9h2 M16.5 12.5h2")},
            {"more",new Mark("M4.5 11.2l1 .8-1 .8-1-.8z M11.9 11.2l1 .8-1 .8-1-.8z","M19.2 11.2l1 .8-1 .8-1-.8z")},
            {"file",new Mark("M5 3h9l5 5v13H5z M14 3v5h5 M8 12h8","M8 16h5")},
            {"folder",new Mark("M3 6h6l2 2h9v11H3z","M4 10.5h8")},
            {"folder-add",new Mark("M3 6h6l2 2h9v11H3z","M12 11v5 M9.5 13.5h5")},
            {"workspace",new Mark("M3 8h18v11H3z M8 8V5h8v3 M3 12q9 4 18 0","M10 13h4v2h-4z")},
            {"settings",new Mark("M3 6h7 M14 6h7 M3 12h12 M19 12h2 M3 18h3 M10 18h11","M10 4v4 M16 10v4 M7 16v4")},
            {"help",new Mark("M3 5q5-1 9 2 4-3 9-2v13q-5-1-9 2-4-3-9-2z M12 7v13","M6 9l3 1 M6 12l3 1")},
            {"archive",new Mark("M3 4.5h18v5H3z M5 9.5v10h14v-10","M10 13h4")},
            {"history",new Mark("M4 10a8 8 0 1 1 1 7 M3 5.5V10h4.5","M12 7.5v5l3 1.8")},
            {"team",new Mark("M12 3l3 2v3l-3 2-3-2V5z M5.5 14l3 2v3l-3 2-3-2v-3z M18.5 14l3 2v3l-3 2-3-2v-3z","M12 10v2l-5 2 M12 12l5 2")},
            {"photo",new Mark("M4 5h16v14H4z M4.5 16l4.5-4 4 3 3-3 4 4","M15.5 8.5h.8v.8h-.8z")},
            {"art",new Mark("M5 15.5L15.5 5l3.5 3.5L8.5 19l-5 1z M12.5 8l3.5 3.5","M18.5 2.5v3 M17 4h3")},
            {"auto",new Mark("M12 2.5l9.5 9.5-9.5 9.5L2.5 12z","M15.5 8.5l-2 5-5 2 2-5z")},
            {"globe",new Mark("M21 12a9 9 0 1 1-18 0 9 9 0 0 1 18 0z M12 3q-8 9 0 18 M3 12h18","M12 3q5 5 4 11")},
            {"search",new Mark("M16 10a6 6 0 1 1-12 0 6 6 0 1 1 12 0z M14.5 14.5L21 21","M7 10a3 3 0 0 1 3-3")},
            {"storage",new Mark("M5.5 5h13l2 8.5v4a2 2 0 0 1-2 2h-13a2 2 0 0 1-2-2v-4z M4 13.5h16","M14 16.5h1 M17 16.5h1")},
            {"edit",new Mark("M5 16L16 5l3 3L8 19l-4 1z M13.5 7.5l3 3","M12 20h8")},
            {"chat",new Mark("M3.5 5h17v12h-10l-5 4v-4h-2z","M7 9h8 M7 12h5")},
            {"save",new Mark("M4 4h13l3 3v13H4z M8 4v6h8V4 M8 20v-6h8v6","M13 6v2")},
            {"check",new Mark("M3.5 12l5.5 5.5L20 6.5","M15 17.5l5-5")},
            {"uncheck",new Mark("M5 5h14v14H5z","M9 12h6")},
            {"model",new Mark("M5 6h14v13H5z M8 3v3 M12 3v3 M16 3v3 M8 19v2 M12 19v2 M16 19v2 M3 9h2 M19 9h2 M3 15h2 M19 15h2","M9 10h6v5H9z")},
            {"learn",new Mark("M2.5 9L12 4l9.5 5L12 14z M6 11v5q6 5 12 0v-5","M21 10v6")},
            {"speed",new Mark("M3 17a9 9 0 1 1 18 0z M6 13l1.5.5 M12 8v1.5","M12 15l4-4.5")},
            {"download",new Mark("M4 15v5h16v-5 M12 3.5v11.5","M7.5 11l4.5 4.5 4.5-4.5")},
            {"lock",new Mark("M4.5 10h15v10h-15z M8 10V7a4 4 0 0 1 8 0v3","M12 14v2.5")},
            {"external",new Mark("M13 5H5v14h14v-8","M13 4h7v7 M11 13l9-9")},
            {"qr",new Mark("M3 3h7v7H3z M14 3h7v7h-7z M3 14h7v7H3z","M14 14h3v3h4 M14 20h3 M20 20h1")},
            {"copy",new Mark("M8 8h12v13H8z M16 4V3H4v14h1","M11 12h6")},
            {"up",new Mark("M12 20V5 M5.5 11.5L12 5","M12 5l6.5 6.5")},
            {"down",new Mark("M12 4v15 M5.5 12.5L12 19","M12 19l6.5-6.5")},
            {"back",new Mark("M20 12H5 M11.5 5.5L5 12","M5 12l6.5 6.5")},
            {"forward",new Mark("M4 12h15 M12.5 5.5L19 12","M19 12l-6.5 6.5")},
            {"home",new Mark("M3 11l9-8 9 8 M5 10v10h14V10","M10 20v-6h4v6")},
            {"music",new Mark("M8 16V6l11-2v11 M8 16q-5-1-5 2.5t5 .5z M19 15q-5-1-5 2.5t5 .5z","M9 9l9-1.8")},
            {"screen",new Mark("M2.5 4.5h19v13h-19z M12 18v3 M8 21h8","M6 8h4 M6 8v3")},
            {"sound",new Mark("M3 10h4l5-5v14l-5-5H3z M15 8q4 4 0 8","M18 5q7 7 0 14")},
            {"play",new Mark("M6 4l14 8-14 8z","M9 9v6")},
            {"profile",new Mark("M15.5 7a3.5 3.5 0 1 1-7 0 3.5 3.5 0 0 1 7 0z M4 20v-2q1-5 8-5t8 5v2z","M10 16.5h4")},
            {"trash",new Mark("M3.5 7h17 M6 7l1 13h10l1-13 M9 7V4h6v3","M10 10.5v6 M14 10.5v6")},
            {"sheet",new Mark("M3.5 4.5h17v15h-17z M3.5 10h17 M10 10v9","M13.5 14h3.5")},
            {"calendar",new Mark("M3.5 6h17v14h-17z M3.5 10h17 M8 3v5 M16 3v5","M8 14h3 M14 16.5h2")},
            {"compare",new Mark("M3 5h7v14H3z M14 5h7v14h-7z","M6 9h1 M17 13h1")},
            {"info",new Mark("M21 12a9 9 0 1 1-18 0 9 9 0 0 1 18 0z M12 11v5","M12 7.5v.5")}
        };
        internal static ICollection<string> Kinds {get{return marks.Keys;}}
        internal static bool Known(string name){return name!=null&&marks.ContainsKey(name);}
        public static readonly DependencyProperty KindProperty=DependencyProperty.Register("Kind",typeof(string),typeof(OstumIcon),
            new FrameworkPropertyMetadata(null,FrameworkPropertyMetadataOptions.AffectsRender,(o,e)=>((OstumIcon)o).UpdateMotion()));
        public string Kind {get{return (string)GetValue(KindProperty);}set{SetValue(KindProperty,value);}}
        static readonly DependencyProperty inkProperty=TextElement.ForegroundProperty.AddOwner(typeof(OstumIcon),
            new FrameworkPropertyMetadata(Brushes.Black,FrameworkPropertyMetadataOptions.Inherits|FrameworkPropertyMetadataOptions.AffectsRender));
        static readonly Brush copper=Frozen(172,82,42),peach=Frozen(255,194,140);
        static Brush Frozen(byte r,byte g,byte b){var brush=new SolidColorBrush(Color.FromRgb(r,g,b));brush.Freeze();return brush;}
        public OstumIcon(){
            Width=Height=18;IsHitTestVisible=false;Focusable=false;
            IsVisibleChanged+=(s,e)=>UpdateMotion();
        }
        void UpdateMotion(){
            if(Kind=="record"&&IsVisible&&SystemParameters.ClientAreaAnimation&&!SystemParameters.HighContrast){
                BeginAnimation(OpacityProperty,new DoubleAnimation(1,.65,TimeSpan.FromMilliseconds(750)){AutoReverse=true,RepeatBehavior=RepeatBehavior.Forever});
            }else if(HasAnimatedProperties)BeginAnimation(OpacityProperty,null);
        }
        static readonly ConditionalWeakTable<Brush,Pen> pens=new ConditionalWeakTable<Brush,Pen>();
        static Pen Stroke(Brush brush){return pens.GetValue(brush,b=>{
            var pen=new Pen(b,1.85){StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round,LineJoin=PenLineJoin.Round};
            if(b.IsFrozen)pen.Freeze();return pen;
        });}
        protected override void OnRender(DrawingContext dc){
            Mark mark;if(Kind==null||!marks.TryGetValue(Kind,out mark))return;
            double scale=Math.Min(ActualWidth,ActualHeight)/24;
            var ink=(Brush)GetValue(inkProperty);var solid=ink as SolidColorBrush;
            bool light=solid!=null&&(solid.Color.R+solid.Color.G+solid.Color.B)>510;
            dc.PushTransform(new MatrixTransform(scale,0,0,scale,(ActualWidth-24*scale)/2,(ActualHeight-24*scale)/2));
            dc.DrawGeometry(null,Stroke(ink),mark.Line);
            dc.DrawGeometry(null,Stroke(SystemParameters.HighContrast?ink:light?peach:copper),mark.Detail);
            dc.Pop();
        }
    }
}
