"""Compare deterministic, unrestricted generation before and after loading the installed LoRAs."""
import argparse
import hashlib
import json
import os
from pathlib import Path

from bench import Server


QUESTIONS=(
    'Объясни одним предложением, для чего типографии нужны поля под обрез.',
    'Придумай три коротких названия для мастерской, которая реставрирует мебель.',
    'Напиши функцию Python, которая возвращает сумму чётных чисел в списке.',
    'Переведи на английский: «Мы согласуем дизайн до конца недели».',
)


def main():
    p=argparse.ArgumentParser();p.add_argument('--out',type=Path,required=True);p.add_argument('--adapters',type=Path);args=p.parse_args()
    out=args.out.resolve();out.mkdir(parents=True,exist_ok=True)
    user=Path(os.environ['LOCALAPPDATA']);app=user/'Programs/Ostum Desktop';data=user/'SferaAI'
    engine=data/'acceleration/llama-b12547-cuda12'
    common=['-c','8192','--fit-target','1024','-t','8','--no-cache-idle-slots']
    if (engine/'verified-b12547.txt').exists():common+=['--device','CUDA0']
    else:engine=app/'engines/llama-b12547'
    adapters=args.adapters.resolve() if args.adapters else app/'adapters'
    weights=[w for w in [data/'personal/work-formats.gguf']+[adapters/n for n in ('source-check.gguf','tool-routing.gguf','general-evidence.gguf')] if w.exists()]
    options=[]
    for w in weights:options.extend(['--lora',str(w)])
    options+=['--lora-init-without-apply'];outputs={}
    for name,extra in [('base',[]),('installed',options)]:
        with Server(engine,data/'library/models/zarum-9b-q4_k_m.gguf',common+extra,out/(name+'.log')) as srv:
            answers=[]
            for task in QUESTIONS:
                prompt=srv.template('Ты — Сфера. Отвечай на языке пользователя.',task)
                body=dict(prompt=prompt,temperature=0,seed=42,n_predict=48,return_tokens=True,cache_prompt=False)
                if extra:body['lora']=[dict(id=i,scale=0) for i in range(len(weights))]
                reply=srv.post('completion',body)
                if not reply['content'].strip():raise RuntimeError('Empty generation')
                answers.append((reply.get('tokens'),reply['content']))
            outputs[name]=answers
    observations=[]
    for before,after in zip(outputs['base'],outputs['installed']):
        identical=before==after
        observations.append(dict(identical=identical,token_count=len(before[0] or []),base_sha256=hashlib.sha256(before[1].encode()).hexdigest(),installed_sha256=hashlib.sha256(after[1].encode()).hexdigest()))
    report=dict(questions=len(QUESTIONS),identical=sum(row['identical'] for row in observations),observations=observations)
    (out/'free-generation.json').write_text(json.dumps(report,indent=2),encoding='utf8');print(report)


if __name__=='__main__':main()
