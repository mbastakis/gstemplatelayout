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

# Install metrics-server for HPA to work properly
echo "Setting up metrics-server..."
chmod +x k8s/metrics-server/install-metrics-server.sh
./k8s/metrics-server/install-metrics-server.sh

# Install monitoring stack if not already installed
if ! kubectl get namespace $MONITORING_NAMESPACE &> /dev/null || \
   ! kubectl get deployment -n $MONITORING_NAMESPACE grafana &> /dev/null; then
  echo "Setting up monitoring stack..."
  chmod +x k8s/monitoring/install-monitoring.sh
  ./k8s/monitoring/install-monitoring.sh
else
  echo "Monitoring stack is already installed, skipping installation."
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

# Add Grafana dashboard info
echo ""
echo "==== MONITORING DASHBOARD ===="
echo "Grafana URL: http://$(kubectl get svc -n $MONITORING_NAMESPACE grafana -o jsonpath='{.status.loadBalancer.ingress[0].ip}'):3000"
echo "Grafana username: admin"
echo "Grafana password: $(kubectl get secret -n $MONITORING_NAMESPACE grafana -o jsonpath="{.data.admin-password}" | base64 --decode)"
