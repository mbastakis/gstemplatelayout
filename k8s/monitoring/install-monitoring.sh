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

# Uninstall existing components if force reinstall flag is set
if [ "$FORCE_REINSTALL" = true ]; then
    echo "Force reinstall flag detected, removing existing monitoring stack in namespace '$NAMESPACE'..."
    
    # Ask for confirmation before deleting the namespace
    read -p "Are you sure you want to delete the '$NAMESPACE' namespace? This will delete ALL resources in this namespace. [y/N] " -n 1 -r
    echo
    if [[ ! $REPLY =~ ^[Yy]$ ]]; then
        echo "Namespace deletion aborted. Will try to reinstall components without namespace deletion."
        
        # Uninstall existing releases without deleting namespace
        echo "Uninstalling existing Helm releases..."
        helm uninstall prometheus-stack -n $NAMESPACE 2>/dev/null || true
        helm uninstall loki-stack -n $NAMESPACE 2>/dev/null || true
    else
        # Uninstall existing releases
        echo "Uninstalling existing Helm releases..."
        helm uninstall prometheus-stack -n $NAMESPACE 2>/dev/null || true
        helm uninstall loki-stack -n $NAMESPACE 2>/dev/null || true
        
        # Delete the namespace to clean up everything
        echo "Deleting namespace..."
        kubectl delete namespace $NAMESPACE 2>/dev/null || true
        
        # Wait for namespace to be deleted
        echo "Waiting for namespace to be deleted..."
        kubectl wait --for=delete namespace/$NAMESPACE --timeout=120s 2>/dev/null || true
        
        # Create the namespace again
        echo "Creating namespace..."
        kubectl create namespace $NAMESPACE
    fi
fi

# Create namespace if it doesn't exist
kubectl create namespace $NAMESPACE --dry-run=client -o yaml | kubectl apply -f -

# Add Helm repositories
echo "Adding Helm repositories..."
helm repo add prometheus-community https://prometheus-community.github.io/helm-charts || true
helm repo add grafana https://grafana.github.io/helm-charts || true
helm repo update

# Install Prometheus Stack
echo "Installing Prometheus Stack..."
helm upgrade --install prometheus-stack prometheus-community/kube-prometheus-stack \
  --namespace $NAMESPACE \
  --set grafana.service.type=LoadBalancer \
  --set grafana.service.port=80 \
  --set grafana.adminPassword=prom-operator \
  --wait --timeout 5m

# Install Loki Stack
echo "Installing Loki Stack..."
helm upgrade --install loki-stack grafana/loki-stack \
  --namespace $NAMESPACE \
  --set grafana.enabled=false \
  --set prometheus.enabled=false \
  --set loki.persistence.enabled=true \
  --set loki.persistence.size=10Gi \
  --wait --timeout 5m

# Wait for all pods to be ready
echo "Waiting for all pods to be ready..."
kubectl wait --for=condition=ready pod -l app.kubernetes.io/name=grafana -n $NAMESPACE --timeout=300s || true
sleep 5

# Set up port-forward for Grafana
echo "Setting up port-forward for Grafana..."
kubectl port-forward svc/prometheus-stack-grafana 3000:80 -n $NAMESPACE &
PF_PID=$!

# Wait for port-forward to be ready
echo "Waiting for port-forward..."
sleep 5

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
NAMESPACE=$NAMESPACE ./import-dashboard.sh

# Kill port-forward
echo "Cleaning up port-forward..."
kill $PF_PID

# Display Grafana access information
echo ""
echo "==== MONITORING DASHBOARD ===="
GRAFANA_PORT=$(kubectl get svc -n $NAMESPACE prometheus-stack-grafana -o jsonpath='{.spec.ports[0].port}')
GRAFANA_URL="http://localhost:3000"  # Port-forwarded URL
EXTERNAL_URL=""

if [ -n "$(kubectl get svc -n $NAMESPACE prometheus-stack-grafana -o jsonpath='{.status.loadBalancer.ingress[0].ip}' 2>/dev/null)" ]; then
    EXTERNAL_URL="http://$(kubectl get svc -n $NAMESPACE prometheus-stack-grafana -o jsonpath='{.status.loadBalancer.ingress[0].ip}'):${GRAFANA_PORT}"
elif [ -n "$(kubectl get svc -n $NAMESPACE prometheus-stack-grafana -o jsonpath='{.status.loadBalancer.ingress[0].hostname}' 2>/dev/null)" ]; then
    EXTERNAL_URL="http://$(kubectl get svc -n $NAMESPACE prometheus-stack-grafana -o jsonpath='{.status.loadBalancer.ingress[0].hostname}'):${GRAFANA_PORT}"
fi

echo "Grafana port-forwarded URL: $GRAFANA_URL (available while this script runs)"
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