#!/usr/bin/env bash
# Copy the acquired + converted models/ and adapters/ into the cluster's PVC.
# Run from the repository root after `tools/acquire` and `tools/convert`.
set -euo pipefail
NS=lora-hotswap
for d in models adapters; do [ -d "$d" ] || { echo "missing ./$d: run tools/acquire and tools/convert first" >&2; exit 1; }; done

kubectl apply -f deploy/k8s/namespace.yaml -f deploy/k8s/storage.yaml
kubectl -n "$NS" delete pod assets-loader --ignore-not-found >/dev/null
kubectl -n "$NS" apply -f - <<'POD'
apiVersion: v1
kind: Pod
metadata: { name: assets-loader, namespace: lora-hotswap }
spec:
  restartPolicy: Never
  securityContext: { runAsUser: 10001, fsGroup: 10001 }
  containers:
    - name: loader
      image: busybox:1.37
      command: ["sleep", "3600"]
      volumeMounts: [{ name: data, mountPath: /data }]
  volumes: [{ name: data, persistentVolumeClaim: { claimName: lora-hotswap-data } }]
POD
kubectl -n "$NS" wait --for=condition=Ready pod/assets-loader --timeout=180s
# Only what the server reads: no .venv, no raw downloads of unused files.
tar -cf - models adapters | kubectl -n "$NS" exec -i assets-loader -- tar -xf - -C /data
kubectl -n "$NS" exec assets-loader -- du -sh /data/models /data/adapters
kubectl -n "$NS" delete pod assets-loader
