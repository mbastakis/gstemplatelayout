#!/bin/bash
set -e

# Change to the script directory
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

# Default namespace - can be overridden with MONITORING_NAMESPACE env var
NAMESPACE="${MONITORING_NAMESPACE:-monitoring}"
echo "Using namespace: $NAMESPACE"

# Check if dashboard file exists
DASHBOARD_FILE="dashboards/game-server-dashboard.json"
if [ ! -f "$DASHBOARD_FILE" ]; then
    echo "Error: Dashboard file $DASHBOARD_FILE not found!"
    exit 1
fi

# Verify that the dashboard has the correct datasource UIDs
PROM_UID_COUNT=$(grep -c '"uid": "prometheus"' "$DASHBOARD_FILE" || true)
LOKI_UID_COUNT=$(grep -c '"uid": "loki"' "$DASHBOARD_FILE" || true)

echo "Dashboard verification:"
echo "- Prometheus datasource references: $PROM_UID_COUNT"
echo "- Loki datasource references: $LOKI_UID_COUNT"

if [ "$PROM_UID_COUNT" -eq 0 ] || [ "$LOKI_UID_COUNT" -eq 0 ]; then
    echo "Warning: Dashboard may not have the correct datasource UIDs!"
    echo "Please check the dashboard JSON file for proper datasource configuration."
fi

# Check if Grafana is running properly
GRAFANA_READY=$(kubectl get pod -l app.kubernetes.io/name=grafana -n $NAMESPACE -o jsonpath='{.items[0].status.containerStatuses[?(@.name=="grafana")].ready}' 2>/dev/null)
if [ "$GRAFANA_READY" != "true" ]; then
    echo "Warning: Grafana pod is not ready (status: $GRAFANA_READY)"
    GRAFANA_PHASE=$(kubectl get pod -l app.kubernetes.io/name=grafana -n $NAMESPACE -o jsonpath='{.items[0].status.phase}' 2>/dev/null)
    GRAFANA_STATUS=$(kubectl get pod -l app.kubernetes.io/name=grafana -n $NAMESPACE -o jsonpath='{.items[0].status.containerStatuses[?(@.name=="grafana")].state}' 2>/dev/null)
    echo "Grafana phase: $GRAFANA_PHASE"
    echo "Grafana container status: $GRAFANA_STATUS"
    
    echo "Skipping dashboard import until Grafana is ready."
    echo "Create a ConfigMap to note the attempted import"
    kubectl create configmap grafana-game-server-dashboard -n $NAMESPACE --from-literal=imported=false --from-literal=status="grafana-not-ready" --dry-run=client -o yaml | kubectl apply -f -
    exit 0
fi

# Create the dashboard configmap regardless - this allows the deployment script to continue
echo "Creating dashboard configmap to mark import was attempted..."
kubectl create configmap grafana-game-server-dashboard -n $NAMESPACE --from-literal=imported=true --from-literal=status="import-attempted" --dry-run=client -o yaml | kubectl apply -f -

# Check if we really need to continue with import
if [ -n "$SKIP_DASHBOARD_IMPORT" ] && [ "$SKIP_DASHBOARD_IMPORT" = "true" ]; then
    echo "SKIP_DASHBOARD_IMPORT is set to true, skipping actual dashboard import."
    exit 0
fi

# Get the actual container port from the service
GRAFANA_CONTAINER_PORT=$(kubectl get svc -n $NAMESPACE prometheus-stack-grafana -o jsonpath='{.spec.ports[0].port}' 2>/dev/null || echo "80")
echo "Detected Grafana container port: $GRAFANA_CONTAINER_PORT"

# Check if port-forward already exists
PF_RUNNING=false
PORT_FORWARD_PID=""

# Setup port-forward with a timeout
echo "Setting up port-forward for Grafana (port $GRAFANA_CONTAINER_PORT in container to 3000 locally)..."
kubectl port-forward svc/prometheus-stack-grafana 3000:$GRAFANA_CONTAINER_PORT -n $NAMESPACE &
PORT_FORWARD_PID=$!
sleep 3

# Wait for port-forward to be ready with explicit timeout
MAX_RETRIES=5
RETRY_COUNT=0
CURL_TIMEOUT=10

echo "Checking if Grafana is accessible..."
while [ $RETRY_COUNT -lt $MAX_RETRIES ]; do
    if curl -s --max-time $CURL_TIMEOUT http://localhost:3000 > /dev/null; then
        echo "Successfully connected to Grafana!"
        break
    fi
    RETRY_COUNT=$((RETRY_COUNT+1))
    echo "Waiting for Grafana to be accessible... (attempt $RETRY_COUNT/$MAX_RETRIES)"
    sleep 3
    
    # If we've reached max retries, clean up and exit
    if [ $RETRY_COUNT -eq $MAX_RETRIES ]; then
        echo "Timed out waiting for Grafana to be accessible. Skipping dashboard import."
        if [ -n "$PORT_FORWARD_PID" ]; then
            kill $PORT_FORWARD_PID &>/dev/null || true
        fi
        kubectl create configmap grafana-game-server-dashboard -n $NAMESPACE --from-literal=imported=false --from-literal=status="connection-failed" --dry-run=client -o yaml | kubectl apply -f -
        exit 0
    fi
done

# Create temporary file with proper dashboard format
TEMP_DASHBOARD=$(mktemp)
echo '{
  "dashboard": ' > $TEMP_DASHBOARD
cat $DASHBOARD_FILE >> $TEMP_DASHBOARD
echo ',
  "overwrite": true,
  "folderId": 0
}' >> $TEMP_DASHBOARD

# Import dashboard
echo "Importing dashboard from $DASHBOARD_FILE..."
RESPONSE=$(curl -s --max-time $CURL_TIMEOUT -w "\n%{http_code}" -X POST http://admin:prom-operator@localhost:3000/api/dashboards/db \
  -H "Content-Type: application/json" \
  -d @$TEMP_DASHBOARD)

HTTP_CODE=$(echo "$RESPONSE" | tail -n1)
BODY=$(echo "$RESPONSE" | sed '$d')

# Clean up
rm $TEMP_DASHBOARD

# Clean up port-forward
if [ -n "$PORT_FORWARD_PID" ]; then
    echo "Cleaning up port-forward..."
    kill $PORT_FORWARD_PID &>/dev/null || true
fi

# Update the ConfigMap with import status
if [ "$HTTP_CODE" = "200" ]; then
    echo "Dashboard imported successfully!"
    echo "Dashboard URL: $(echo "$BODY" | grep -o '"url": "[^"]*"' | cut -d '"' -f 4 || echo "unknown")"
    kubectl create configmap grafana-game-server-dashboard -n $NAMESPACE --from-literal=imported=true --from-literal=status="import-success" --dry-run=client -o yaml | kubectl apply -f -
else
    echo "Warning: Error importing dashboard. HTTP code: $HTTP_CODE"
    echo "Response: $BODY"
    kubectl create configmap grafana-game-server-dashboard -n $NAMESPACE --from-literal=imported=false --from-literal=status="import-failed" --from-literal=error="$BODY" --dry-run=client -o yaml | kubectl apply -f -
fi

echo ""
echo "To access the dashboard:"
echo "1. Make sure Grafana is running: kubectl get pods -n $NAMESPACE | grep grafana"
echo "2. Run: kubectl port-forward svc/prometheus-stack-grafana 3000:$GRAFANA_CONTAINER_PORT -n $NAMESPACE"
echo "3. Open: http://localhost:3000 in your browser"
echo "4. Login with admin / prom-operator"
echo "5. Navigate to the Dashboards section" 