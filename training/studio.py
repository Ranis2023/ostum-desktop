"""Train and evaluate a local Zarum output-layer LoRA from approved question/answer pairs."""
import argparse
import collections
import hashlib
import json
import math
import os
import re
import sys
import urllib.request
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import numpy as np
from bench import Server
from tune import head_rows, lora_file

SYSTEM = "Ты Zarum. Ответь пользователю на русском языке."


def normalized(text):
    return " ".join(re.findall(r"\w+", text.casefold()))


def divide(rows):
    result = []
    for row in rows:
        prompt, answer = row["prompt"].strip(), row["answer"].strip()
        if prompt and answer and (prompt, answer) not in {(r["prompt"], r["answer"]) for r in result}:
            result.append(dict(prompt=prompt, answer=answer, group=(row.get("group") or normalized(prompt)).strip()))
    if not 5 <= len(result) <= 200:
        raise ValueError("Нужно от 5 до 200 разных утверждённых примеров.")
    # Keep near-copy questions and all revisions of a source together.
    parent = list(range(len(result)))
    def root(i):
        while parent[i] != i:
            parent[i] = parent[parent[i]]
            i = parent[i]
        return i
    words = [set(normalized(row["prompt"]).split()) for row in result]
    for i, row in enumerate(result):
        for j in range(i):
            if row["group"] == result[j]["group"] or len(words[i] & words[j]) / max(1, len(words[i] | words[j])) >= .78:
                parent[root(i)] = root(j)
    groups = sorted(set(map(root, range(len(result)))), key=lambda n: hashlib.sha256(result[n]["prompt"].encode()).digest())
    if len(groups) < 3:
        raise ValueError("Нужны примеры как минимум из трёх разных групп.")
    held = set(groups[:max(1, len(groups)//5)])
    return ([r for i, r in enumerate(result) if root(i) not in held], [r for i, r in enumerate(result) if root(i) in held])


def score(text, target):
    a, b = collections.Counter(normalized(text).split()), collections.Counter(normalized(target).split())
    return round(2 * sum((a & b).values()) / max(1, sum(a.values()) + sum(b.values())), 4)


def main():
    p = argparse.ArgumentParser()
    for name in ("engine", "model", "data", "out"):
        p.add_argument("--"+name, type=Path, required=True)
    args = p.parse_args()
    # Training only contacts the authenticated server on 127.0.0.1.
    urllib.request.install_opener(urllib.request.build_opener(urllib.request.ProxyHandler({})))
    rows = json.loads(args.data.read_text(encoding="utf-8-sig"))
    train, test = divide(rows)
    out = args.out.resolve()
    out.mkdir(parents=True, exist_ok=True)
    if (out/"adapter.gguf").exists():
        raise ValueError("Для новой версии нужна пустая папка.")
    prefix = ["-c", "1536", "--fit-target", "1024", "-t", str(min(8,max(1,(os.cpu_count() or 4)//2)))]
    features, labels = [], []
    print(f"Обучение: {len(train)} примеров. Отложенная проверка: {len(test)}.", flush=True)
    with Server(args.engine, args.model, prefix+["--embeddings","--pooling","last","--embd-normalize","-1"], out/"features.log") as srv:
        for i, row in enumerate(train):
            template = srv.template(SYSTEM, row["prompt"])
            prompt = srv.post("tokenize", {"content":template,"parse_special":True,"add_special":False})["tokens"]
            ans = srv.post("tokenize", {"content":row["answer"],"add_special":False})["tokens"][:48]
            if not ans or len(prompt)+len(ans)>1500:
                raise ValueError("Сократите длинный учебный пример.")
            # Teacher forcing at the first and evenly spaced answer positions, bounded for laptops.
            for n in sorted(set(np.linspace(0, len(ans)-1, min(6,len(ans)),dtype=int).tolist())):
                value=srv.post("embeddings",{"content":prompt+ans[:n],"embd_normalize":-1})[0]["embedding"]
                vec=np.asarray(value,dtype=np.float32)
                features.append(vec.reshape(-1,vec.shape[-1])[-1]);labels.append(ans[n])
            print(f"Признаки {i+1}/{len(train)}", flush=True)
    x=np.stack(features).astype(np.float64)
    ids=sorted(set(labels))
    arch,vocab,w=head_rows(args.model,args.engine,ids)
    norm=np.maximum(np.linalg.norm(x,axis=1,keepdims=True),1e-8)
    unit=x/norm
    base=x@w.T
    y=np.zeros_like(base)
    for i,label in enumerate(labels):
        y[i,ids.index(label)]=8.
    d=(y-(base-base.mean(axis=1,keepdims=True)))/norm
    ridge=.06*np.eye(len(x))
    delta=np.linalg.solve(unit@unit.T+ridge,d).T@unit
    u,s,vt=np.linalg.svd(delta,full_matrices=False)
    rank=min(8,len(s));a=vt[:rank];b=np.zeros((vocab,rank),dtype=np.float16)
    b[ids]=u[:,:rank]*s[:rank]
    adapter=out/"candidate.gguf"
    lora_file(adapter,arch,a,b,name="OSTUM Pro local answer adapter")
    report=[]
    with Server(args.engine,args.model,prefix+["--lora",str(adapter),"--lora-init-without-apply"],out/"evaluate.log") as srv:
        for i,row in enumerate(test):
            prompt=srv.template(SYSTEM,row["prompt"])
            item=dict(prompt=row["prompt"],expected=row["answer"])
            for label,scale in (("base",0),("trained",1)):
                item[label]=srv.post("completion",dict(prompt=prompt,n_predict=90,temperature=0,seed=42,cache_prompt=False,lora=[dict(id=0,scale=scale)]))["content"].strip()
                item[label+"_f1"]=score(item[label],row["answer"])
            item["verbatim_training_answer"]=any(len(normalized(r["answer"]))>=32 and normalized(r["answer"]) in normalized(item["trained"]) for r in train)
            report.append(item);print(f"Проверка {i+1}/{len(test)}",flush=True)
    result=dict(training=len(train),held_out=len(test),rank=rank,metric="token F1",cases=report,
                baseline=sum(r["base_f1"] for r in report)/len(report),adapted=sum(r["trained_f1"] for r in report)/len(report),
                regressions=sum(r["trained_f1"]<r["base_f1"] for r in report),
                verbatim=sum(r["verbatim_training_answer"] for r in report))
    (out/"evaluation.json").write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding="utf-8")
    adapter.replace(out/"adapter.gguf")
    print("Отложенная проверка завершена. Версия готова к просмотру.",flush=True)


if __name__=="__main__":
    main()
