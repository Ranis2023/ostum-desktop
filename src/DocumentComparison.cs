using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Sfera {
    internal static class DocumentComparison {
        // Only a request for a literal comparison takes this path. Analytical questions stay with Zarum.
        static readonly Regex intent=new Regex(@"^\s*(?:пожалуйста[, ]+)?(?:сравни(?:те)?\s+(?:две последние |два последних |последние две |две |два |оба |обе )?(?:версии|редакции|документа|файла|вложения|документы|файлы)(?: между собой)?|(?:покажи|перечисли) (?:изменения|различия|отличия) между (?:двумя )?(?:версиями|редакциями|файлами|документами)|что изменилось (?:во второй версии|в новой редакции))\s*[.!?]*\s*$",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
        public static bool Requested(string text){return intent.IsMatch(text??"");}

        public static string Run(List<Turn> history,Turn answer,CancellationToken ct) {
            var ids=history.AsEnumerable().Reverse().Select(t=>t.DocumentId).Where(id=>!string.IsNullOrEmpty(id)).Distinct().Take(2).Reverse().ToArray();
            if(ids.Length<2)return "Для сверки нужны два вложения в этом чате. Отправьте сначала исходную версию, затем новую и напишите «Сравни две версии».";
            ct.ThrowIfCancellationRequested();
            var oldDoc=Documents.Load(ids[0]);var newDoc=Documents.Load(ids[1]);
            if(oldDoc==null||newDoc==null)return "Одно из двух последних вложений недоступно. Прикрепите его снова и повторите сверку.";
            var original=oldDoc.Parts;var revised=newDoc.Parts;
            var positions=new Dictionary<string,Queue<int>>(StringComparer.Ordinal);
            for(int i=0;i<original.Count;i++){
                ct.ThrowIfCancellationRequested();Queue<int> queue;
                if(!positions.TryGetValue(original[i].Text,out queue))positions[original[i].Text]=queue=new Queue<int>();
                queue.Enqueue(i);
            }
            var matched=new bool[original.Count];var added=new List<DocumentPart>();var reordered=new List<DocumentPart>();int highest=-1;
            foreach(var part in revised){
                ct.ThrowIfCancellationRequested();Queue<int> queue;
                if(!positions.TryGetValue(part.Text,out queue)||queue.Count==0){added.Add(part);continue;}
                int index=queue.Dequeue();matched[index]=true;if(index<highest)reordered.Add(part);else highest=index;
            }
            var removed=original.Where((p,i)=>!matched[i]).ToList();
            answer.Sources=new List<Source>{Origin(oldDoc,removed),Origin(newDoc,added)};
            var message=new StringBuilder("Сверка двух последних вложений\n[1] Исходная версия — ").Append(oldDoc.Name)
                .Append("\n[2] Новая версия — ").Append(newDoc.Name).Append("\n\n");
            if(removed.Count==0&&added.Count==0&&reordered.Count==0)message.AppendLine("Извлечённый текст и порядок фрагментов совпадают.");
            else{
                message.Append("Удалено фрагментов: ").Append(removed.Count).Append(". Добавлено: ").Append(added.Count).AppendLine(".");
                if(reordered.Count>0){message.AppendLine("Изменился порядок совпадающих фрагментов.");Examples(message,"В другом порядке [2]",reordered);}
                Examples(message,"Было [1]",removed);Examples(message,"Стало [2]",added);
            }
            if(oldDoc.Truncated||newDoc.Truncated)message.AppendLine("\nБольшое вложение загружено частично. Сверка охватывает доступный текст.");
            return message.ToString().TrimEnd();
        }
        static Source Origin(ResearchDocument doc,List<DocumentPart> changed) {
            var part=changed.FirstOrDefault()??doc.Parts.FirstOrDefault();
            return new Source{Title=doc.Name,Location=part==null?"текст вложения":part.Label,Excerpt=part==null?"":part.Text};
        }
        static void Examples(StringBuilder message,string heading,List<DocumentPart> parts) {
            if(parts.Count==0)return;
            message.AppendLine().AppendLine(heading);
            foreach(var part in parts.Take(4))message.Append("• ").Append(part.Label).Append(" — ").AppendLine(part.Text);
            if(parts.Count>4)message.Append("Показаны первые 4 из ").Append(parts.Count).AppendLine(".");
        }
    }
}
