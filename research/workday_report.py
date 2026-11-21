"""Build an offline Russian audit report from the local run records."""
import argparse
import json
from pathlib import Path
from statistics import median
from PIL import Image

from reportlab.lib.colors import HexColor
from reportlab.lib.pagesizes import A4
from reportlab.lib.utils import simpleSplit
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from reportlab.pdfgen import canvas


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def number(value, precision=1):
    return f"{value:.{precision}f}".replace(".", ",")


def collect(root, audit):
    def chat(folder, casefile):
        conditions = {v["id"]: v for v in read(root / "research" / casefile)}
        results = read(audit / folder / "application.json")
        for item in results:
            item["passed"] = item["correct"]
            item["request"] = conditions[item["id"]]["text"]
            item["expected"] = conditions[item["id"]]["expected"]
        return results

    groups = {
        "baseline": chat("chat-base", "app_cases.json"),
        "business": chat("workday", "workday_cases.json"),
        "dialog": read(audit / "flow-runs/office/flows.json"),
        "media": read(audit / "flow-runs/media/flows.json"),
        "repeat": read(audit / "flow-rechecks/repeat/flows.json"),
        "project": read(audit / "project/project.json"),
        "formulas": read(audit / "excel-formulas/formulas.json"),
        "help": read(audit / "help.json"),
        "distribution": read(audit / "distribution/install.json"),
    }
    sizes = {"baseline": 26, "business": 24, "dialog": 16, "media": 9, "repeat": 8, "project": 14, "formulas": 10, "help": 5, "distribution": 5}
    for group, expected in sizes.items():
        if len(groups[group]) != expected:
            raise ValueError(f"Incomplete run {group}: {len(groups[group])}/{expected}")
    supports = {
        "catalog": read(audit / "catalog.json"),
        "offlineKit": read(audit / "kit.json"),
        "guards": read(audit / "guards.log"),
        "speech": read(audit / "voice/result.json"),
        "windowsOcrPrototype": read(audit / "ocr.json"),
        "trainingEvaluation": read(audit / "project/evaluation.json"),
        "microsoftExcel": read(audit / "excel-formulas/excel.json"),
        "unrelatedEdit": read(audit / "excel-formulas/unrelated-edit.json"),
    }
    for key in ("office", "corpus", "policy", "subscription", "update", "workspace"):
        lines = (audit / (key + ".log")).read_text(encoding="utf-8-sig").splitlines()
        supports[key] = [line for line in lines if line.startswith(("PASS", "evidence hits"))]
    cutoff = audit / "media-edit-timeout.log"
    supports["interactiveEditBudget"] = cutoff.read_text(encoding="utf-8-sig").strip() if cutoff.exists() else next(r["evidence"] for r in groups["media"] if r["id"]=="M02")
    # Recover the persisted, generated artefact from this run's synthetic chat only.
    for path in (audit / "workday").glob("private-test-*/chats/*.json"):
        turns = read(path)["Turns"]
        if turns and turns[0].get("Text", "").startswith("Напиши только окончательный код серии."):
            supports["unexpectedArt"] = {"generated": turns[-1].get("Generated"), "fileExists": Path(turns[-1]["Image"]).is_file()}
    if not supports.get("unexpectedArt", {}).get("fileExists"):
        raise ValueError("Missing evidence for the unexpected image")
    return dict(version="5.4.0", date="2026-11-20", windows="Windows x64", gpu="RTX 4050 Laptop 6 GiB", ramGiB=16,
                testProfile="synthetic", groups=groups, supportingChecks=supports)


W, H = A4
INK, LIGHT, RULE, GREEN, ORANGE = "#172E27", "#52645E", "#D5DFD8", "#21715C", "#A54E2A"


class Report:
    def __init__(self, dest):
        for face, filename in (("UI", "segoeui.ttf"), ("Medium", "seguisb.ttf"), ("Bold", "segoeuib.ttf")):
            pdfmetrics.registerFont(TTFont(face, "C:/Windows/Fonts/" + filename))
        self.c = canvas.Canvas(str(dest), pagesize=A4)
        self.c.setTitle("OSTUM Desktop 5.4.0 | Проверка и план улучшений")
        self.c.setAuthor("OSTUM · проверка на Windows")
        self.page = 0

    def text(self, x, y, message, size=10, font="UI", color=INK):
        self.c.setFillColor(HexColor(color)); self.c.setFont(font, size)
        self.c.drawString(x, H - y, str(message))

    def box(self, x, y, width, height, fill, radius=0):
        self.c.setFillColor(HexColor(fill))
        self.c.roundRect(x, H - y - height, width, height, radius, fill=1, stroke=0)

    def rule(self, y, x=42, width=W - 84):
        self.c.setStrokeColor(HexColor(RULE)); self.c.setLineWidth(.7)
        self.c.line(x, H - y, x + width, H - y)

    def para(self, x, y, content, width, size=10.5, leading=15, font="UI", color=INK):
        for line in simpleSplit(content, font, size, width):
            self.text(x, y, line, size, font, color); y += leading
        return y

    def link(self, x, y, label, url):
        width = pdfmetrics.stringWidth(label, "Medium", 9)
        self.text(x, y, label, 9, "Medium", GREEN)
        self.c.linkURL(url, (x, H-y-3, x+width, H-y+10), relative=0, thickness=0)

    def start(self, label, title, sub):
        if self.page: self.c.showPage()
        self.page += 1
        self.box(0, 0, W, H, "#FCFCF8")
        self.text(42, 39, "OSTUM DESKTOP  /  5.4.0", 10, "Bold", GREEN)
        self.text(450, 39, "20 ноября 2026", 10, "UI", LIGHT); self.rule(53)
        self.text(42, 83, label, 10, "Medium", ORANGE)
        self.text(41, 124, title, 28, "Bold")
        self.para(43, 149, sub, 507, 11, 15, color=LIGHT)
        self.rule(790); self.text(42, 810, "Проверка на Windows   /   Ryzen 7 7435HS   /   RTX 4050 Laptop 6 ГБ   /   ОЗУ 16 ГБ", 8.1, color=LIGHT)
        self.text(513, 810, f"{self.page} / 6", 8.4, "Medium", GREEN)

    def entry(self, y, title, copy, color=GREEN):
        self.text(43, y, title, 13, "Medium", color)
        return self.para(43, y + 21, copy, 507, 10.9, 15.4) + 13

    def step(self, y, key, title, action, acceptance):
        self.text(43, y, key, 10, "Bold", ORANGE)
        self.text(89, y, title, 13, "Medium")
        end = self.para(89, y + 20, action, 457, 10.2, 14.3)
        end = self.para(89, end + 3, acceptance, 457, 9.8, 14, "Medium", GREEN)
        self.rule(end + 2); return end + 18


def build(dest, audit, record):
    g, s = record["groups"], record["supportingChecks"]
    all_cases = [r for rows in g.values() for r in rows]
    by_id = {row["id"]: row for row in all_cases}
    # The interpretation is a dated audit of 5.4.0. Never carry old findings into a changed run.
    expected_failures={"day-16","F04","F11","F16","M02","P03","P04","P05","H02","X02","X03","X06","X07","X08","X09","X10","R01","R08"}
    if {r["id"] for r in all_cases if not r["passed"]}!=expected_failures:
        raise ValueError("Outcomes changed: inspect the new raw results and revise the narrative before exporting a PDF.")
    with Image.open(audit/"flow-runs/media/created.png") as sample:
        sample.thumbnail((380,380))
        sample.save(audit/"cup-preview.png")
    ratio = lambda rows: f"{sum(bool(x['passed']) for x in rows)}/{len(rows)}"
    doc = Report(dest)
    successes=sum(bool(r["passed"]) for r in all_cases)
    doc.start("Результат проверки", "Что исправить в первую очередь",
              f"Из {len(all_cases)} сценарных проверок {successes} выполнили заданное условие. Один дефект мог затронуть несколько сценариев. Документы и профиль для проверки созданы специально.")
    doc.box(42, 189, W-84, 83, "#E7F0E9", 10)
    for x, value, label in [(59, "30 вместо 465", "Сумма диапазона в чате"), (250, "13,33 вместо 20", "Среднее в редакторе"), (437, "99 с", "Код серии")]:
        doc.text(x, 226, value, 17, "Bold"); doc.text(x, 250, label, 8.6, color=LIGHT)
    y = doc.entry(307, "Диапазон Excel требует настоящего расчёта",
                  "В B2:B31 находились целые числа от 1 до 30. Чат извлёк несколько фрагментов и назвал последнее значение итогом всего диапазона. Вопрос об отдельной ячейке B19 дал верный ответ 18.")
    y = doc.entry(y, "Правка обычной заметки повлияла на формулы",
                  "В книгу с проверенными в Microsoft Excel результатами через движок OSTUM записали текст только в H1. Формулы сохранились. У шести формул показанное значение изменилось существенно или исчезло. Для смет и отчётов это первый блок исправлений.")
    y = doc.entry(y, "Одна реплика про две ячейки испортила тип значения",
                  "После «Измени ячейку B2 на 9750 и C2 на 30» в предпросмотре появилась одна правка. После подтверждения B2 содержала строку «9750 и C2 на 30», а C2 осталась равна 20. До подтверждения исходник сохранился.")
    y = doc.entry(y, "Оркестратор выбрал изображение для текстового ответа",
                  "В запросе с заменой маркировки ИГ-48 на ИГ-84 помощник правильно определил цель, затем вызвал художника. Первый прогон создал PNG и ответил «Пробел». Повтор снова дошёл до художника. Формулировка с явным полем «Код» вернула ИГ-84 за "+number(by_id["R03"]["seconds"],2)+" с.")
    doc.para(43, y+6, "До исправления итог по диапазону и формулы с пропусками стоит сверять в Excel. В предпросмотре составной команды проверьте адрес и тип каждой предлагаемой правки.", 507, 10.8, 15.4, "Medium", ORANGE)

    doc.start("Охват", "Что прошло проверку",
              "В таблице указан результат заранее заданного условия. Наборы отличаются сложностью. Строки каталога и проверки контрольных сумм отражены отдельно.")
    x1, x2, x3 = 51, 277, 330
    doc.box(42,185,W-84,28,"#E7F0E9",5)
    for x, t in [(x1,"Набор"),(x2,"Прошли"),(x3,"Что проверялось")]: doc.text(x,204,t,9.8,"Medium")
    project = g["project"]
    matrix = [
        ("Базовые задания чата", ratio(g["baseline"]), "История, расчёты, цитаты и JSON"),
        ("Новые рабочие запросы", ratio(g["business"]), "Скидка, сбор, сроки, коды и тезисы"),
        ("Диалоги и вложения", ratio(g["dialog"]), "PDF, CSV, две редакции, отмена, память"),
        ("Медиа, поиск и Windows", ratio(g["media"]), "Фото, рисунок, WAV, агенты, окно и веб"),
        ("Повторы и соседние сценарии", ratio(g["repeat"]), "Варианты запросов, импорт и скорость"),
        ("Чат с рабочей папкой Pro", ratio([r for r in project if r["id"][0]=="P"]), "Диапазоны, правки, поиск и контекст"),
        ("Жизненный цикл обучения", ratio([r for r in project if r["id"][0]=="T"]), "Пары, обучение, выбор, отзыв"),
        ("Формулы против Microsoft Excel", ratio(g["formulas"]), "Пустоты, нули, текст и границы"),
        ("Офлайн-справка в Edge", ratio(g["help"]), "Поиск, навигация и команда печати"),
        ("Доставка и ремонт файлов", ratio(g["distribution"]), "USB, повтор, докачка и повреждённый файл"),
    ]
    yy=233
    for title, score, coverage in matrix:
        doc.text(x1, yy, title, 10, "Medium"); doc.text(x2, yy, score, 10, "Bold", GREEN if len(set(score.split("/")))==1 else ORANGE)
        doc.text(x3, yy, coverage, 8.75); doc.rule(yy+12); yy+=34
    yy=doc.entry(597,"Что проверено дополнительно",
        "22 проверки формата и арифметики прошли. Поиск вернул нужную выдержку в 8 из 8 запросов по синтетическому каталогу. У 9 дополнительных файлов моделей доступны заголовки GGUF, совпали размеры и опубликованные SHA-256. В комплекте для флешки совпали 15 контрольных сумм.")
    doc.entry(yy,"Установка, доступ и политика организации",
        "Старый тестовый клиент увидел 5.4.0 на GitHub и скачал проверенный установщик. Проверены отказ в доступе к папке, вложенные ограничения, автономная активация Pro и повторный запуск. Бот проверялся на тестовых ответах Telegram.")

    doc.start("Данные и расчёты", "Один файл в OSTUM и Excel",
        "В A2:A4 записали 10, пустую ячейку и 30. В столбце B вместо пропуска стоит настоящий ноль. В C посередине находится слово «пауза». Столбец G пуст.")
    doc.box(42,185,W-84,31,"#E7F0E9",5)
    for x,t in [(54,"Формула"),(300,"OSTUM"),(435,"Microsoft Excel")]:doc.text(x,205,t,10,"Medium")
    oracle={x["id"]:x for x in s["microsoftExcel"]}
    for i,row in enumerate(g["formulas"]):
        y=238+i*29
        dp=str(row["ostum"] or "пусто");dp="13,33333" if dp.startswith("13.33333") else dp
        doc.text(54,y,row["formula"],10,"Medium");doc.text(300,y,dp,10,color=GREEN if row["passed"] else ORANGE);doc.text(435,y,oracle[row["id"]]["text"],10);doc.rule(y+10)
    y=doc.entry(556,"Контроль на уже рассчитанной книге",
                "Excel пересчитал и сохранил контрольную копию. OSTUM изменил единственную текстовую ячейку H1. Среднее 20 превратилось в 13,33, минимум 10 в 0. Для диапазона с текстом пропали результаты трёх функций. Ошибка деления на ноль превратилась в 0.")
    y=doc.entry(y,"Критерий для выпуска исправления",
                "Редактор и режим предварительного просмотра должны воспроизводить значения и ошибки Excel. Одиночное изменение текста должно сохранять корректные результаты независимых формул. Проверять нужно и заново открытый файл.")
    doc.link(43,y+5,"Microsoft · правила AVERAGE","https://support.microsoft.com/en-us/excel/functions/average-function")
    doc.link(229,y+5,"Microsoft · правила MIN","https://support.microsoft.com/en-us/excel/functions/min-function")

    doc.start("Чат, агенты и скорость", "Где теряется время и контекст",
        "Замеры выполнены на одном компьютере. Время включает работу нужных инструментов. При холодном старте, другом размере файла и нагрузке оно меняется.")
    direct=[x["latency"] for x in g["baseline"] if x["direct"]];others=[x["latency"] for x in g["baseline"] if not x["direct"]]
    y=doc.entry(199,"Короткие ответы",
                f"В базовом наборе медиана девяти прямых вычислений составила {number(median(direct),2)} с, остальных семнадцати запросов — {number(median(others),2)} с. Три базовых задания оценивались по общему формату. При ручном чтении объяснение примера 6 + 7 оказалось круговым.")
    y=doc.entry(y,"Два вложения и память текущего файла",
                "В PDF ответственной была Марина Тихая, в TXT кодом был КД-428. Чат сослался на оба файла, но взял ТК-4172 из PDF. Повтор с явными именами файлов дал верный ответ. В рабочей папке чат забыл ранее заданное имя ячейки «Маяк». После закрытия панели код из файла остался внутри рабочего сеанса.")
    y=doc.entry(y,"Реальный параллельный запуск",
                f"Команда вернула окончательную сумму 7340 и код приёмки ТК-4172. Интервалы аналитика и проверяющего пересекались. Полная обработка заняла {number(by_id['M08']['seconds'])} с. Одна ссылка на страницу NASA прошла через разрешённый веб-поиск.")
    doc.box(42,y-1,507,110,"#E7F0E9",8)
    doc.c.drawImage(str(audit/"cup-preview.png"),52,H-(y+98),86,86,mask="auto")
    doc.para(150,y+22, f"Генерация изображения 1024 × 1024 заняла {number(by_id['M03']['seconds'])} с. Редактирование синего фона перешло на CPU. После шести минут на CPU запрос остановили кнопкой чата.",385,10.1,14)
    doc.para(150,y+71,f"Генерация 10 с музыки заняла {number(by_id['M04']['seconds'])} с. Проверен стереофайл 48 кГц с ненулевым аудиосигналом.",385,10.1,14)
    y+=139
    y=doc.para(43,y,"Отдельная проба 384 × 384 заняла "+number(by_id["R08"]["seconds"])+" с. Редактирование не выполнило запрос на оранжевый фон. Среди 1225 проверенных точек оранжевых было 0.",507,9.9,13.8,color=LIGHT)+8
    measure=by_id["R07"]["evidence"]
    doc.text(43,y,"Локальный замер Zarum",13,"Medium",GREEN)
    y=doc.para(43,y+20,measure[:240],506,10.1,14)
    ev=s["trainingEvaluation"]
    doc.para(43,y+10,f"Обучение на {ev['training']} синтетических парах завершилось. На {ev['held_out']} отложенном примере F1 изменился с {number(ev['baseline'],4)} до {number(ev['adapted'],4)}. Этот малый набор проверяет механику, а вывод о качестве требует отдельной рабочей выборки.",507,10.1,14)

    doc.start("Очередность работ", "План улучшений",
              "Сначала устранить ошибки, влияющие на сохранённый файл и расчёты. Критерий следующего выпуска — прохождение обоих блоков P0 и повтор проверок чата.")
    y=doc.step(195,"P0","Привести расчёты редактора к правилам Excel",
               "Различать пропуск, ноль, строку и ошибку. Сохранять результаты независимых формул. Для неподдерживаемого выражения явно показывать состояние пересчёта.",
               "Приёмка • 10 из 10 примеров совпадают с Excel. Запись в H1 сохраняет значения соседних формул.")
    y=doc.step(y,"P0","Считать диапазоны и разбирать несколько правок",
               "Передавать диапазон вычислительному инструменту целиком. Каждому адресу и типу значения отводить отдельную строку предпросмотра. Уточнять неоднозначную реплику.",
               "Приёмка • B2:B31 даёт 465 по 30 ячейкам. Для B2 и C2 видны две числовые правки. Отмена сохраняет исходник.")
    y=doc.step(y,"P1","Проверять выбранный инструмент и источник факта",
               "Явные требования к тексту и JSON ограничивают допустимый результат. Для факта из вложения сохранять имя файла и находить подтверждающую цитату до отправки ответа.",
               "Приёмка • 30 текстовых запросов обходятся без запуска художника. В 20 задачах с двумя файлами код и ссылка совпадают.")
    y=doc.step(y,"P1","Сохранить контекст открытого файла и управлять памятью",
               "Хранить несколько последних реплик в памяти разрешённого сеанса. Добавить в чат просмотр и удаление выбранной записи персонального профиля.",
               "Приёмка • «Маяк» означает B17 внутри беседы. После закрытия файла код не попадает в общий чат. Удалённая заметка исчезает из профиля.")
    y=doc.step(y,"P1","Дать выбор скорости при создании изображений",
               "Предлагать размер предпросмотра, показывать переход на CPU и разрешать продолжить, уменьшить размер либо остановиться. Отдельно оценивать соответствие референсу.",
               "Приёмка • Интерфейс показывает движок и размер до долгой генерации. После отмены следующий запрос отвечает.")
    y=doc.step(y,"P2","Исправить поиск в справке и оценку адаптеров",
               "Связать найденный раздел с его ссылкой по ID. Пустому TXT давать собственное сообщение. При регрессии адаптера показать базовый и обученный ответы перед выбором.",
               "Приёмка • Поиск «микрофон» ведёт в «Голос». При худшем результате адаптера пользователь видит предупреждение.")
    if y>794: raise ValueError("Plan page overflow "+str(y))

    doc.start("Новые рабочие инструменты","Что добавить дальше",
              "Эти возможности дополняют существующие редакторы и реестр поручений. Для каждой есть повседневная задача и способ проверить, помогла ли функция.")
    ocr=s["windowsOcrPrototype"];times=", ".join(str(r["ms"]) for r in ocr)
    y=doc.entry(194,"1. Читать сканы накладных прямо в чате",
                f"Сейчас OSTUM сообщает об отсутствии текстового слоя в PDF. Отдельная офлайн-проба Windows OCR на этом ПК правильно прочитала номер и сумму на {sum(r['correct'] for r in ocr)} из {len(ocr)} русских образцов, включая слегка повёрнутый. На один подготовленный растр ушло {times} мс. Это результат трёх чистых образцов.")
    y=doc.para(43,y-4,"Первый выпуск • Показывать распознанное значение поверх исходной страницы. Проверить 50 обезличенных сканов разных поставщиков, включая печати, перекос и мелкий шрифт.",507,10.1,14,"Medium",GREEN)+14
    y=doc.entry(y,"2. Из папки входящих счетов собрать реестр Excel",
                "По разрешению пользователя обработать выбранные файлы, предложить строки с номером, датой и суммой. Каждая сумма открывает нужную страницу. Дубли и расхождения видны до записи в XLSX.")
    y=doc.para(43,y-4,"Приёмка • На 20 документах одна строка соответствует одному счёту. Похожие артикулы не смешиваются. Повторный импорт показывает дубли.",507,10.1,14,"Medium",GREEN)+14
    y=doc.entry(y,"3. Показать срок и источник каждого поручения",
                "Поверх существующего реестра добавить запросы «Что просрочено?» и «Что сдаём на неделе?». Результат ведёт к исходной ячейке или абзацу. По команде пользователя можно создавать локальный файл календаря.")
    y=doc.para(43,y-4,"Приёмка • На контрольной неделе исключены выполненные задачи, срок учитывает часовой пояс, повторный экспорт сохраняет ID поручения.",507,10.1,14,"Medium",GREEN)+17
    doc.text(43,y,"Границы проверки",12,"Medium")
    y=doc.para(43,y+18,"Рабочие файлы и лицензия тестового Pro-профиля были синтетическими. Голос проверен записью синтезатора и локальным распознавателем. В меню обнаружены четыре входа, в том числе USB PnP. Запись человека и фотографии его экрана не выполнялись. Окно Windows для проверки сворачивания создал сам тест.",507,9.6,13.4,color=LIGHT)
    y=doc.para(43,y+2,"Другие видеокарты, чистая установка OSTUM на другом физическом ПК и качество всех больших моделей требуют отдельных прогонов. Тестовые задания и результаты приложены отдельно.",507,9.6,13.4,color=LIGHT)
    doc.link(43,y+8,"Microsoft · Windows OCR","https://learn.microsoft.com/en-us/uwp/api/windows.media.ocr.ocrengine")
    doc.link(242,y+8,"IETF · формат календарей iCalendar","https://www.rfc-editor.org/rfc/rfc5545.html")
    if y>770: raise ValueError("Recommendations overflow "+str(y))
    doc.c.save()


if __name__ == "__main__":
    cli=argparse.ArgumentParser()
    cli.add_argument("audit",type=Path);cli.add_argument("pdf",type=Path);cli.add_argument("raw",type=Path)
    params=cli.parse_args();root=Path(__file__).resolve().parents[1]
    record=collect(root,params.audit)
    for path in (params.pdf,params.raw):path.parent.mkdir(parents=True,exist_ok=True)
    params.raw.write_text(json.dumps(record,ensure_ascii=False,indent=2),encoding="utf-8")
    build(params.pdf,params.audit,record)
