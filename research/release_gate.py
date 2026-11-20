"""Apply the recorded study thresholds and check the shipping assemblies against regression data."""
import argparse
import json
from pathlib import Path
from statistics import median

def read(p):return json.loads(p.read_text(encoding='utf-8-sig'))
def score(p):return read(p)['score']
def passed(rows,kind):return sum(r['correct'] for r in rows if r['kind']==kind)
def main():
    p=argparse.ArgumentParser();p.add_argument('--run',type=Path,required=True);p.add_argument('--verify',action='store_true')
    a=p.parse_args();d=a.run
    current=score(d/'evidence/baseline-closed.json');rcb=score(d/'transfer/candidate-regression.json')
    raw,grammar,new=[score(d/('transfer/'+v+'.json')) for v in ('raw-external','grammar-external','candidate-external')]
    checks=dict(new_external_plus_five=new['correct']>=raw['correct']+5,external_macro=new['macro_f1']>raw['macro_f1'],
                better_than_grammar=new['correct']>grammar['correct'],rcb_plus_five=rcb['correct']>=current['correct']+5,
                rcb_macro=rcb['macro_f1']>current['macro_f1'],external_per_class=all(y['correct']>=x['correct']-3 for x,y in zip(raw['per_class'],new['per_class'])))
    first=score(d/'evidence/final-closed.json');proposed=dict(general_evidence=all(checks.values()),first_candidate=first['correct']>=current['correct']+5 and first['macro_f1']>current['macro_f1'],transfer_checks=checks)
    base=read(d/'current/results.json');kv=read(d/'kv8/results.json');loads=['concurrency_'+str(k) for k in (1,2,4)]
    throughput=[kv[k]['aggregate_tps']/base[k]['aggregate_tps'] for k in loads]
    memory=base[loads[1]]['resources']['peak_gpu_board_mib']-kv[loads[1]]['resources']['peak_gpu_board_mib']
    proposed['kv8']=all(x>=1.10 for x in throughput) or memory>=256 and min(throughput)>=.95
    proposed['kv_memory_saved_mib']=memory;proposed['kv_throughput_ratios']=throughput
    before,after=[read(d/(n+'/application.json')) for n in ('app-before','app-final')]
    calc=lambda o:median(r['latency'] for r in o if r['kind']=='numeric')
    ordinary=all(not r['direct'] and r['correct'] for r in after if r['kind']=='mixed')
    controls=read(d/'guard.json')
    appchecks=dict(arithmetic=passed(after,'numeric')==6 and calc(before)>=2*calc(after) and ordinary,
                   history=passed(after,'history')>=passed(before,'history')+3 and all(controls.values()),
                   json=passed(after,'json')>passed(before,'json'),quotations=passed(after,'quote')>passed(before,'quote'))
    if not all(appchecks.values()):raise ValueError('App threshold missed: '+str(appchecks))
    proposed['application']=appchecks;proposed['automatic_thinking']=False
    if a.verify:
        regress=read(d/'legacy-regressions/results.json');free=read(d/'free/free-generation.json')
        if regress['work']['tuned']<51 or regress['routing']['tuned']<42 or free['identical']!=4:
            raise ValueError('Baseline regression: '+str((regress['work'],regress['routing'],free['identical'])))
        proposed['business_controls']=regress['work']['tuned'];proposed['route_controls']=regress['routing']['tuned']
        proposed['unrestricted_unchanged']=free['identical']
    dest=d/('release.json' if a.verify else 'proposed.json')
    dest.write_text(json.dumps(proposed,indent=2),encoding='utf8');print(json.dumps(proposed),flush=True)

if __name__=='__main__':main()
