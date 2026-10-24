# Deploying to Kubernetes

Tested on a single-node k3s cluster (amd64). Everything lives in the `lora-hotswap` namespace and
touches nothing else.

```bash
# 1. models + adapters into a PVC (run from the repo root, after tools/acquire and tools/convert)
deploy/k8s/load-assets.sh

```

```bash
# 2. API keys: one per tenant plus an admin key. Keep the plain keys; only digests go in the cluster.
for t in SQL ALPACA TLDR CLINICAL ADMIN; do export ${t}_KEY=$(openssl rand -hex 24); done
dig() { printf %s "$1" | shasum -a 256 | cut -d' ' -f1; }
kubectl -n lora-hotswap create secret generic lora-hotswap-keys \
  --from-literal=sql="$(dig "$SQL_KEY")" --from-literal=alpaca="$(dig "$ALPACA_KEY")" \
  --from-literal=tldr="$(dig "$TLDR_KEY")" --from-literal=clinical="$(dig "$CLINICAL_KEY")" \
  --from-literal=admin="$(dig "$ADMIN_KEY")"

# 3. deploy
kubectl apply -f deploy/k8s/config.yaml -f deploy/k8s/service.yaml -f deploy/k8s/deployment.yaml
kubectl -n lora-hotswap rollout status deploy/lora-hotswap --timeout=15m
```

The first start builds the app in an init container (a few minutes). Reach it with
`kubectl -n lora-hotswap port-forward svc/lora-hotswap 8080:80`, then:

```bash
curl -s localhost:8080/healthz
curl -s localhost:8080/v1/generate -H "authorization: Bearer $SQL_KEY" -H 'content-type: application/json' \
  -d '{"tenant":"sql","prompt":"Write a SQL query that returns the five customers with the highest total order value. Answer with the query only."}'
```

Pin `GIT_REF` in `deployment.yaml` to a commit for reproducible deploys. Add an Ingress or
IngressRoute of your own for outside access, with TLS: keys travel in a header.
