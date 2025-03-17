#!/bin/bash
set -e

# Configuration
REGISTRY_NAME="localhost:5000"  # Change this to your registry
NAMESPACE="gs-template"

# Build the solution
echo "Building solution..."
cd src
dotnet build

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

# Replace registry name in YAML files
for file in k8s/*.yaml; do
  sed "s|\${REGISTRY_NAME}|$REGISTRY_NAME|g" $file > $file.tmp
  mv $file.tmp $file
done

# Apply Kubernetes manifests
kubectl apply -f k8s/master-server.yaml -n $NAMESPACE
kubectl apply -f k8s/game-server.yaml -n $NAMESPACE

echo "Deployment completed successfully!"
echo "To deploy client simulator: kubectl apply -f k8s/client-simulator.yaml -n $NAMESPACE"
echo "To monitor game server scaling: kubectl get hpa game-server-hpa -n $NAMESPACE --watch" 