#!/bin/bash
set -e

# Script directory
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

# Default namespace - can be overridden with MONITORING_NAMESPACE env var
NAMESPACE="${MONITORING_NAMESPACE:-monitoring}"

# Check if force reinstall flag is set
FORCE_REINSTALL=false
if [ "$1" == "--force-reinstall" ]; then
    FORCE_REINSTALL=true
fi

# Create namespace if it doesn't exist
kubectl create namespace $NAMESPACE --dry-run=client -o yaml | kubectl apply -f -

# Check if promethus and grafana are already installed
if [ "$FORCE_REINSTALL" != true ] && \
   kubectl get deployment -n $NAMESPACE prometheus-stack-grafana &> /dev/null && \
   kubectl get deployment -n $NAMESPACE prometheus-stack-kube-prom-operator &> /dev/null; then
    echo "Prometheus stack is already installed. Skipping installation."
    echo "Use --force-reinstall flag if you want to reinstall."
    
    # Get the actual container port
    GRAFANA_CONTAINER_PORT=$(kubectl get svc -n $NAMESPACE prometheus-stack-grafana -o jsonpath='{.spec.ports[0].port}' 2>/dev/null || echo "80")
    
    # Skip to dashboard import
    echo "Checking for dashboard..."
    if ! kubectl get configmap -n $NAMESPACE grafana-game-server-dashboard &> /dev/null; then
        echo "Importing dashboard only..."
        MONITORING_NAMESPACE=$NAMESPACE ./import-dashboard.sh
    else
        echo "Dashboard already exists. Skipping import."
    fi
    
    # Display Grafana access information
    echo ""
    echo "==== MONITORING DASHBOARD ===="
    GRAFANA_URL="http://localhost:3000"  # Port-forwarded URL
    EXTERNAL_URL=""

    if [ -n "$(kubectl get svc -n $NAMESPACE prometheus-stack-grafana -o jsonpath='{.status.loadBalancer.ingress[0].ip}' 2>/dev/null)" ]; then
        EXTERNAL_URL="http://$(kubectl get svc -n $NAMESPACE prometheus-stack-grafana -o jsonpath='{.status.loadBalancer.ingress[0].ip}'):${GRAFANA_CONTAINER_PORT}"
    elif [ -n "$(kubectl get svc -n $NAMESPACE prometheus-stack-grafana -o jsonpath='{.status.loadBalancer.ingress[0].hostname}' 2>/dev/null)" ]; then
        EXTERNAL_URL="http://$(kubectl get svc -n $NAMESPACE prometheus-stack-grafana -o jsonpath='{.status.loadBalancer.ingress[0].hostname}'):${GRAFANA_CONTAINER_PORT}"
    fi

    echo "Grafana port-forwarded URL: $GRAFANA_URL (run 'kubectl port-forward svc/prometheus-stack-grafana 3000:${GRAFANA_CONTAINER_PORT} -n monitoring')"
    if [ -n "$EXTERNAL_URL" ]; then
        echo "Grafana external URL: $EXTERNAL_URL"
    fi
    echo "Grafana username: admin"
    echo "Grafana password: prom-operator"
    
    exit 0
fi

# Uninstall existing components if force reinstall flag is set
if [ "$FORCE_REINSTALL" = true ]; then
    echo "Force reinstall flag detected, removing existing monitoring stack in namespace '$NAMESPACE'..."
    
    # Ask for confirmation before deleting existing releases
    read -p "Are you sure you want to reinstall the monitoring stack? This will delete existing Prometheus and Grafana. [y/N] " -n 1 -r
    echo
    if [[ ! $REPLY =~ ^[Yy]$ ]]; then
        echo "Reinstallation aborted."
        exit 0
    fi
    
    # Uninstall existing releases
    echo "Uninstalling existing Helm releases..."
    helm uninstall prometheus-stack -n $NAMESPACE 2>/dev/null || true
    helm uninstall loki-stack -n $NAMESPACE 2>/dev/null || true
    
    # Wait for resources to be deleted
    echo "Waiting for resources to be deleted..."
    sleep 10
fi

# Add Helm repositories
echo "Adding Helm repositories..."
helm repo add prometheus-community https://prometheus-community.github.io/helm-charts || true
helm repo add grafana https://grafana.github.io/helm-charts || true
helm repo update

# Install Prometheus Stack with increased timeout
echo "Installing Prometheus Stack..."
helm upgrade --install prometheus-stack prometheus-community/kube-prometheus-stack \
  --namespace $NAMESPACE \
  --set grafana.service.type=LoadBalancer \
  --set grafana.service.port=80 \
  --set grafana.adminPassword=prom-operator \
  --timeout 10m \
  --atomic

# Install Loki Stack with increased timeout
echo "Installing Loki Stack..."
helm upgrade --install loki-stack grafana/loki-stack \
  --namespace $NAMESPACE \
  --set grafana.enabled=false \
  --set prometheus.enabled=false \
  --set loki.persistence.enabled=true \
  --set loki.persistence.size=10Gi \
  --timeout 10m \
  --atomic

# Wait for all pods to be ready
echo "Waiting for all pods to be ready..."
kubectl wait --for=condition=ready pod -l app.kubernetes.io/name=grafana -n $NAMESPACE --timeout=300s || true
sleep 5

# Check if grafana is running before trying port-forward
if ! kubectl get pod -l app.kubernetes.io/name=grafana -n $NAMESPACE | grep -q "Running"; then
    echo "Grafana is not running yet. Skipping port-forward and dashboard import."
    echo "You can import the dashboard later with: ./import-dashboard.sh"
    exit 0
fi

# Get the actual container port
GRAFANA_CONTAINER_PORT=$(kubectl get svc -n $NAMESPACE prometheus-stack-grafana -o jsonpath='{.spec.ports[0].port}' 2>/dev/null || echo "80")
echo "Detected Grafana container port: $GRAFANA_CONTAINER_PORT"

# Kill any existing port-forwards
echo "Cleaning up any existing port-forwards..."
pkill -f "kubectl port-forward.*grafana" &>/dev/null || true
sleep 2

# Set up port-forward for Grafana
echo "Setting up port-forward for Grafana (port $GRAFANA_CONTAINER_PORT in container to 3000 locally)..."
kubectl port-forward svc/prometheus-stack-grafana 3000:$GRAFANA_CONTAINER_PORT -n $NAMESPACE &
PF_PID=$!

# Wait for port-forward to be ready
echo "Waiting for port-forward..."
sleep 5
MAX_RETRIES=10
RETRY_COUNT=0

while ! curl -s http://localhost:3000 > /dev/null && [ $RETRY_COUNT -lt $MAX_RETRIES ]; do
    echo "Waiting for Grafana to be accessible... (attempt $((RETRY_COUNT+1))/$MAX_RETRIES)"
    sleep 3
    RETRY_COUNT=$((RETRY_COUNT+1))
done

if [ $RETRY_COUNT -eq $MAX_RETRIES ]; then
    echo "Timed out waiting for Grafana to be accessible. Skipping datasource and dashboard setup."
    kill $PF_PID &>/dev/null || true
    exit 1
fi

# Add datasources using Grafana API
echo "Adding Prometheus datasource..."
curl -s -X POST http://admin:prom-operator@localhost:3000/api/datasources \
  -H "Content-Type: application/json" \
  --data-binary '{
    "name":"Prometheus",
    "type":"prometheus",
    "uid":"prometheus",
    "url":"http://prometheus-stack-kube-prom-prometheus:9090",
    "access":"proxy",
    "isDefault":true
  }'

echo "Adding Loki datasource..."
curl -s -X POST http://admin:prom-operator@localhost:3000/api/datasources \
  -H "Content-Type: application/json" \
  --data-binary '{
    "name":"Loki",
    "type":"loki",
    "uid":"loki",
    "url":"http://loki-stack:3100",
    "access":"proxy",
    "isDefault":false
  }'

# Import dashboard
echo "Importing dashboard..."
MONITORING_NAMESPACE=$NAMESPACE ./import-dashboard.sh

# Kill port-forward
echo "Cleaning up port-forward..."
kill $PF_PID &>/dev/null || true

# Create a ConfigMap to mark that the dashboard has been imported
kubectl create configmap grafana-game-server-dashboard -n $NAMESPACE --from-literal=imported=true --dry-run=client -o yaml | kubectl apply -f -

# Display Grafana access information
echo ""
echo "==== MONITORING DASHBOARD ===="
GRAFANA_URL="http://localhost:3000"  # Port-forwarded URL
EXTERNAL_URL=""

if [ -n "$(kubectl get svc -n $NAMESPACE prometheus-stack-grafana -o jsonpath='{.status.loadBalancer.ingress[0].ip}' 2>/dev/null)" ]; then
    EXTERNAL_URL="http://$(kubectl get svc -n $NAMESPACE prometheus-stack-grafana -o jsonpath='{.status.loadBalancer.ingress[0].ip}'):${GRAFANA_CONTAINER_PORT}"
elif [ -n "$(kubectl get svc -n $NAMESPACE prometheus-stack-grafana -o jsonpath='{.status.loadBalancer.ingress[0].hostname}' 2>/dev/null)" ]; then
    EXTERNAL_URL="http://$(kubectl get svc -n $NAMESPACE prometheus-stack-grafana -o jsonpath='{.status.loadBalancer.ingress[0].hostname}'):${GRAFANA_CONTAINER_PORT}"
fi

echo "Grafana port-forwarded URL: $GRAFANA_URL (run 'kubectl port-forward svc/prometheus-stack-grafana 3000:${GRAFANA_CONTAINER_PORT} -n $NAMESPACE')"
if [ -n "$EXTERNAL_URL" ]; then
    echo "Grafana external URL: $EXTERNAL_URL"
fi
echo "Grafana username: admin"
echo "Grafana password: prom-operator"

# Verify Prometheus and Loki services
echo ""
echo "==== VERIFYING SERVICES ===="
echo "Prometheus service:"
kubectl get svc -n $NAMESPACE prometheus-stack-kube-prom-prometheus
echo ""
echo "Loki service:"
kubectl get svc -n $NAMESPACE loki-stack

echo ""
echo "Monitoring stack installed successfully in namespace: $NAMESPACE"
echo "To use a different namespace, set the MONITORING_NAMESPACE environment variable before running this script."

echo ""
echo "==== TROUBLESHOOTING ===="
echo "If you don't see datasources in Grafana:"
echo "1. Try verifying the service URLs:"
echo "   kubectl get svc -n $NAMESPACE | grep prometheus"
echo "   kubectl get svc -n $NAMESPACE | grep loki"
echo "2. Manually add datasources with these URLs:"
echo "   Prometheus: http://prometheus-stack-kube-prom-prometheus:9090"
echo "   Loki: http://loki-stack:3100"
echo "3. To verify datasources via API:"
echo "   curl -s http://admin:prom-operator@localhost:3000/api/datasources | jq" 