"""Exercise the installed OOXML code on synthetic files with no network or AI."""
import argparse
from pathlib import Path
import shutil
import sys
import tempfile
import zipfile

sys.path.insert(0,str(Path(__file__).resolve().parents[1]/"src"))
import office


def read(path):
    return office.execute(dict(action="read",path=str(path)))


def change(path, rev, rows, sheet=""):
    office.execute(dict(action="commit",path=str(path),revision=rev,patches=rows,sheet=sheet))


def run(base):
    old, new, excel = (base/n for n in ("initial.docx","review.docx","budget.xlsx"))
    office.write_new(old,["Тема","Первый срок","Конец"],True)
    office.write_new(new,["Тема","Новый срок","Примечание","Конец","Подпись","Дата"],True)
    with zipfile.ZipFile(old,"a") as package:package.writestr("customXml/item1.xml","<preserved/>")
    original=read(old)
    diff=office.execute(dict(action="compare",path=str(old),other=str(new)))["patches"]
    change(old,original["revision"],[r for r in diff if r["value"]!="Примечание"])
    assert [r["value"] for r in read(old)["rows"]]==["Тема","Новый срок","Конец","Подпись","Дата"]
    try:change(old,original["revision"],[dict(key="1",value="потеря")])
    except ValueError:pass
    else:raise AssertionError("stale revision accepted")
    change(old,read(old)["revision"],[dict(key="2",operation="comment",value="Уточнить дату.")])
    assert read(old)["rows"][1]["comment"]=="Уточнить дату."
    with zipfile.ZipFile(old) as package:assert package.read("customXml/item1.xml")==b"<preserved/>"
    backup=office.execute(dict(path=str(old),action="versions"))["versions"][-1]
    office.execute(dict(path=str(old),action="restore",revision=read(old)["revision"],version=backup))
    assert read(old)["revision"]==original["revision"]
    office.write_new(excel,[["Количество","Цена","Сумма"],[3,15,""],[1,2,""]])
    change(excel,read(excel)["revision"],[dict(key="C2",value="=A2*B2"),dict(key="C3",value="=A3*B3"),dict(key="C4",value="=SUM(C2:C3)")],"Данные")
    assert {r["key"]:r["display"] for r in read(excel)["rows"]}["C4"]=="47"
    offers=[]
    for number in range(3):
        path=base/("offer"+str(number)+".xlsx");offers.append(str(path))
        office.write_new(path,[["Позиция","Количество","Ед.","Цена без НДС","Валюта","НДС, %"],["Бумага",2,"шт" if number!=2 else "уп.",10 if number else "","RUB" if number!=2 else "EUR",20]])
    out=base/"comparison.xlsx";office.execute(dict(action="offers",path=str(out),sources=offers))
    table=office.offers(offers)
    assert len(table)==4 and any(r[6]==24 for r in table) and any(r[7]=="Требует уточнения" for r in table)
    assert any("offer" in r["value"] for r in read(out)["rows"])
    print("PASS · DOCX selective changes, comments, unknown parts, version restore, stale-file conflict, XLSX formulas and three suppliers")


if __name__=="__main__":
    parser=argparse.ArgumentParser()
    parser.add_argument("--keep",type=Path);args=parser.parse_args()
    if args.keep:
        args.keep.mkdir(exist_ok=False,parents=True);run(args.keep)
    else:
        with tempfile.TemporaryDirectory() as t:run(Path(t))
