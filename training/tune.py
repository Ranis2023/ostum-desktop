"""Fit a small GGUF LoRA on Qwen's frozen final activations, using local llama.cpp."""
import argparse
import ctypes as ct
import json
import os
import pathlib
import struct
import urllib.request
import numpy as np

P = pathlib.Path
PROMPT = "Рабочее поручение: {task}\nОпредели подходящий формат работы. 0 — обычный ответ; 1 — структура слайдов; 2 — работа с макетом; 3 — раскадровка; 4 — привлечение клиентов; 5 — таблица для сравнения; 6 — исследовательская записка; 7 — работающий сайт или интерфейс. Код:"
FORMATS = {0:"B",1:"b",2:"H",3:"h",4:"I",5:"i",6:"f",7:"?",10:"Q",11:"q",12:"d"}

def post(args, route, body):
    req = urllib.request.Request("http://127.0.0.1:"+str(args.port)+"/"+route, json.dumps(body,ensure_ascii=False).encode(), {"Content-Type":"application/json","Authorization":"Bearer "+args.key.read_text().strip()})
    with urllib.request.urlopen(req,timeout=240) as reply:
        return json.load(reply)

def read_head(model):
    with model.open("rb") as f:
        def num(fmt):
            return struct.unpack("<"+fmt,f.read(struct.calcsize("<"+fmt)))[0]
        def string():
            return f.read(num("Q")).decode("utf-8",errors="replace")
        def skip(t):
            if t in FORMATS:
                f.seek(struct.calcsize("<"+FORMATS[t]),1)
            elif t==8:
                n=num("Q"); f.seek(n,1)
            elif t==9:
                typ,n=num("I"),num("Q")
                if typ in FORMATS: f.seek(n*struct.calcsize("<"+FORMATS[typ]),1)
                else:
                    for _ in range(n): skip(typ)
            else: raise ValueError(t)
        assert f.read(4)==b"GGUF" and num("I")==3
        tensors,keys=num("Q"),num("Q")
        arch,align=None,32
        for _ in range(keys):
            name,typ=string(),num("I")
            if name=="general.architecture": arch=string()
            elif name=="general.alignment": align=num("I")
            else: skip(typ)
        outputs={}
        for _ in range(tensors):
            name=string()
            dims=[num("Q") for _ in range(num("I"))]
            typ,offset=num("I"),num("Q")
            if name in ("output.weight","token_embd.weight"): outputs[name]=(dims,typ,offset)
        data_start=((f.tell()+align-1)//align)*align
    dims,typ,offset=outputs["output.weight"]
    return arch,dims,typ,data_start+offset

def head_rows(model, native, ids):
    arch,(hidden,vocab),typ,start=read_head(model)
    class Traits(ct.Structure):
        _fields_=[("name",ct.c_char_p),("block",ct.c_int64),("interleave",ct.c_int64),("size",ct.c_size_t),("quant",ct.c_bool),("to_float",ct.c_void_p),("from_float",ct.c_void_p)]
    with os.add_dll_directory(str(native)):
        lib=ct.CDLL(str(native/"ggml-base.dll"))
        lib.ggml_get_type_traits.argtypes=[ct.c_int]; lib.ggml_get_type_traits.restype=ct.POINTER(Traits)
        tr=lib.ggml_get_type_traits(typ).contents
        dequant=ct.CFUNCTYPE(None,ct.c_void_p,ct.c_void_p,ct.c_int64)(tr.to_float)
        length=hidden//tr.block*tr.size
        arr=np.empty((len(ids),hidden),np.float32)
        with model.open("rb") as f:
            for i,token in enumerate(ids):
                f.seek(start+token*length); row=f.read(length)
                dequant(row,arr[i].ctypes.data,hidden)
    return arch,vocab,arr

def lora_file(path, arch, a, b, name="Sfera personal work formats"):
    def s(t):
        val=t.encode(); return struct.pack("<Q",len(val))+val
    entries=[("general.architecture",8,s(arch)),("general.type",8,s("adapter")),("adapter.type",8,s("lora")),("adapter.lora.alpha",6,struct.pack("<f",float(len(a)))),("general.name",8,s(name))]
    values=[("output.weight.lora_a",a),("output.weight.lora_b",b)]
    def pad(n): return (-n)%32
    with path.open("wb") as f:
        f.write(struct.pack("<4sIQQ",b"GGUF",3,len(values),len(entries)))
        for name,t,data in entries: f.write(s(name)+struct.pack("<I",t)+data)
        offset=0
        for name,mat in values:
            f.write(s(name)+struct.pack("<IQQIQ",2,mat.shape[1],mat.shape[0],1,offset))
            offset+=mat.size*2+pad(mat.size*2)
        f.write(bytes(pad(f.tell())))
        for _,mat in values:
            f.write(mat.astype("<f2").tobytes()); f.write(bytes(pad(f.tell())))

def main():
    p=argparse.ArgumentParser()
    for name in ("key","model","native","data","out"): p.add_argument("--"+name,type=P,required=True)
    p.add_argument("--port",type=int,default=45386); p.add_argument("--verify",action="store_true")
    args=p.parse_args()
    rows=[(s.split("|",2)) for s in args.data.read_text(encoding="utf-8-sig").splitlines() if s and not s.startswith("#")]
    args.out.mkdir(parents=True,exist_ok=True)
    def prompt(task): return PROMPT.format(task=task)
    if args.verify:
        tests=[(label,task) for split,label,task in rows if split=="test"]
        details=[dict(expected=label) for label,_ in tests]
        for scale,field in ((0,"base"),(1,"personal")):
            for i,(_,task) in enumerate(tests):
                packet=post(args,"completion",{"prompt":prompt(task),"n_predict":1,"temperature":0,"cache_prompt":False,"grammar":'root ::= [0-7]',"lora":[{"id":0,"scale":scale}]})
                details[i][field]=packet["content"].strip()
            print("native phase",field,flush=True)
        report=dict(training=sum(r[0]=="train" for r in rows),held_out=len(details),base_correct=sum(r["expected"]==r["base"] for r in details),personal_correct=sum(r["expected"]==r["personal"] for r in details),held_out_predictions=details)
        (args.out/"evaluation.json").write_text(json.dumps(report,indent=2),encoding="utf-8")
        print(json.dumps({k:v for k,v in report.items() if k!="held_out_predictions"})); return
    ids=[post(args,"tokenize",{"content":str(i),"add_special":False})["tokens"] for i in range(8)]
    assert all(len(x)==1 for x in ids); ids=[x[0] for x in ids]
    arch,vocab,w=head_rows(args.model,args.native,ids)
    x=[]
    for i,(_,_,task) in enumerate(rows):
        packet=post(args,"embeddings",{"content":prompt(task),"embd_normalize":-1})
        x.append(np.array(packet[0]["embedding"][-1],dtype=np.float32))
        if (i+1)%10==0: print("features",i+1,"/",len(rows),flush=True)
    x=np.asarray(x); labels=np.array([int(r[1]) for r in rows]); train=np.array([r[0]=="train" for r in rows])
    scores=x@w.T
    # Fit only on training rows. Ridge works in the small sample Gram matrix.
    # A constant projected along the mean supplies the class-prior adjustment.
    xx=x[train].astype(np.float64); norms=np.linalg.norm(xx,axis=1,keepdims=True); unit=xx/norms
    desired=np.eye(8)[labels[train]]*13.0; base=scores[train]-scores[train].mean(axis=1,keepdims=True)
    grams=unit@unit.T
    weights=(np.linalg.solve(grams+0.002*np.eye(len(xx)),(desired-base)/norms).T@unit).astype(np.float32)
    a=weights; b=np.zeros((vocab,len(a)),np.float16)
    for i,token in enumerate(ids): b[token,i]=1
    lora_file(args.out/"work-formats.gguf",arch,a,b)
    for key,mask in [("train",train),("test",~train)]:
        print(key,"base",int(np.sum(scores[mask].argmax(1)==labels[mask])),"personal",int(np.sum((scores[mask]+x[mask]@a.T).argmax(1)==labels[mask])),"total",int(mask.sum()))
if __name__=="__main__": main()
