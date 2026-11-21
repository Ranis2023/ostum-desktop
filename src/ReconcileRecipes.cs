using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Sfera {
    internal sealed class ReconcileRecipe {
        public string Name {get;set;}
        public string[] Sheets {get;set;}
        public string[] Keys {get;set;}
        internal bool Valid {get{return !string.IsNullOrWhiteSpace(Name)&&Name.Length<=100&&new[]{Sheets,Keys}.All(a=>a!=null&&a.Length==2&&a.All(x=>!string.IsNullOrWhiteSpace(x)&&x.Length<=32767));}}
        internal static string Folder(ProjectRule project){
            string dir=Path.Combine(project.Root,".ostum-workflows");
            ProTools.UnderProject(project,dir);return dir;
        }
        internal static List<ReconcileRecipe> List(ProjectRule project){
            string folder=Folder(project);var recipes=new List<ReconcileRecipe>();
            if(!Directory.Exists(folder))return recipes;
            foreach(string file in Directory.EnumerateFiles(folder,"*.json")){
                Guid id;if(!Guid.TryParseExact(Path.GetFileNameWithoutExtension(file),"N",out id))continue;
                try{WorkPolicy.LocalPath(file);if(new FileInfo(file).Length>256*1024)continue;
                    var recipe=Store.Json().Deserialize<ReconcileRecipe>(File.ReadAllText(file));
                    if(recipe!=null&&recipe.Valid)recipes.Add(recipe);
                }catch(IOException){}catch(UnauthorizedAccessException){}catch(ArgumentException){}catch(InvalidOperationException){}
            }
            return recipes.OrderBy(r=>r.Name,StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        internal void Save(ProjectRule project){
            if(!Valid)throw new InvalidDataException("Проверьте название сценария, листы и ключевые столбцы.");
            string folder=Folder(project);Store.Write(Path.Combine(folder,Guid.NewGuid().ToString("N")+".json"),this);
        }
    }
    public sealed partial class ProjectWindow {
        async Task RunRecipe(){
            RequireProject();var recipes=ReconcileRecipe.List(project);
            int selected=Choose(DialogHost,"Сценарии сверки этого проекта",new[]{"Настроить новую сверку…"}.Concat(recipes.Select(r=>r.Name+"   ·   "+r.Keys[0]+" → "+r.Keys[1])).ToArray());
            if(selected<0)return;await Reconcile(selected==0?null:recipes[selected-1]);
        }
        bool ReviewReconcile(string[][] rows,ReconcileRecipe recipe){
            if(rows==null||rows.Length<2||rows[0].Length!=7)throw new InvalidDataException("Движок вернул неполную сверку.");
            var w=WorkspaceUi.Dialog(DialogHost,"Проверка различий",970,540);
            var root=new DockPanel{Margin=new Thickness(20)};w.Content=root;
            var top=ReviewTable.Top(root);top.Children.Add(WorkspaceUi.Text(w,recipe.Name??"Сверка двух книг","Title"));
            top.Children.Add(WorkspaceUi.Text(w,rows[1][0]=="Различий не найдено"?"Различий не найдено.":"Строк в отчёте · "+(rows.Length-1),"Subhead"));
            top.Children.Add(WorkspaceUi.Text(w,"Проверьте значения и адреса ячеек. Сохраните отдельный отчёт или запомните настройки для следующей пары книг.","Hint"));
            var bar=ReviewTable.Bottom(root);var grid=ReviewTable.New();root.Children.Add(grid);
            for(int i=0;i<7;i++)ReviewTable.Column(grid,rows[0][i],"["+i+"]",i>=5?156:i==0?163:108);
            grid.ItemsSource=rows.Skip(1).ToArray();
            var recipeButton=WorkspaceUi.Button(w,"Сохранить сценарий…",()=>{
                string name=Ask(w,"Сохранить сценарий","Название для следующего запуска",recipe.Name??"Сверка по "+recipe.Keys[0]);if(string.IsNullOrWhiteSpace(name))return;
                try{recipe.Name=name.Trim();recipe.Save(project);MessageBox.Show(w,"Сценарий сохранён в этом проекте.","OSTUM");}
                catch(Exception ex){MessageBox.Show(w,ex.Message,"Сценарий сверки");}
            },"Flat");bar.Children.Add(recipeButton);
            var cancel=WorkspaceUi.Button(w,"Закрыть",()=>w.DialogResult=false,"Flat");cancel.IsCancel=true;bar.Children.Add(cancel);
            bar.Children.Add(WorkspaceUi.Button(w,"Сохранить отчёт…",()=>w.DialogResult=true,"Primary"));return w.ShowDialog()==true;
        }
    }
}
