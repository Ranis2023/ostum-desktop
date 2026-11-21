"""Local OOXML editor. Only the files named by the workstation UI are opened."""
import ast
import collections
import datetime
import decimal
import difflib
import functools
import hashlib
import io
import json
import operator
import os
import posixpath
import re
import shutil
import sys
import tempfile
import uuid
import zipfile
from pathlib import Path
from xml.dom import Node, minidom
from xml.sax.saxutils import escape, quoteattr

W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main"
X = "http://schemas.openxmlformats.org/spreadsheetml/2006/main"
R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships"
P = "http://schemas.openxmlformats.org/package/2006/relationships"
T = "http://schemas.openxmlformats.org/package/2006/content-types"
D = decimal.Decimal
MAX_ROWS = 25000


def doc(xml):
    if len(xml) > 32*1024**2 or re.search(b"<!DOCTYPE|<!ENTITY", xml, re.I):
        raise ValueError("Эта часть документа превышает лимит или содержит DTD.")
    return minidom.parseString(xml)


def children(el, ns, tag):
    return [n for n in el.childNodes if n.nodeType == Node.ELEMENT_NODE and n.namespaceURI == ns and n.localName == tag]


def sub(el, ns, tag):
    return list(el.getElementsByTagNameNS(ns, tag))


def text(el):
    return "".join(n.data for n in el.childNodes if n.nodeType in (Node.TEXT_NODE, Node.CDATA_SECTION_NODE))


def settext(el, value):
    for n in list(el.childNodes):
        el.removeChild(n)
    el.appendChild(el.ownerDocument.createTextNode(value))


def element(parent, ns, tag, value=None):
    if ":" not in tag and parent.namespaceURI==ns and parent.prefix:
        tag=parent.prefix+":"+tag
    e = parent.ownerDocument.createElementNS(ns, tag)
    if value is not None:
        settext(e, value)
    parent.appendChild(e)
    return e


def column(n):
    out = ""
    while n:
        n, part = divmod(n-1, 26)
        out = chr(65+part)+out
    return out


def address(s):
    m = re.fullmatch(r"([A-Z]{1,3})([1-9][0-9]{0,6})", s)
    if not m:
        raise ValueError("Адрес ячейки должен выглядеть как B3.")
    c = functools.reduce(lambda n, ch: n*26+ord(ch)-64, m[1], 0)
    r = int(m[2])
    if c > 16384 or r > 1048576:
        raise ValueError("Адрес вне листа Excel.")
    return r, c


def digest(path):
    with open(path, "rb") as f:
        return hashlib.file_digest(f, "sha256").hexdigest()


def part_path(base, target):
    if "\\" in target or ":" in target:
        raise ValueError("Необычный адрес части Office.")
    name = posixpath.normpath(target[1:] if target.startswith("/") else posixpath.join(base, target))
    if name.startswith("../") or name.startswith("/"):
        raise ValueError("Неверный адрес части Office.")
    return name


class Office:
    def __init__(self, path):
        self.path = Path(path)
        self.z = zipfile.ZipFile(path)
        self.cache, self.dirty = {}, set()
        info = self.z.infolist()
        if len(info)>10000 or sum(p.file_size for p in info)>256*1024**2 or len(set(self.z.namelist()))!=len(info):
            self.z.close()
            raise ValueError("Большой или неоднозначный пакет Office.")
        self.is_word = "word/document.xml" in self.z.namelist()
        self.read_only = any(s.lower().startswith("_xmlsignatures/") or s.lower().endswith("vbaproject.bin") for s in self.z.namelist())
        self.sheets = {}
        self.strings = []
        self.formats = {}
        self.styles = []
        if not self.is_word:
            book = self.part("xl/workbook.xml")
            rel = self.part("xl/_rels/workbook.xml.rels")
            links = {n.getAttribute("Id"):part_path("xl",n.getAttribute("Target")) for n in sub(rel,P,"Relationship") if n.getAttribute("TargetMode")!="External"}
            self.sheets = {n.getAttribute("name"):links[n.getAttributeNS(R,"id")] for n in sub(book,X,"sheet") if n.getAttributeNS(R,"id") in links}
            if "xl/sharedStrings.xml" in self.z.namelist():
                self.strings = ["".join(text(n) for n in sub(si,X,"t")) for si in sub(self.part("xl/sharedStrings.xml"),X,"si")]
            if "xl/styles.xml" in self.z.namelist():
                styles = self.part("xl/styles.xml")
                self.formats = {e.getAttribute("numFmtId"):e.getAttribute("formatCode") for e in sub(styles,X,"numFmt")}
                xfs = sub(styles,X,"cellXfs")
                if xfs:
                    self.styles = [n.getAttribute("numFmtId") for n in children(xfs[0],X,"xf")]
        self.revision = digest(path)

    def part(self, name, change=False):
        if name not in self.cache:
            self.cache[name] = doc(self.z.read(name))
        if change:
            self.dirty.add(name)
        return self.cache[name]

    def newpart(self, name, xml):
        self.cache[name] = doc(xml.encode())
        self.dirty.add(name)
        return self.cache[name]

    def paragraphs(self):
        return sub(self.part("word/document.xml"), W, "p")

    def rows(self, sheet=None):
        if self.is_word:
            comments = {}
            tables={id(t):index+1 for index,t in enumerate(sub(self.part("word/document.xml"),W,"tbl"))}
            if "word/comments.xml" in self.z.namelist():
                for e in sub(self.part("word/comments.xml"),W,"comment"):
                    comments[e.getAttributeNS(W,"id")] = " ".join(text(t) for t in sub(e,W,"t"))
            result = []
            for i,p in enumerate(self.paragraphs()):
                runs = sub(p,W,"t")
                v = "".join(text(t) for t in runs)
                style = sub(p,W,"pStyle")
                ancestors = []; n = p.parentNode;table=0
                while n:
                    if n.nodeType==Node.ELEMENT_NODE and n.namespaceURI==W and n.localName in ("tc","tr"):
                        ancestors.append(str(children(n.parentNode,W,n.localName).index(n)+1))
                    if n.nodeType==Node.ELEMENT_NODE and n.namespaceURI==W and n.localName=="tbl" and not table:table=tables[id(n)]
                    n = n.parentNode
                fmt = style[0].getAttributeNS(W,"val") if style else ""
                if ancestors:
                    fmt += " · таблица "+"/".join(reversed(ancestors))
                rich=[dict(value="".join(text(t) for t in children(run,W,"t")),bold=bool(sub(run,W,"b")),italic=bool(sub(run,W,"i"))) for run in sub(p,W,"r")]
                result.append(dict(key=str(i+1),value=v,display=v,style=fmt.strip(" ·"),table=table,runs=rich,
                                   tableRow=int(ancestors[1]) if len(ancestors)>1 else 0,tableColumn=int(ancestors[0]) if ancestors else 0,
                                   bold=bool(sub(p,W,"b")),italic=bool(sub(p,W,"i")),
                                   comment=" ".join(comments.get(c.getAttributeNS(W,"id"),"") for c in sub(p,W,"commentReference"))))
        else:
            sheet = sheet or next(iter(self.sheets))
            result = []
            for cell in sub(self.part(self.sheets[sheet]), X, "c"):
                k = cell.getAttribute("r")
                if not k: continue
                v = children(cell,X,"v")
                raw = text(v[0]) if v else ""
                typ = cell.getAttribute("t")
                if typ=="s" and raw:
                    raw=self.strings[int(raw)]
                elif typ=="inlineStr":
                    raw="".join(text(t) for t in sub(cell,X,"t"))
                elif typ=="b":
                    raw="TRUE" if raw=="1" else "FALSE"
                f = children(cell,X,"f")
                format_id = int(cell.getAttribute("s") or 0)
                fmt = self.styles[format_id] if format_id<len(self.styles) else "0"
                format_name=self.formats.get(fmt,{"1":"0","2":"0.00","3":"#,##0","4":"#,##0.00","9":"0%","10":"0.00%","14":"yyyy-mm-dd","22":"yyyy-mm-dd hh:mm"}.get(fmt,""))
                display=raw
                if raw and typ in ("","n") and fmt in ("1","2","3","4","9","10"):
                    try:
                        num=D(raw)*(100 if fmt in ("9","10") else 1)
                        display=format(num,("," if fmt in ("3","4") else "")+(".2f" if fmt in ("2","4","10") else ".0f")).replace(","," ").replace(".",",")+("%" if fmt in ("9","10") else "")
                    except (ValueError,decimal.InvalidOperation):pass
                if raw and (fmt in ("14","15","16","17","22") or re.search(r"y{2,4}|дд|yyyy",format_name,re.I)) and typ in ("","n"):
                    try:
                        prop=sub(self.part("xl/workbook.xml"),X,"workbookPr")
                        start=datetime.datetime(1904,1,1) if prop and prop[0].getAttribute("date1904") in ("1","true") else datetime.datetime(1899,12,30)
                        display=(start+datetime.timedelta(days=float(raw))).isoformat(sep=" ").removesuffix(" 00:00:00")
                    except (ArithmeticError,ValueError): pass
                kind="formula" if f else "text" if typ in ("inlineStr","s","str") else typ or "n"
                if f and (not v or f[0].getAttribute("ca") in ("1","true")):
                    display="Требуется пересчёт в Excel"
                result.append(dict(key=k,value="="+text(f[0]) if f else raw,display=display,kind=kind,style=format_name,comment=""))
        if len(result)>MAX_ROWS: raise ValueError("Лимит редактора — 25 000 заполненных ячеек или абзацев за одно открытие.")
        return result

    def change_word(self, row, paragraphs):
        k = int(row["key"])-1
        if not 0 <= k < len(paragraphs): raise ValueError("Абзац отсутствует.")
        p = paragraphs[k]
        action = row.get("operation","replace")
        if action=="style":
            self.word_style(row["value"])
            props=children(p,W,"pPr")
            if props:prop=props[0]
            else:
                prop=p.ownerDocument.createElementNS(W,"w:pPr");p.insertBefore(prop,p.firstChild)
            styles=children(prop,W,"pStyle")
            style=styles[0] if styles else prop.ownerDocument.createElementNS(W,"w:pStyle")
            if not styles:prop.insertBefore(style,prop.firstChild)
            style.setAttributeNS(W,"w:val",row["value"]);return
        if action=="comment":
            self.comment(p,row["value"]);return
        if action=="delete":
            if p.parentNode.localName=="tc" and len(children(p.parentNode,W,"p"))==1:
                raise ValueError("Оставьте в ячейке таблицы хотя бы один абзац.")
            p.parentNode.removeChild(p);return
        if action in ("before","after"):
            newp=p.ownerDocument.createElementNS(W,"w:p")
            prop=children(p,W,"pPr")
            if prop: newp.appendChild(prop[0].cloneNode(True))
            p.parentNode.insertBefore(newp,p if action=="before" else p.nextSibling)
            p=newp
        if any(sub(p,W,t) for t in ("fldChar","instrText","drawing","ins","del","tab","br")):
            raise ValueError("Для правки сложного поля, рисунка или существующей рецензии используйте Word.")
        nodes=sub(p,W,"t")
        if not nodes: nodes=[element(element(p,W,"w:r"),W,"w:t")]
        value=row["value"]
        for i,t in enumerate(nodes):
            end=len(value) if i==len(nodes)-1 else min(len(text(t)),len(value))
            settext(t,value[:end]);t.setAttribute("xml:space","preserve");value=value[end:]

    def word_style(self,style):
        if style not in ("Normal","Title","Heading1","Heading2"):raise ValueError("Выберите встроенный стиль.")
        name="word/styles.xml"
        if name in self.cache or name in self.z.namelist():styles=self.part(name,True)
        else:
            styles=self.newpart(name,'<w:styles xmlns:w="'+W+'"/>')
            relname="word/_rels/document.xml.rels"
            rel=self.part(relname,True) if relname in self.cache or relname in self.z.namelist() else self.newpart(relname,'<Relationships xmlns="'+P+'"/>')
            r=element(rel.documentElement,P,"Relationship");r.setAttribute("Id","ostum"+uuid.uuid4().hex);r.setAttribute("Type",R+"/styles");r.setAttribute("Target","styles.xml")
            c=element(self.part("[Content_Types].xml",True).documentElement,T,"Override");c.setAttribute("PartName","/"+name);c.setAttribute("ContentType","application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml")
        if any(s.getAttributeNS(W,"styleId")==style for s in sub(styles,W,"style")):return
        st=element(styles.documentElement,W,"w:style");st.setAttributeNS(W,"w:type","paragraph");st.setAttributeNS(W,"w:styleId",style)
        element(st,W,"w:name").setAttributeNS(W,"w:val",style)
        if style!="Normal":
            rp=element(st,W,"w:rPr");element(rp,W,"w:b");element(rp,W,"w:sz").setAttributeNS(W,"w:val",{"Title":"44","Heading1":"36","Heading2":"28"}[style])

    def comment(self,p,value):
        name="word/comments.xml"
        if name in self.z.namelist() or name in self.cache: comments=self.part(name,True)
        else:
            comments=self.newpart(name,'<w:comments xmlns:w="'+W+'"/>')
            relname="word/_rels/document.xml.rels"
            rel=self.part(relname,True) if relname in self.z.namelist() or relname in self.cache else self.newpart(relname,'<Relationships xmlns="'+P+'"/>')
            r=element(rel.documentElement,P,"Relationship")
            r.setAttribute("Id","ostum"+uuid.uuid4().hex);r.setAttribute("Type",R+"/comments");r.setAttribute("Target","comments.xml")
            c=element(self.part("[Content_Types].xml",True).documentElement,T,"Override")
            c.setAttribute("PartName","/"+name);c.setAttribute("ContentType","application/vnd.openxmlformats-officedocument.wordprocessingml.comments+xml")
        i=str(max([-1]+[int(c.getAttributeNS(W,"id")) for c in sub(comments,W,"comment")])+1)
        c=element(comments.documentElement,W,"w:comment");c.setAttributeNS(W,"w:id",i);c.setAttributeNS(W,"w:author","OSTUM")
        element(element(element(c,W,"w:p"),W,"w:r"),W,"w:t",value)
        ref=element(element(p,W,"w:r"),W,"w:commentReference");ref.setAttributeNS(W,"w:id",i)

    def change_cell(self, sheet, row):
        root=self.part(self.sheets[sheet],True)
        k=row["key"].upper();r,c=address(k)
        data=sub(root,X,"sheetData")[0]
        if sub(root,X,"sheetProtection"): raise ValueError("Лист защищён владельцем.")
        rows=children(data,X,"row")
        line=next((n for n in rows if int(n.getAttribute("r"))==r),None)
        if line is None:
            line=root.createElementNS(X,"row");line.setAttribute("r",str(r))
            data.insertBefore(line,next((n for n in rows if int(n.getAttribute("r"))>r),None))
        cells=children(line,X,"c")
        cell=next((e for e in cells if e.getAttribute("r")==k),None)
        if cell is None:
            cell=root.createElementNS(X,"c");cell.setAttribute("r",k)
            line.insertBefore(cell,next((e for e in cells if address(e.getAttribute("r"))[1]>c),None))
        if row.get("operation")=="format":
            self.format_cell(cell,row["value"]);return
        for f in children(cell,X,"f"):
            if f.getAttribute("t") in ("shared","array","dataTable"): raise ValueError("Эта формула входит в общий диапазон. Откройте его в Excel.")
        for tag in ("v","is","f"):
            for e in children(cell,X,tag):cell.removeChild(e)
        if cell.hasAttribute("t"):cell.removeAttribute("t")
        v=row["value"]
        if row.get("operation")=="literal":
            cell.setAttribute("t","inlineStr");element(element(cell,X,"is"),X,"t",v).setAttribute("xml:space","preserve")
        elif v.startswith("="):
            if re.search(r"\[|https?:|WEBSERVICE\s*\(|HYPERLINK\s*\(|\|",v,re.I): raise ValueError("Внешние формулы в редакторе отключены.")
            element(cell,X,"f",v[1:])
        elif re.fullmatch(r"-?(0|[1-9][0-9]*)(\.[0-9]+)?",v):
            element(cell,X,"v",v)
        elif v in ("TRUE","FALSE"):
            cell.setAttribute("t","b");element(cell,X,"v","1" if v=="TRUE" else "0")
        elif v:
            cell.setAttribute("t","inlineStr");t=element(element(cell,X,"is"),X,"t",v);t.setAttribute("xml:space","preserve")
        dimension=sub(root,X,"dimension")
        if dimension:
            endpoints=[address(a) for a in dimension[0].getAttribute("ref").split(":")]+[(r,c)]
            dimension[0].setAttribute("ref",column(min(n[1] for n in endpoints))+str(min(n[0] for n in endpoints))+":"+column(max(n[1] for n in endpoints))+str(max(n[0] for n in endpoints)))

    def format_cell(self,cell,value):
        name="xl/styles.xml"
        supported={"Общий":"0","0":"1","0.00":"2","#,##0.00":"4","0.00%":"10","yyyy-mm-dd":"14"}
        if value not in supported:raise ValueError("Выберите один из доступных форматов.")
        if name not in self.z.namelist() and name not in self.cache:
            self.newpart(name,'<styleSheet xmlns="'+X+'"><fonts count="1"><font><sz val="11"/><name val="Calibri"/></font></fonts><fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills><borders count="1"><border/></borders><cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs><cellXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/></cellXfs><cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles></styleSheet>')
            r=element(self.part("xl/_rels/workbook.xml.rels",True).documentElement,P,"Relationship")
            r.setAttribute("Id","ostum"+uuid.uuid4().hex);r.setAttribute("Type",R+"/styles");r.setAttribute("Target","styles.xml")
            c=element(self.part("[Content_Types].xml",True).documentElement,T,"Override")
            c.setAttribute("PartName","/"+name);c.setAttribute("ContentType","application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml")
        group=sub(self.part(name,True),X,"cellXfs")[0];formats=children(group,X,"xf")
        new=formats[int(cell.getAttribute("s") or "0")].cloneNode(True)
        new.setAttribute("numFmtId",supported[value]);new.setAttribute("applyNumberFormat","1")
        index=next((i for i,f in enumerate(formats) if f.toxml()==new.toxml()),len(formats))
        if index==len(formats):group.appendChild(new);group.setAttribute("count",str(len(formats)+1))
        cell.setAttribute("s",str(index))

    def commit(self, patches, sheet, revision):
        if self.read_only:raise ValueError("Подписанные документы и макросы доступны для просмотра.")
        if self.revision!=revision:raise ValueError("Файл изменился. Откройте актуальную версию.")
        if not patches or len(patches)>MAX_ROWS:raise ValueError("Выберите изменения для сохранения.")
        paras=self.paragraphs() if self.is_word else []
        if self.is_word:
            self.part("word/document.xml",True)
            # Anchors refer to the original paragraphs. Insertions at the end share the last anchor.
            after={}
            for row in patches:
                if row.get("operation")=="after":
                    k=int(row["key"])-1;anchor=after.get(k,paras[k])
                    self.change_word(dict(row,key="1"),[anchor]);after[k]=anchor.nextSibling
                else:self.change_word(row,paras)
        else:
            for row in patches:self.change_cell(sheet,row)
            book=self.part("xl/workbook.xml",True);calc=sub(book,X,"calcPr")
            (calc[0] if calc else element(book.documentElement,X,"calcPr")).setAttribute("fullCalcOnLoad","1")
            changed={row["key"].upper() for row in patches if row.get("operation")!="format"}
            if changed:
                self.recalculate(sheet,changed)
        fd, tmp=tempfile.mkstemp(prefix=".ostum-",suffix=".tmp",dir=self.path.parent);os.close(fd)
        try:
            with zipfile.ZipFile(tmp,"w",zipfile.ZIP_DEFLATED) as out:
                for entry in self.z.infolist():
                    value=self.cache[entry.filename].toxml(encoding="utf-8") if entry.filename in self.dirty else self.z.read(entry)
                    out.writestr(entry,value)
                for name in self.dirty-set(self.z.namelist()):out.writestr(name,self.cache[name].toxml(encoding="utf-8"))
            self.z.close()
            replace(self.path,Path(tmp),revision)
        finally:
            Path(tmp).unlink(missing_ok=True)

    def recalculate(self,sheet,changed):
        # Follow references from changed inputs. Preserve Excel's original XML and
        # cached type/value for every independent formula, including unsupported ones.
        cells={}
        formulas={}
        for name,part in self.sheets.items():
            for cell in sub(self.part(part),X,"c"):
                key=(name,cell.getAttribute("r"))
                cells[key]=cell
                fs=children(cell,X,"f")
                if fs:formulas[key]=fs[0]
        lookup={name.casefold():name for name in self.sheets}
        dependents=collections.defaultdict(set)
        areas=collections.defaultdict(list)
        affected={(sheet,k) for k in changed}
        opaque=set()
        defined={n.getAttribute("name").casefold() for n in sub(self.part("xl/workbook.xml"),X,"definedName")}
        for key,f in formulas.items():
            expression=text(f)
            reference_text=re.sub(r'"(?:[^"]|"")*"','',expression)
            # Preserve Excel's cached values for indirect, shared and named
            # dependencies. Mark them and their consumers for Excel's calculation.
            if (f.getAttribute("t") in ("shared","array","dataTable") or
                re.search(r"\b(?:INDIRECT|OFFSET)\s*\(|\[|\$?[A-Z]{1,3}:\$?[A-Z]{1,3}\b|\d+:\d+|[A-Z0-9_]+:[A-Z0-9_]+!",reference_text,re.I) or
                defined.intersection(w.casefold() for w in re.findall(r"[\w.]+",reference_text))):
                opaque.add(key)
            for m in CELL_REF.finditer(reference_text):
                name=lookup.get((m[1] or m[2] or key[0]).replace("''","'").casefold())
                if name is None:continue
                start=m[3].replace("$","").upper()
                if not m[4]:dependents[name,start].add(key)
                else:areas[name].append((rect(start,m[4].replace("$","").upper()),key))
        affected.update(opaque)
        queue=collections.deque(affected)
        while queue:
            current=queue.popleft()
            r,c=address(current[1])
            hits=set(dependents[current])
            hits.update(key for (top,left,bottom,right),key in areas[current[0]] if top<=r<=bottom and left<=c<=right)
            for hit in hits-affected:
                affected.add(hit)
                queue.append(hit)
        memo={}
        def calc(key,trail):
            if key in memo:return memo[key]
            if key in opaque:raise ValueError("Требуется пересчёт в Excel.")
            if key not in affected or key not in formulas:return cell_value(cells.get(key))
            if key in trail or len(trail)>64:raise ValueError()
            expression=text(formulas[key]).upper().replace("$","")
            if len(expression)>512:raise ValueError()
            def value(k):return calc((key[0],k),trail|{key})
            def totals(m):
                top,left,bottom,right=rect(m[2],m[3])
                if (bottom-top+1)*(right-left+1)>MAX_ROWS:raise ValueError()
                def numbers():
                    for r in range(top,bottom+1):
                        for c in range(left,right+1):
                            try:yield value(column(c)+str(r))
                            except FormulaError:
                                if m[1]!="COUNT":raise
                return "("+str(aggregate(m[1],numbers())[0])+")"
            expression=re.sub(r"\b(SUM|AVERAGE|MIN|MAX|COUNT)\(\s*([A-Z]+[0-9]+)\s*:\s*([A-Z]+[0-9]+)\s*\)",totals,expression)
            def visit(n):
                if isinstance(n,ast.Constant) and type(n.value) in (int,float):return D(str(n.value))
                if isinstance(n,ast.Name) and re.fullmatch(r"[A-Z]{1,3}[1-9]\d*",n.id):
                    v=value(n.id)
                    if v is None:return D(0)
                    if isinstance(v,bool):return D(int(v))
                    if not isinstance(v,D):raise ValueError()
                    return v
                if isinstance(n,ast.UnaryOp) and isinstance(n.op,(ast.UAdd,ast.USub)):return (-1 if isinstance(n.op,ast.USub) else 1)*visit(n.operand)
                ops={ast.Add:operator.add,ast.Sub:operator.sub,ast.Mult:operator.mul,ast.Div:operator.truediv}
                if isinstance(n,ast.BinOp) and type(n.op) in ops:
                    left,right=visit(n.left),visit(n.right)
                    if isinstance(n.op,ast.Div) and right==0:raise FormulaError("#DIV/0!")
                    return ops[type(n.op)](left,right)
                raise ValueError()
            result=visit(ast.parse(expression,mode="eval").body)
            memo[key]=result
            return result
        for key in affected & formulas.keys():
            cell=cells[key]
            self.dirty.add(self.sheets[key[0]])
            if key in opaque:
                formulas[key].setAttribute("ca","1")
                continue
            try:result=format(calc(key,set()),".15g")
            except FormulaError as exc:result=str(exc)
            except (ZeroDivisionError,decimal.DivisionByZero):result="#DIV/0!"
            except (ValueError,ArithmeticError,SyntaxError,RecursionError):result=None
            if result is None:
                formulas[key].setAttribute("ca","1")
                continue
            for old in children(cell,X,"v"):cell.removeChild(old)
            if formulas[key].hasAttribute("ca"):formulas[key].removeAttribute("ca")
            if cell.hasAttribute("t"):cell.removeAttribute("t")
            if result is not None:
                if result.startswith("#"):cell.setAttribute("t","e")
                element(cell,X,"v",result)


CELL_REF=re.compile(r"(?<![\w.])(?:(?:'((?:[^']|'')+)'|([\w.]+))!)?(\$?[A-Z]{1,3}\$?[1-9]\d{0,6})(?::(\$?[A-Z]{1,3}\$?[1-9]\d{0,6}))?(?![\w.(])",re.I)


class FormulaError(Exception):
    pass


def rect(a,b):
    r,c=address(a);s,d=address(b)
    return min(r,s),min(c,d),max(r,s),max(c,d)


def cell_value(cell):
    if cell is None:return None
    typ=cell.getAttribute("t")
    vs=children(cell,X,"v")
    fs=children(cell,X,"f")
    if fs and (not vs or fs[0].getAttribute("ca") in ("1","true")):raise ValueError("Для формулы нужен пересчёт в Excel.")
    if typ=="e":raise FormulaError(text(vs[0]))
    if typ=="b":return bool(vs and text(vs[0])=="1")
    if typ not in ("","n"):return ""
    return D(text(vs[0])) if vs and text(vs[0]) else None


def aggregate(op,values):
    nums=[v for v in values if isinstance(v,D)]
    if op=="AVERAGE" and not nums:raise FormulaError("#DIV/0!")
    if op=="COUNT":return D(len(nums)),len(nums)
    if op=="AVERAGE":return sum(nums)/len(nums),len(nums)
    if op=="MIN":return min(nums,default=D(0)),len(nums)
    if op=="MAX":return max(nums,default=D(0)),len(nums)
    if op!="SUM":raise ValueError("Неизвестная операция диапазона.")
    return sum(nums,D(0)),len(nums)


def versions(path):
    folder=path.parent/".ostum-versions"
    if folder.is_symlink() or folder.is_junction():raise ValueError("Папка версий является ссылкой.")
    key=hashlib.sha256(path.name.casefold().encode()).hexdigest()[:32]
    return folder, key


def replace(path,prepared,revision):
    folder,key=versions(path);folder.mkdir(exist_ok=True)
    lock_path=folder/(key+".lock")
    if lock_path.is_symlink() or lock_path.is_junction():raise ValueError("Недопустимый путь блокировки.")
    with lock_path.open("a+b") as lock:
        lock.seek(0)
        if os.name=="nt":
            import msvcrt
            msvcrt.locking(lock.fileno(),msvcrt.LK_LOCK,1)
        else:
            import fcntl
            fcntl.flock(lock,fcntl.LOCK_EX)
        replace_locked(path,prepared,revision,folder,key)


def replace_locked(path,prepared,revision,folder,key):
    if digest(path)!=revision:raise ValueError("Файл изменён другим пользователем.")
    backup=folder/(key+"-"+datetime.datetime.now(datetime.UTC).strftime("%Y%m%dT%H%M%S%fZ")+path.suffix)
    if os.name=="nt":
        import ctypes
        replace_win=ctypes.WinDLL("kernel32",use_last_error=True).ReplaceFileW
        if not replace_win(str(path),str(prepared),str(backup),0,None,None):raise ctypes.WinError(ctypes.get_last_error())
    else:
        shutil.copyfile(path,backup);os.replace(prepared,path)


def compare(old,new):
    a=[r["value"] for r in old];b=[r["value"] for r in new];out=[]
    for op,i,j,k,l in difflib.SequenceMatcher(None,a,b,autojunk=True).get_opcodes():
        if op=="equal":continue
        common=min(j-i,l-k)
        for n in range(common):out.append(dict(key=str(i+n+1),old=a[i+n],value=b[k+n],operation="replace"))
        for n in range(i+common,j):out.append(dict(key=str(n+1),old=a[n],value="",operation="delete"))
        for n in range(k+common,l):out.append(dict(key=str(j+1 if j<len(a) else len(a)),old="",value=b[n],operation="before" if j<len(a) else "after"))
    return out


def write_new(path, data, word=False, sheet="Данные"):
    def rel(target,kind):return '<Relationship Id="rId1" Type="'+R+'/'+kind+'" Target="'+target+'"/>'
    def rels(body):return '<Relationships xmlns="'+P+'">'+body+'</Relationships>'
    main="word/document.xml" if word else "xl/workbook.xml"
    spec="wordprocessingml.document" if word else "spreadsheetml.sheet"
    parts={"_rels/.rels":rels(rel(main,"officeDocument"))}
    ct='<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/'+main+'" ContentType="application/vnd.openxmlformats-officedocument.'+spec+'.main+xml"/>'
    if word:
        parts[main]='<w:document xmlns:w="'+W+'"><w:body>'+''.join('<w:p><w:r><w:t xml:space="preserve">'+escape(str(v))+'</w:t></w:r></w:p>' for v in data)+'<w:sectPr/></w:body></w:document>'
    else:
        parts[main]='<workbook xmlns="'+X+'" xmlns:r="'+R+'"><sheets><sheet sheetId="1" name='+quoteattr(sheet)+' r:id="rId1"/></sheets></workbook>'
        parts["xl/_rels/workbook.xml.rels"]=rels(rel("worksheets/sheet1.xml","worksheet"))
        ct+='<Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>'
        rows=[]
        for r,row in enumerate(data,1):
            cells=[]
            for c,v in enumerate(row,1):
                ref=column(c)+str(r)
                if type(v) in (int,float,D):cells.append('<c r="'+ref+'"><v>'+str(v)+'</v></c>')
                else:cells.append('<c r="'+ref+'" t="inlineStr"><is><t xml:space="preserve">'+escape(str(v))+'</t></is></c>')
            rows.append('<row r="'+str(r)+'">'+''.join(cells)+'</row>')
        parts["xl/worksheets/sheet1.xml"]='<worksheet xmlns="'+X+'"><sheetData>'+''.join(rows)+'</sheetData></worksheet>'
    parts["[Content_Types].xml"]='<Types xmlns="'+T+'">'+ct+'</Types>'
    # Create privately, then publish an entire package without overwriting an existing file.
    fd,tmp=tempfile.mkstemp(dir=path.parent);os.close(fd)
    try:
        with zipfile.ZipFile(tmp,"w",zipfile.ZIP_DEFLATED) as z:
            for name,xml in parts.items():z.writestr(name,xml.encode())
        if os.name=="nt":os.rename(tmp,path)
        else:os.link(tmp,path)
    finally:Path(tmp).unlink(missing_ok=True)


def offers(sources):
    result=[["Позиция","Количество","Ед.","Цена без НДС","Валюта","НДС, %","Итого с НДС","Проверка","Источник","Ячейка"]]
    for source in sources:
        book=Office(source)
        try:
            sheet=next(iter(book.sheets));rows=collections.defaultdict(dict)
            for c in book.rows(sheet):
                r,col=address(c["key"]);rows[r][col]=c["display"]
            header={k.strip().casefold():c for c,k in rows[1].items()}
            required=("позиция","количество","ед.","цена без ндс","валюта","ндс, %")
            if any(k not in header for k in required):raise ValueError("Нужны столбцы "+", ".join(required)+" в "+Path(source).name)
            for r in sorted(k for k in rows if k>1):
                vals=[rows[r].get(header[k],"").strip() for k in required]
                if not any(vals):continue
                check="Сопоставлять в этой единице и валюте";total=""
                try:
                    q,price,tax=[D(vals[k].removesuffix("%").replace("\u00a0","").replace(" ","").replace(",",".")) for k in (1,3,5)]
                    if min(q,price,tax)<0 or q==0 or tax>100 or not all(vals[k] for k in (0,2,4)):raise ValueError()
                    total=(q*price*(1+tax/100)).quantize(D(".01"));vals[1],vals[3],vals[5]=q,price,tax
                except (ValueError,decimal.InvalidOperation):check="Требует уточнения"
                result.append(vals+[total,check,Path(source).name,sheet+"!"+column(header["цена без ндс"])+str(r)])
        finally:book.z.close()
    return [result[0]]+sorted(result[1:],key=lambda v:(v[0],v[2],v[4],v[6]=="" ,v[6] or 0))


TASK_HEADERS=["Поручение","Ответственный","Срок","Статус","Файл","Лист","Фрагмент","Текст источника"]
TASK_FILE="Поручения OSTUM.xlsx"
INVOICE_FILE="Реестр счетов OSTUM.xlsx"
INVOICE_HEADERS=["Номер","Дата","Сумма","Валюта","Источник","Страница / фрагмент","SHA-256 источника"]


def table_records(book,sheet,headers):
    if sheet not in book.sheets:raise ValueError("Неверное имя листа реестра.")
    cells=collections.defaultdict(dict)
    for cell in book.rows(sheet):
        r,c=address(cell["key"]);cells[r][c]=cell["display"]
    if [cells[1].get(c) for c in range(1,len(headers)+1)]!=headers:raise ValueError("Заголовки реестра изменены.")
    return [(r,[values.get(c,"") for c in range(1,len(headers)+1)]) for r,values in sorted(cells.items()) if r>1]


def add_invoices(path,req):
    if path.name!=INVOICE_FILE:raise ValueError("Выберите реестр счетов OSTUM.")
    book=Office(path) if path.exists() else None
    try:
        existing=table_records(book,"Счета",INVOICE_HEADERS) if book else []
        known={r[-1] for _,r in existing}
        nextrow=max((n for n,_ in existing),default=1)+1
        fresh=[];skipped=0
        for record in req["records"]:
            name=record["Source"]
            if Path(name).name!=name or name in (".","..") or ":" in name:raise ValueError("Недопустимое имя источника.")
            source=path.parent/name
            if source.is_symlink() or source.is_junction() or digest(source)!=record["Hash"]:raise ValueError("Источник изменился. Повторите импорт.")
            if record["Hash"] in known:skipped+=1;continue
            try:value=D((record.get("Amount") or "").replace(" ","").replace(",","."))
            except decimal.InvalidOperation:raise ValueError("Проверьте сумму счёта "+name) from None
            if not value.is_finite() or not (record.get("Number") or "").strip() or not (record.get("Currency") or "").strip():raise ValueError("Проверьте номер, валюту и сумму.")
            date=record.get("Date") or ""
            if date:datetime.date.fromisoformat(date)
            fresh.append([record["Number"],date,value,record["Currency"],name,record["Location"],record["Hash"]])
            known.add(record["Hash"])
        if (nextrow+len(fresh)-1)*len(INVOICE_HEADERS)>MAX_ROWS:raise ValueError("Лимит размера реестра превышен.")
        if fresh and book:
            patches=[dict(key=column(c)+str(r),value=str(v),operation="replace" if c==3 else "literal")
                     for r,row in enumerate(fresh,nextrow) for c,v in enumerate(row,1)]
            book.commit(patches,"Счета",book.revision)
        elif fresh:write_new(path,[INVOICE_HEADERS]+fresh,sheet="Счета")
        return dict(added=len(fresh),skipped=skipped)
    finally:
        if book:book.z.close()


def active_tasks(book,period,today):
    items=[]
    sunday=today+datetime.timedelta(days=6-today.weekday())
    for row,record in table_records(book,"Поручения",TASK_HEADERS):
        title,owner,due,status,source,sheet,key,_=record
        if not title or not due or status.strip().casefold() in ("готово","выполнено","завершено","отменено"):continue
        try:day=datetime.date.fromisoformat(due)
        except ValueError:raise ValueError("Проверьте дату срока в строке "+str(row)) from None
        if period=="overdue" and day>=today or period=="week" and not today<=day<=sunday:continue
        items.append(dict(title=title,owner=owner,due=due,source=source,location=(sheet+"!" if sheet else "")+key,row=row))
    return sorted(items,key=lambda t:(t["due"],t["row"]))


def calendar_bytes(items,register):
    def escaped(s):return s.replace("\\","\\\\").replace("\r\n","\n").replace("\r","\n").replace("\n","\\n").replace(",","\\,").replace(";","\\;")
    def fold(line):
        chunks=[];piece=bytearray()
        for ch in line:
            raw=ch.encode()
            if len(piece)+len(raw)>75:chunks.append(bytes(piece));piece=bytearray(b" ")
            piece.extend(raw)
        return b"\r\n".join(chunks+[bytes(piece)])
    lines=["BEGIN:VCALENDAR","VERSION:2.0","PRODID:-//OSTUM//Desktop//RU","CALSCALE:GREGORIAN"]
    stamp=datetime.datetime.now(datetime.UTC).strftime("%Y%m%dT%H%M%SZ")
    for item in items:
        identity=str(register.resolve()).casefold()+"\n"+str(item["row"])+"\n"+item["source"]+"\n"+item["location"]
        uid=hashlib.sha256(identity.encode()).hexdigest()+"@ostum.local"
        lines.extend(["BEGIN:VTODO","UID:"+uid,"DTSTAMP:"+stamp,"DUE;VALUE=DATE:"+item["due"].replace("-",""),
                      "SUMMARY:"+escaped(item["title"]),"DESCRIPTION:"+escaped(" · ".join(filter(None,[item["owner"],item["source"],item["location"]]))),"END:VTODO"])
    return b"\r\n".join(map(fold,lines+["END:VCALENDAR"]))+b"\r\n"


def add_task(path,req):
    source=Path(req["other"])
    if path.name!=TASK_FILE or path.parent.resolve()!=source.parent.resolve() or path.resolve()==source.resolve():
        raise ValueError("Реестр хранится рядом с исходным документом.")
    title=req["title"].strip()
    if not title or len(title)>500:raise ValueError("Введите поручение длиной от 1 до 500 символов.")
    due=req.get("due","")
    if due:datetime.date.fromisoformat(due)
    if len(req.get("owner",""))>160:raise ValueError("Сократите имя ответственного.")
    book=Office(source)
    try:
        if book.revision!=req["revision"]:raise ValueError("Источник изменился. Откройте его заново.")
        rows=book.rows(req.get("sheet"))
        fragment=next((r for r in rows if r["key"]==req["key"]),None)
        if fragment is None:raise ValueError("Фрагмент отсутствует в документе.")
        record=[title,req.get("owner","").strip(),due,"Открыто",source.name,req.get("sheet",""),fragment["key"],fragment["value"][:2000]]
    finally:book.z.close()
    if not path.exists():
        try:
            write_new(path,[TASK_HEADERS,record],sheet="Поручения")
            return
        except FileExistsError:pass
    register=Office(path)
    try:
        if "Поручения" not in register.sheets:raise ValueError("Имя реестра уже занимает другая книга.")
        cells={r["key"]:r["value"] for r in register.rows("Поручения")}
        if [cells.get(column(i)+"1") for i in range(1,9)]!=TASK_HEADERS:raise ValueError("Столбцы реестра изменены. Проверьте заголовки.")
        if len(cells)+len(record)>MAX_ROWS:raise ValueError("Реестр заполнен. Сохраните его под другим именем и создайте следующий.")
        n=max(address(k)[0] for k in cells)+1
        patches=[dict(key=column(i)+str(n),value=value,old="",operation="literal") for i,value in enumerate(record,1)]
        register.commit(patches,"Поручения",register.revision)
    finally:register.z.close()


def indexed_sheet(path,sheet,key_column,revision):
    book=Office(path)
    try:
        if book.is_word or sheet not in book.sheets:raise ValueError("Выберите существующий лист XLSX.")
        if book.revision!=revision:raise ValueError("Таблица изменилась. Начните сверку заново.")
        rows=collections.defaultdict(dict)
        for cell in book.rows(sheet):
            n,c=address(cell["key"])
            if cell["value"] or cell["display"]:rows[n][c]=cell
        names={c:h["display"].strip() for c,h in rows.pop(1,{}).items()}
        cols={name.casefold():c for c,name in names.items()}
        if not names or "" in cols or len(cols)!=len(names) or any(c not in names for row in rows.values() for c in row):
            raise ValueError("В первой строке нужны уникальные названия всех заполненных столбцов: "+path.name)
        if key_column not in cols:raise ValueError("Выбранный заголовок отсутствует.")
        index=collections.defaultdict(list)
        for n,row in sorted(rows.items()):
            key=row.get(cols[key_column],{})
            value=key.get("display","") if key.get("kind")=="formula" else key.get("value","")
            index[value.strip()].append((n,row))
        return names,cols,index
    finally:book.z.close()


def reconcile(req):
    paths=list(map(Path,req["sources"]));sheets=req["sheets"];keys=[k.strip().casefold() for k in req["keys"]]
    if len(paths)!=2 or paths[0].resolve()==paths[1].resolve():raise ValueError("Выберите две разные книги.")
    tabs=[indexed_sheet(p,s,k,v) for p,s,k,v in zip(paths,sheets,keys,req["revisions"],strict=True)]
    (an,ac,a),(bn,bc,b)=tabs
    output=[["Результат","Ключ","Столбец","Раньше","Теперь","Исходная книга · "+paths[0].name,"Новая книга · "+paths[1].name]]
    def add(*row):
        if (len(output)+1)*7>MAX_ROWS:raise ValueError("Отчёт превышает 25 000 ячеек. Сверьте меньшие выборки.")
        output.append(row)
    def loc(side,n,c):return sheets[side]+"!"+column(c)+str(n)
    left_headers=ac.keys()-{keys[0]};right_headers=bc.keys()-{keys[1]}
    for k in sorted(left_headers ^ right_headers):
        if k in left_headers:add("Столбец удалён","",an[ac[k]],"","",loc(0,1,ac[k]),"")
        else:add("Столбец добавлен","",bn[bc[k]],"","","",loc(1,1,bc[k]))
    common=[k for k in ac if k in bc and k not in keys]
    for key in sorted(a.keys()|b.keys()):
        left=a.get(key,[]);right=b.get(key,[])
        if not key or len(left)>1 or len(right)>1 or not left or not right:
            label="Пустой ключ" if not key else "Ключ неоднозначен" if len(left)>1 or len(right)>1 else "Строка удалена" if left else "Строка добавлена"
            for side,items in enumerate((left,right)):
                for n,row in items:
                    where=loc(side,n,(ac,bc)[side][keys[side]])
                    add(label,key,"","","",where if side==0 else "",where if side==1 else "")
            continue
        (ln,lr),(rn,rr)=left[0],right[0]
        for k in common:
            before=lr.get(ac[k],{});after=rr.get(bc[k],{})
            if any(before.get(field,"")!=after.get(field,"") for field in ("value","kind","display")):
                def shown(cell):
                    value=cell.get("value","");display=cell.get("display","")
                    return value if display==value or not display else value+" · "+display
                label="Тип ячейки изменён" if before.get("value")==after.get("value") and before.get("kind")!=after.get("kind") else "Значение изменено"
                add(label,key,an[ac[k]],shown(before),shown(after),loc(0,ln,ac[k]),loc(1,rn,bc[k]))
    if len(output)==1:add("Различий не найдено","","","","","","")
    return output


def execute(req):
    path=Path(req["path"]);op=req["action"]
    if op=="create":
        write_new(path,req["data"],path.suffix.lower()==".docx");return {}
    if op=="offers":
        write_new(path,offers(req["sources"]),sheet="Сравнение");return {}
    if op=="reconcile":
        write_new(path,reconcile(req),sheet="Сверка");return {}
    if op=="preview_reconcile":
        return {"differences":reconcile(req)}
    if op=="task":
        add_task(path,req);return {}
    if op=="invoices":return add_invoices(path,req)
    if op=="deadlines":
        if path.name!=TASK_FILE:raise ValueError("Выберите реестр поручений.")
        book=Office(path)
        try:
            items=active_tasks(book,req["period"],datetime.date.fromisoformat(req["today"]))
            result=dict(tasks=items)
            if req.get("calendar"):result["calendar"]=calendar_bytes(items,path).decode()
            return result
        finally:book.z.close()
    if op=="versions":
        folder,key=versions(path)
        return {"versions":sorted((p.name for p in folder.glob(key+"-*"+path.suffix)),reverse=True)}
    if op=="restore":
        folder,key=versions(path);name=req["version"]
        if not re.fullmatch(re.escape(key)+r"-\d{8}T\d{12}Z"+re.escape(path.suffix),name):raise ValueError("Неизвестная версия.")
        source=folder/name
        if source.is_symlink() or source.is_junction():raise ValueError("Недопустимый путь версии.")
        fd,tmp=tempfile.mkstemp(dir=path.parent);os.close(fd)
        try:
            shutil.copyfile(source,tmp);replace(path,Path(tmp),req["revision"])
        finally:Path(tmp).unlink(missing_ok=True)
        return {}
    book=Office(path)
    try:
        sheet=req.get("sheet") or next(iter(book.sheets),"")
        if op=="read":return dict(kind="word" if book.is_word else "excel",sheets=list(book.sheets),sheet=sheet,revision=book.revision,readOnly=book.read_only,rows=book.rows(sheet))
        if op=="aggregate":
            if book.is_word or sheet not in book.sheets:raise ValueError("Выберите лист Excel.")
            if book.revision!=req["revision"]:raise ValueError("Таблица изменилась. Откройте её заново.")
            top,left,bottom,right=rect(req["first"],req["last"])
            def values():
                for cell in sub(book.part(book.sheets[sheet]),X,"c"):
                    r,c=address(cell.getAttribute("r"))
                    if top<=r<=bottom and left<=c<=right:yield cell_value(cell)
            try:value,count=aggregate(req["function"],values())
            except FormulaError as exc:raise ValueError("Результат Excel: "+str(exc)) from exc
            return dict(number=format(value,"f"),numericCount=count,areaCount=(bottom-top+1)*(right-left+1))
        if op=="commit":
            book.commit(req["patches"],sheet,req["revision"]);return {}
        if op=="compare":
            other=Office(req["other"])
            try:
                if not book.is_word or not other.is_word:raise ValueError("Выберите два DOCX.")
                return dict(patches=compare(book.rows(),other.rows()))
            finally:other.z.close()
        raise ValueError("Неизвестная операция.")
    finally:book.z.close()


if __name__=="__main__":
    if sys.argv[1:]==["--reader"]:
        for line in sys.stdin:
            try:
                request=json.loads(line)
                if request["action"] not in ("read","aggregate","versions","deadlines","preview_reconcile"):
                    raise ValueError("Этот режим предназначен для чтения.")
                print(json.dumps(execute(request),ensure_ascii=True),flush=True)
            except Exception as exc:
                print(json.dumps({"error":str(exc)},ensure_ascii=True),flush=True)
            finally:
                request=line=None
    else:
        try:print(json.dumps(execute(json.load(sys.stdin)),ensure_ascii=True))
        except Exception as exc:
            print(json.dumps({"error":str(exc)},ensure_ascii=True));sys.exit(1)
