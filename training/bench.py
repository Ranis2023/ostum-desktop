"""Compare latency and throughput of the installed Qwen model on this PC."""
import argparse
import json
import os
from pathlib import Path
import secrets
import socket
import statistics
import subprocess
import time
import urllib.request

class Server:
    def __init__(self, engine, model, switches, log):
        self.engine,self.model,self.switches,self.log=engine,model,switches,log
    def __enter__(self):
        with socket.socket() as sock:
            sock.bind(("127.0.0.1",0)); self.port=sock.getsockname()[1]
        self.key=secrets.token_hex(32); self.base="http://127.0.0.1:"+str(self.port)
        self.handle=self.log.open("wb"); self.start=time.perf_counter()
        command=[str(self.engine/"llama-server.exe"),"-m",str(self.model),"--host","127.0.0.1","--port",str(self.port),"--api-key",self.key,"--offline","--no-ui","--no-mmproj","--no-warmup","--parallel","1","--cache-ram","0","--ctx-checkpoints","0","--batch-size","512","--ubatch-size","128",*self.switches]
        self.proc=subprocess.Popen(command,cwd=self.engine,stdout=self.handle,stderr=self.handle,creationflags=getattr(subprocess,"CREATE_NO_WINDOW",0))
        try:
            for _ in range(480):
                if self.proc.poll() is not None: raise RuntimeError("model server failed: "+self.log.name)
                try:
                    with urllib.request.urlopen(self.base+"/health",timeout=2) as response:
                        if response.status==200: self.startup=time.perf_counter()-self.start;return self
                except OSError:time.sleep(.25)
            raise TimeoutError("server startup")
        except BaseException:
            self.__exit__(None,None,None);raise
    def __exit__(self,*_):
        if self.proc.poll() is None:self.proc.terminate()
        self.proc.wait(timeout=25);self.handle.close()
    def request(self,route,body):
        return urllib.request.Request(self.base+"/"+route,json.dumps(body,ensure_ascii=False).encode("utf8"),{"Content-Type":"application/json","Authorization":"Bearer "+self.key})
    def post(self,route,body):
        with urllib.request.urlopen(self.request(route,body),timeout=240) as reply:return json.load(reply)
    def template(self, system, user):
        return self.post("apply-template",{"messages":[{"role":"system","content":system},{"role":"user","content":user}],"chat_template_kwargs":{"enable_thinking":False},"add_generation_prompt":True})["prompt"]
    def complete(self,prompt,count=64):
        body={"prompt":prompt,"n_predict":count,"temperature":0,"seed":42,"stream":True,"cache_prompt":False,"ignore_eos":True}
        start=time.perf_counter();first=None;timings={};length=0
        with urllib.request.urlopen(self.request("completion",body),timeout=180) as reply:
            for line in reply:
                if not line.startswith(b"data: "):continue
                content=line[6:].strip()
                if content==b"[DONE]":break
                d=json.loads(content)
                if d.get("content"):
                    if first is None:first=time.perf_counter()-start
                    length+=len(d["content"])
                if d.get("timings"):timings=d["timings"]
        return dict(first=first,seconds=time.perf_counter()-start,token_s=timings.get("predicted_per_second"),prefill=timings.get("prompt_per_second"),tokens=timings.get("predicted_n"),characters=length)

def main():
    p=argparse.ArgumentParser();p.add_argument("--out",type=Path,required=True);args=p.parse_args()
    local=Path(os.environ["LOCALAPPDATA"]); engine=local/"Programs/Ostum Desktop/engines/llama-b12547"; weights=local/"SferaAI/library/models/zarum-9b-q4_k_m.gguf"
    adapter=local/"SferaAI/personal/work-formats.gguf"
    baseline=["--ctx-size","8192","--threads","8","--fit-target","1024"];common=["--lora",str(adapter),"--lora-init-without-apply"] if adapter.exists() else []
    configs={
        "baseline":baseline,
        "reserve512":["-c","8192","-t","8","--fit-target","512"],
        "balanced":["-c","8192","-t","6","--fit-target","512","-fa","on","-ctk","q8_0","-ctv","q8_0"],
        "short":["-c","4096","-t","6","--fit-target","448","-fa","on","-ctk","q8_0","-ctv","q8_0"],
        "short8":["-c","4096","-t","8","--fit-target","448","-fa","on","-ctk","q8_0","-ctv","q8_0"]
    }
    prompts=["Составь три тезиса для обсуждения оформления выставочного стенда. Кратко объясни каждый.",
             "Объясни, чем первичный источник отличается от пересказа, и приведи практический пример.",
             "Сделай вывод для заказчика. "+"У первого подрядчика цена 7200 рублей, срок пять дней. Второй предлагает 6300 рублей и срок семь дней. "*13]
    args.out.mkdir(parents=True,exist_ok=True);results={}
    for name, switches in configs.items():
        try:
            with Server(engine,weights,[*switches,*common,"--no-cache-idle-slots"],args.out/(name+".log")) as s:
                rendered=[s.template("Ты Сфера. Ответь пользователю по-русски.",p) for p in prompts]
                s.complete(rendered[0],8)
                observations=[s.complete(p) for p in rendered]
                results[name]={"startup":s.startup,"observations":observations,"median_first":statistics.median(x["first"] for x in observations),"median_tokens_s":statistics.median(x["token_s"] for x in observations)}
        except Exception as err:results[name]={"error":str(err)}
        (args.out/"benchmark.json").write_text(json.dumps(results,indent=2),encoding="utf8")
        print(name,{k:v for k,v in results[name].items() if k!="observations"},flush=True)
if __name__=="__main__":main()
