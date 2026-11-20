"""Adapt a private Qwen format adapter to locally reviewed, de-identified work requests."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import struct

import numpy as np

from bench import Server
from tune import FORMATS, PROMPT, head_rows, lora_file


def adapter_a(path, rank, hidden):
    with path.open("rb") as f:
        def number(fmt):
            return struct.unpack("<" + fmt, f.read(struct.calcsize("<" + fmt)))[0]
        def string():
            return f.read(number("Q")).decode("utf-8")
        assert f.read(4) == b"GGUF" and number("I") == 3
        tensors, count = number("Q"), number("Q")
        assert tensors == 2
        for _ in range(count):
            key, kind = string(), number("I")
            if kind == 8: string()
            elif kind in FORMATS: number(FORMATS[kind])
            else: raise ValueError("Unsupported adapter metadata")
        selected = None
        for _ in range(tensors):
            name = string()
            dims = [number("Q") for _ in range(number("I"))]
            kind, offset = number("I"), number("Q")
            if name == "output.weight.lora_a":
                assert dims == [hidden, rank] and kind == 1
                selected = offset
        if selected is None: raise ValueError("Missing output-head adapter")
        f.seek((f.tell() + 31) // 32 * 32 + selected)
        return np.frombuffer(f.read(2 * rank * hidden), dtype="<f2").astype(np.float32).reshape(rank, hidden)


def fit(rows, x, base, old):
    split = np.array([r["split"] for r in rows])
    real = np.array([r["source"] == "history" for r in rows])
    y = np.array([r["label"] for r in rows])
    current = base + x @ old.T
    guess = current.argmax(1)
    dev = (split == "dev") & real
    anchors = (split == "train") & ~real
    def criterion(scores, norm):
        pred = scores.argmax(1)
        groups = [float(np.mean(pred[dev & (y == c)] == c)) for c in range(8)]
        return (int(np.sum(pred[anchors] == y[anchors])) >= int(np.sum(guess[anchors] == y[anchors])) - 1,
                int(np.sum(pred[dev] == y[dev])), float(np.mean(groups)), -float(norm))
    best = criterion(current, 0), old, dict(prior_only=True)
    for include in (False, True):
        selected = (split == "train") & (real | include)
        xx = x[selected].astype(np.float64)
        norm = np.linalg.norm(xx, axis=1, keepdims=True)
        unit = xx / norm
        gram = unit @ unit.T
        baseline = current[selected] - current[selected].mean(axis=1, keepdims=True)
        for ridge in (.003, .015, .08, .3):
            projection = np.linalg.solve(gram + ridge * np.eye(len(xx)), unit)
            for margin in (7., 13., 19.):
                target = np.eye(8)[y[selected]] * margin
                update = ((target - baseline) / norm).T @ projection
                for strength in (.5, .8, 1.1):
                    delta = (strength * update).astype(np.float32)
                    score = criterion(current + x @ delta.T, np.linalg.norm(delta))
                    if score > best[0]:
                        best = score, old + delta, dict(with_anchors=include, ridge=ridge, margin=margin, strength=strength)
    return best


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--data", type=Path, required=True)
    p.add_argument("--out", type=Path, required=True)
    p.add_argument("--prior", type=Path, required=True)
    args = p.parse_args()
    data, out, prior = args.data.resolve(), args.out.resolve(), args.prior.resolve()
    out.mkdir(parents=True, exist_ok=True)
    rows = [dict(r, source="history") for r in json.loads((data/"format-history-reviewed.json").read_text(encoding="utf8"))]
    assert len({r["group"] for r in rows}) == len(rows)
    for line in (data/"format-baseline.txt").read_text(encoding="utf8").splitlines():
        split, label, task = line.split("|", 2)
        rows.append(dict(source="synthetic", split=split, text=task, label=int(label)))
    fingerprint = hashlib.sha256(json.dumps(rows,ensure_ascii=False,sort_keys=True).encode()).hexdigest()
    local=Path(os.environ["LOCALAPPDATA"])
    engine=local/"Programs/Ostum Desktop/engines/llama-b12547"
    model=local/"SferaAI/library/models/zarum-9b-q4_k_m.gguf"
    opts=["-c","1024","-t","6","--fit-target","1100"]
    cache=out/"features.npz"
    if cache.exists():
        saved=np.load(cache)
        if str(saved["fingerprint"])!=fingerprint: raise ValueError("Evaluated requests changed")
        x,ids=saved["x"],saved["ids"].tolist()
    else:
        vectors=[]
        with Server(engine,model,opts+["--embeddings","--pooling","last","--embd-normalize","-1"],out/"embed.log") as srv:
            tokens=[srv.post("tokenize",{"content":str(i),"add_special":False})["tokens"] for i in range(8)]
            assert all(len(t)==1 for t in tokens)
            ids=[t[0] for t in tokens]
            for i,row in enumerate(rows):
                reply=srv.post("embeddings",{"content":PROMPT.format(task=row["text"]),"embd_normalize":-1})
                v=np.asarray(reply[0]["embedding"],np.float32)
                vectors.append(v.reshape(-1,v.shape[-1])[-1])
                if (i+1)%40==0:print("features",i+1,"/",len(rows),flush=True)
        x=np.asarray(vectors);np.savez(cache,x=x,ids=ids,fingerprint=fingerprint)
    arch,vocab,head=head_rows(model,engine,ids)
    old=adapter_a(prior,8,x.shape[1])
    score,w,settings=fit(rows,x,x@head.T,old)
    b=np.zeros((vocab,8),np.float16)
    for i,token in enumerate(ids):b[token,i]=1
    target=out/"work-formats.gguf";lora_file(target,arch,w,b,name="Sfera personal work formats v2")
    tests=[r for r in rows if r["split"]=="test"]
    answers=[dict(source=r["source"],expected=r["label"]) for r in tests]
    with Server(engine,model,opts+["--lora",str(prior),"--lora",str(target),"--lora-init-without-apply"],out/"test.log") as srv:
        for adapter,key in [(-1,"base"),(0,"previous"),(1,"candidate")]:
            for i,row in enumerate(tests):
                prompt=PROMPT.format(task=row["text"])
                body=dict(prompt=prompt,temperature=0,n_predict=1,grammar="root ::= [0-7]",lora=[dict(id=j,scale=int(j==adapter)) for j in range(2)])
                answers[i][key]=int(srv.post("completion",body)["content"].strip())
            print("native phase",key,flush=True)
    stats={}
    for source in ("history","synthetic"):
        obs=[r for r in answers if r["source"]==source]
        stats[source]=dict(total=len(obs),**{key:sum(r[key]==r["expected"] for r in obs) for key in ("base","previous","candidate")})
    report=dict(task="Private work format classification",history_training=sum(r["source"]=="history" and r["split"]=="train" for r in rows),
                history_development=sum(r["source"]=="history" and r["split"]=="dev" for r in rows),native=stats,selection=settings,
                dataset_sha256=fingerprint,prior_sha256=hashlib.sha256(prior.read_bytes()).hexdigest(),observations=answers)
    (out/"evaluation.json").write_text(json.dumps(report,indent=2),encoding="utf-8")
    print(stats,flush=True)


if __name__=="__main__":main()
