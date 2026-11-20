"""Evaluate installed GGUF adapters through the production CUDA or Vulkan server."""
import argparse
import json
import os
import re
from pathlib import Path
from bench import Server
from evidence import LABELS, SYSTEM, prepare, user
from refine_evidence import DOMAIN, extra_rows
from route import read_cases


def main():
    cli=argparse.ArgumentParser()
    for key in ("data","engine","adapters","out"):cli.add_argument("--"+key,required=True,type=Path)
    args=cli.parse_args();out=args.out.resolve();out.mkdir(parents=True,exist_ok=True)
    local=Path(os.environ["LOCALAPPDATA"])
    model=local/"SferaAI/library/models/zarum-9b-q4_k_m.gguf"
    personal=local/"SferaAI/personal/work-formats.gguf"
    work=args.adapters.resolve()/"source-check.gguf";route=args.adapters.resolve()/"tool-routing.gguf"
    weights=([personal] if personal.is_file() else [])+[work,route]
    general=args.adapters.resolve()/"general-evidence.gguf"
    if general.is_file():weights.append(general)
    options=["-c","8192","--fit-target","1024","-t","8","--no-cache-idle-slots","--reasoning-format","deepseek","--reasoning-budget","768"]
    for weight in weights:options.extend(["--lora",str(weight)])
    options.append("--lora-init-without-apply")
    def scales(which):return [dict(id=i,scale=int(i==which)) for i in range(len(weights))]
    public=prepare(args.data)
    cases=[r for r in extra_rows(args.data,public) if r["split"]=="test"]
    evidence=[dict(expected=r["label"],source=r["source"],used=bool(re.search(DOMAIN,r["premise"]+"\n"+r["hypothesis"],re.I))) for r in cases]
    routes=[r for r in read_cases() if r[0]=="test"];routing=[dict(expected=r[1]) for r in routes]
    with Server(args.engine.resolve(),model,options,out/"native.log") as srv:
        for label,applied in (("base",False),("tuned",True)):
            for row,obs in zip(cases,evidence):
                chosen=weights.index(work) if obs["used"] else weights.index(general) if general.is_file() else -1
                if applied and chosen==-1:obs[label]=obs["base"];continue
                packet=dict(messages=[dict(role="system",content=SYSTEM),dict(role="user",content=user(row))],temperature=0,max_tokens=10,lora=scales(chosen if applied else -1),chat_template_kwargs=dict(enable_thinking=False))
                txt=srv.post("v1/chat/completions",packet)["choices"][0]["message"]["content"].strip().casefold().lstrip("* «\"")
                obs[label]=next((i for i,w in enumerate(LABELS) if txt.startswith(w.casefold())),-1)
            for row,obs in zip(routes,routing):
                packet=dict(prompt=row[2],n_predict=1,temperature=0,grammar="root ::= [A-I]",lora=scales(weights.index(route) if applied else -1))
                obs[label]=srv.post("completion",packet)["content"].strip()
            print("native phase",label,flush=True)
    def score(obs):return dict(total=len(obs),base=sum(z["expected"]==z["base"] for z in obs),tuned=sum(z["expected"]==z["tuned"] for z in obs))
    report=dict(routing=score(routing),work=score([r for r in evidence if r["source"]=="work"]),RCB=score([r for r in evidence if r["source"]=="RCB"]),routing_observations=routing,evidence_observations=evidence)
    (out/"results.json").write_text(json.dumps(report,indent=2),encoding="utf8")
    print({k:v for k,v in report.items() if "observations" not in k},flush=True)
if __name__=="__main__":main()
