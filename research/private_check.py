"""Native evaluation of the privately reviewed, disjoint work-format audit without publishing any request text."""
import argparse,hashlib,json,os,sys
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1];sys.path.insert(0,str(ROOT/'training'))
from bench import Server
from tune import PROMPT

def main():
    p=argparse.ArgumentParser();p.add_argument('--out',required=True,type=Path);a=p.parse_args();out=a.out.resolve();out.mkdir(parents=True,exist_ok=True)
    data=Path(os.environ['LOCALAPPDATA'])/'SferaAI';personal=data/'personal';source=personal/'learning/audit-independent-formats.json';rows=json.loads(source.read_text(encoding='utf8'))
    current=personal/'work-formats.gguf';prior=personal/'work-formats-704ff08affbb.gguf';engine=data/'acceleration/llama-b12547-cuda12';model=data/'library/models/zarum-9b-q4_k_m.gguf'
    assert len({r['group'] for r in rows})==len(rows)
    switches=['-t','8','-c','8192','--device','CUDA0','--fit-target','1024','--lora',str(prior),'--lora',str(current),'--lora-init-without-apply']
    obs=[dict(expected=r['label']) for r in rows]
    with Server(engine,model,switches,out/'private.log') as srv:
        for which,key in ((-1,'raw'),(0,'old'),(1,'installed')):
            for i,r in enumerate(rows):
                msg=srv.post('completion',dict(prompt=PROMPT.format(task=r['text']),temperature=0,seed=20261120,n_predict=1,grammar='root ::= [0-7]',lora=[dict(id=j,scale=int(j==which)) for j in range(2)]))['content'].strip()
                obs[i][key]=int(msg)
    scores={key:sum(r[key]==r['expected'] for r in obs) for key in ('raw','old','installed')};report=dict(total=len(obs),scores=scores,sha256=hashlib.sha256(source.read_bytes()).hexdigest(),observations=obs);(out/'aggregate.json').write_text(json.dumps(report,indent=2),encoding='utf8');print(scores,'of',len(obs))
if __name__=='__main__':main()
