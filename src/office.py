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
                result.append(dict(key=k,value="="+text(f[0]) if f else raw,display=display,style=format_name,comment=""))
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
        if v.startswith("="):
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
            self.recalculate(sheet)
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

    def recalculate(self,sheet):
        root=self.part(self.sheets[sheet],True)
        cells={c.getAttribute("r"):c for c in sub(root,X,"c")}
        cache={}
        def calc(key,trail):
            if key in cache:return cache[key]
            if len(trail)>64 or key in trail:raise ValueError()
            cell=cells.get(key)
            if cell is None:return 0
            fs=children(cell,X,"f")
            if not fs:
                vs=children(cell,X,"v")
                if cell.getAttribute("t") not in ("","n","b"):raise ValueError()
                return float(text(vs[0])) if vs else 0
            expression=text(fs[0]).upper().replace("$","")
            if len(expression)>512:raise ValueError()
            def value(k):return calc(k,trail|{key})
            def totals(m):
                a,b=address(m[2]),address(m[3])
                if (b[0]-a[0]+1)*(b[1]-a[1]+1)>4096:raise ValueError()
                nums=[value(column(c)+str(r)) for r in range(a[0],b[0]+1) for c in range(a[1],b[1]+1)]
                return str({"SUM":sum,"AVERAGE":lambda x:sum(x)/len(x),"MIN":min,"MAX":max}[m[1]](nums))
            expression=re.sub(r"(SUM|AVERAGE|MIN|MAX)\(([A-Z]+[0-9]+):([A-Z]+[0-9]+)\)",totals,expression)
            syntax=ast.parse(expression,mode="eval").body
            def visit(n):
                if isinstance(n,ast.Constant) and type(n.value) in (float,int):return float(n.value)
                if isinstance(n,ast.Name) and re.fullmatch(r"[A-Z]{1,3}[1-9]\d*",n.id):return value(n.id)
                if isinstance(n,ast.UnaryOp) and isinstance(n.op,(ast.UAdd,ast.USub)):return (-1 if isinstance(n.op,ast.USub) else 1)*visit(n.operand)
                ops={ast.Add:operator.add,ast.Sub:operator.sub,ast.Mult:operator.mul,ast.Div:operator.truediv}
                if isinstance(n,ast.BinOp) and type(n.op) in ops:return ops[type(n.op)](visit(n.left),visit(n.right))
                raise ValueError()
            cache[key]=visit(syntax);return cache[key]
        for k,c in cells.items():
            if children(c,X,"f"):
                for old in children(c,X,"v"):c.removeChild(old)
                try:
                    v=calc(k,set())
                    if -1e308<v<1e308:element(c,X,"v",format(v,".15g"))
                except (ValueError,ArithmeticError,SyntaxError,RecursionError):pass


def versions(path):
    folder=path.parent/".ostum-versions"
    if folder.is_symlink() or folder.is_junction():raise ValueError("Папка версий является ссылкой.")
    key=hashlib.sha256(path.name.casefold().encode()).hexdigest()[:32]
    return folder, key


def replace(path,prepared,revision):
    folder,key=versions(path);folder.mkdir(exist_ok=True)
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


def execute(req):
    path=Path(req["path"]);op=req["action"]
    if op=="create":
        write_new(path,req["data"],path.suffix.lower()==".docx");return {}
    if op=="offers":
        write_new(path,offers(req["sources"]),sheet="Сравнение");return {}
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
    try:print(json.dumps(execute(json.load(sys.stdin)),ensure_ascii=True))
    except Exception as exc:
        print(json.dumps({"error":str(exc)},ensure_ascii=True));sys.exit(1)
