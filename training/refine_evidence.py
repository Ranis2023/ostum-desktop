"""Fit a rank-one update for missing evidence. Preserve the existing yes/no logits."""
import argparse
import json
import os
import random
import re
import zipfile
from pathlib import Path
import numpy as np
from bench import Server
from tune import head_rows,lora_file
from evidence import prepare,stable,user,SYSTEM,LABELS,KEYS,metric

DOMAIN = r"смет|договор|макет|монтаж|закуп|объявлени|реклам|оплат|сч[её]т|плат[её]ж|выручк|клиент|заявк|цен[ауыуе]|доставк|постав|подряд|тираж|бриф|слайд|презентац|проект|услуг|кампани|исполнител|отч[её]т|бюджет|покуп|стоимост|участник|публикац|пакет|сайт|прототип|баннер|лицензи|печат|поддержк|склад|подписк|витрин|продаж|акт[аеу]?\b|провер[кч]|материал[аыов]*\b|образец|спецификац"
def extra_rows(root,original):
    new=[]
    for line in (Path(__file__).parent/"evidence-contrast.txt").read_text(encoding="utf-8-sig").splitlines():
        if not line.strip():continue
        split,material,*assertions=line.split("|")
        assert len(assertions)==3
        for label,claim in enumerate(assertions):
            new.append(dict(split=split,premise=material,hypothesis=claim,label=label,source="work"))
    keys={stable(r["premise"]) for r in original}
    with zipfile.ZipFile(root/"RCB.zip") as z:
        val=[json.loads(line) for line in z.read("RCB/val.jsonl").splitlines()]
    rng=random.Random(90471);rng.shuffle(val);counts=[0,0,0]
    for row in val:
        cls=KEYS.get(row["label"]);key=stable(row["premise"])
        if cls is None or counts[cls]>=12 or key in keys or len(row["premise"])>1300:continue
        if re.search(r"изнасил|самоубий|взрывчат|террор|боеголов|пытк|геном|вирус|наркотик|убий|мошеннич|порно",row["premise"]+" "+row["hypothesis"],re.I):continue
        keys.add(key);counts[cls]+=1;new.append(dict(split="test",source="RCB",label=cls,premise=row["premise"],hypothesis=row["hypothesis"]))
    assert counts==[12]*3
    return new

def fit(x,y,base,rows,gate):
    split=np.array([r["split"] for r in rows]);business=np.array([r["source"]=="work" for r in rows])
    dev=split=="dev";db=dev&business;dp=dev&~business
    basepred=base.argmax(1);best=None;base_pub=int(sum(basepred[dp]==y[dp]))
    baseline_margin=base[:,2]-np.maximum(base[:,0],base[:,1])
    for include in (False,True):
        mask=(split=="train")&(business|include)&gate
        xx=x[mask].astype(np.float64);norm=np.linalg.norm(xx,axis=1,keepdims=True);u=xx/norm
        gram=u@u.T
        for ridge in (.002,.01,.06,.25,.85):
            projection=np.linalg.solve(gram+ridge*np.eye(len(u)),u)
            for margin in (4.,9.,16.):
                targets=np.where(y[mask]==2,margin,-margin)-baseline_margin[mask]
                weight=(targets[:,None]/norm).T@projection
                for strength in (.55,.75,1.,1.3):
                    w=(weight*strength).astype(np.float32);scores=base.copy()
                    scores[:,2]+=gate*(x@w[0])
                    pred=scores.argmax(1)
                    acc_pub=int(sum(pred[dp]==y[dp]));acc_work=int(sum(pred[db]==y[db]))
                    # Primary target is business evidence. Require preservation of public development accuracy.
                    key=(acc_pub>=base_pub,acc_work,acc_pub,metric(pred[db],y[db])[0],-float(np.linalg.norm(w)))
                    if best is None or key>best[0]:best=key,w,dict(public_examples=include,ridge=ridge,margin=margin,strength=strength)
    return best

def main():
    p=argparse.ArgumentParser();p.add_argument("--data",required=True,type=Path);p.add_argument("--out",required=True,type=Path);args=p.parse_args();root=args.data.resolve();out=args.out.resolve();out.mkdir(parents=True,exist_ok=True)
    first=prepare(root);extra=extra_rows(root,first)
    cache=np.load(root/"nli-features.npz");ids=cache["ids"].tolist()
    local=Path(os.environ["LOCALAPPDATA"]);engine=local/"Programs/Ostum Desktop/engines/llama-b12547";model=local/"SferaAI/library/models/zarum-9b-q4_k_m.gguf"
    config=["-c","768","--fit-target","1100","-t","6"];morefile=root/"business-features.npy"
    if morefile.exists():added=np.load(morefile)
    else:
        added=[]
        with Server(engine,model,config+["--embeddings","--pooling","last","--embd-normalize","-1"],root/"business-embed.log") as srv:
            for i,row in enumerate(extra):
                rendered=srv.template(SYSTEM,user(row));tok=srv.post("tokenize",{"content":rendered,"parse_special":True,"add_special":False})["tokens"]
                vec=np.asarray(srv.post("embeddings",{"content":tok,"embd_normalize":-1})[0]["embedding"],np.float32)
                added.append(vec.reshape(-1,vec.shape[-1])[-1])
                if(i+1)%40==0:print("new features",i+1,"/",len(extra),flush=True)
        added=np.asarray(added);np.save(morefile,added)
    assert len(added)==len(extra)
    old_indices=[i for i,row in enumerate(first) if row["split"]!="test"]
    rows=[first[i] for i in old_indices]+extra;x=np.vstack([cache["x"][old_indices],added]);y=np.array([r["label"] for r in rows])
    gate=np.array([bool(re.search(DOMAIN,r["premise"]+"\n"+r["hypothesis"],re.I)) for r in rows])
    arch,vocab,head=head_rows(model,engine,ids);scores=x@head.T
    key,weight,settings=fit(x,y,scores,rows,gate);print("dev selection",key,settings,flush=True)
    b=np.zeros((vocab,1),np.float16);b[ids[2],0]=1
    path=out/"source-check.gguf";lora_file(path,arch,weight,b,name="Sfera source checking")
    test=[(r,bool(g)) for r,g in zip(rows,gate) if r["split"]=="test"]
    observations=[dict(source=r["source"],expected=r["label"],adapter_used=g) for r,g in test]
    examples=[]
    with Server(engine,model,config+["--lora",str(path),"--lora-init-without-apply"],root/"business-test.log") as srv:
        for apply,tag in [(False,"base"),(True,"tuned")]:
            for i,(r,g) in enumerate(test):
                if apply and not g:observations[i][tag]=observations[i]["base"];continue
                packet={"messages":[{"role":"system","content":SYSTEM},{"role":"user","content":user(r)}],"temperature":0,"max_tokens":10,"lora":[{"id":0,"scale":1 if apply and g else 0}],"chat_template_kwargs":{"enable_thinking":False}}
                reply=srv.post("v1/chat/completions",packet)["choices"][0]["message"]["content"].strip().lower().lstrip("* «\"")
                observations[i][tag]=next((j for j,w in enumerate(LABELS) if reply.startswith(w.lower())),-1)
                if apply and i in (0,1,2,17):
                    packet["max_tokens"]=100
                    answer=srv.post("v1/chat/completions",packet)["choices"][0]["message"]["content"]
                    examples.append(dict(material=r["premise"],claim=r["hypothesis"],answer=answer))
            print("native phase",tag,flush=True)
    stats={}
    for source in ("all","work","RCB"):
        o=[r for r in observations if source=="all" or r["source"]==source]
        stats[source]=dict(total=len(o),base=sum(v["base"]==v["expected"] for v in o),tuned=sum(v["tuned"]==v["expected"] for v in o),used=sum(v["adapter_used"] for v in o))
    report=dict(task="Russian document claim verification",rank=1,fit_candidates=sum(r["split"]=="train" for r in rows),fit_used=sum(r["split"]=="train" and (r["source"]=="work" or settings["public_examples"]) and g for r,g in zip(rows,gate)),dev=sum(r["split"]=="dev" for r in rows),native=stats,settings=settings,observations=observations)
    (out/"source-check-evaluation.json").write_text(json.dumps(report,indent=2,default=int),encoding="utf8");(out/"examples.json").write_text(json.dumps(examples,ensure_ascii=False,indent=2),encoding="utf8")
    print("native",stats,flush=True)
if __name__=="__main__":main()
