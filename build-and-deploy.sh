#!/bin/bash
set -e

# Check if Helm is installed
if ! command -v helm &> /dev/null; then
  echo "Helm is not installed. Installing via snap..."
  sudo snap install helm --classic
fi

# Check if .NET SDK is installed (for verification only)
if ! command -v dotnet &> /dev/null; then
  echo "Warning: .NET is not installed. While not needed for deployment, it's required for development."
  echo "You can install .NET 8 SDK from: https://dotnet.microsoft.com/download/dotnet/8.0"
fi

# Configuration
REGISTRY_NAME="localhost:5000"  # Change this if needed
NAMESPACE="gs-template"
MONITORING_NAMESPACE="monitoring"
SKIP_MONITORING="${SKIP_MONITORING:-false}"

# Check if the local Docker registry is running
echo "Checking if the local Docker registry at $REGISTRY_NAME is available..."
if ! curl -s "http://$REGISTRY_NAME/v2/_catalog" > /dev/null; then
  echo "Local Docker registry not found. Starting a new local registry..."
  docker run -d -p 5000:5000 --restart=always --name registry registry:2
else
  echo "Local Docker registry is running."
fi

cd src

# Build and push Docker images
echo "Building and pushing Docker images..."

# Master Server
echo "Building Master Server..."
docker build -t $REGISTRY_NAME/master-server:latest -f MasterServer/Dockerfile .
docker push $REGISTRY_NAME/master-server:latest

# Game Server
echo "Building Game Server..."
docker build -t $REGISTRY_NAME/game-server:latest -f GameServer/Dockerfile .
docker push $REGISTRY_NAME/game-server:latest

# Client Simulator
echo "Building Client Simulator..."
docker build -t $REGISTRY_NAME/client-simulator:latest -f ClientSimulator/Dockerfile .
docker push $REGISTRY_NAME/client-simulator:latest

cd ..

# Deploy to Kubernetes
echo "Deploying to Kubernetes..."

# Create namespace if it doesn't exist
kubectl create namespace $NAMESPACE --dry-run=client -o yaml | kubectl apply -f -

# Install metrics-server for HPA to work properly if not already installed
if ! kubectl get deployment metrics-server -n kube-system &> /dev/null; then
  echo "Setting up metrics-server..."
  chmod +x k8s/metrics-server/install-metrics-server.sh
  ./k8s/metrics-server/install-metrics-server.sh
else
  echo "Metrics-server is already installed, skipping installation."
fi

# Check if monitoring needs to be installed or updated
if [ "$SKIP_MONITORING" != "true" ]; then
  if ! kubectl get namespace $MONITORING_NAMESPACE &> /dev/null; then
    # Monitoring namespace doesn't exist, install monitoring stack
    echo "Setting up monitoring stack (namespace doesn't exist)..."
    chmod +x k8s/monitoring/install-monitoring.sh
    MONITORING_NAMESPACE=$MONITORING_NAMESPACE k8s/monitoring/install-monitoring.sh || {
      echo "Warning: Monitoring stack installation failed, but continuing with deployment"
    }
  elif ! kubectl get deployment -n $MONITORING_NAMESPACE prometheus-stack-grafana &> /dev/null; then
    # Grafana not found, install monitoring stack
    echo "Setting up monitoring stack (Grafana not found)..."
    chmod +x k8s/monitoring/install-monitoring.sh
    MONITORING_NAMESPACE=$MONITORING_NAMESPACE k8s/monitoring/install-monitoring.sh || {
      echo "Warning: Monitoring stack installation failed, but continuing with deployment"
    }
  else
    # Monitoring already set up, check if dashboards need to be imported
    echo "Monitoring stack already installed, checking dashboards..."
    if ! kubectl get configmap -n $MONITORING_NAMESPACE grafana-game-server-dashboard &> /dev/null; then
      echo "Importing game server dashboard..."
      chmod +x k8s/monitoring/import-dashboard.sh
      SKIP_DASHBOARD_IMPORT=false MONITORING_NAMESPACE=$MONITORING_NAMESPACE k8s/monitoring/import-dashboard.sh || {
        echo "Warning: Dashboard import failed, but continuing with deployment"
      }
    else
      echo "Game server dashboard already imported, skipping monitoring setup."
    fi
  fi
else
  echo "SKIP_MONITORING is set to true, skipping monitoring stack setup."
fi

# Replace registry name in YAML files
for file in k8s/*.yaml; do
  sed "s|\${REGISTRY_NAME}|$REGISTRY_NAME|g" "$file" > "$file.tmp"
  mv "$file.tmp" "$file"
done

# Apply Kubernetes manifests
kubectl apply -f k8s/master-server.yaml -n $NAMESPACE
kubectl apply -f k8s/game-server.yaml -n $NAMESPACE

echo "Deployment completed successfully!"
echo "To deploy client simulator: kubectl apply -f k8s/client-simulator.yaml -n $NAMESPACE"
echo "To monitor game server scaling: kubectl get hpa game-server-hpa -n $NAMESPACE --watch"

# Add more helpful commands for monitoring
echo ""
echo "==== MONITORING COMMANDS ===="
echo "View all pods: kubectl get pods -n $NAMESPACE"
echo "Watch game server autoscaling: kubectl get hpa game-server-hpa -n $NAMESPACE --watch"
echo "Watch pods being created/deleted: kubectl get pods -n $NAMESPACE --watch"
echo ""
echo "==== LOG COMMANDS ===="
echo "View logs from all game servers: kubectl logs -f -l app=game-server -n $NAMESPACE"
echo "View logs from all client simulators: kubectl logs -f -l app=client-simulator -n $NAMESPACE"
echo "View logs from a specific pod: kubectl logs -f <pod-name> -n $NAMESPACE"
echo "View logs with timestamps: kubectl logs -f <pod-name> -n $NAMESPACE --timestamps"
echo ""
echo "==== TESTING COMMANDS ===="
echo "Deploy more client simulators to trigger scaling: kubectl scale deployment client-simulator -n $NAMESPACE --replicas=10"
echo "Restart client simulators: kubectl rollout restart deployment client-simulator -n $NAMESPACE"
echo ""

# Check if monitoring is setup
if kubectl get namespace $MONITORING_NAMESPACE &> /dev/null; then
  echo "==== MONITORING DASHBOARD ===="
  # Check if Grafana is ready
  GRAFANA_READY=$(kubectl get pod -l app.kubernetes.io/name=grafana -n $MONITORING_NAMESPACE -o jsonpath='{.items[0].status.containerStatuses[?(@.name=="grafana")].ready}' 2>/dev/null)
  
  if [ "$GRAFANA_READY" = "true" ]; then
    GRAFANA_CONTAINER_PORT=$(kubectl get svc -n $MONITORING_NAMESPACE prometheus-stack-grafana -o jsonpath='{.spec.ports[0].port}' 2>/dev/null || echo "80")
    echo "Grafana is ready! Access at:"
    echo "- URL: http://localhost:3000 (after running port-forward)"
    echo "- Command: kubectl port-forward svc/prometheus-stack-grafana 3000:$GRAFANA_CONTAINER_PORT -n $MONITORING_NAMESPACE"
    echo "- Username: admin"
    echo "- Password: prom-operator"
  else
    echo "Grafana is not ready yet. Check its status with:"
    echo "kubectl get pods -n $MONITORING_NAMESPACE | grep grafana"
    echo "kubectl describe pod -l app.kubernetes.io/name=grafana -n $MONITORING_NAMESPACE"
  fi
  
  # Show status of key monitoring components
  echo ""
  echo "Monitoring component status:"
  echo "- Prometheus: $(kubectl get pods -n $MONITORING_NAMESPACE -l app.kubernetes.io/name=prometheus -o jsonpath='{.items[0].status.phase}' 2>/dev/null || echo "Not found")"
  echo "- Grafana: $(kubectl get pods -n $MONITORING_NAMESPACE -l app.kubernetes.io/name=grafana -o jsonpath='{.items[0].status.phase}' 2>/dev/null || echo "Not found")"
  echo "- Loki: $(kubectl get pods -n $MONITORING_NAMESPACE -l app=loki -o jsonpath='{.items[0].status.phase}' 2>/dev/null || echo "Not found")"
fi
