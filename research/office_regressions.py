"""Check workbook semantics that are easy to lose while serializing an edit."""
import importlib.util
import json
import sys
from pathlib import Path
from openpyxl import Workbook

module,folder=Path(sys.argv[1]),Path(sys.argv[2])
spec=importlib.util.spec_from_file_location("runtime_office",module)
office=importlib.util.module_from_spec(spec);spec.loader.exec_module(office)
folder.mkdir(parents=True,exist_ok=True)
path=folder/"formula-dependencies.xlsx"
wb=Workbook();sheet=wb.active;sheet.title="Основной"
for row in [[10],[None],[30],["10"],[False]]:sheet.append(row)
wb.create_sheet("Свод");wb.save(path)
req=dict(path=str(path),sheet="Основной")
def read():return office.execute(dict(req,action="read"))
def cells():return {r["key"]:r for r in read()["rows"]}
def change(**pairs):
    office.execute(dict(req,action="commit",revision=read()["revision"],patches=[dict(key=k,value=v) for k,v in pairs.items()]))
results=[]
def check(name,actual,expected):results.append(dict(id=name,passed=actual==expected,observed=actual,expected=expected))
change(B1="=SUM(A1:A5)",B2="=AVERAGE(A5:A1)",B3="=COUNT(A1:A5)",B4="=1/0",B5="=B1+7",B6="=COUNT(B1:B4)")
for k,value in {"B1":40,"B2":20,"B3":2,"B4":"#DIV/0!","B5":47,"B6":3}.items():
    check("types-"+k,cells()[k]["display"],str(value))
change(A2="5")
check("transitive",cells()["B5"]["display"],"52")
check("average-after-change",float(cells()["B2"]["display"]),15.)
def total(first,last,op):
    return office.execute(dict(req,action="aggregate",revision=read()["revision"],first=first,last=last,function=op))
t=total("A1","A5","SUM")
check("whole-range",(t["number"],t["numericCount"],t["areaCount"]),("45",3,5))
before=path.read_bytes()
try:office.execute(dict(req,action="commit",revision=read()["revision"],patches=[dict(key="A1",value="55"),dict(key="XFE3",value="12")]))
except ValueError:pass
check("invalid-batch-atomic",before==path.read_bytes(),True)
change(C2="=UNSUPPORTED(A2)")
check("uncalculated-display",cells()["C2"]["display"],"Требуется пересчёт в Excel")
try:total("C2","C2","SUM");blocked=False
except ValueError:blocked=True
check("uncomputed-range",blocked,True)
book=office.Office(path)
try:
    items=[dict(row=2,title="Сверить, затем подписать; согласовать\nв отделе "+("й"*80),owner="Анна",due="2026-11-20",source="Акт.docx",location="строка 3")]
    cal=office.calendar_bytes(items,path)
    check("ics-75-octets",max(map(len,cal.split(b"\r\n")))<=75,True)
    unfolded=cal.replace(b"\r\n ",b"").decode()
    check("ics-escaping",r"Сверить\, затем подписать\; согласовать\nв отделе" in unfolded,True)
finally:book.z.close()
(folder/"regressions.json").write_text(json.dumps(results,ensure_ascii=False,indent=2),encoding="utf-8")
print(sum(row["passed"] for row in results),"/",len(results))
if not all(row["passed"] for row in results):sys.exit(1)
