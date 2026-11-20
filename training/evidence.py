"""Train and evaluate a Russian source-verification LoRA on local Qwen3.5."""
import argparse
from collections import Counter
import hashlib
import json
import os
from pathlib import Path
import random
import re
import zipfile
import numpy as np
from bench import Server
from tune import head_rows, lora_file

SYSTEM = ("Ты проверяешь одно утверждение только по предоставленному материалу. Начни ответ с «Да.», если оно следует из текста, "
          "с «Нет.», если текст ему прямо противоречит, или с «Недостаточно данных.», если в тексте нет ответа. "
          "После вердикта кратко поясни его по материалу. Не добавляй внешних фактов.")
LABELS=["Да","Нет","Недостаточно данных"]
KEYS={"entailment":0,"contradiction":1,"neutral":2}
def user(row):return "Материал:\n"+row["premise"]+"\n\nУтверждение:\n"+row["hypothesis"]
def stable(text):return re.sub(r"\W+"," ",text.casefold()).strip()
def prepare(root):
    corpus=[]; used=set(); rng=random.Random(1401)
    def select(source,split,n):
        result=Counter()
        for row in source:
            label=KEYS.get(row["label"])
            key=stable(row["premise"])
            if label is None or key in used or result[label]>=n or len(row["premise"])>1300:continue
            # Keep this local work exercise focused on nonsensitive factual text.
            if re.search(r"изнасил|самоубий|взрывчат|террор|боеголов|пытк|геном|вирус|наркотик|убий|мошеннич|порно",row["premise"]+" "+row["hypothesis"],re.I):continue
            result[label]+=1;used.add(key);corpus.append({"premise":row["premise"],"hypothesis":row["hypothesis"],"label":label,"split":split,"source":"RCB"})
    with zipfile.ZipFile(root/"RCB.zip") as z:
        train=[json.loads(line) for line in z.read("RCB/train.jsonl").splitlines()]
        val=[json.loads(line) for line in z.read("RCB/val.jsonl").splitlines()]
        rng.shuffle(train);rng.shuffle(val)
        select(val,"test",14);select(train,"dev",14);select(train,"train",70)
    for line in (Path(__file__).parent/"evidence-work.txt").read_text(encoding="utf-8-sig").splitlines():
        if not line.strip():continue
        split,label,premise,hypothesis=line.split("|",3)
        if stable(premise) in used:raise ValueError("duplicate premise")
        used.add(stable(premise));corpus.append(dict(split=split,label=int(label),premise=premise,hypothesis=hypothesis,source="work"))
    return corpus

def metric(pred,y):
    counts=[]
    for i in range(3):
        tp=np.sum((pred==i)&(y==i)); denom=np.sum(pred==i)+np.sum(y==i)
        counts.append(float(2*tp/denom) if denom else 0.)
    return (float(np.mean(counts)),int(np.sum(pred==y)))
def main():
    p=argparse.ArgumentParser();p.add_argument("--data",type=Path,required=True);p.add_argument("--out",type=Path,required=True);args=p.parse_args()
    data=args.data.resolve();out=args.out.resolve();out.mkdir(parents=True,exist_ok=True);rows=prepare(data);print("splits",Counter(r["split"] for r in rows),flush=True)
    local=Path(os.environ["LOCALAPPDATA"]);engine=local/"Programs/Ostum Desktop/engines/llama-b12547";model=local/"SferaAI/library/models/zarum-9b-q4_k_m.gguf"
    base=["-c","768","--fit-target","1100","--threads","6"]
    cache=data/"nli-features.npz";fingerprint=hashlib.sha256(json.dumps([SYSTEM,rows],ensure_ascii=False,sort_keys=True).encode()).hexdigest()
    if cache.exists():
        stored=np.load(cache,allow_pickle=False)
        if str(stored["hash"])!=fingerprint:raise ValueError("cached tasks differ")
        x,ids=stored["x"],stored["ids"].tolist()
    else:
        features=[]
        with Server(engine,model,base+["--embeddings","--pooling","last","--embd-normalize","-1"],data/"embed.log") as server:
            ids=[server.post("tokenize",{"content":w,"add_special":False})["tokens"][0] for w in LABELS]
            if len(set(ids))!=3:raise ValueError("distinct decision tokens required")
            print("tokens",ids,flush=True)
            for i,row in enumerate(rows):
                rendered=server.template(SYSTEM,user(row))
                tokens=server.post("tokenize",{"content":rendered,"parse_special":True,"add_special":False})["tokens"]
                em=server.post("embeddings",{"content":tokens,"embd_normalize":-1})[0]["embedding"]
                vector=np.array(em,dtype=np.float32).reshape(-1,np.asarray(em).shape[-1])[-1]
                features.append(vector)
                if (i+1)%35==0:print("features",i+1,"/",len(rows),flush=True)
        x=np.array(features);np.savez(cache,x=x,ids=ids,hash=fingerprint)
    arch,vocab,head=head_rows(model,engine,ids); base_scores=x@head.T
    y=np.array([r["label"] for r in rows]); masks={s:np.array([r["split"]==s for r in rows]) for s in ("train","dev","test")}
    train,dev=masks["train"],masks["dev"];xx=x[train].astype(np.float64);norm=np.linalg.norm(xx,axis=1,keepdims=True);u=xx/norm
    gram=u@u.T;centered=base_scores[train]-base_scores[train].mean(axis=1,keepdims=True)
    baseline=metric(base_scores[dev].argmax(1),y[dev]);best=None
    # Tune ridge and strength on development only. Keep the independent test labels out of selection.
    for reg in (.002,.012,.05,.15,.45,1.0):
        inverse=np.linalg.solve(gram+reg*np.eye(len(xx)),np.eye(len(xx)))
        for target in (5.,10.,16.,23.):
            weights=((inverse@((np.eye(3)[y[train]]*target-centered)/norm)).T@u).astype(np.float32)
            for scale in (.45,.7,1.,1.35):
                candidate=weights*scale
                score=metric((base_scores[dev]+x[dev]@candidate.T).argmax(1),y[dev])
                key=(*score,-np.linalg.norm(candidate))
                if best is None or key>best[0]:best=(key,candidate,reg,target,scale)
    print("dev base/adapter",baseline,best[0],flush=True)
    b=np.zeros((vocab,3),np.float16)
    for i,token in enumerate(ids):b[token,i]=1
    adapter=out/"source-check.gguf";lora_file(adapter,arch,best[1],b)
    testrows=[r for r in rows if r["split"]=="test"];observations=[dict(source=r["source"],expected=r["label"]) for r in testrows]
    with Server(engine,model,base+["--lora",str(adapter),"--lora-init-without-apply"],data/"test.log") as server:
        for scale,key in ((0,"base"),(1,"tuned")):
            for i,row in enumerate(testrows):
                response=server.post("v1/chat/completions",{"messages":[{"role":"system","content":SYSTEM},{"role":"user","content":user(row)}],"temperature":0,"max_tokens":10,"chat_template_kwargs":{"enable_thinking":False},"lora":[{"id":0,"scale":scale}]})
                ans=(response["choices"][0]["message"].get("content") or "").strip().lower().strip("* «\"")
                label=next((j for j,w in enumerate(LABELS) if ans.startswith(w.lower())), -1)
                observations[i][key]=label
            print("native completed",key,flush=True)
    score={}
    for k in ("all","work","RCB"):
        group=[r for r in observations if k=="all" or r["source"]==k]
        score[k]=dict(total=len(group),base=sum(r["base"]==r["expected"] for r in group),tuned=sum(r["tuned"]==r["expected"] for r in group))
    result=dict(train=int(train.sum()),dev=int(dev.sum()),test=len(testrows),native=score,dev_accuracy={"base":baseline[1],"tuned":best[0][1]},training_source="RussianNLP/russian_super_glue RCB revision 53cb9d7f38b34c308a2eea3f4797f9edc7947d8b and synthetic work examples",observations=observations)
    (out/"source-check-evaluation.json").write_text(json.dumps(result,indent=2),encoding="utf8")
    print("native",score,flush=True)
if __name__=="__main__":main()
