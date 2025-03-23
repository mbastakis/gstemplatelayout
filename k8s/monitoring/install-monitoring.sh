#!/bin/bash
set -e

NAMESPACE="monitoring"

# Check if Helm is installed
if ! command -v helm &> /dev/null; then
  echo "Helm is not installed. Installing via snap..."
  sudo snap install helm --classic
fi

# Create namespace if it doesn't exist
kubectl create namespace $NAMESPACE --dry-run=client -o yaml | kubectl apply -f -

# Add Helm repositories
echo "Adding Helm repositories..."
helm repo add prometheus-community https://prometheus-community.github.io/helm-charts || true
helm repo add grafana https://grafana.github.io/helm-charts || true
helm repo update

# Install Prometheus stack (includes Prometheus, Alertmanager, and node-exporter)
echo "Installing Prometheus stack..."
helm upgrade --install prometheus prometheus-community/kube-prometheus-stack \
  --namespace $NAMESPACE \
  --set grafana.enabled=false \
  --wait --timeout 5m

# Install Loki
echo "Installing Loki..."
helm upgrade --install loki grafana/loki-stack \
  --namespace $NAMESPACE \
  --set grafana.enabled=false \
  --set prometheus.enabled=false \
  --set loki.persistence.enabled=true \
  --set loki.persistence.size=10Gi \
  --wait --timeout 5m

# Install Grafana with preconfigured datasources
echo "Installing Grafana..."
helm upgrade --install grafana grafana/grafana \
  --namespace $NAMESPACE \
  --values $(dirname "$0")/grafana-values.yaml \
  --wait --timeout 5m

# Get Grafana admin password
echo "Grafana installed!"
echo "Grafana admin password:"
kubectl get secret --namespace $NAMESPACE grafana -o jsonpath="{.data.admin-password}" | base64 --decode
echo

# Get Grafana URL
echo "Grafana URL:"
kubectl get svc --namespace $NAMESPACE grafana -o jsonpath="{.status.loadBalancer.ingress[0].ip}"
echo ":3000"

# Create a sample Kubernetes monitoring dashboard
echo "Creating Kubernetes monitoring dashboard..."
DASHBOARD_ID="11074" # A decent Kubernetes monitoring dashboard
DASHBOARD_URL="https://grafana.com/api/dashboards/${DASHBOARD_ID}/revisions/1/download"

# Make sure the dashboards directory exists
kubectl -n $NAMESPACE exec $(kubectl -n $NAMESPACE get pods -l "app.kubernetes.io/name=grafana" -o jsonpath="{.items[0].metadata.name}") -- \
  mkdir -p /var/lib/grafana/dashboards/default || true

# Download dashboard and copy it to Grafana pod
wget -qO /tmp/dashboard.json $DASHBOARD_URL 2>/dev/null || \
  curl -s -o /tmp/dashboard.json $DASHBOARD_URL

kubectl cp /tmp/dashboard.json $NAMESPACE/$(kubectl -n $NAMESPACE get pods -l "app.kubernetes.io/name=grafana" -o jsonpath="{.items[0].metadata.name}"):/var/lib/grafana/dashboards/default/kubernetes-dashboard.json

echo "Monitoring stack installed successfully!" 