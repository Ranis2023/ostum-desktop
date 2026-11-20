"""Repeat the full ordinary-material RCB panel with the actual installed adapter stack."""
import argparse
import hashlib
import os
from pathlib import Path
from evidence_study import DATA,ENGINE,MODEL,OPTIONS,SYSTEM,Server,judge,read,save,stats,user

def main():
    p=argparse.ArgumentParser();p.add_argument('--run',required=True,type=Path);a=p.parse_args()
    root=a.run;rows=read(root/'evidence/closed.json')
    app=Path(os.environ['LOCALAPPDATA'])/'Programs/Ostum Desktop'
    weights=[w for w in (DATA/'personal/work-formats.gguf',app/'adapters/source-check.gguf',app/'adapters/tool-routing.gguf') if w.is_file()]
    release=read(root/'release.json')
    if release['general_evidence'] or (app/'adapters/general-evidence.gguf').exists():
        raise ValueError('This replay targets the approved release with the original ordinary-material verifier.')
    options=list(OPTIONS)
    for w in weights:options.extend(['--lora',str(w)])
    if weights:options.append('--lora-init-without-apply')
    observed=[]
    with Server(ENGINE,MODEL,options,root/'deployed-evidence.log') as srv:
        for r in rows:
            request=dict(messages=[dict(role='system',content=SYSTEM),dict(role='user',content=user(r))],temperature=0,
                         max_tokens=10,seed=20261120,chat_template_kwargs=dict(enable_thinking=False),
                         lora=[dict(id=i,scale=0) for i in range(len(weights))])
            answer=srv.post('v1/chat/completions',request)['choices'][0]['message']['content']
            observed.append(dict(id=r['id'],expected=r['label'],predicted=judge(answer)))
    report=dict(score=stats(observed),observations=observed,weights={w.name:hashlib.sha256(w.read_bytes()).hexdigest() for w in weights})
    save(root/'deployed-evidence.json',report);print(report['score'])

if __name__=='__main__':main()
