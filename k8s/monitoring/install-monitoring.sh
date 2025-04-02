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
    echo "Grafana is now accessible at: http://localhost:3000"
    echo "You can also access it at: http://localhost:30080 (via nodePort)"
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
echo "Using values file at: $SCRIPT_DIR/grafana-values.yaml"
if [ ! -f "$SCRIPT_DIR/grafana-values.yaml" ]; then
  echo "ERROR: Values file not found at $SCRIPT_DIR/grafana-values.yaml"
  echo "Current directory: $(pwd)"
  echo "Script directory: $SCRIPT_DIR"
  exit 1
fi

helm upgrade --install prometheus-stack prometheus-community/kube-prometheus-stack \
  --namespace $NAMESPACE \
  --values "$SCRIPT_DIR/grafana-values.yaml" \
  --timeout 3m \
  --set kubeScheduler.enabled=false \
  --set kubeControllerManager.enabled=false \
  --set kubeEtcd.enabled=false \
  --set grafana.sidecar.datasources.enabled=false \
  --set grafana.sidecar.dashboards.enabled=true

# Install Loki Stack with increased timeout
echo "Installing Loki Stack..."
helm upgrade --install loki-stack grafana/loki-stack \
  --namespace $NAMESPACE \
  --set grafana.enabled=false \
  --set prometheus.enabled=false \
  --set loki.persistence.enabled=true \
  --set loki.persistence.size=10Gi \
  --timeout 3m

# Wait for all pods to be ready
echo "Waiting for all pods to be ready..."
echo "Checking if Grafana pods exist..."
GRAFANA_PODS=$(kubectl get pods -l app.kubernetes.io/name=grafana -n $NAMESPACE --no-headers 2>/dev/null | wc -l)
if [ "$GRAFANA_PODS" -gt 0 ]; then
    echo "Found $GRAFANA_PODS Grafana pods, waiting up to 60s for readiness..."
    kubectl wait --for=condition=ready pod -l app.kubernetes.io/name=grafana -n $NAMESPACE --timeout=60s || echo "Timed out waiting for Grafana pods"
else
    echo "No Grafana pods found yet, continuing anyway..."
fi
sleep 5

# Check if grafana is running before trying port-forward
echo "Checking if Grafana service is available..."
if ! kubectl get svc prometheus-stack-grafana -n $NAMESPACE &>/dev/null; then
    echo "Grafana service not found yet. Skipping port-forward and dashboard import."
    echo "You can import the dashboard later with: ./import-dashboard.sh"
    exit 0
fi

# Try to find Grafana pod for at most 90 seconds
MAX_ATTEMPTS=6
ATTEMPT=0
GRAFANA_RUNNING=false

while [ $ATTEMPT -lt $MAX_ATTEMPTS ]; do
    if kubectl get pod -l app.kubernetes.io/name=grafana -n $NAMESPACE | grep -q "Running"; then
        GRAFANA_RUNNING=true
        break
    fi
    ATTEMPT=$((ATTEMPT+1))
    echo "Waiting for Grafana pod to be in Running state... (attempt $ATTEMPT/$MAX_ATTEMPTS)"
    sleep 15
done

if [ "$GRAFANA_RUNNING" != "true" ]; then
    echo "Grafana pod not in Running state after $MAX_ATTEMPTS attempts. Skipping port-forward and dashboard import."
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

# Import dashboard
echo "Importing dashboard..."
MONITORING_NAMESPACE=$NAMESPACE ./import-dashboard.sh

# Kill port-forward
echo "Cleaning up port-forward..."
kill $PF_PID &>/dev/null || true

# Create a ConfigMap to mark that the dashboard has been imported
kubectl create configmap grafana-game-server-dashboard -n $NAMESPACE --from-literal=imported=true --dry-run=client -o yaml | kubectl apply -f -

# Start persistent port-forward in the background
echo "Starting persistent port-forward for Grafana..."
nohup kubectl port-forward svc/prometheus-stack-grafana 3000:$GRAFANA_CONTAINER_PORT -n $NAMESPACE > /tmp/grafana-port-forward.log 2>&1 &
PORT_FORWARD_PERSISTENT_PID=$!
echo "Port-forward started with PID: $PORT_FORWARD_PERSISTENT_PID"
echo "You can kill it later with: kill $PORT_FORWARD_PERSISTENT_PID"
echo "Log file: /tmp/grafana-port-forward.log"

# Display Grafana access information
echo ""
echo "==== MONITORING DASHBOARD ===="
echo "Grafana is now accessible at: http://localhost:3000"
echo "You can also access it at: http://localhost:30080 (via nodePort)"
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