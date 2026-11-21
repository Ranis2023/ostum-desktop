"""Exercise the installed OSTUM Office engine on missing cells, real zeroes and text."""
import argparse
import importlib.util
import json
import math
import shutil
from pathlib import Path

from openpyxl import Workbook


def run(runtime, dest):
    spec = importlib.util.spec_from_file_location("deployed_ostum_office", runtime / "office.py")
    office = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(office)
    dest.mkdir(parents=True, exist_ok=True)
    path = dest / "ranges.xlsx"
    book = Workbook()
    tab = book.active
    tab.title = "Data"
    tab.append(["Blank", "Zero", "Text"])
    tab.append([10, 10, 10])
    tab.append([None, 0, "пауза"])
    tab.append([30, 30, 30])
    book.save(path)
    cases = [
        ("SUM(A2:A4)", 40), ("AVERAGE(A2:A4)", 20), ("MIN(A2:A4)", 10),
        ("MAX(A2:A4)", 30), ("AVERAGE(B2:B4)", 40 / 3), ("SUM(C2:C4)", 40),
        ("MAX(C2:C4)", 30), ("AVERAGE(C2:C4)", 20), ("SUM(A4:A2)", 40),
        ("AVERAGE(G2:G4)", "#DIV/0!"),
    ]
    request = dict(path=str(path), sheet="Data")
    initial = office.execute(dict(request, action="read"))
    patches = [dict(key="D" + str(i), value="=" + expression) for i, (expression, _) in enumerate(cases, 2)]
    office.execute(dict(request, action="commit", revision=initial["revision"], patches=patches))
    cells = {r["key"]: r for r in office.execute(dict(request, action="read"))["rows"]}
    results = []
    for i, (expression, expected) in enumerate(cases, 2):
        cell = "D" + str(i)
        actual = cells[cell]["display"]
        try:
            correct = math.isclose(float(actual), expected, rel_tol=1e-10)
        except (TypeError, ValueError):
            correct = actual == expected
        results.append(dict(id=f"X{i-1:02d}", cell=cell, formula=expression, expected=expected, ostum=actual, passed=correct))
    (dest / "formulas.json").write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding="utf-8")

def unrelated_edit(runtime, dest, source):
    spec = importlib.util.spec_from_file_location("deployed_ostum_office", runtime / "office.py")
    office = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(office)
    path = dest / "unrelated-note.xlsx"
    shutil.copyfile(source, path)
    req = dict(path=str(path), sheet="Data")
    initial = office.execute(dict(req, action="read"))
    office.execute(dict(req, action="commit", revision=initial["revision"], patches=[dict(key="H1", value="Примечание отдела")]))
    first = {r["key"]: r for r in initial["rows"]}
    last = {r["key"]: r for r in office.execute(dict(req, action="read"))["rows"]}
    cells = ["D" + str(i) for i in range(2, 12)]
    result = [dict(cell=k, formula=first[k]["value"], before=first[k]["display"], after=last[k]["display"], sameFormula=first[k]["value"] == last[k]["value"]) for k in cells]
    (dest / "unrelated-edit.json").write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")


if __name__ == "__main__":
    cli = argparse.ArgumentParser()
    cli.add_argument("runtime", type=Path)
    cli.add_argument("dest", type=Path)
    cli.add_argument("--reference-copy", type=Path)
    options = cli.parse_args()
    if options.reference_copy:
        unrelated_edit(options.runtime, options.dest, options.reference_copy)
    else:
        run(options.runtime, options.dest)
