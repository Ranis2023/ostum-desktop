using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Sfera;

class ProbeRow {public string id{get;set;}public string kind{get;set;}public string text{get;set;}public string topic{get;set;}public string expected{get;set;}}
class AppProbe {
    static T Field<T>(Desktop d,string name) where T:class {return (T)typeof(Desktop).GetField(name,BindingFlags.NonPublic|BindingFlags.Instance).GetValue(d);}
    static string Canon(string text){return Regex.Replace((text??"").ToUpperInvariant(),@"[\s\p{Pd}]","");}
    static string JsonForm(object v){
        var dict=v as IDictionary<string,object>;if(dict!=null)return "{"+string.Join(",",dict.Keys.OrderBy(k=>k).Select(k=>Store.Json().Serialize(k)+":"+JsonForm(dict[k])))+"}";
        var array=v as object[];if(array!=null)return "["+string.Join(",",array.Select(JsonForm))+"]";return Store.Json().Serialize(v);
    }
    static bool MatchesJson(string response,string expected){try{return JsonForm(Store.Json().DeserializeObject(response))==JsonForm(Store.Json().DeserializeObject(expected));}catch(ArgumentException){return false;}catch(InvalidOperationException){return false;}}
    static void Isolate(string folder){
        string models=Store.Load().Models;
        typeof(Store).GetField("Home").SetValue(null,folder);
        if(Store.Home!=folder||!WorkPolicy.Within(Store.Chats,folder)||!WorkPolicy.Within(Store.Images,folder))
            throw new Exception("Cannot isolate application data");
        Store.Save(new Preferences{Models=models,Chat="quality",Art="sketch",Think=false,AllowWeb=false,Personalize=false});
    }
    static void OldTurns(Conversation c,ProbeRow row){
        c.Turns.Add(new Turn{Role="user",Text="Проект «"+row.topic+"». Согласованный код образца — "+row.expected+". Это пригодится при передаче в печать."});
        c.Turns.Add(new Turn{Role="assistant",Text="Данные по образцу приняты."});
        for(int i=0;i<14;i++){
            c.Turns.Add(new Turn{Role="user",Text="По сопутствующей заявке "+(110+i)+" проверь размеры упаковки и наличие крепежа."});
            c.Turns.Add(new Turn{Role="assistant",Text="Размеры переданы на проверку. Ожидаем подтверждение склада."});
        }
    }
    static async Task Run(Desktop d,string engines,ProbeRow[] rows,string file){
        var window=d.Window;var obs=new List<object>();var input=(TextBox)window.FindName("Input");var transcript=(StackPanel)window.FindName("Transcript");
        foreach(var row in rows){
            ((Button)window.FindName("NewChat")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));var chat=Field<Conversation>(d,"active");
            if(row.topic!=null){OldTurns(chat,row);typeof(Desktop).GetMethod("ShowChat",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(d,null);}
            input.Text=row.text;var sw=Stopwatch.StartNew();double first=-1;
            var task=(Task)typeof(Desktop).GetMethod("Send",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(d,null);
            while(!task.IsCompleted){
                await Task.Delay(22);var live=transcript.Children.OfType<StackPanel>().LastOrDefault();var text=live==null?null:live.Children.OfType<TextBlock>().LastOrDefault();
                if(first<0&&text!=null&&text.Text.Length>0&&text.Text!="Zarum"&&text.Text!="Ostum Desktop"&&text.Text!=row.text)first=sw.Elapsed.TotalSeconds;
                if(sw.Elapsed.TotalMinutes>4)throw new TimeoutException(row.id);
            }
            await task;double elapsed=sw.Elapsed.TotalSeconds;var turn=chat.Turns.Last();string ans=turn.Role=="assistant"?turn.Text??"":"";var steps=turn.Steps??new List<string>();
            bool quick=steps.Any(s=>s.Contains("прямой расчёт"));
            bool correct;if(row.kind=="verdict")correct=ans.TrimStart().TrimStart('*','«').StartsWith(row.expected,StringComparison.OrdinalIgnoreCase)&&steps.Any(s=>s.Contains("проверка утверждения"));else if(row.kind=="json")correct=MatchesJson(ans,row.expected);else if(row.kind=="quote")correct=ans.Trim(' ','.','«','»','\n','\r').Equals(row.expected,StringComparison.OrdinalIgnoreCase);else if(row.kind=="mixed")correct=!quick&&ans.Count(char.IsLetter)>=12;else if(row.kind=="invalid")correct=Regex.IsMatch(ans,@"нол|нул|нельз|не определ|невозмож|не имеет",RegexOptions.IgnoreCase);else if(row.topic!=null)correct=Canon(ans).Contains(Canon(row.expected));else correct=Regex.IsMatch(Regex.Replace(ans,@"(?<=\d)[ \u00a0\u202f](?=\d)",""),@"(?<!\d)"+Regex.Escape(row.expected)+@"(?!\d)");
            obs.Add(new{id=row.id,kind=row.kind,correct=correct,direct=quick,ttft=first<0?elapsed:first,latency=elapsed,answer=ans,steps=steps});File.WriteAllText(file,Store.Json().Serialize(obs));
        }
    }
    [STAThread]static int Main(string[] args){string output=args[3];try{
        Isolate(args[0]);var rows=Store.Json().Deserialize<ProbeRow[]>(File.ReadAllText(args[1]));var app=new Application();var d=new Desktop(args[2]);var w=d.Window;int rc=1;
        w.ShowActivated=false;w.ShowInTaskbar=false;w.WindowStartupLocation=WindowStartupLocation.Manual;w.Left=-4500;w.Top=15;
        w.ContentRendered+=async(s,e)=>{try{await Run(d,args[2],rows,output);rc=0;}catch(Exception ex){File.WriteAllText(output+".error",ex.ToString());}finally{w.Close();}};app.Run(w);return rc;
    }catch(Exception ex){File.WriteAllText(output+".error",ex.ToString());return 1;}}
}
