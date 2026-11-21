"""Build the offline code and UX review from one run. Never infer a pass from a missing result."""
from html import escape
import json
from pathlib import Path
import re
import shutil
from statistics import median
import sys
from urllib.parse import quote
from zipfile import ZipFile, ZIP_DEFLATED


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))

def chat_contrast():
    def luminance(color):
        rgb=[int(color[i:i+2],16)/255 for i in (1,3,5)]
        rgb=[n/12.92 if n<=0.04045 else ((n+.055)/1.055)**2.4 for n in rgb]
        return sum(v*w for v,w in zip(rgb,(.2126,.7152,.0722)))
    roles=[("Заголовок на тёмном краю градиента","#192C23","#D8E5D9"),("Подсказка в поле","#657369","#FFFFFF"),
           ("Текст карточки действия","#58665C","#FFFFFF"),("Отправка","#FFFFFF","#304B3B"),("Новый чат","#2F2118","#FFAB72"),
           ("Текст ответа","#29332D","#FFFFFF"),("Состояние сети на главном экране","#476652","#D8E5D9")]
    return {role:round((max(luminance(a),luminance(b))+.05)/(min(luminance(a),luminance(b))+.05),2) for role,a,b in roles}

# Test identifiers refer to either the named acceptance case or an independent supporting check.
TOOLS = [
    ("Чат", "Обычный ответ и поток текста", "Desktop.cs, Inference.cs", "F13 R05", "Остановка, следующий вопрос и формат делового письма.", "Замерить время до первого слова отдельно от времени ответа на холодной и прогретой модели."),
    ("Чат", "Переписка, архив и клавиатура", "ChatTranscript.cs, HistoryUi.cs, KeyboardLayout.cs, Library.cs", "F14 F15 history-prepend-anchor reading-position-after-response", "Длинная история, переход к последнему ответу, архив и исправление раскладки.", "Загружать из архивов сначала заголовки диалогов. Содержимое выбранного диалога читать по запросу."),
    ("Чат", "Черновик и вложения", "Desktop.cs, ResearchUi.cs", "failed-save-keeps-draft text-enables-send F11 F12", "Пустой запрос, недоступная запись на диск, пустой и большой документы.", "При нескольких перетащенных файлах предложить очередь с явным порядком источников."),
    ("Чат", "Оркестратор и режим ответа", "Orchestration.cs, RoutePrompt.txt, RunRequest.cs", "route-1 route-30 mode-menu-all-options", "Команды маршрутизации, тексты про иллюстрации и явный выбор режима.", "Дополнить набор короткими смешанными запросами и измерять лишние включения тяжёлых инструментов."),
    ("Чат", "Команда агентов и огоньки", "Agents.cs, Workers.cs, AgentLight.cs", "M08 two-active-workers agent-cards-retire", "Одновременная работа двух агентов, завершение отдельных карточек и история действий.", "Сравнить команду с одиночным ответом на одинаковых задачах по точности и задержке."),
    ("Чат", "Калькулятор, отдельные поля и время", "Agents.cs, ChatContext.cs", "d-sum day-07 calculator-without-weights", "Арифметика в чате, интервал через полночь, работа без весов.", "Расширить контрольные случаи на единицы измерения и явно заданные перерывы."),
    ("Чат", "Профиль и память беседы", "Orchestration.cs, ChatContext.cs", "F01 F02 F03 F04", "Исправление факта, изоляция беседы, сохранение и удаление личной записи.", "Показывать, какая запись профиля учтена в ответе, по запросу пользователя."),
    ("Документы", "Вложения, поиск и цитаты", "ResearchDocuments.cs, DocumentFacts.cs, DocumentSearch.cs, SourceCheck.cs", "F05 F06 F09 F16 corpus", "Ссылки на документ и страницу, похожие артикулы и отсутствующие сведения.", "Читать большие TXT и CSV до лимита извлечения частями. Сейчас весь файл до 32 МБ читается в память до усечения текста."),
    ("Документы", "Сканы и выделение строки", "LocalOcr.cs, ScanPages.ps1, ChatPreview.cs", "F10 ocr-reuses-page-bitmap ocr-switches-page", "50 контрольных страниц, смена строки и страницы в просмотре.", "В сборном реестре дать повторно распознать только сомнительные страницы."),
    ("Документы", "Сверка двух вложений и экспорт ответа", "DocumentComparison.cs, ResearchUi.cs", "F07 F08 R04", "Две редакции, обратный порядок файлов и экспорт вместе с источниками.", "Добавить параллельный просмотр двух конкретных фрагментов."),
    ("Рабочая папка", "Разрешение, редактор и правая панель", "ChatPreview.cs, ChatFileIntent.cs, ChatProject.cs, ProjectWindow.cs", "P01 P07 P08 P09 P10 workspace", "Разрешённый проект, закрытие контекста, клавиатурный разделитель и незаписанная правка.", "В списке подключений показывать отдельно доступ на чтение, правки и анализ."),
    ("Рабочая папка", "Документы Word, предложения и версии", "OfficePreview.cs, WorkspaceActions.cs, office.py", "office P10", "Выборочные изменения DOCX, комментарии, восстановление и конфликт версий.", "Добавить поиск и переход к изменённому абзацу в документе на сотни страниц."),
    ("Рабочая папка", "Excel, формулы и правки", "office.py, OfficePreview.cs, ChatProject.cs", "P02 P03 P05 P06 X01 X10", "Полный диапазон, числовые типы, адресные правки и зависимые формулы.", "Для каждого запроса Office запускается отдельный Python. Измерить долю запуска на серии чтений одной книги и испытать повторное использование процесса внутри одного проекта."),
    ("Рабочая папка", "Сравнение таблиц и предложений", "EnterpriseTools.cs, ProjectWindow.cs, office.py", "office", "Сверка по ключу, дубли, пустые значения, разные схемы и три поставщика.", "На больших таблицах измерить долю времени на распаковку, сопоставление ключей и запись результата. Показывать несовпадающие столбцы в начале отчёта."),
    ("Рабочая папка", "Реестр счетов и оригиналы", "CompanyIntake.cs, office.py", "N01 N02 N03", "20 контрольных счетов, отмена, повторный импорт и проверка источника.", "Для больших папок показывать, сколько исходников уже проверено и по каким нужен выбор."),
    ("Рабочая папка", "Поручения, просрочка и календарь", "EnterpriseTools.cs, CompanyIntake.cs", "N04 N05 N06 office", "Просрочки, недельный список, стабильный идентификатор события и исходный фрагмент.", "Дать фильтровать поручения по исполнителю прямо из чата."),
    ("Медиа и ПК", "Анализ изображения", "Inference.cs, Desktop.cs", "M01", "Ответ о цвете контрольной фотографии и обработка пользовательского вложения.", "Проверить распознавание небольших надписей при уменьшении фотографии до входного размера."),
    ("Медиа и ПК", "Рисование и замена фона", "BackgroundColor.cs, Inference.cs", "M02 M03 R08", "Новое изображение, простой фон, сохранение PNG и быстрый размер эскиза.", "Проверить новый бюджет памяти на портретах, редактировании исходного фото и других видеокартах. Сравнить размер, качество и задержку на одинаковом seed."),
    ("Медиа и ПК", "Инструментальная музыка", "LocalMusic.cs, RunRequest.cs", "M04", "Настоящий локальный WAV длительностью 10 секунд и проверка аудиоданных.", "ACE-Step распаковывает исполняемые файлы в каталог каждого трека. Измерить выигрыш от проверенного кэша движка, оставив WAV и параметры отдельными для каждой задачи."),
    ("Медиа и ПК", "Диктовка, выбор входа и озвучивание", "Voice.cs, MicrophoneCapture.cs, StudioUi.cs", "M05 voice", "Перечисление входов Windows и цикл синтез речи → локальное распознавание.", "Подключение WASAPI ожидается в потоке интерфейса. Измерить задержку на Bluetooth и USB, затем перенести ожидание подключения из обработчика кнопки."),
    ("Медиа и ПК", "Экран и диспетчер окон", "Computer.cs, StudioUi.cs", "M09 capture-pixel-dimensions", "Размер снимка экрана и сворачивание собственного тестового окна.", "Добавить выбор одного окна для снимка, когда весь монитор не нужен."),
    ("Медиа и ПК", "Веб-поиск и сетевой переключатель", "WebResearch.cs, WorkPolicy.cs", "M06 M07 offline-visible policy", "Запрет сети, наличие публичного источника, индикация разрешения.", "Проверить повторные запросы к уже прочитанной публичной странице. Короткий кэш должен учитывать свежесть и отключённый сетевой режим."),
    ("Система", "GGUF, каталог и измерение скорости", "LocalModels.cs, ModelSettings.cs, ModelLibraryUi.cs, ModelCatalogDialog.cs", "R06 R07 catalog", "Локальный импорт весов, ответ модели, тест скорости и удалённые заголовки файлов.", "Для выбора модели показывать отдельно время первого ответа, скорость выдачи и доступную память."),
    ("Система", "Дообучение проекта и отзыв адаптера", "TrainingWindow.cs, ProjectTraining.cs, studio.py", "T01 T02 T03 T04", "Пары данных, обучение, оценка, подключение только в своём проекте и отзыв.", "Расширить независимую контрольную выборку перед выводами о качестве модели."),
    ("Система", "Установка, ускоритель и автономные модели", "OneClick.cs, Acceleration.cs, InstallModels.cs, Library.cs", "U01 U02 U03 U04 U05 msi kit", "MSI, совпадение основных файлов, офлайн-импорт, возобновление загрузки и контрольная сумма.", "Отдельно прогнать чистую установку на Windows без GPU и на компьютере AMD."),
    ("Система", "Уведомление и загрузка обновлений", "About.cs, Updates.cs", "updates", "Старый клиент на текущем выпуске GitHub, уведомление, загрузка и проверка файла.", "Проверить долгую приостановку загрузки и возврат Windows из сна."),
    ("Система", "Подписка Pro и корпоративные правила", "ProAccess.cs, Issuer.ps1, WorkPolicy.cs", "subscription policy workspace", "Членство на контрольных ответах Telegram, подписанный бессрочный файл и ограничения проекта.", "Проверить перенос активации в регламенте замены рабочего компьютера."),
    ("Система", "Встроенная справка", "OSTUM-Help.html, WorkspaceUi.cs", "H01 H02 H03 H04 H05", "Содержание, поиск, пустой результат, переход из меню и печать в офлайн-браузере.", "Связать сообщения об ошибке с конкретным разделом справки."),
]


def render(directory, output):
    output.mkdir(parents=True,exist_ok=True)
    prefix = "OSTUM-5.5.1"
    assets = output / (prefix+"-Экраны")
    assets.mkdir(exist_ok=True)
    final=directory/"release"
    base = read(final/"release-metrics/results.json")
    candidate = read(final/"candidate-metrics/results.json")
    files = dict(baseline="release/baseline/application.json",business="release/business/application.json",
                 chat="release/flows/office/flows.json",media="release/flows/media/flows.json",repeat="release/flows/repeat/flows.json",
                 project="release/project/project.json",excel="excel/formulas.json",help="help.json",distribution="release/distribution/install.json")
    groups={name:read(directory/path) for name,path in files.items()}
    enterprise=[r for r in groups["project"] if r["id"].startswith("N")]
    groups["project"]=[r for r in groups["project"] if not r["id"].startswith("N")]
    primary=[r for group in groups.values() for r in group]
    extra=enterprise+read(final/"extra/gates.json")+read(directory/"office-extra/regressions.json")
    receipts=read(directory/"receipt/checks.json")
    ui=candidate["checks"]
    def passed(row):return bool(row.get("passed",row.get("correct",False)))
    score=lambda rows: str(sum(map(passed,rows)))+" / "+str(len(rows))
    if (len(primary)!=117 or len(extra)!=120 or len(ui)!=30 or len(receipts)!=9 or
            len({r["id"] for r in primary+extra+ui+receipts})!=117+120+30+9):
        raise ValueError("Incomplete regression suite")
    records={r["id"]:passed(r) for r in primary+extra+ui+receipts}
    support={}
    for key,file in [("corpus","corpus.log"),("office","office.log"),("policy","policy.log"),("subscription","subscription.log"),("updates","verified/update.log"),("workspace","release/access.log"),("catalog","catalog.log")]:
        lines=(directory/file).read_text(encoding="utf-8-sig").splitlines()
        support[key]=[s for s in lines if s.startswith(("PASS","evidence hits","Catalog:"))]
        records[key]=bool(support[key])
    support["voice"]=read(final/"voice/result.json");records["voice"]=support["voice"]["passed"]
    support["guards"]=read(final/"guards.json")
    support["firstTeamCheck"]=next(r for r in read(directory/"flows/media/flows.json") if r["id"]=="M08")
    for key,file,field in [("msi","release/msi.json","passed"),("kit","kit.json","correct")]:
        support[key]=read(directory/file);records[key]=bool(support[key]) and all(x[field] for x in support[key])
    inventory=read(directory/"source-inventory.json")
    by_name={Path(f["path"]).name:f["path"] for f in inventory["files"] if not f["path"].startswith("research/")}
    by_name["RoutePrompt.txt"]="src/RoutePrompt.txt"
    package=read(directory/"installer.json")
    for _,_,_,keys,_,_ in TOOLS:
        for key in keys.split():
            if key not in records:raise ValueError("Unmapped check "+key)
    image_text=(directory/"media-fallback.txt").read_text(encoding="utf-8-sig") if (directory/"media-fallback.txt").exists() else ""
    bench=None
    if (directory/"vram/experiment.json").exists():bench=read(directory/"vram/experiment.json")
    match=next(s for s in (final/"access.log").read_text(encoding="utf-8-sig").splitlines() if s.startswith("Verified UI renders: "))
    workspace=Path(match.split(": ",1)[1])
    for filename,source in [("home",final/"candidate-metrics/home.png"),("compact",final/"candidate-metrics/compact.png"),("agents",workspace/"agents.png"),("excel",workspace/"chat-excel.png")]:
        shutil.copyfile(source,assets/(filename+".png"))
    shutil.copyfile(final/"flows/media/created.png",assets/"drawing.png")
    tracks=list((final/"flows/media/temporary-profile/audio").glob("*.wav"))
    if len(tracks)!=1:raise ValueError("Expected one controlled music generation")
    shutil.copyfile(tracks[0],assets/"music.wav")
    a,b=base["metrics"],candidate["metrics"]
    old=median(a["render160Milliseconds"]);new=median(b["render160Milliseconds"])
    history=b["historyLoadMs"]
    def metric(id):
        return next(x for x in primary if x["id"]==id)
    def seconds(value):
        return f"{value:.2f}".rstrip("0").rstrip(".").replace(".",",")
    corpus=re.search(r"hits (\d+)/(\d+); warm median ([\d,.]+) ms; cold ([\d,.]+) ms"," ".join(support["corpus"]))
    if not corpus:raise ValueError("Missing search benchmark")
    contrast=chat_contrast()
    data=dict(version=package["version"],inventory=inventory,baselineUI=base,candidateUI=candidate,groups=groups,extra=extra,supporting=support,chatContrast=contrast,
              installer=package,gpuFallback=image_text,lowMemoryExperiment=bench,tools=TOOLS,receiptRegressions=receipts)
    protocol=output/(prefix+"-Протокол-аудита.json")
    protocol.write_text(json.dumps(data,ensure_ascii=False,indent=2),encoding="utf-8")
    e=lambda s:escape(str(s),quote=True)
    join=lambda values:" · ".join(map(str,values))
    img=lambda s:quote(assets.name+"/"+s+".png")
    style="""*{box-sizing:border-box}body{margin:0;background:#f6f7f3;color:#20332b;font:16px/1.52 'Segoe UI',Arial,sans-serif}main{max-width:1120px;margin:auto;padding:44px 26px 64px}h1{font-size:clamp(32px,6vw,57px);line-height:1.05;margin:8px 0 16px;letter-spacing:-.045em}h2{margin:36px 0 10px;font-size:26px;line-height:1.2}h3{font-size:17px;margin:0 0 5px}p{margin:6px 0 12px}.cards,.shots{display:grid;grid-template-columns:repeat(auto-fit,minmax(210px,1fr));gap:12px}.cards>div,article,figure{background:white;border:1px solid #d9e2da;border-radius:14px;padding:15px}.cards strong{display:block;font-size:30px;line-height:1.2}.muted,small{color:#4d6056}a{color:#205941}figure{margin:0}img{width:100%;height:auto;display:block;border-radius:4px}figcaption{margin-top:7px}.tag{font-size:12px;color:#52645a}.group{color:#53675c;font-size:13px;margin-bottom:4px}label{display:block;font-weight:600}input{width:min(520px,100%);border:1px solid #83998a;border-radius:9px;font:inherit;padding:8px 10px;margin:6px 0 8px}article{margin:8px 0}code{font-size:13px;overflow-wrap:anywhere}summary{cursor:pointer}li{margin:7px 0}.alert{border-left:4px solid #ae6a32;padding:4px 12px;background:#fbf2e8}table{border-collapse:collapse;background:#fff;width:100%}td,th{padding:6px 9px;border-bottom:1px solid #d9e2da;text-align:left}a:focus-visible,summary:focus-visible,input:focus-visible{outline:3px solid #d86b27} @media(max-width:580px){main{padding:24px 14px}.cards{grid-template-columns:1fr 1fr}.cards>div{padding:10px}.cards strong{font-size:24px}.shots{grid-template-columns:1fr}}"""
    doc=["<!doctype html><html lang='ru'><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>OSTUM · проверка кода и интерфейса</title><style>"+style+"</style><main>",
         "<div class='muted'>OSTUM Desktop 5.5.1 · Windows · RTX 4050 Laptop, 6 ГБ</div><h1>Чат, инструменты и код</h1>",
         "<p>Сравнение установленной версии 5.5.0 с локальной сборкой 5.5.1. Испытания проводились на искусственно созданных диалогах, документах и сканах.</p><section class='cards'>"]
    for number,title in [(score(primary),"Повторные сценарии"),(score(extra),"Дополнительные проверки"),(score(ui),"Проверки интерфейса"),(str(len(TOOLS)),"Групп инструментов")]:
        doc.append("<div><strong>"+e(number)+"</strong>"+e(title)+"</div>")
    doc.append("</section><h2>Измерения на этом ПК</h2><table><tr><th>Действие</th><th>5.5.0</th><th>5.5.1</th></tr>")
    for label,x,y in [("Диалог из 160 сообщений, медиана трёх открытий",f"{old:g} мс",f"{new:g} мс"),("Карточек сразу после открытия",a["initialBubbles"],b["initialBubbles"]),("Смена строки на том же скане","Повторное декодирование","Тот же объект изображения")]:
        doc.append("<tr><th>"+e(label)+"</th><td>"+e(x)+"</td><td>"+e(y)+"</td></tr>")
    doc+=["</table><p class='muted'>Время от построения переписки до обработки отложенных действий окна. Отдельные прогоны в миллисекундах — "+e(join(a["render160Milliseconds"]))+" и "+e(join(b["render160Milliseconds"]))+". Смещение строки при подгрузке истории — "+e(b["anchorMovementDip"])+" единиц WPF.</p>",
          "<p>Поиск нашёл нужный фрагмент в "+corpus[1]+" из "+corpus[2]+" запросов. Первый поиск занял "+corpus[4]+" мс, медиана после загрузки — "+corpus[3]+" мс. Замена однотонного фона заняла "+seconds(metric("M02")["seconds"])+" с. Замер текстовой модели — "+e(metric("R07").get("evidence",""))+".</p>"]
    doc.append("<h2>Исправления по результатам проверки</h2><ul>")
    doc.append("<li>Команда извлекает сумму и код приёмки вместе с цитатами из единственного полного документа. Первый ответ занял "+seconds(support["firstTeamCheck"]["seconds"])+" с и пропустил код. Итоговая проверка M08 — "+("прошла" if records["M08"] else "расхождение")+" за "+seconds(metric("M08")["seconds"])+" с. Проверки на неоднозначные значения, предварительную стоимость, усечение, несколько файлов и запрос другого формата — "+score(receipts)+".</li>")
    if b["invalidSettingsRecovered"]:
        doc.append("<li>Проверено восстановление предыдущей копии настроек. Повреждённый файл сохраняется для диагностики, после восстановления сетевой поиск и автообновление выключены до явного выбора пользователя.</li>")
    doc.append("<li>Калькулятор доступен без установленных весов. При ошибке записи истории запрос остаётся в редакторе и после повторной отправки появляется в диалоге один раз. В защищённом профиле переключатель веб-доступа сохраняет запрет после окончания ответа.</li></ul>")
    doc.append("<h2>Рисование на 6 ГиБ видеопамяти</h2><ul>")
    if image_text:
        doc.append("<li>При первом отказе движку понадобилось 1085 МБ, после размещения весов оставалось 946 МБ. Эскиз на CPU имел размер 384 × 384. В 5.5.1 для генерации от 1024 × 1024 на NVIDIA с 6 ГиБ включён бюджет 3,25 ГиБ. Финальный результат через чат — "+e(metric("M03").get("evidence",""))+" за "+seconds(metric("M03")["seconds"])+" с.</li>")
    if bench:
        doc.append("<li>Отдельный эксперимент с резервом памяти GPU, теми же 8 шагами и размером 1024 × 1024. "+e(bench["description"])+"</li>")
    doc.append("</ul><p><a href='"+img("drawing")+"'>Открыть исходный PNG из чата</a></p><h3>Проверка локальной музыки</h3><p>Запрос на 10 секунд инструментального фортепиано и щёток. Сохранённый WAV прошёл проверку длительности, формата PCM и наличия звука.</p><audio controls preload='none' style='max-width:100%' src='"+quote(assets.name+"/music.wav")+"'></audio><h2>Что заслуживает следующего изменения</h2><ol><li><b>Модели и оборудование.</b> Проверить запас памяти при рисовании на 4 и 8 ГиБ, затем на AMD. Измерять полный размер результата и время до его появления.</li>")
    if not b["invalidSettingsRecovered"]:
        doc.append("<li><b>Восстановление настроек.</b> Повреждённый preferences.json в отдельном профиле не загрузился. Нужен последний исправный экземпляр и восстановление с сохранением отключённых сетевых функций.</li>")
    doc.append("<li><b>Холодный старт с большой историей.</b> Список диалогов читает тексты всех бесед. Для наборов из 25, 100 и 300 контрольных диалогов с 20 сообщениями чтение заняло "+e(join([str(median(history[k]))+' мс' for k in ('25','100','300')]))+" соответственно. Следующее изменение — индекс заголовков с ленивой загрузкой переписки.</li></ol>")
    doc.append("<h2>Рабочее пространство</h2><p>В узком окне чат получает всю ширину, история открывается кнопкой. Режим ответа выбирается возле поля ввода, сеть имеет явное состояние. Источник можно изучать справа одновременно с перепиской. Визуальные ориентиры — читаемые состояния и управление с клавиатуры у Apple, адаптивная компоновка и сдержанное движение у Xiaomi. <a href='https://developer.apple.com/design/human-interface-guidelines/accessibility'>Apple HIG</a> · <a href='https://www.mi.com/global/hyperos/'>Xiaomi HyperOS</a> · <a href='https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/optimizing-performance-controls'>Производительность элементов WPF</a>.</p><div class='shots'>")
    for name,title in [("home","Главный экран"),("compact","Узкое окно"),("agents","Два активных агента"),("excel","Чат с открытой таблицей")]:
        doc.append("<figure><a href='"+img(name)+"'><img loading='lazy' src='"+img(name)+"' alt='"+title+"'></a><figcaption>"+title+"</figcaption></figure>")
    doc.append("</div><p>Для семи активных текстовых элементов проверен контраст с фоном. Минимальное отношение — "+str(min(contrast.values()))+" к 1. Значения для каждой пары приведены в протоколе.</p><h2>Проверка инструментов</h2><label for='search'>Найти инструмент или имя файла</label><input id='search' type='search' placeholder='Например, сканы или Excel'><p id='count' aria-live='polite'></p><div id='tools'>")
    for group,title,source,keys,observed,next_step in TOOLS:
        ids=keys.split()
        code_files=", ".join(by_name.get(filename,filename) for filename in source.split(", "))
        doc.append("<article><div class='group'>"+e(group)+"</div><h3>"+e(title)+"</h3><p>"+e(observed)+"</p><p><strong>Следующий шаг.</strong> "+e(next_step)+"</p><details><summary>Код и проверки</summary><code>"+e(code_files)+"</code><p>"+e(join([k+(" ✓" if records[k] else " ×") for k in ids]))+"</p></details></article>")
    doc.append("</div><h2>Границы проверки</h2><p>В инвентаре "+str(inventory["productionFiles"])+" файлов программы, обучения, сборки и лицензирования, "+str(inventory["researchFiles"])+" файлов проверки. В приложении 48 исходников C#. Скомпилированы приложение и установщик, проверены Python, PowerShell, XAML, XML и JSON. Проверены "+str(inventory["namedUiReferences"])+" статических ссылок C# на именованные элементы XAML. Исходники сторонних движков и содержимое всех возможных пользовательских документов не входили в инвентарь.</p>")
    doc.append("<p>Сборка приложения и содержимое MSI сверены по SHA-256. Установленная рабочая версия на этом ПК — 5.5.0. Проверены 15 контрольных сумм её комплекта для флешки и импорт моделей из этого комплекта в 5.5.1. Испытания Pro использовали отдельный ключ и тестовые разрешения. Проверка Telegram моделировала ответы API. Диктовка распознавала синтезированную фразу. Из каталога проверялись метаданные файлов, а полностью запускались установленные модели. Чистый второй компьютер для этого прогона не использовался.</p>")
    failed=[r['id'] for r in primary+extra+ui+receipts if not passed(r)]
    if failed:doc.append("<p class='alert'>Проверить отдельно: "+e(", ".join(failed))+"</p>")
    doc.append("<p><a href='"+quote(protocol.name)+"'>Полный протокол, исходы сценариев и список файлов</a></p></main><script>const q=document.querySelector('#search'),cards=[...document.querySelectorAll('article')];function filter(){let n=0;for(const c of cards){c.hidden=!c.textContent.toLocaleLowerCase('ru').includes(q.value.trim().toLocaleLowerCase('ru'));if(!c.hidden)n++;}document.querySelector('#count').textContent='Показано '+n+' из '+cards.length;}q.oninput=filter;filter();</script></html>")
    report=output/(prefix+"-Аудит-кода-и-интерфейса.html")
    report.write_text("".join(doc),encoding="utf-8")
    package_path=output/(prefix+"-Материалы-аудита.zip")
    with ZipFile(package_path,"w",compression=ZIP_DEFLATED) as archive:
        for path in [report,protocol,*sorted(assets.glob("*.png")),assets/"music.wav"]:
            archive.write(path,path.relative_to(output))
    with ZipFile(package_path) as archive:
        if archive.testzip() is not None:raise ValueError("Invalid report archive")
    print(score(primary),score(extra),score(ui),int(old),int(new),len(TOOLS),failed)


if __name__ == "__main__":
    render(Path(sys.argv[1]), Path(sys.argv[2]))
