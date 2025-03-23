#!/bin/bash
set -e

# Check if Helm is installed
if ! command -v helm &> /dev/null; then
  echo "Helm is not installed. Installing via snap..."
  sudo snap install helm --classic
fi

# Check if metrics-server is already installed
if ! kubectl get deployment metrics-server -n kube-system &> /dev/null; then
  echo "Installing metrics-server for HPA functionality..."
  
  # Add the metrics-server helm repo if not already added
  helm repo add metrics-server https://kubernetes-sigs.github.io/metrics-server/ || true
  helm repo update
  
  # Install metrics-server with kubelet-insecure-tls flag for development environments
  helm upgrade --install metrics-server metrics-server/metrics-server \
    --namespace kube-system \
    --set args={--kubelet-insecure-tls} \
    --wait --timeout 2m
    
  echo "Metrics server installed successfully!"
else
  echo "Metrics-server is already installed, skipping installation."
fi 