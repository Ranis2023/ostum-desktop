using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Sfera {
    internal static class OstumActionIcons {
        internal static readonly DataTemplate Symbol,Only,Label,MenuLabel;
        static readonly Dictionary<string,DataTemplate> tabLabels=new Dictionary<string,DataTemplate>();
        static readonly Dictionary<string,string> labels=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        static void Names(string kind,params string[] names){foreach(string name in names)labels.Add(name,kind);}
        static OstumActionIcons(){
            Names("mic","Записать голосовое сообщение","Подключить диктовку…","Голосовой диалог");
            Names("send","Отправить");Names("record","Завершить запись");Names("transcribe","Распознать");
            Names("stop","Стоп","Остановить","Остановить озвучивание");
            Names("close","Закрыть","Отмена","Отменить","Отменить распознавание");Names("art","Нарисовать","Вариация","Рисование","Рисование · создать изображение");
            Names("auto","Авто","Авто · подобрать инструменты");Names("team","Команда","Команда · несколько агентов");
            Names("history","История","Версии файла","Вернуть из архива","Вернуться к базе","Обновить","Проверить обновления");
            Names("archive","Архивировать диалог");Names("trash","Удалить диалог…","Отозвать версию");
            Names("edit","Изменить формат","Открыть для правок","Правка");Names("folder-add","Подключить папку");
            Names("copy","Копировать","Скопировать код");Names("sound","Озвучить");Names("play","Слушать");
            Names("save","Сохранить","Сохранить…","Сохранить выбранные","Сохранить WAV…","Сохранить PNG…");
            Names("chat","Спросить","Обсудить","Добавить комментарий");Names("compare","Сверить две таблицы Excel…","Сравнить с другим DOCX");
            Names("sheet","Открыть реестр поручений этой папки","Собрать реестр счетов…","Шаблоны и закупочные предложения…");
            Names("calendar","Экспортировать сроки в календарь…","Создать поручение","Создать поручение из фрагмента…");
            Names("file","Посмотреть источник","Открыть источник поручения","Открыть источник выбранного счёта","Импорт JSON");
            Names("check","Отметить выполненным","Применить","Выбрать","Применить в проекте","Принять");
            Names("uncheck","Снять выбор");Names("learn","Обучить Zarum","Учебные примеры");
            Names("lock","Обезличить контакты");Names("speed","Тесты разработчика");Names("forward","Продолжить");
            Names("download","Скачать и применить","Скачать обновление","Установить и перезапустить");
            Names("profile","Мой рабочий профиль…");Names("info","О программе и обновлениях…");Names("music","Создать инструментальную музыку…");
            Names("storage","Место на диске…","Очистить выбранное…");Names("search","Найти сообщение…","К сообщению","Найти решения и поручения");
            Names("compare","Сценарии сверки Excel…","Сохранить сценарий…");Names("record","Записать с микрофона…","Совещание · запись и протокол…");
            Names("sound","Открыть запись…","Прослушать реплику");Names("save","Сохранить протокол…","Сохранить отчёт…");
            Names("screen","Приложить снимок экрана","Окна и приложения…","Блокнот","Калькулятор","Проводник","Развернуть","Свернуть","На передний план");
            Names("panel","Левая половина","Правая половина");Names("external","Перейти","Исходный код","Что нового");
            Symbol=Make(null,true);Only=Make("button",true);Label=Make("button",false);MenuLabel=Make("button",false,true);
        }
        internal static DataTemplate Tab(string kind){
            DataTemplate template;if(!tabLabels.TryGetValue(kind,out template)){template=Make(kind,false);tabLabels.Add(kind,template);}
            return template;
        }
        internal static DataTemplate For(string label){
            string kind;return labels.TryGetValue(label,out kind)?Tab(kind):null;
        }
        internal static T Install<T>(T view) where T:FrameworkElement{
            view.Resources["OstumSymbol"]=Symbol;view.Resources["OstumOnly"]=Only;view.Resources["OstumLabel"]=Label;
            view.Resources["OstumMenu"]=MenuLabel;
            foreach(string kind in new[]{"files","edit","search","globe"})view.Resources["OstumTab"+kind]=Tab(kind);
            return view;
        }
        internal static OstumIcon Menu(string label,string kind=null){
            if(kind==null&&!labels.TryGetValue(label,out kind))return null;
            return new OstumIcon{Kind=kind,Width=16,Height=16};
        }
        sealed class ActionKind : IMultiValueConverter {
            public object Convert(object[] values,Type target,object arg,CultureInfo culture){
                var tag=values[0] as string;
                if(tag!=null&&tag.StartsWith("only:",StringComparison.Ordinal))tag=tag.Substring(5);
                if(tag=="mic"&&object.Equals(values[1],"Отменить распознавание"))return "transcribe";
                string kind;
                // File names, titles and other variable labels must not change an explicitly chosen symbol.
                if(tag==null||tag=="send"||tag=="mic"||tag=="auto"||tag=="archive"||tag=="check"||tag=="close"||tag=="download"||tag=="back"||tag=="learn"){
                    if(labels.TryGetValue(values[1] as string??"",out kind))return kind;
                }
                return tag;
            }
            public object[] ConvertBack(object value,Type[] types,object arg,CultureInfo culture){throw new NotSupportedException();}
        }
        static readonly ActionKind resolve=new ActionKind();
        static DataTemplate Make(string kind,bool only,bool menu=false){
            var icon=new FrameworkElementFactory(typeof(OstumIcon));
            icon.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);
            if(only){icon.SetValue(FrameworkElement.WidthProperty,20d);icon.SetValue(FrameworkElement.HeightProperty,20d);}
            if(kind==null)icon.SetBinding(OstumIcon.KindProperty,new Binding());
            else if(kind=="button"){
                var button=new RelativeSource(RelativeSourceMode.FindAncestor,typeof(Button),1);
                var binding=new MultiBinding{Converter=resolve};
                binding.Bindings.Add(new Binding("Tag"){RelativeSource=button});
                binding.Bindings.Add(new Binding("Content"){RelativeSource=button});
                icon.SetBinding(OstumIcon.KindProperty,binding);
            }else icon.SetValue(OstumIcon.KindProperty,kind);
            if(only)return new DataTemplate{VisualTree=icon};
            var group=new FrameworkElementFactory(typeof(DockPanel));
            icon.SetValue(FrameworkElement.MarginProperty,new Thickness(0,0,6,0));group.AppendChild(icon);
            if(menu){
                var arrow=new FrameworkElementFactory(typeof(OstumIcon));
                arrow.SetValue(OstumIcon.KindProperty,"chevron");arrow.SetValue(FrameworkElement.WidthProperty,12d);arrow.SetValue(FrameworkElement.HeightProperty,12d);
                arrow.SetValue(DockPanel.DockProperty,Dock.Right);arrow.SetValue(FrameworkElement.MarginProperty,new Thickness(4,0,0,0));
                arrow.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);group.AppendChild(arrow);
            }
            var label=new FrameworkElementFactory(typeof(TextBlock));label.SetBinding(TextBlock.TextProperty,new Binding());
            label.SetValue(TextBlock.TextWrappingProperty,TextWrapping.Wrap);
            label.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);group.AppendChild(label);
            return new DataTemplate{VisualTree=group};
        }
    }
}
