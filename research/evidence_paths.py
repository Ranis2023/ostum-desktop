"""Select six frozen public checks for exercising the actual installed desktop dispatch."""
import argparse
import json
from pathlib import Path


def main():
    p=argparse.ArgumentParser()
    p.add_argument('--evidence',required=True,type=Path)
    p.add_argument('--out',required=True,type=Path)
    p.add_argument('--deployed',action='store_true')
    p.add_argument('--predictions',type=Path)
    a=p.parse_args()
    def read(name):return json.loads((a.evidence/name).read_text(encoding='utf8'))
    rows=read('closed.json')
    scores=json.loads(a.predictions.read_text(encoding='utf8')) if a.predictions else read(('final' if a.deployed else 'baseline')+'-closed.json')
    predicted={r['id']:r['predicted'] for r in scores['observations']}
    labels=['Да','Нет','Недостаточно данных']
    subset=[]
    for c in range(3):
        examples=sorted((r for r in rows if r['label']==c),key=lambda r:r['id'])[:2]
        for r in examples:
            subset.append(dict(id='public-'+r['id'][:12],kind='verdict',expected=labels[predicted[r['id']]],
                               text='Материал:\n'+r['premise']+'\n\nУтверждение:\n'+r['hypothesis']))
    a.out.write_text(json.dumps(subset,ensure_ascii=False,indent=2),encoding='utf8')


if __name__=='__main__':
    main()
