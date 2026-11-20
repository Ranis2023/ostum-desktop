"""Build the private expert-labelled hold-out locally, detecting repeated requests across chats."""
import argparse,difflib,hashlib,json,os,re
from pathlib import Path

root=Path(os.environ['LOCALAPPDATA'])/'SferaAI/personal/learning'
parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('--redactions',type=Path,help='Private UTF-8 file of names to replace, one name per line.')
args=parser.parse_args()
names=[name.strip() for name in args.redactions.read_text(encoding='utf-8-sig').splitlines() if name.strip()] if args.redactions else []
redactions=re.compile('|'.join(re.escape(name) for name in sorted(names,key=len,reverse=True)),re.I) if names else None
read=lambda f:json.loads((root/f).read_text(encoding='utf8'))
rows=read('extra-heldout-pool.json');old=read('candidate-set-v2.json')+read('candidate-fresh-before-training.json')
labels={0:7,1:5,2:4,3:4,4:4,5:4,6:4,7:4,8:1,9:1,10:1,11:1,12:3,13:7,16:7,18:7,19:7,20:6,21:7,23:7,24:0,25:3,26:7,27:7,29:7,30:2,31:2,32:2,33:2,35:6,36:0}
def canon(s):return re.sub(r'[^а-яёa-z0-9]+',' ',re.sub(r'\[[^\]]+\]|\blovable\b|\bgamma\b',' ',s.casefold())).strip()
seen=[canon(r['text']) for r in old];groups={r['group'] for r in old};out=[];dropped=0
for i,c in labels.items():
    r=rows[i];assert r['group'] not in groups;groups.add(r['group']);text=redactions.sub('[имя]',r['text']) if redactions else r['text'];q=canon(text)
    if any(difflib.SequenceMatcher(None,q,s,autojunk=False).ratio()>=.77 for s in seen):dropped+=1;continue
    seen.append(q);out.append(dict(group=r['group'],label=c,text=text))
data=json.dumps(out,ensure_ascii=False,indent=2).encode();p=root/'audit-independent-formats.json'
if p.exists():assert p.read_bytes()==data
else:p.write_bytes(data)
print('private holdout',len(out),'duplicate exclusions',dropped,'sha256',hashlib.sha256(data).hexdigest())
