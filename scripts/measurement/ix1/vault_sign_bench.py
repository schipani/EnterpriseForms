"""IX-1 measurement (C-126): sequential ES256 sign latency of a Vault Transit ecdsa-p256 key, measured from the host.

Synthetic input only. Run against a throwaway Vault dev container, never a shared or production Vault, for example:
  docker run --rm -d --name ix1-vault-probe -p 18299:8200 -e VAULT_DEV_ROOT_TOKEN_ID=probe-root hashicorp/vault:1.20.4
  docker exec -e VAULT_ADDR=http://127.0.0.1:8200 -e VAULT_TOKEN=probe-root ix1-vault-probe vault secrets enable transit
  docker exec -e VAULT_ADDR=http://127.0.0.1:8200 -e VAULT_TOKEN=probe-root ix1-vault-probe vault write -f transit/keys/search-entry-seal-v1 type=ecdsa-p256
Environment overrides: IX1_VAULT_URL, IX1_VAULT_TOKEN. Prints one JSON line.
"""
import json, time, urllib.request, base64, statistics, os
url = os.environ.get("IX1_VAULT_URL", "http://127.0.0.1:18299") + "/v1/transit/sign/search-entry-seal-v1/sha2-256"
msg = base64.b64encode(os.urandom(600)).decode()
body = json.dumps({"input": msg, "key_version": 1, "marshaling_algorithm": "jws"}).encode()
def once():
    req = urllib.request.Request(url, data=body, headers={"X-Vault-Token": os.environ.get("IX1_VAULT_TOKEN", "probe-root"), "Content-Type": "application/json"})
    t = time.perf_counter()
    with urllib.request.urlopen(req) as r:
        r.read()
    return (time.perf_counter() - t) * 1000
for _ in range(20): once()
samples = sorted(once() for _ in range(500))
q = lambda p: samples[min(len(samples) - 1, int(p * len(samples)))]
print(json.dumps({"provider": "Vault Transit 1.20.4 dev, ecdsa-p256, local Docker, sequential HTTP/1.1 from host", "n": len(samples), "p50_ms": round(q(0.50), 2), "p95_ms": round(q(0.95), 2), "p99_ms": round(q(0.99), 2), "max_ms": round(samples[-1], 2), "mean_ms": round(statistics.mean(samples), 2)}))
