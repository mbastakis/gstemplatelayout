#!/bin/bash
set -e

# Change to the script directory
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

# Default namespace - can be overridden with NAMESPACE env var
NAMESPACE="${NAMESPACE:-monitoring}"
echo "Using namespace: $NAMESPACE"

# Check if dashboard file exists
DASHBOARD_FILE="dashboards/game-server-dashboard.json"
if [ ! -f "$DASHBOARD_FILE" ]; then
    echo "Error: Dashboard file $DASHBOARD_FILE not found!"
    exit 1
fi

# Verify that the dashboard has the correct datasource UIDs
PROM_UID_COUNT=$(grep -c '"uid": "prometheus"' "$DASHBOARD_FILE")
LOKI_UID_COUNT=$(grep -c '"uid": "loki"' "$DASHBOARD_FILE")

echo "Dashboard verification:"
echo "- Prometheus datasource references: $PROM_UID_COUNT"
echo "- Loki datasource references: $LOKI_UID_COUNT"

if [ "$PROM_UID_COUNT" -eq 0 ] || [ "$LOKI_UID_COUNT" -eq 0 ]; then
    echo "Warning: Dashboard may not have the correct datasource UIDs!"
    echo "Please check the dashboard JSON file for proper datasource configuration."
fi

# Wait for Grafana to be ready
echo "Waiting for Grafana to be ready..."
kubectl wait --for=condition=ready pod -l app.kubernetes.io/name=grafana -n $NAMESPACE --timeout=300s

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
RESPONSE=$(curl -s -w "\n%{http_code}" -X POST http://admin:prom-operator@localhost:3000/api/dashboards/db \
  -H "Content-Type: application/json" \
  -d @$TEMP_DASHBOARD)

HTTP_CODE=$(echo "$RESPONSE" | tail -n1)
BODY=$(echo "$RESPONSE" | sed '$d')

# Clean up
rm $TEMP_DASHBOARD

# Check response
if [ "$HTTP_CODE" = "200" ]; then
    echo "Dashboard imported successfully!"
    echo "Dashboard URL: $(echo "$BODY" | grep -o '"url": "[^"]*"' | cut -d '"' -f 4)"
else
    echo "Error importing dashboard. HTTP code: $HTTP_CODE"
    echo "Response: $BODY"
    exit 1
fi

echo ""
echo "To verify that Prometheus and Loki are working correctly, try:"
echo "1. kubectl get pods -n $NAMESPACE | grep loki"
echo "2. kubectl get svc -n $NAMESPACE | grep prometheus"
echo "3. Check Grafana's datasource configuration: http://localhost:3000/datasources" 