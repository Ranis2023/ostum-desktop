"""Train and test the single-token tool selector of the installed Qwen3.5 9B."""
import argparse
import hashlib
import json
import os
from pathlib import Path

import numpy as np

from bench import Server
from tune import head_rows, lora_file


ROOT = Path(__file__).resolve().parents[1]
CODES = "ABCDEFGHI"


def read_cases():
    template = (ROOT / "src/RoutePrompt.txt").read_text(encoding="utf-8-sig").strip()
    rows = []
    for line in (ROOT / "training/route-cases.txt").read_text(encoding="utf-8-sig").splitlines():
        split, code, image, context, task = line.split("|", 4)
        rows.append((split, code, template.format(context, image, task)))
    assert len({row[2] for row in rows}) == len(rows)
    return rows


def train(features, rows, base):
    split = np.array([r[0] for r in rows])
    labels = np.array([CODES.index(r[1]) for r in rows])
    train_mask = split == "train"
    dev = split == "dev"
    x = features[train_mask].astype(np.float64)
    norm = np.linalg.norm(x, axis=1, keepdims=True)
    unit = x / norm
    gram = unit @ unit.T
    prior = base[train_mask] - base[train_mask].mean(axis=1, keepdims=True)
    best = None
    for ridge in (0.002, 0.015, 0.08, 0.35):
        projection = np.linalg.solve(gram + ridge * np.eye(len(x)), unit)
        for target in (7., 12., 18.):
            desired = np.eye(len(CODES))[labels[train_mask]] * target
            weight = ((desired - prior) / norm).T @ projection
            for strength in (0.75, 1., 1.2):
                delta = (weight * strength).astype(np.float32)
                guess = (base[dev] + features[dev] @ delta.T).argmax(1)
                correct = guess == labels[dev]
                # Once the score ties, favour balanced classes, then the smaller update.
                minimum = min(np.sum(correct[labels[dev] == i]) for i in range(len(CODES)))
                score = (int(sum(correct)), int(minimum), -float(np.linalg.norm(delta)))
                if best is None or score > best[0]:
                    best = score, delta, dict(ridge=ridge, target=target, strength=strength)
    return best


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", type=Path, required=True)
    out = parser.parse_args().out.resolve()
    out.mkdir(parents=True, exist_ok=True)
    rows = read_cases()
    local = Path(os.environ["LOCALAPPDATA"])
    engine = local / "Programs/Ostum Desktop/engines/llama-b12547"
    model = local / "SferaAI/library/models/zarum-9b-q4_k_m.gguf"
    options = ["-c", "1024", "--fit-target", "1100", "-t", "6"]
    stamp = hashlib.sha256(json.dumps(rows, ensure_ascii=False).encode()).hexdigest()
    cache = out / "route-features.npz"
    if cache.exists():
        loaded = np.load(cache)
        if str(loaded["stamp"]) != stamp:
            raise ValueError("Route features belong to a different corpus")
        features, ids = loaded["x"], loaded["ids"].tolist()
    else:
        vectors = []
        with Server(engine, model, options + ["--embeddings", "--pooling", "last", "--embd-normalize", "-1"], out / "embed.log") as server:
            tokens = [server.post("tokenize", {"content": c, "add_special": False})["tokens"] for c in CODES]
            assert all(len(chunk) == 1 for chunk in tokens)
            ids = [chunk[0] for chunk in tokens]
            for i, (_, _, prompt) in enumerate(rows):
                reply = server.post("embeddings", {"content": prompt, "embd_normalize": -1})
                vec = np.array(reply[0]["embedding"], dtype=np.float32)
                vectors.append(vec.reshape(-1, vec.shape[-1])[-1])
                if (i + 1) % 40 == 0:
                    print("features", i + 1, "/", len(rows), flush=True)
        features = np.array(vectors)
        np.savez(cache, x=features, ids=ids, stamp=stamp)
    arch, vocab, head = head_rows(model, engine, ids)
    score, weights, selected = train(features, rows, features @ head.T)
    b = np.zeros((vocab, len(CODES)), dtype=np.float16)
    for slot, token in enumerate(ids):
        b[token, slot] = 1
    adapter = out / "tool-routing.gguf"
    lora_file(adapter, arch, weights, b, name="Sfera tool routing")
    test = [r for r in rows if r[0] == "test"]
    results = [dict(expected=code) for _, code, _ in test]
    with Server(engine, model, options + ["--lora", str(adapter), "--lora-init-without-apply"], out / "verify.log") as server:
        for scale, label in ((0, "base"), (1, "tuned")):
            for i, (_, _, prompt) in enumerate(test):
                body = dict(prompt=prompt, n_predict=1, temperature=0, cache_prompt=True,
                            grammar="root ::= [A-I]", lora=[dict(id=0, scale=scale)])
                results[i][label] = server.post("completion", body)["content"].strip()
            print("native phase", label, flush=True)
    report = dict(task="Sfera tool selection in Russian", rank=len(CODES), training=sum(r[0] == "train" for r in rows),
                  development=sum(r[0] == "dev" for r in rows), development_correct=score[0], test=len(test), choices=selected,
                  base_correct=sum(r["expected"] == r["base"] for r in results), tuned_correct=sum(r["expected"] == r["tuned"] for r in results),
                  cases_sha256=stamp, predictions=results)
    (out / "evaluation.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print({k: v for k, v in report.items() if k != "predictions"}, flush=True)


if __name__ == "__main__":
    main()
