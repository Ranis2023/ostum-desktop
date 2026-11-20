"""Evaluate Qwen's thinking mode against numeric labels from the pinned external MGSM-Rev2 corpus."""
import argparse,csv,hashlib,json,os,re,sys,time
from decimal import Decimal,InvalidOperation
from pathlib import Path
import numpy as np
ROOT=Path(__file__).resolve().parents[1];sys.path.insert(0,str(ROOT/'training'));from bench import Server
LOCAL=Path(os.environ['LOCALAPPDATA'])/'SferaAI';MODEL=LOCAL/'library/models/zarum-9b-q4_k_m.gguf';ENGINE=LOCAL/'acceleration/llama-b12547-cuda12'
SYS='Реши данную арифметическую задачу. В окончательном ответе укажи одно число. Сохраняй единицы из вопроса. Не придумывай дополнительных условий.'
def save(p,x):p.write_text(json.dumps(x,ensure_ascii=False,indent=2),encoding='utf8')
def canonical(text):
    lines=[s.strip() for s in text.strip().splitlines() if s.strip()]
    if not lines:return None
    numbers=re.findall(r'(?<![\d.,])[-−]?(?:\d{1,3}(?:[ ,\u00a0\u202f]\d{3})+|\d+)(?:[.,]\d+)?',lines[-1]);
    if not numbers:return None
    n=re.sub(r'[ \u00a0\u202f]','',numbers[-1]).replace('−','-');n=re.sub(r'(?<=\d),(?=\d{3}(?:\D|$))','',n).replace(',','.')
    try:return str(Decimal(n).normalize())
    except InvalidOperation:return None
def main():
    p=argparse.ArgumentParser();p.add_argument('stage',choices=['freeze','baseline','develop','final']);p.add_argument('--data',required=True,type=Path);p.add_argument('--out',required=True,type=Path);a=p.parse_args();out=a.out.resolve();out.mkdir(parents=True,exist_ok=True)
    tables={lang:list(csv.reader((a.data/('mgsm_'+lang+'.tsv')).open(encoding='utf-8-sig'),delimiter='\t')) for lang in ('ru','en')}
    if a.stage=='freeze':
        if (out/'manifest.json').exists():raise ValueError('Frozen split exists')
        ids=sorted(range(250),key=lambda n:hashlib.sha256(('Sfera-MGSM-20261120/'+str(n)).encode()).digest());assert len(tables['ru'])==len(tables['en'])==250
        save(out/'manifest.json',dict(development=ids[:8],closed_ru=ids[8:28],closed_en=ids[28:32],criterion='Promote automatic reasoning only if development gains at least two of eight, then at least three of twenty independent Russian answers, and p95 <=25 seconds at the same generation cap. Otherwise retain it as an experiment.',seed=20261120));return
    manifest=json.loads((out/'manifest.json').read_text(encoding='utf8'));wanted=('development','closed_ru','closed_en') if a.stage=='baseline' else ('development',) if a.stage=='develop' else ('closed_ru','closed_en');think=a.stage!='baseline'
    opts=['-c','8192','-t','8','--device','CUDA0','--fit-target','1024','--reasoning-format','deepseek','--reasoning-budget','768','--no-cache-idle-slots'];result={}
    with Server(ENGINE,MODEL,opts,out/(a.stage+'.log')) as srv:
        for split in wanted:
            obs=[];lang='en' if split.endswith('en') else 'ru'
            for i in manifest[split]:
                task,truth=tables[lang][i];start=time.perf_counter()
                req=dict(messages=[dict(role='system',content=SYS),dict(role='user',content=task)],temperature=0,seed=20261120,max_tokens=512,chat_template_kwargs=dict(enable_thinking=think))
                packet=srv.post('v1/chat/completions',req);choice=packet['choices'][0];answer=choice['message'].get('content') or '';v=canonical(answer)
                obs.append(dict(id=i,correct=v==canonical(truth) and choice['finish_reason']!='length',predicted=v,expected=canonical(truth),finish=choice['finish_reason'],seconds=time.perf_counter()-start,tokens=packet['usage']['completion_tokens']))
            result[split]=dict(total=len(obs),correct=sum(r['correct'] for r in obs),p50=float(np.percentile([r['seconds'] for r in obs],50)),p95=float(np.percentile([r['seconds'] for r in obs],95)),observations=obs);save(out/(a.stage+'.json'),result);print(a.stage,split,result[split]['correct'],len(obs),flush=True)
if __name__=='__main__':main()
