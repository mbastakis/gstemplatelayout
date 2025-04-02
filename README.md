# Game Server Template with FluxCD GitOps

## Overview

This repository contains a template for a game server infrastructure, now managed via GitOps using FluxCD. The project includes:

- Master Server: Coordinates game sessions
- Game Server: Handles gameplay for specific sessions
- Client Simulator: Simulates client connections for testing

## Getting Started with FluxCD GitOps

### Prerequisites

- Kubernetes cluster
- kubectl configured for your cluster
- [Flux CLI](https://fluxcd.io/docs/installation/) installed
- Git repository for your configuration

### Installation

1. **Bootstrap FluxCD on your cluster:**

```bash
# Export your GitHub personal access token
export GITHUB_TOKEN=<your-token>

# Bootstrap FluxCD (replace with your GitHub username and repository)
flux bootstrap github \
  --owner=<github-username> \
  --repository=gs-template \
  --path=flux/clusters/default \
  --personal
```

2. **Configure the registry URL:**

Edit `flux/apps/overlays/dev/registry-patch.yaml` to update the image URLs to match your container registry.

3. **Commit and push changes:**

```bash
git add .
git commit -m "Update registry URL"
git push
```

FluxCD will automatically detect changes and apply them to your cluster.

## Monitoring

The monitoring stack is deployed as part of the infrastructure components and includes:

- Prometheus for metrics collection
- Grafana for visualization
- Loki for log aggregation

### Accessing Dashboards

```bash
# Port-forward Grafana service
kubectl port-forward svc/prometheus-stack-grafana 3000:80 -n monitoring
```

Then open http://localhost:3000 in your browser (default credentials: admin/prom-operator).

## Manual Container Building (Development Only)

For development purposes, you can build and push container images manually:

```bash
# Start local registry if not running
docker run -d -p 5000:5000 --restart=always --name registry registry:2

# Build and push images
cd src
docker build -t localhost:5000/master-server:latest -f MasterServer/Dockerfile .
docker push localhost:5000/master-server:latest

docker build -t localhost:5000/game-server:latest -f GameServer/Dockerfile .
docker push localhost:5000/game-server:latest

docker build -t localhost:5000/client-simulator:latest -f ClientSimulator/Dockerfile .
docker push localhost:5000/client-simulator:latest
```

## Testing the Deployment

After FluxCD has deployed all components:

1. Verify all deployments are running:
```bash
kubectl get pods -n gs-template
```

2. Scale the client simulator to trigger game server scaling:
```bash
kubectl scale deployment client-simulator -n gs-template --replicas=5
```

3. Monitor the game server HPA:
```bash
kubectl get hpa game-server-hpa -n gs-template --watch
```

## Troubleshooting

If you encounter issues with FluxCD, check the status of Flux components:

```bash
# Check Flux controllers
flux check

# View the Git repository source status
flux get sources git

# View the status of Kustomizations
flux get kustomizations

# View logs from Flux controllers
kubectl logs -n flux-system deployment/source-controller
kubectl logs -n flux-system deployment/kustomize-controller
```

## Migrating from Script-Based Deployment

This project has been migrated from a script-based deployment approach to GitOps using FluxCD. The old shell scripts (`build-and-deploy.sh`, `install-metrics-server.sh`, etc.) are no longer needed, as all deployment is now managed declaratively through Git.
