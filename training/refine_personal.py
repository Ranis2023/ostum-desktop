"""Train a private format LoRA, anchored to its previous training set, and evaluate on independent chats."""
import argparse
import hashlib
import json
import os
from pathlib import Path

import numpy as np

from bench import Server
from personal_history import adapter_a
from tune import PROMPT, head_rows, lora_file


def label_margin_correction(scores, labels, margin):
    """Minimum change to each logit vector that puts the requested label ahead by margin."""
    answer = np.zeros_like(scores)
    for i, (row, label) in enumerate(zip(scores, labels)):
        competitors = np.sort(np.delete(row, label))[::-1]
        top = row[label]
        for count, value in enumerate(competitors, start=1):
            if value + margin <= top:
                break
            top += (value + margin - top) / (count + 1)
        answer[i] = np.minimum(row, top - margin) - row
        answer[i, label] = top - row[label]
    return answer


def folds_for(labels, groups):
    result = np.zeros(len(labels), dtype=int)
    for label in set(labels):
        items = np.flatnonzero(labels == label)
        ordered = sorted(items, key=lambda i: hashlib.sha256(('format-cv/' + groups[i]).encode()).digest())
        for j, i in enumerate(ordered):
            result[i] = j % 5
    return result


def fit(hidden, labels, anchor_indices, prior_scores, real_count, groups):
    x = hidden.astype(np.float64)
    length = np.linalg.norm(x, axis=1)
    unit = x / length[:, None]
    anchors = unit[anchor_indices]
    # Protect old training requests explicitly. Leave held-out synthetic and private requests out of fitting.
    basis = np.linalg.svd(anchors, full_matrices=False)[2]
    prior = prior_scores.argmax(1)
    folds = folds_for(labels[:real_count], groups)
    def rank(prediction, movement):
        correct = prediction == labels[:real_count]
        macro = np.mean([correct[labels[:real_count] == c].mean() for c in set(labels[:real_count])])
        return int(correct.sum()), float(macro), -float(movement)
    initial = rank(prior[:real_count], 0.0)
    best = (initial, None, None)
    for protect in (0., .8, 1.):
        directions = unit[:real_count] - protect * (unit[:real_count] @ basis.T) @ basis
        kernel = unit @ directions.T
        for ridge in (.015, .12, .7):
            # A normalized kernel gives both ordinary and anchor-projected fits a comparable trust region.
            lam = ridge * np.mean(np.diag(kernel[:real_count]))
            def solve(keep, target):
                gram = kernel[np.ix_(keep, keep)] + lam * np.eye(len(keep))
                return np.linalg.solve(gram, target[keep] / length[keep, None])
            for margin in (1.5, 4., 9.):
                target = label_margin_correction(prior_scores[:real_count], labels[:real_count], margin)
                cross = np.zeros_like(target)
                for fold in range(5):
                    learn = np.flatnonzero(folds != fold)
                    test = np.flatnonzero(folds == fold)
                    cross[test] = length[test, None] * kernel[np.ix_(test, learn)] @ solve(learn, target)
                coefficients = solve(np.arange(real_count), target)
                weight = coefficients.T @ directions
                old_examples = length[anchor_indices, None] * kernel[anchor_indices] @ coefficients
                for scale in (.4, .75, 1., 1.4):
                    anchored = (prior_scores[anchor_indices] + scale * old_examples).argmax(1)
                    if np.sum(anchored == labels[anchor_indices]) < np.sum(prior[anchor_indices] == labels[anchor_indices]):
                        continue
                    guess = (prior_scores[:real_count] + scale * cross).argmax(1)
                    ranking = rank(guess, np.linalg.norm(weight) * scale)
                    if ranking > best[0]:
                        best = ranking, (scale * weight).astype(np.float32), dict(protect=protect,ridge=ridge,margin=margin,scale=scale)
    return best, initial


def main():
    p = argparse.ArgumentParser()
    p.add_argument('--data', type=Path, required=True)
    p.add_argument('--features', type=Path, required=True)
    p.add_argument('--prior', type=Path, required=True)
    p.add_argument('--out', type=Path, required=True)
    p.add_argument('--stage', choices=('fit','test'), required=True)
    args = p.parse_args();out=args.out.resolve();out.mkdir(exist_ok=True,parents=True)
    root=Path(os.environ['LOCALAPPDATA'])/'SferaAI'
    cuda=root/'acceleration/llama-b12547-cuda12'
    using_cuda=(cuda/'verified-b12547.txt').is_file() and (cuda/'ggml-cuda.dll').is_file()
    engine=cuda if using_cuda else root.parent/'Programs/Ostum Desktop/engines/llama-b12547'
    model=root/'library/models/zarum-9b-q4_k_m.gguf'
    history=json.loads((args.data/'format-history-reviewed.json').read_text(encoding='utf8'))
    rows=[dict(r, source='history') for r in history]
    for line in (args.data/'format-baseline.txt').read_text(encoding='utf8').splitlines():
        split,label,task=line.split('|',2);rows.append(dict(source='synthetic',split=split,label=int(label),text=task))
    # The final trial is blind to the new hold-out, including its features and measured baseline.
    prior_file=args.prior.resolve();candidate=out/'work-formats.gguf'
    if args.stage=='fit':
        cache=np.load(args.features);x=cache['x'];ids=cache['ids'].tolist()
        fingerprint=hashlib.sha256(json.dumps(rows,ensure_ascii=False,sort_keys=True).encode()).hexdigest()
        if fingerprint!=str(cache['fingerprint']):raise ValueError('Original evaluation data changed')
        arch,vocab,head=head_rows(model,engine,ids);old=adapter_a(prior_file,8,x.shape[1])
        prior=x@(head+old).T;labels=np.array([r['label'] for r in rows]);anchors=np.array([i for i,r in enumerate(rows) if r['source']=='synthetic' and r['split']=='train'])
        selected,initial=fit(x,labels,anchors,prior,len(history),[r['group'] for r in history]);score,delta,settings=selected
        b=np.zeros((vocab,8),np.float16)
        for i,token in enumerate(ids):b[token,i]=1
        lora_file(candidate,arch,old if delta is None else old+delta,b,name='Sfera personal work formats v3')
        evidence=dict(training_groups=len(history),anchors=len(anchors),previous_cv=initial,selected_cv=score,selection=settings,data_sha256=fingerprint,
                      prior_sha256=hashlib.sha256(prior_file.read_bytes()).hexdigest(),candidate_sha256=hashlib.sha256(candidate.read_bytes()).hexdigest())
        (out/'fit.json').write_text(json.dumps(evidence,indent=2),encoding='utf8');print(evidence,flush=True);return
    report=json.loads((out/'fit.json').read_text(encoding='utf8'))
    if report['candidate_sha256']!=hashlib.sha256(candidate.read_bytes()).hexdigest():raise ValueError('Evaluated adapter changed')
    independent=json.loads((args.data/'format-independent-29.json').read_text(encoding='utf8'))
    assert not {r['group'] for r in independent} & {r['group'] for r in history}
    tests=[dict(r,source='history') for r in independent]+[r for r in rows if r['source']=='synthetic' and r['split']=='test']
    answers=[dict(source=r['source'],expected=r['label']) for r in tests]
    opts=['-c','8192','-t','8','--fit-target','1024','--lora',str(prior_file),'--lora',str(candidate),'--lora-init-without-apply']
    if using_cuda:opts+=['--device','CUDA0']
    with Server(engine,model,opts,out/'native.log') as srv:
        for adapter,key in [(-1,'base'),(0,'previous'),(1,'candidate')]:
            for i,row in enumerate(tests):
                body=dict(prompt=PROMPT.format(task=row['text']),grammar='root ::= [0-7]',temperature=0,n_predict=1,lora=[dict(id=j,scale=int(j==adapter)) for j in range(2)])
                answers[i][key]=int(srv.post('completion',body)['content'].strip())
            print('tested',key,flush=True)
    stats={}
    for source in ('history','synthetic'):
        subset=[r for r in answers if r['source']==source]
        stats[source]=dict(total=len(subset),**{k:sum(r[k]==r['expected'] for r in subset) for k in ('base','previous','candidate')})
    report.update(native=stats,observations=answers);(out/'evaluation.json').write_text(json.dumps(report,indent=2),encoding='utf8');print(stats)


if __name__=='__main__':main()
