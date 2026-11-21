"""Join the original acceptance checks by ID and retain the evidence of this run."""
import argparse
import html
import json
from datetime import datetime
from pathlib import Path
from zipfile import ZipFile,ZIP_DEFLATED
from openpyxl import load_workbook


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def render(audit,output):
    before=read(output/"OSTUM-5.4.0-Протокол.json")
    files={"baseline":"release/baseline/application.json","business":"release/business/application.json",
           "dialog":"release/flows/office/flows.json","media":"release/flows/media/flows.json","repeat":"release/flows/repeat/flows.json",
           "project":"release/project/project.json","formulas":"excel-formulas/formulas.json","help":"help.json","distribution":"distribution-release/install.json"}
    names={"baseline":"Базовый чат","business":"Рабочие задачи","dialog":"Диалог и документы","media":"Медиа, сеть и агенты","repeat":"Повторные проверки","project":"Pro и обучение","formulas":"Формулы Excel","help":"Справка","distribution":"Установка и загрузки"}
    old={r["id"]:r for rows in before["groups"].values() for r in rows}
    groups={};extra=[];seen=set()
    for group,path in files.items():
        groups[group]=[]
        for row in read(audit/path):
            if row["id"] not in old:
                if group!="project" or not row["id"].startswith("N"):raise ValueError(row["id"])
                extra.append(dict(row,group="enterprise"));continue
            if row["id"] in seen:raise ValueError("Duplicate "+row["id"])
            seen.add(row["id"]);earlier=old[row["id"]]
            item=dict(row,passed=row.get("passed",row.get("correct")),previous=earlier["passed"],title=row.get("title") or earlier.get("request") or row.get("formula") or row["id"])
            for k in ("request","expected"):
                if k in earlier:item.setdefault(k,earlier[k])
            groups[group].append(item)
    if seen!=old.keys() or len(seen)!=117:raise ValueError("Missing scenarios: "+str(old.keys()-seen))
    for row in read(audit/"verified-acceptance/gates.json"):
        extra.append(dict(row,group=row["id"].split("-")[0]))
    extra.extend(dict(row,group="office") for row in read(audit/"office-extra/regressions.json"))
    for name,count in {"enterprise":6,"route":30,"source":20,"scan":50,"office":14}.items():
        if len({r["id"] for r in extra if r["group"]==name})!=count:raise ValueError("Incomplete additional group "+name)
    rows=[r for group in groups.values() for r in group]
    support={k:read(audit/path) for k,path in {"voice":"voice/result.json","catalog":"catalog.json","kit":"kit.json","guards":"guards.log","checksums":"installed.json","independentFormulas":"excel-formulas/unrelated-edit.json","excel":"excel-formulas/excel.json","adapter":"release/project/evaluation.json"}.items()}
    for key,path in {"retrieval":"corpus.log","policy":"policy.log","subscription":"subscription.log","updates":"update.log","workspace":"release/access.log","office":"office.log"}.items():
        support[key]=[s for s in (audit/path).read_text(encoding="utf-8-sig",errors="replace").splitlines() if s.startswith(("PASS","evidence hits"))]
    project=audit/"release/project/Проверяемый проект"
    typed=[]
    for version in (project/".ostum-versions").glob("*.xlsx"):
        w=load_workbook(version,read_only=True,data_only=False)
        try:
            sheet=w.active
            if sheet["B2"].value==9750 and sheet["C2"].value==30:
                typed=[{"address":k,"value":sheet[k].value,"type":sheet[k].data_type} for k in ("B2","C2")]
                break
        finally:w.close()
    if not typed or any(c["type"]!="n" for c in typed):raise ValueError("Missing numeric multi-edit evidence")
    support["typedBatch"]=typed
    intermediate={}
    for file,ident in (("final-business/application.json","day-07"),("installed-flows/office/flows.json","F02"),("project/project.json","T02")):
        path=audit/file
        if path.exists():
            item=next((r for r in read(path) if r["id"]==ident),None)
            if item and not item.get("passed",item.get("correct")):
                intermediate[ident]=item.get("answer") or (item.get("error") or "").split("During handling")[0].strip()
    support["intermediateFailures"]=intermediate
    support["installerSHA256"]=(Path(__file__).resolve().parents[1]/"dist/SHA256SUMS.txt").read_text(encoding="ascii").split()[0]
    support["publicRelease"]=read(audit/"public-release.json")
    measured=datetime.fromtimestamp((audit/files["project"]).stat().st_mtime).date().isoformat()
    data=dict(version="5.5.0",previousVersion=before["version"],date=measured,gpu=before["gpu"],ramGiB=before["ramGiB"],testProfile="synthetic",groups=groups,additional=extra,supportingChecks=support)
    (output/"OSTUM-5.5.0-Протокол.json").write_text(json.dumps(data,ensure_ascii=False,indent=2),encoding="utf-8")
    e=lambda s:html.escape(str(s))
    def cell_text(item):
        return item.get("evidence") or item.get("answer") or item.get("observed") or item.get("measure") or item.get("ostum") or ""
    def case(item):
        detail=cell_text(item)
        if isinstance(detail,(dict,list,tuple)):detail=json.dumps(detail,ensure_ascii=False)
        details=e(detail or ("Условие выполнено." if item["passed"] else "Условие не выполнено."))
        if item.get("error"):details+="\n"+e(item["error"])
        return "<details class='case "+("ok" if item["passed"] else "fail")+"'><summary><code>"+e(item["id"])+"</code><span>"+e(item.get("title") or item.get("request") or item["id"])+"</span><b>"+("Прошёл" if item["passed"] else "Расхождение")+"</b></summary><pre>"+details+"</pre></details>"
    coverage="".join("<tr><th>"+name+"</th><td>"+str(sum(r["previous"] for r in groups[group]))+" / "+str(len(groups[group]))+"</td><td>"+str(sum(r["passed"] for r in groups[group]))+" / "+str(len(groups[group]))+"</td></tr>" for group,name in names.items())
    checks=lambda records:"".join(case(item) for item in records)
    sections="".join("<h3>"+name+"</h3>"+checks(groups[k]) for k,name in names.items())
    extra_names={"enterprise":"Импорт, просрочки и календарь через Pro","route":"Текстовые маршруты","source":"Цитаты из двух вложений","scan":"Контрольные страницы OCR","office":"Типы, зависимости, запись и календарный формат"}
    additional="".join("<h3>"+name+"</h3>"+checks([r for r in extra if r["group"]==k]) for k,name in extra_names.items())
    observations={r["id"]:r for r in rows+extra}
    count=sum(r["passed"] for r in rows);extended=sum(r["passed"] for r in extra)
    changed=[r for r in rows if r["previous"]!=r["passed"]]
    notes=support["independentFormulas"];intact=sum(r["sameFormula"] and r["before"]==r["after"] for r in notes)
    speed=observations["R07"]["evidence"];art=observations["M02"]["evidence"];training=support["adapter"]
    supporttext=[
        f"Microsoft Excel пересчитал контрольную книгу. После отдельной записи в H1 сохранились формулы и результаты в {intact} из {len(notes)} ячеек.",
        "В сохранённой книге B2 = 9750 и C2 = 30. Независимое чтение файла подтвердило числовой тип обеих ячеек. До подтверждения оригинал совпадал побайтно.",
        "Утилита измерения модели в приложении. "+speed,
        "Изменение цвета тестового фона. "+art,
        f"Локальное обучение на {training['training']} примерах. Отложено {training['held_out']}. F1 базы {training['baseline']:.4f}, с адаптером {training['adapted']:.4f}. По одной паре нельзя судить об общем качестве.",
        "Диктовка. "+support["voice"]["speaker"]+" → «"+support["voice"]["recognized"]+"». Источник звука синтетический.",
        f"Офлайн-комплект. Проверены контрольные суммы {sum(x['correct'] for x in support['kit'])} из {len(support['kit'])} файлов. Модели каталога проверены по заголовкам GGUF, размеру и метаданным, без полного скачивания каждого варианта.",
        "При проверке обновления тестовый старый клиент запрашивал текущий публичный выпуск GitHub. Проверялись уведомление, скачивание, контрольная сумма и отказ при изменении файла.",
        "Рабочая машина — Windows x64, RTX 4050 Laptop 6 ГБ. Файлы, сканы, диалоги, реестры и проекты созданы для проверки. В сценарии F10 сохранён прежний критерий и добавлена проверка номера, суммы и координат в скане."
    ]
    rawlink="OSTUM-5.5.0-Протокол.json"
    doc="""<!doctype html><html lang="ru"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Проверка OSTUM Desktop 5.5.0</title>
<style> *{box-sizing:border-box}body{margin:0;background:#f4f5ef;color:#20332d;font:16px/1.55 "Segoe UI",Arial,sans-serif}main{max-width:980px;margin:auto;padding:46px 24px 80px}h1{font-size:clamp(30px,6vw,54px);line-height:1.05;letter-spacing:-.045em;margin:6px 0 22px}h2{font-size:25px;margin:38px 0 12px}h3{font-size:18px;margin:24px 0 7px}p{margin:6px 0 14px}.stats{display:flex;gap:12px;flex-wrap:wrap}.stats div{flex:1;min-width:180px;background:#fff;border:1px solid #d4ddd2;border-radius:14px;padding:16px 20px}.stats strong{display:block;font-size:30px;color:#205b43}table{width:100%;border-collapse:collapse;background:#fff}td,th{padding:6px 13px;border-bottom:1px solid #dde4dd;text-align:left}td{text-align:right}a{color:#225c46}.case{border-bottom:1px solid #dde4dc;background:#fff;padding:6px 10px}.case summary{cursor:pointer;display:flex;gap:10px;align-items:baseline}.case code{min-width:65px}.case b{margin-left:auto;font-size:12px;color:#296444}.fail b{color:#a03a24}.case pre{white-space:pre-wrap;overflow-wrap:anywhere;font:14px/1.5 "Segoe UI",sans-serif;margin:8px}button{font:inherit;background:#fff;color:#235c43;border:1px solid #a4b6aa;border-radius:8px;padding:5px 11px;cursor:pointer}body.onlyfail .case.ok{display:none}li{margin-bottom:8px}</style><main>"""
    doc+="<p>OSTUM Desktop · локальная проверка Windows</p><h1>Результаты 5.5.0</h1><div class='stats'>"
    for val,label in [(str(count)+" / 117","Повторные сценарии"),(str(extended)+" / "+str(len(extra)),"Дополнительные проверки"),("99 / 117","Предыдущая версия 5.4.0")]:
        doc+="<div><strong>"+e(val)+"</strong>"+e(label)+"</div>"
    doc+="</div><h2>Сравнение с предыдущей версией</h2><table><thead><tr><th>Группа</th><td>5.4.0</td><td>5.5.0</td></tr></thead><tbody>"+coverage+"</tbody></table>"
    doc+="<h2>Что подтвердилось на этом ПК</h2><ul>"+"".join("<li>"+e(t)+"</li>" for t in supporttext)+"</ul>"
    if intermediate:
        changes={"day-07":"Промежуточный ответ по смене с 22:40 до 01:15 был 95 минут. После передачи явных интервалов калькулятору правильный итог составляет 155 минут.",
                 "F02":"Новый чат сохранил изоляцию, но заменил запрошенное «не указан» на «не найден в источниках». Для пустой истории приложение теперь возвращает заданную пользователем фразу.",
                 "T02":"Один прогон остановился по тайм-ауту при оценке адаптера. Студия теперь выбирает установленный CUDA-движок и получает контрольный ответ по мере генерации."}
        doc+="<h2>Исправления между прогонами</h2><ul>"+"".join("<li>"+e(changes[k])+"</li>" for k in intermediate)+"</ul>"
    doc+="<h2>Изменившиеся результаты</h2>"+checks(changed)+"<h2>Все сценарии и наблюдения</h2><p>Нажмите строку, чтобы раскрыть результат. <a href='"+rawlink+"'>Полный протокол JSON</a></p><button id='filter'>Показать только расхождения</button>"+sections+"<h2>Новые проверки</h2>"+additional
    doc+="</main><script>const b=document.getElementById('filter');b.onclick=()=>{const active=document.body.classList.toggle('onlyfail');b.textContent=active?'Показать все проверки':'Показать только расхождения';};</script></html>"
    (output/"OSTUM-5.5.0-Повторная-проверка.html").write_text(doc,encoding="utf-8")
    with ZipFile(output/"OSTUM-5.5.0-Контрольные-материалы.zip","w",compression=ZIP_DEFLATED) as z:
        for name in ("OSTUM-5.5.0-Протокол.json","OSTUM-5.5.0-Повторная-проверка.html"):z.write(output/name,name)
        for name in ("excel-formulas/excel-recalculated.xlsx","excel-formulas/unrelated-note.xlsx","excel-formulas/unrelated-edit.json",
                     "release/project/Проверяемый проект/Входящие/Реестр счетов OSTUM.xlsx","release/project/Сроки.ics",
                     "scans/batch0.pdf","scans/batch1.pdf","scans/answers.json","office-extra/regressions.json","installed.json"):
            z.write(audit/name,name)
        research=Path(__file__).resolve().parent
        for name in ("retest_installed.ps1","workday_cases.json","WorkdayFlows.cs","WorkdayProject.cs","ImprovementChecks.cs","intake_samples.py","office_regressions.py","retest_report.py"):
            z.write(research/name,"research/"+name)
    with ZipFile(output/"OSTUM-5.5.0-Контрольные-материалы.zip") as z:
        if z.testzip() is not None:raise ValueError("Damaged archive")
    print(count,"/117,",extended,"/",len(extra))


if __name__=="__main__":
    parser=argparse.ArgumentParser();parser.add_argument("audit",type=Path);parser.add_argument("output",type=Path)
    args=parser.parse_args();render(args.audit,args.output)
