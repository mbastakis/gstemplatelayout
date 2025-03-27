# Metrics Server

This directory contains the installation script for the Kubernetes Metrics Server, which is required for the Horizontal Pod Autoscaler (HPA) to function properly.

## Files

- `install-metrics-server.sh`: Script to install metrics-server using Helm

## Usage

The metrics server can be installed by running:

```bash
./install-metrics-server.sh
```

This script will:
1. Check if Helm is installed and install it if needed
2. Check if the metrics-server is already deployed
3. Install metrics-server if it's not already present

## Why Metrics Server

The Kubernetes Metrics Server is essential for:

- Horizontal Pod Autoscaler (HPA): Allows automatic scaling based on CPU and memory usage
- `kubectl top` commands: Enables monitoring of resource usage in pods and nodes
- Custom metrics API: Provides a foundation for more advanced autoscaling mechanisms

## Configuration

The metrics-server is configured with the `--kubelet-insecure-tls` flag, which is typically required for development environments like Docker Desktop. This allows the metrics-server to communicate with kubelets without requiring valid certificates.

## Verification

You can verify that the metrics-server is working correctly by running:

```bash
kubectl get apiservices v1beta1.metrics.k8s.io
```

Or by checking that the HPA can get metrics:

```bash
kubectl get hpa
```

The output should show CPU and memory percentages rather than `<unknown>`. 