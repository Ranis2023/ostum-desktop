"""Inventory the first-party source tree, parse its structured inputs and find UI references."""
import ast
from collections import Counter
import hashlib
import json
from pathlib import Path
import re
import subprocess
import sys
from xml.etree import ElementTree


def inventory(root, output):
    suffixes = {".cs", ".py", ".ps1", ".xaml", ".json", ".xml", ".config", ".manifest", ".wxs", ".html", ".cjs", ".cmd"}
    files = [root / "build.ps1", root / "src/RoutePrompt.txt"]
    for folder in ("src", "packaging", "training", "subscription", "research"):
        files.extend(p for p in (root / folder).rglob("*") if p.suffix in suffixes and "__pycache__" not in p.parts)
    files = sorted(set(files))
    rows, issues = [], []
    names, refs = set(), set()
    for f in files:
        path = f.relative_to(root).as_posix()
        raw = f.read_bytes()
        source = raw.decode("utf-8-sig")
        ext = f.suffix
        row = dict(path=path, lines=len(source.splitlines()), sha256=hashlib.sha256(raw).hexdigest(), language=ext)
        try:
            if ext == ".py":
                tree = ast.parse(source, filename=path)
                row["functions"] = sum(isinstance(n, (ast.FunctionDef, ast.AsyncFunctionDef)) for n in ast.walk(tree))
            elif ext in (".xml", ".manifest", ".wxs", ".xaml", ".config"):
                tree = ElementTree.fromstring(source)
                if ext == ".xaml":
                    names.update(n.attrib["{http://schemas.microsoft.com/winfx/2006/xaml}Name"] for n in tree.iter()
                                 if "{http://schemas.microsoft.com/winfx/2006/xaml}Name" in n.attrib)
            elif ext == ".json":
                json.loads(source)
            elif ext == ".cjs":
                subprocess.run(["node", "--check", str(f)], capture_output=True, check=True)
            elif ext == ".cs" and path.startswith("src/"):
                refs.update(re.findall(r'\b(?:Find<\w+>|FindName)\("([A-Za-z]\w*)"\)', source))
                row["declaredTypes"] = re.findall(r"\b(?:class|enum|struct) (\w+)", source)
        except (ValueError, SyntaxError, ElementTree.ParseError, subprocess.CalledProcessError) as error:
            issues.append(dict(path=path, error=str(error)))
        rows.append(row)
    ps = """
    $files=@(Get-ChildItem -LiteralPath src,packaging,subscription,research,training -Recurse -Filter *.ps1 -File)+(Get-Item -LiteralPath build.ps1)
    foreach($f in $files){
        $tokens=$null;$errors=$null
        if($f.Name -eq 'ScanPages.ps1'){
            # The application embeds the UTF-8 source and passes it through -EncodedCommand.
            $null=[System.Management.Automation.Language.Parser]::ParseInput([IO.File]::ReadAllText($f.FullName),$f.FullName,[ref]$tokens,[ref]$errors)
        }else{
            $null=[System.Management.Automation.Language.Parser]::ParseFile($f.FullName,[ref]$tokens,[ref]$errors)
        }
        if($errors){foreach($e in $errors){[Console]::WriteLine($f.Name+': '+$e.Message)};exit 1}
    }
    """
    powershell = subprocess.run(["powershell.exe", "-NoProfile", "-Command", ps], cwd=root, capture_output=True)
    if powershell.returncode:
        issues.append(dict(path="PowerShell", error=powershell.stdout.decode(errors="replace")))
    production = [r for r in rows if not r["path"].startswith("research/")]
    training_sets=[dict(path=p.relative_to(root).as_posix(),lines=len(p.read_text(encoding="utf-8-sig").splitlines()),
                        sha256=hashlib.sha256(p.read_bytes()).hexdigest()) for p in sorted((root/"training").glob("*.txt"))]
    result = dict(files=rows, trainingSets=training_sets, productionFiles=len(production), researchFiles=len(rows)-len(production),
                  productionLines=sum(r["lines"] for r in production),
                  productionByLanguage=dict(Counter(r["language"] for r in production)),
                  namedUiReferences=len(refs), unmatchedUiNames=sorted(refs-names), errors=issues)
    output.write_text(json.dumps(result, ensure_ascii=False, indent=2),encoding="utf-8")
    print(len(production), "production files;", len(rows)-len(production), "QA files;", len(issues), "parse errors;", len(refs-names), "unresolved UI names")
    return int(bool(issues or refs-names))


if __name__ == "__main__":
    raise SystemExit(inventory(Path(__file__).resolve().parents[1], Path(sys.argv[1])))
