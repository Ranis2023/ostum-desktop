"""Reproducible answer, context, concurrency and resource probes for local Qwen."""
import argparse,ast,concurrent.futures,ctypes as C,hashlib,json,os,re,statistics,subprocess,sys,threading,time,urllib.request
from pathlib import Path
import numpy as np

ROOT=Path(__file__).resolve().parents[1];sys.path.insert(0,str(ROOT/'training'))
from bench import Server
LOCAL=Path(os.environ['LOCALAPPDATA']);DATA=LOCAL/'SferaAI';APP=LOCAL/'Programs/Ostum Desktop';MODEL=DATA/'library/models/zarum-9b-q4_k_m.gguf';ENGINE=DATA/'acceleration/llama-b12547-cuda12';SEED=20261120
PUBLIC=json.loads((Path(__file__).parent/'probes.json').read_text(encoding='utf8'))
SYSTEM='Ты — Сфера, локальный помощник. Выполняй требования к формату. Отвечай на языке пользователя.'

def save(path,value):path.write_text(json.dumps(value,ensure_ascii=False,indent=2),encoding='utf8')
def pure_code(code,tests):
    m=re.fullmatch(r'\s*```(?:python)?\s*\n(.*?)\n```\s*',code,flags=re.S|re.I);code=m[1] if m else code
    allowed=(ast.Module,ast.FunctionDef,ast.arguments,ast.arg,ast.Return,ast.Assign,ast.AugAssign,ast.For,ast.If,ast.IfExp,ast.Name,ast.Load,ast.Store,ast.Constant,ast.List,ast.Tuple,ast.BinOp,ast.Add,ast.Sub,ast.Mult,ast.Mod,ast.Pow,ast.UnaryOp,ast.USub,ast.Not,ast.Compare,ast.Eq,ast.NotEq,ast.Lt,ast.LtE,ast.Gt,ast.GtE,ast.Call,ast.ListComp,ast.GeneratorExp,ast.comprehension,ast.keyword,ast.Subscript,ast.Expr,ast.Attribute,ast.BoolOp,ast.Or,ast.And,ast.Is,ast.IsNot)
    functions={'sum':sum,'min':min,'max':max,'len':len,'int':int,'list':list,'List':list}
    try:
        tree=ast.parse(code);nodes=list(ast.walk(tree));defs=[n for n in nodes if isinstance(n,ast.FunctionDef)]
        if len(nodes)>155 or len(tree.body)!=1 or len(defs)!=1 or defs[0].name!='solve' or len(defs[0].args.args)!=1 or defs[0].decorator_list:return False
        for n in nodes:
            if not isinstance(n,allowed):return False
            if isinstance(n,ast.Name) and n.id.startswith('_'):return False
            if isinstance(n,ast.Attribute) and (n.attr!='append' or not isinstance(n.value,ast.Name)):return False
            if isinstance(n,ast.Call) and not (isinstance(n.func,ast.Name) and n.func.id in functions or isinstance(n.func,ast.Attribute) and n.func.attr=='append'):return False
            if isinstance(n,ast.Constant) and isinstance(n.value,(int,float)) and abs(n.value)>200:return False
            if isinstance(n,ast.BinOp) and isinstance(n.op,ast.Pow) and not (isinstance(n.right,ast.Constant) and isinstance(n.right.value,int) and 0<=n.right.value<=4):return False
            if isinstance(n,(ast.For,ast.comprehension)) and not (isinstance(n.iter,ast.Name) and n.iter.id==defs[0].args.args[0].arg):return False
        env={'__builtins__':functions};exec(compile(tree,'<pure-qa>','exec'),env)
        return all(env['solve'](list(arg))==expected for arg,expected in tests)
    except (ValueError,SyntaxError,TypeError,NameError,IndexError,KeyError,ArithmeticError):return False

def grade(row,result):
    clean=result.strip()
    if 'match' in row:return re.fullmatch(row['match'],clean.casefold()) is not None
    if 'exact' in row:return clean==row['exact']
    if 'object' in row:
        try:return json.dumps(json.loads(clean),sort_keys=True,ensure_ascii=False)==json.dumps(row['object'],sort_keys=True,ensure_ascii=False)
        except ValueError:return False
    return pure_code(clean,row['tests'])

def answer(srv,task,scales,limit=96,**extra):
    req=dict(messages=[dict(role='system',content=SYSTEM),dict(role='user',content=task)],temperature=0,seed=SEED,max_tokens=limit,chat_template_kwargs=dict(enable_thinking=False));req.update(extra)
    if scales:req['lora']=scales
    return srv.post('v1/chat/completions',req)['choices'][0]['message']

def schema(data):
    def kind(v):return {'type':'boolean'} if isinstance(v,bool) else {'type':'integer'} if isinstance(v,int) else {'type':'array','items':{'type':'string'}} if isinstance(v,list) else {'type':'string'}
    return {'type':'object','properties':{k:kind(v) for k,v in data.items()},'required':list(data),'additionalProperties':False}

def long_input(srv,target,position):
    identifier={640:'ГРАНАТ-317',2700:'ЛИЛИЯ-824',6750:'ЯНТАРЬ-491'}[target]
    lines=['Строка {}. Отдел {} получил макет и подтвердил обычный график.'.format(i,1+i%9) for i in range(560)]
    def build(n):
        text=lines[:n];text.insert(int(n*position),'Согласованный шифр партии особого заказа: '+identifier+'.');return '\n'.join(text)+'\nКакой согласован шифр партии особого заказа? Ответь только шифром.'
    lo,hi=1,len(lines)
    while hi-lo>1:
        m=(lo+hi)//2
        if len(srv.post('tokenize',dict(content=build(m),add_special=False))['tokens'])<=target:lo=m
        else:hi=m
    material=build(lo);return material,identifier,len(srv.post('tokenize',dict(content=material,add_special=False))['tokens'])

def quality(srv,scales,brief=False):
    rows=PUBLIC if not brief else [r for i,r in enumerate(PUBLIC) if i%3==0]
    result=[]
    for row in rows:
        t=time.perf_counter();msg=answer(srv,row['task'],scales,150 if row['kind']=='code' else 80).get('content') or ''
        result.append(dict(id=row['id'],kind=row['kind'],correct=grade(row,msg),seconds=time.perf_counter()-t,answer=msg))
    for row in [r for r in rows if r['kind']=='json']:
        fmt={'type':'json_schema','json_schema':{'name':'order','strict':True,'schema':schema(row['object'])}}
        msg=answer(srv,row['task'],scales,response_format=fmt).get('content') or '';result.append(dict(id='constrained-'+row['id'],kind='constrained_json',correct=grade(row,msg),answer=msg))
    function=schema({'quantity':0,'unit_price':0});tools=[{'type':'function','function':{'name':'quote_total','description':'Рассчитать общую стоимость по количеству и цене единицы.','parameters':function}},{'type':'function','function':{'name':'find_material','description':'Найти файл по названию.','parameters':schema({'name':''})}}]
    for q,price in [(6,140),(11,65),(3,920)][:1 if brief else 3]:
        msg=answer(srv,'Вызови инструмент для расчёта цены заказа: '+str(q)+' шт. по '+str(price)+' рублей.',scales,tools=tools,tool_choice='auto');ok=False
        try:
            call=msg['tool_calls'][0]['function'];ok=call['name']=='quote_total' and json.loads(call['arguments'])==dict(quantity=q,unit_price=price)
        except (KeyError,IndexError,TypeError,ValueError):pass
        result.append(dict(id='tool-'+str(q),kind='function_calling',correct=ok,tool_calls=msg.get('tool_calls')))
    for target in ((2700,) if brief else (640,2700,6750)):
        for pos in ((.82,) if brief else (.12,.82)):
            material,truth,tokens=long_input(srv,target,pos);msg=answer(srv,material,scales,24).get('content') or '';result.append(dict(id='context-%d-%d'%(target,100*pos),kind='context',tokens=tokens,correct=msg.strip(' «».\n')==truth,answer=msg))
    first=next(r for r in result if r['id']==rows[0]['id']);again=answer(srv,rows[0]['task'],scales,80).get('content') or '';result.append(dict(id='repeat',kind='stability',correct=again==first['answer']))
    return result

class Counters(C.Structure):
    _fields_=[('cb',C.c_uint32),('faults',C.c_uint32)]+[(n,C.c_size_t) for n in ('peak_working','working','p1','p2','p3','p4','page','peak_page','private')]
def memory(proc):
    counters=Counters();counters.cb=C.sizeof(counters);f=C.windll.psapi.GetProcessMemoryInfo
    return [counters.working/2**20,counters.private/2**20] if f(C.c_void_p(int(proc._handle)),C.byref(counters),counters.cb) else [0,0]
class Meter:
    def __init__(self,server):self.server=server;self.samples=[];self.start=time.perf_counter()
    def __enter__(self):
        cmd=['nvidia-smi','--query-gpu=memory.used,power.draw,utilization.gpu','--format=csv,noheader,nounits','--loop-ms=500'];self.proc=subprocess.Popen(cmd,stdout=subprocess.PIPE,stderr=subprocess.DEVNULL,text=True,creationflags=subprocess.CREATE_NO_WINDOW)
        def collect():
            for line in self.proc.stdout:
                try:
                    v,w,u=[float(i.strip()) for i in line.strip().split(',')];self.samples.append([time.perf_counter()-self.start,v,w,u,*memory(self.server.proc)])
                except ValueError:continue
        self.thread=threading.Thread(target=collect,daemon=True);self.thread.start();return self
    def __exit__(self,*_):self.proc.terminate();self.proc.wait(timeout=5);self.thread.join(3);self.proc.stdout.close()
    def record(self):
        a=self.samples
        if not a:return dict(samples=0)
        return dict(samples=len(a),peak_gpu_board_mib=max(r[1] for r in a),peak_server_working_mib=max(r[4] for r in a),peak_server_private_mib=max(r[5] for r in a),mean_gpu_utilization=np.mean([r[3] for r in a]),gpu_board_wh=sum((v[0]-u[0])*(v[2]+u[2])/7200 for u,v in zip(a,a[1:])),sampled_seconds=a[-1][0]-a[0][0])

def stream(srv,prompt,scales):
    body=dict(prompt=prompt,n_predict=32,temperature=0,seed=SEED,ignore_eos=True,cache_prompt=False,stream=True)
    if scales:body['lora']=scales
    start=time.perf_counter();first=None;timings={}
    try:
        with urllib.request.urlopen(srv.request('completion',body),timeout=120) as resp:
            for line in resp:
                if not line.startswith(b'data: '):continue
                raw=line[6:].strip()
                if raw==b'[DONE]':break
                packet=json.loads(raw)
                if first is None and packet.get('content'):first=time.perf_counter()-start
                if packet.get('timings'):timings=packet['timings']
        if first is None or timings.get('predicted_n',0)<1:raise ValueError('empty stream')
        return dict(ttft=first,latency=time.perf_counter()-start,tokens=timings['predicted_n'],tps=timings['predicted_per_second'])
    except Exception as e:return dict(error=type(e).__name__+': '+str(e))

def benchmark(srv,scales,workers):
    ps=[srv.template(SYSTEM,'Заявка '+str(i)+'. '+q['task']) for i,q in enumerate(PUBLIC[:12])];stream(srv,ps[0],scales)
    with Meter(srv) as meter,concurrent.futures.ThreadPoolExecutor(max_workers=workers) as pool:
        begin=time.perf_counter();rows=list(pool.map(lambda prompt:stream(srv,prompt,scales),ps));duration=time.perf_counter()-begin
    good=[r for r in rows if 'error' not in r];out={'requested':len(rows),'errors':len(rows)-len(good),'aggregate_tps':sum(r['tokens'] for r in good)/duration,'wall_seconds':duration,'resources':meter.record(),'observations':rows}
    if good:
        for k in ('ttft','latency','tps'):
            vals=[r[k] for r in good];out[k]={'p50':float(np.percentile(vals,50)),'p95':float(np.percentile(vals,95))}
    return out

def main():
    p=argparse.ArgumentParser();p.add_argument('--variant',choices=['raw','current','kv8','final'],required=True);p.add_argument('--out',type=Path,required=True);p.add_argument('--only',choices=['quality','performance','both'],default='both');a=p.parse_args();out=a.out.resolve();out.mkdir(parents=True,exist_ok=True)
    adapters=[] if a.variant=='raw' else [w for w in (DATA/'personal/work-formats.gguf',APP/'adapters/source-check.gguf',APP/'adapters/tool-routing.gguf') if w.exists()];new=APP/'adapters/general-evidence.gguf'
    if a.variant=='final' and new.exists():adapters.append(new)
    scales=[dict(id=i,scale=0) for i in range(len(adapters))];args=['-t','8','--fit-target','1024','--device','CUDA0','--no-cache-idle-slots','--reasoning-format','deepseek','--reasoning-budget','768']
    for w in adapters:args.extend(['--lora',str(w)])
    if adapters:args.append('--lora-init-without-apply')
    if a.variant=='kv8':args+=['--flash-attn','on','-ctk','q8_0','-ctv','q8_0']
    report={'variant':a.variant,'probe_sha256':hashlib.sha256((Path(__file__).parent/'probes.json').read_bytes()).hexdigest(),'seed':SEED}
    with Server(ENGINE,MODEL,args+['-c','8192'],out/'single.log') as srv:
        report['startup_s']=srv.startup
        if a.only!='performance':
            report['quality']=quality(srv,scales,a.variant=='kv8');save(out/'results.json',report);print(a.variant,'quality',sum(r['correct'] for r in report['quality']),len(report['quality']),flush=True)
        if a.only!='quality':report['concurrency_1']=benchmark(srv,scales,1);save(out/'results.json',report)
    if a.only!='quality':
        with Server(ENGINE,MODEL,args+['-c','8192','--parallel','2'],out/'double.log') as srv:
            for w in (2,4):report['concurrency_'+str(w)]=benchmark(srv,scales,w);save(out/'results.json',report)
    print(a.variant,'done',flush=True)
if __name__=='__main__':main()
