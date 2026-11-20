"""Blind, premise-disjoint RCB study. Fit never loads the closed rows or their labels."""
import argparse,collections,hashlib,json,os,re,sys,zipfile
from pathlib import Path
import numpy as np

ROOT=Path(__file__).resolve().parents[1];sys.path.insert(0,str(ROOT/'training'))
from bench import Server
from evidence import KEYS,LABELS,SYSTEM,metric,prepare,stable,user
from refine_evidence import DOMAIN,extra_rows
from tune import head_rows,lora_file

LOCAL=Path(os.environ['LOCALAPPDATA']);DATA=LOCAL/'SferaAI';ENGINE=DATA/'acceleration/llama-b12547-cuda12';MODEL=DATA/'library/models/zarum-9b-q4_k_m.gguf'
OPTIONS=['-c','8192','-t','8','--device','CUDA0','--fit-target','1024','--no-cache-idle-slots']
def read(p):return json.loads(p.read_text(encoding='utf8'))
def save(p,obj):p.write_text(json.dumps(obj,ensure_ascii=False,indent=2),encoding='utf8')
def sha(s):return hashlib.sha256(s.encode()).hexdigest()
def order(rows,key):return sorted(rows,key=lambda r:sha('20261120:'+key+r['id']))

def freeze(out,archive):
    if (out/'manifest.json').exists():raise ValueError('The study split has already been frozen')
    prior=prepare(archive.parent);previous=[r for r in prior+extra_rows(archive.parent,prior) if r['source']=='RCB']
    seen={stable(r['premise']) for r in previous};old_test={stable(r['premise']) for r in previous if r['split']=='test'}
    by_key={};exclude=re.compile(r'изнасил|самоубий|взрывчат|террор|боеголов|пытк|геном|вирус|наркотик|убий|мошеннич|порно',re.I)
    with zipfile.ZipFile(archive) as z:
        for origin in ('train','val'):
            for line in z.read('RCB/'+origin+'.jsonl').splitlines():
                item=json.loads(line);p=item['premise'];h=item['hypothesis'];k=stable(p)
                if k in old_test or len(p)>1450 or exclude.search(p+' '+h) or re.search(DOMAIN,p+'\n'+h,re.I):continue
                by_key.setdefault(k,dict(id=sha(k),origin=origin,premise=p,hypothesis=h,label=KEYS[item['label']],seen=k in seen))
    rows=list(by_key.values());closed=[];development=[]
    for c,quota in enumerate((37,8,44)):
        choices=order([r for r in rows if r['label']==c and not r['seen']],'closed')
        closed.extend(choices[:quota])
    held={r['id'] for r in closed}
    for c in range(3):development.extend(order([r for r in rows if r['label']==c and r['id'] not in held],'dev')[:16])
    used=held|{r['id'] for r in development};train=order([r for r in rows if r['id'] not in used],'training')
    assert len(used)==len(closed)+len(development) and not any(r['seen'] for r in closed)
    manifest={}
    for name,part in [('train',train),('dev',order(development,'d')),('closed',order(closed,'c'))]:
        save(out/(name+'.json'),part);manifest[name]={'count':len(part),'class_counts':dict(collections.Counter(r['label'] for r in part)), 'id_sha256':sha('\n'.join(sorted(r['id'] for r in part)))}
    manifest['archive_sha256']=hashlib.sha256(archive.read_bytes()).hexdigest();save(out/'manifest.json',manifest);print(manifest,flush=True)

def judge(text):
    first=text.strip().casefold().lstrip('* «"');return next((i for i,s in enumerate(LABELS) if first.startswith(s.casefold())),-1)
def native(rows,out,adapter=None):
    extra=['--lora',str(adapter),'--lora-init-without-apply'] if adapter is not None else []
    answers=[]
    with Server(ENGINE,MODEL,OPTIONS+extra,out) as srv:
        for r in rows:
            req=dict(messages=[dict(role='system',content=SYSTEM),dict(role='user',content=user(r))],temperature=0,seed=20261120,max_tokens=10,chat_template_kwargs=dict(enable_thinking=False))
            if adapter is not None:req['lora']=[dict(id=0,scale=1)]
            text=srv.post('v1/chat/completions',req)['choices'][0]['message']['content']
            answers.append(dict(id=r['id'],expected=r['label'],predicted=judge(text)))
    return answers
def stats(obs):
    y=np.array([r['expected'] for r in obs]);pred=np.array([r['predicted'] for r in obs]);f,n=metric(pred,y)
    return dict(n=len(obs),correct=n,macro_f1=f,errors=int((pred<0).sum()),per_class=[dict(total=int((y==c).sum()),correct=int(((pred==c)&(y==c)).sum())) for c in range(3)])
def fit(out):
    train,dev=read(out/'train.json'),read(out/'dev.json');rows=train+dev;cache=out/'train-features.npz';fingerprint=sha(' '.join(r['id'] for r in rows))
    if cache.exists():
        saved=np.load(cache);assert str(saved['fingerprint'])==fingerprint;x=saved['x'];ids=saved['ids'].tolist()
    else:
        vectors=[]
        with Server(ENGINE,MODEL,OPTIONS+['--embeddings','--pooling','last','--embd-normalize','-1'],out/'embed.log') as srv:
            ids=[srv.post('tokenize',dict(content=w,add_special=False))['tokens'][0] for w in LABELS]
            for i,r in enumerate(rows):
                prompt=srv.template(SYSTEM,user(r));tokens=srv.post('tokenize',dict(content=prompt,parse_special=True,add_special=False))['tokens']
                v=np.asarray(srv.post('embeddings',dict(content=tokens,embd_normalize=-1))[0]['embedding'],dtype=np.float32)
                vectors.append(v.reshape(-1,v.shape[-1])[-1]);
                if (i+1)%100==0:print('training/development features',i+1,flush=True)
        x=np.asarray(vectors);np.savez(cache,x=x,ids=ids,fingerprint=fingerprint)
    arch,vocab,head=head_rows(MODEL,ENGINE,ids);base=x@head.T;y=np.asarray([r['label'] for r in rows]);n=len(train)
    xx=x[:n].astype('float64');scale=np.linalg.norm(xx,axis=1,keepdims=True);u=xx/scale;gram=u@u.T;dev_y=y[n:];current=base[:n]-base[:n].mean(axis=1,keepdims=True)
    best=(metric(base[n:].argmax(1),dev_y),None,None)
    for balanced in (False,True):
        frequencies=np.bincount(y[:n],minlength=3);penalty=frequencies[y[:n]]/frequencies.mean() if balanced else np.ones(n)
        for ridge in (.02,.12,.55,1.5):
            projection=np.linalg.solve(gram+ridge*np.diag(penalty),u)
            for gap in (3.,7.,13.):
                targets=np.eye(3)[y[:n]]*gap;targets-=targets.mean(axis=1,keepdims=True)
                raw=((targets-current)/scale).T@projection
                for strength in (.4,.75,1.,1.25):
                    w=(raw*strength).astype(np.float32);value=metric((base[n:]+x[n:]@w.T).argmax(1),dev_y)
                    if value>best[0]:best=(value,w,dict(balanced=balanced,ridge=ridge,gap=gap,strength=strength))
    if best[1] is None:raise SystemExit('No development gain; preserve baseline')
    b=np.zeros((vocab,3),np.float16)
    for i,token in enumerate(ids):b[token,i]=1
    dest=out/'general-evidence.gguf';lora_file(dest,arch,best[1],b,name='Sfera Russian evidence calibration')
    results=native(dev,out/'candidate-development.log',dest);rec=dict(fit=best[2],native_development=stats(results),weight_sha256=hashlib.sha256(dest.read_bytes()).hexdigest());save(out/'candidate.json',rec);print(rec,flush=True)
def main():
    p=argparse.ArgumentParser();p.add_argument('stage',choices=('freeze','baseline','fit','final'));p.add_argument('--out',type=Path,required=True);p.add_argument('--archive',type=Path,default=ROOT/'.build/evidence/RCB.zip');a=p.parse_args();o=a.out.resolve();o.mkdir(parents=True,exist_ok=True)
    if a.stage=='freeze':freeze(o,a.archive);return
    if a.stage=='fit':fit(o);return
    dest=o/'general-evidence.gguf' if a.stage=='final' else None
    if dest is not None and hashlib.sha256(dest.read_bytes()).hexdigest()!=read(o/'candidate.json')['weight_sha256']:raise ValueError('Candidate changed after freezing')
    for split in (('closed',) if dest else ('dev','closed')):
        rows=read(o/(split+'.json'));obs=native(rows,o/(a.stage+'-'+split+'.log'),dest);summary=stats(obs);save(o/(a.stage+'-'+split+'.json'),dict(score=summary,observations=obs));print(a.stage,split,summary,flush=True)
if __name__=='__main__':main()
