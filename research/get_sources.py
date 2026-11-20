"""Cache the exact official metadata and licensed public evaluation corpus used by this audit."""
import argparse,hashlib,json,urllib.request
from pathlib import Path

QWEN='c202236235762e1c871ad0ccb60c8ee5ba337b9a';RCB='53cb9d7f38b34c308a2eea3f4797f9edc7947d8b'
CHECKSUMS={
    'qwen-config.json':'d0883072e01861ed0b2d47be3c16c36a8e81c224c7ffaa310c6558fb3f932b05',
    'qwen-license.txt':'bbedc3fda3305820b977265f01b8619d87570a6739de3a5582c3464840f1e57a',
    'RCB.zip':'9ecc0bd0cc04e04922349212452166404b5a75b67be9e6aa996c80c64beb6ddb',
    'mgsm_ru.tsv':'a7cd1a19c020512804597cd9d2fa50f01aabbce8dc59e59187d0096cad6a99cf',
    'mgsm_en.tsv':'5920212bc6e777413ca1ffc96162c600b5ea3785d3695a119dc12f6e3f78da68',
}
def fetch(url):
    with urllib.request.urlopen(urllib.request.Request(url,headers={'User-Agent':'Sfera-local-research/1.0'}),timeout=40) as r:return r.read()
def main():
    p=argparse.ArgumentParser();p.add_argument('--out',type=Path,required=True);a=p.parse_args();a.out.mkdir(parents=True,exist_ok=True)
    base='https://huggingface.co/Qwen/Qwen3.5-9B/resolve/'+QWEN+'/'
    math='https://raw.githubusercontent.com/google-research-datasets/MGSM-Rev2/5f091edeae66ebc605ac4f98dca07a6c345f0060/MGSM-Rev2/'
    sources={'qwen-config.json':base+'config.json','qwen-license.txt':base+'LICENSE','RCB.zip':'https://huggingface.co/datasets/RussianNLP/russian_super_glue/resolve/'+RCB+'/data/RCB.zip','mgsm_ru.tsv':math+'mgsm_ru.tsv','mgsm_en.tsv':math+'mgsm_en.tsv'}
    out={}
    for name,url in sources.items():
        dest=a.out/name;payload=dest.read_bytes() if dest.exists() else fetch(url);checksum=hashlib.sha256(payload).hexdigest()
        if checksum!=CHECKSUMS[name]:raise ValueError('Unexpected revision: '+name)
        if not dest.exists():dest.write_bytes(payload)
        out[name]=dict(url=url,bytes=len(payload),sha256=checksum)
    (a.out/'sources.json').write_text(json.dumps(out,indent=2),encoding='utf8');print('verified source files',len(out))
if __name__=='__main__':main()
