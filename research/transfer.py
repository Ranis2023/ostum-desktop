"""Independent evaluation of a discriminative GGUF head. XNLI is only used for evaluation."""
import argparse
import hashlib
import json
from pathlib import Path
import re
from urllib.request import urlopen
import zipfile

import numpy as np
from evidence_study import (DOMAIN, ENGINE, KEYS, LABELS, MODEL, OPTIONS, SYSTEM, Server,
                            head_rows, judge, lora_file, metric, native, read, save, stable, stats, user)

URL='https://dl.fbaipublicfiles.com/XNLI/XNLI-1.0.zip'
ARCHIVE_SHA='4ba1d5e1afdb7161f0f23c66dc787802ccfa8a25a3ddd3b165a35e50df346ab1'
GRAMMAR='root ::= "Да." | "Нет." | "Недостаточно данных."'


def freeze(data,out):
    archive=data/'XNLI-1.0.zip'
    if not archive.exists():
        with urlopen(URL,timeout=90) as r:archive.write_bytes(r.read())
    if hashlib.sha256(archive.read_bytes()).hexdigest()!=ARCHIVE_SHA:raise ValueError('XNLI source checksum')
    if (out/'manifest.json').exists():raise ValueError('Split already frozen')
    choices={};sensitive=re.compile('изнасил|самоубий|взрывчат|террор|боеголов|пытк|геном|вирус|наркотик|убий|мошеннич|порно',re.I)
    with zipfile.ZipFile(archive) as z:
        for line in z.open('XNLI-1.0/xnli.test.jsonl'):
            row=json.loads(line)
            if row['language']!='ru' or row['gold_label'] not in KEYS:continue
            premise,hypothesis=row['sentence1'],row['sentence2']
            if re.search(DOMAIN,premise+'\n'+hypothesis,re.I) or sensitive.search(premise+hypothesis):continue
            key=hashlib.sha256(stable(premise).encode()).hexdigest()
            choices.setdefault(key,dict(id=key,premise=premise,hypothesis=hypothesis,label=KEYS[row['gold_label']]))
    rows=[]
    for label in range(3):
        part=[r for r in choices.values() if r['label']==label]
        rows+=sorted(part,key=lambda r:hashlib.sha256(('20261120-xnli/'+r['id']).encode()).digest())[:30]
    rows.sort(key=lambda r:r['id']);assert len(rows)==90
    save(out/'external.json',rows)
    save(out/'manifest.json',dict(archive_sha256=ARCHIVE_SHA,source=URL,purpose='evaluation only',classes=[30,30,30],
                                  id_sha256=hashlib.sha256('\n'.join(r['id'] for r in rows).encode()).hexdigest()))


def constrained(rows,log,weight=None):
    extra=['--lora',str(weight),'--lora-init-without-apply'] if weight else []
    result=[]
    with Server(ENGINE,MODEL,OPTIONS+extra,log) as server:
        for r in rows:
            body=dict(messages=[dict(role='system',content=SYSTEM),dict(role='user',content=user(r))],
                      temperature=0,seed=20261120,grammar=GRAMMAR,max_tokens=12,chat_template_kwargs=dict(enable_thinking=False))
            if weight:body['lora']=[dict(id=0,scale=1)]
            answer=server.post('v1/chat/completions',body)['choices'][0]['message']['content']
            result.append(dict(id=r['id'],expected=r['label'],predicted=judge(answer)))
    return result


def fit(rcb,out):
    train=read(rcb/'train.json');development=read(rcb/'dev.json')
    cached=np.load(rcb/'train-features.npz',allow_pickle=False)
    expected=hashlib.sha256(' '.join(r['id'] for r in train+development).encode()).hexdigest()
    if str(cached['fingerprint'])!=expected:raise ValueError('Unexpected training feature provenance')
    x=cached['x'].astype(np.float64);ids=cached['ids'].tolist();n=len(train)
    y=np.array([r['label'] for r in train+development]);arch,vocab,heads=head_rows(MODEL,ENGINE,ids)
    mean=x[:n].mean(0);bias=mean/(mean@mean);centered=x-np.outer(x@bias,mean)
    val,vec=np.linalg.eigh(centered[:n]@centered[:n].T/n)
    idx=np.argsort(val)[::-1];val,vec=val[idx],vec[:,idx]
    components=centered[:n].T@vec[:,:128]/np.sqrt(n*np.maximum(val[:128],1e-12))
    # Every feature, including the bias, is a linear function of the original hidden state.
    logit_heads=heads.T.astype(np.float64);logit_heads-=np.outer(bias,mean@logit_heads)
    logit_heads/=np.maximum((x[:n]@logit_heads).std(0),1e-4)
    target=np.eye(3)[y[:n]];weights=n/(3*np.bincount(y[:n])[y[:n]])
    best=None
    for rank in (0,16,48,128):
        pc=components[:,:rank]/np.maximum(val[:rank],1e-9)**.35
        pc=pc-np.outer(bias,mean@pc)
        features=np.column_stack((bias,logit_heads,pc))
        features[:,4:]/=max(1,np.sqrt(rank/8))
        design=x@features;training=design[:n]
        for reg in (.002,.015,.06,.2):
            w=np.zeros((3,design.shape[1]));v=np.zeros_like(w);s=np.zeros_like(w)
            for step in range(1,221):
                scores=training@w.T;scores-=scores.max(1,keepdims=True)
                prob=np.exp(scores);prob/=prob.sum(1,keepdims=True)
                penalty=w.copy();penalty[:,0]=0
                grad=((prob-target)*weights[:,None]).T@training/n+reg*penalty
                v=.9*v+.1*grad;s=.999*s+.001*grad*grad
                w-=.035*(v/(1-.9**step))/(np.sqrt(s/(1-.999**step))+1e-8)
            score=metric((design[n:]@w.T).argmax(1),y[n:])
            if best is None or score>best[0]:best=(score,w@features.T,dict(rank=rank,regularization=reg))
    # Centre learned class logits on the original common logit. A fixed margin favours the
    # complete lexical entries over shorter tokenizations representing the same prefixes.
    learned=best[1]-best[1].mean(0)
    replacement=5*learned+heads.mean(0)[None,:]+np.outer(np.full(3,6.),bias)
    a=(replacement-heads).astype(np.float32);b=np.zeros((vocab,3),dtype=np.float16)
    for i,token in enumerate(ids):b[token,i]=1
    dest=out/'general-evidence.gguf';lora_file(dest,arch,a,b,name='Sfera grounded verdict classifier')
    obs=constrained(development,out/'dev-candidate.log',dest)
    save(out/'candidate.json',dict(selection=best[2],native_development=stats(obs),train=len(train),dev=len(development),
                                   weight_sha256=hashlib.sha256(dest.read_bytes()).hexdigest()))


def main():
    p=argparse.ArgumentParser();p.add_argument('stage',choices=['freeze','baseline','fit','final'])
    for key in ('data','rcb','out'):p.add_argument('--'+key,type=Path,required=True)
    a=p.parse_args();o=a.out.resolve();o.mkdir(parents=True,exist_ok=True)
    if a.stage=='freeze':freeze(a.data,o);return
    if a.stage=='fit':fit(a.rcb,o);return
    rows=read(o/'external.json')
    def record(name,obs):
        report=dict(score=stats(obs),observations=obs);save(o/(name+'.json'),report)
        print(name,report['score'],flush=True)
    if a.stage=='baseline':
        record('raw-external',native(rows,o/'raw-external.log'))
        record('grammar-external',constrained(rows,o/'grammar-external.log'));return
    weight=o/'general-evidence.gguf'
    if hashlib.sha256(weight.read_bytes()).hexdigest()!=read(o/'candidate.json')['weight_sha256']:raise ValueError('Weight changed')
    record('candidate-external',constrained(rows,o/'candidate-external.log',weight))
    record('candidate-regression',constrained(read(a.rcb/'closed.json'),o/'candidate-regression.log',weight))


if __name__=='__main__':main()
