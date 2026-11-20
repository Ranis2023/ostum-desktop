"""Record the installed baseline and archive the application sources without personal contents."""
import argparse,hashlib,json,os,platform,shutil,struct,subprocess,sys,zipfile
from pathlib import Path

ROOT=Path(__file__).resolve().parents[1]
LOCAL=Path(os.environ['LOCALAPPDATA']); DATA=LOCAL/'SferaAI'; APP=LOCAL/'Programs/Ostum Desktop'
def digest(p):
    h=hashlib.sha256()
    with p.open('rb') as stream:
        for block in iter(lambda:stream.read(2**20),b''):h.update(block)
    return h.hexdigest()
def metadata(model):
    nums={0:'B',1:'b',2:'H',3:'h',4:'I',5:'i',6:'f',7:'?',10:'Q',11:'q',12:'d'}
    with model.open('rb') as f:
        def n(fmt):return struct.unpack('<'+fmt,f.read(struct.calcsize('<'+fmt)))[0]
        def string():return f.read(n('Q')).decode('utf8',errors='replace')
        def value(t,keep):
            if t in nums:return n(nums[t])
            if t==8:
                data=f.read(n('Q'));return data.decode('utf8',errors='replace') if keep else None
            if t==9:
                it,m=n('I'),n('Q')
                if not keep and it in nums:f.seek(m*struct.calcsize('<'+nums[it]),1);return {'count':m}
                if keep and m<=200:return [value(it,True) for _ in range(m)]
                for _ in range(m):value(it,False)
                return {'count':m}
            raise ValueError(t)
        assert f.read(4)==b'GGUF' and n('I')==3
        tensors,count=n('Q'),n('Q');out={'tensors':tensors};template=None
        for _ in range(count):
            k=string();typ=n('I');v=value(typ,k not in ('tokenizer.ggml.tokens','tokenizer.ggml.merges','tokenizer.ggml.token_type'))
            if k.startswith(('general.','qwen','tokenizer.')):
                if 'chat_template' in k and isinstance(v,str):
                    template=v;out[k]={'sha256':hashlib.sha256(v.encode()).hexdigest(),'characters':len(v),'thinking_control':'enable_thinking' in v,'tool_calls':'tool_call' in v}
                else:out[k]=v
        types={};parameters=0
        for _ in range(tensors):
            name=string();dims=[n('Q') for _ in range(n('I'))];kind=n('I');n('Q');types[kind]=types.get(kind,0)+1
            count=1
            for dim in dims:count*=dim
            parameters+=count
        out['stored_tensor_elements']=parameters;out['tensor_types']=types
        return out,template
def main():
    p=argparse.ArgumentParser();p.add_argument('--out',type=Path,required=True);args=p.parse_args();out=args.out.resolve();out.mkdir(parents=True,exist_ok=True)
    if (out/'baseline.json').exists():raise SystemExit('Baseline already frozen')
    model=DATA/'library/models/zarum-9b-q4_k_m.gguf';meta,template=metadata(model)
    sources=[p for name in ('src','packaging','training') for p in (ROOT/name).rglob('*') if p.is_file() and '__pycache__' not in p.parts and p.suffix not in ('.pyc','.nupkg')]+[ROOT/'build.ps1']
    with zipfile.ZipFile(out/'source-3.2.1.zip','w',zipfile.ZIP_DEFLATED,compresslevel=5) as z:
        for path in sorted(sources):z.write(path,path.relative_to(ROOT))
    msi=out/'Sfera-3.2.1.msi';os.link(ROOT/'dist/Sfera-Setup.msi',msi)
    paths=[APP/'OstumDesktop.exe',APP/'adapters/source-check.gguf',APP/'adapters/tool-routing.gguf',DATA/'personal/work-formats.gguf',model,DATA/'library/models/zarum-9b-vision-f16.gguf',DATA/'acceleration/llama-b12547-cuda12/llama-server.exe',ROOT/'research/protocol.json',ROOT/'.build/evidence/RCB.zip',msi,out/'source-3.2.1.zip']
    info={p.name:{'bytes':p.stat().st_size,'sha256':digest(p)} for p in paths}
    props={k:v for k,v in json.loads((DATA/'preferences.json').read_text(encoding='utf-8-sig')).items() if k!='Models'}
    (out/'chat-template.jinja').write_text(template or '',encoding='utf8');(out/'gguf.json').write_text(json.dumps(meta,ensure_ascii=False,indent=2),encoding='utf8')
    measurement={'files':info,'preferences':props,'python':sys.version,'windows':platform.version(),'gpu':subprocess.check_output(['nvidia-smi','--query-gpu=name,driver_version,memory.total','--format=csv,noheader'],text=True).strip()}
    (out/'baseline.json').write_text(json.dumps(measurement,ensure_ascii=False,indent=2),encoding='utf8')
    print('snapshot',len(sources),'source files;',len(info),'hashes;',meta.get('general.architecture'),meta['stored_tensor_elements'],flush=True)
if __name__=='__main__':main()
