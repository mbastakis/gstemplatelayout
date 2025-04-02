# FluxCD Migration Test Plan

This document outlines the testing procedure to verify that the FluxCD-based GitOps deployment works correctly after migrating from the manual shell script approach.

## Prerequisites

- Kubernetes cluster is up and running
- kubectl is configured to access the cluster
- FluxCD is installed and bootstrapped on the cluster
- Git repository is configured as the source for FluxCD

## Test Cases

### 1. FluxCD Initialization Tests

| ID | Test Case | Expected Result | Status |
|----|-----------|----------------|--------|
| 1.1 | Verify FluxCD components are running | All Flux controllers show "Running" state | - |
| 1.2 | Verify GitRepository source has synced | Status shows "Ready" and "Fetched revision: main/{commit}" | - |
| 1.3 | Verify Kustomizations are applied | Both infrastructure and applications Kustomizations show "Ready" | - |

```bash
# Verification commands
flux check
flux get sources git
flux get kustomizations
```

### 2. Infrastructure Component Tests

| ID | Test Case | Expected Result | Status |
|----|-----------|----------------|--------|
| 2.1 | Verify Metrics Server deployment | Metrics Server pod is running in kube-system namespace | - |
| 2.2 | Verify Prometheus Stack deployment | Prometheus and Grafana pods are running in monitoring namespace | - |
| 2.3 | Verify Loki Stack deployment | Loki pod is running in monitoring namespace | - |
| 2.4 | Verify Grafana dashboard is available | Grafana dashboard accessible and shows game server metrics | - |

```bash
# Verification commands
kubectl get pods -n kube-system | grep metrics-server
kubectl get pods -n monitoring
kubectl port-forward svc/prometheus-stack-grafana 3000:80 -n monitoring
# Access Grafana at http://localhost:3000 with admin/prom-operator
```

### 3. Application Component Tests

| ID | Test Case | Expected Result | Status |
|----|-----------|----------------|--------|
| 3.1 | Verify Master Server deployment | Master Server pod is running in gs-template namespace | - |
| 3.2 | Verify Game Server deployment | Game Server pod is running and can connect to Master Server | - |
| 3.3 | Verify Client Simulator deployment | Client Simulator pod is running and can connect to Master Server | - |
| 3.4 | Verify Game Server HPA | HPA is active and configured for CPU/memory scaling | - |

```bash
# Verification commands
kubectl get pods -n gs-template
kubectl logs -l app=game-server -n gs-template
kubectl get hpa -n gs-template
```

### 4. Scaling Tests

| ID | Test Case | Expected Result | Status |
|----|-----------|----------------|--------|
| 4.1 | Scale client simulator | Client simulator scales to 5 replicas | - |
| 4.2 | Verify load increase | Game server logs show increased connections | - |
| 4.3 | Verify HPA scaling | Game server pods automatically scale up | - |
| 4.4 | Scale down client simulator | Client simulator scales down to 1 replica | - |
| 4.5 | Verify HPA scale down | Game server pods automatically scale down after stabilization period | - |

```bash
# Verification commands
kubectl scale deployment client-simulator -n gs-template --replicas=5
kubectl get hpa game-server-hpa -n gs-template --watch
kubectl logs -l app=game-server -n gs-template
```

### 5. GitOps Flow Tests

| ID | Test Case | Expected Result | Status |
|----|-----------|----------------|--------|
| 5.1 | Update image tag in Git | Push change to Git, FluxCD detects and applies the change | - |
| 5.2 | Add a new environment variable | Push change to Git, pod gets recreated with new env var | - |
| 5.3 | Rollback a change | Revert commit, deployment reverts to previous state | - |

```bash
# Before test, prepare a change in a branch and merge it when ready
git checkout -b test-update
# Make changes to flux/apps/overlays/dev/registry-patch.yaml or similar
git commit -am "Test update for GitOps verification"
git push
git checkout main
git merge test-update
git push
```

### 6. Monitoring Integration Tests

| ID | Test Case | Expected Result | Status |
|----|-----------|----------------|--------|
| 6.1 | Verify Prometheus metrics | Game server metrics are being collected by Prometheus | - |
| 6.2 | Verify Grafana dashboard | Game server dashboard shows real-time metrics | - |
| 6.3 | Verify Loki logs | Game server logs are accessible via Loki in Grafana | - |

## Test Results

Fill in the "Status" column for each test case with:
- ✅ PASS
- ❌ FAIL
- ⚠️ PARTIAL
- 🔄 NOT TESTED

Document any issues encountered and their resolutions.

## Comparison with Previous Deployment Method

After completing the tests, compare the results with the previous script-based deployment:

1. Deployment reliability: Were there fewer errors/failures?
2. Deployment speed: Was the process faster or slower?
3. Maintenance effort: Is the new approach easier to maintain?
4. Recovery from failures: How easy was it to identify and fix issues?

## Sign-off

When all tests are passing, obtain sign-off from:
- [ ] DevOps Engineer
- [ ] Development Team Lead
- [ ] QA Engineer

Test completion date: _________ 