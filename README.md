# Game Server Template Layout

A proof of concept for an MMO architecture with horizontal autoscaling using C#, Kubernetes, and socket-based communication. This project demonstrates how to replace a Photon on-premise v5 architecture with a custom architecture based on Kubernetes.

## Architecture

The system consists of the following components:

1. **Master Server**: Handles client connections and routes them to available game servers. It keeps track of all game servers and their current player counts.

2. **Game Servers**: Simple socket servers that handle game logic and player connections. They register with the master server and send regular heartbeats with their current status.

3. **Client Simulator**: Simulates multiple clients connecting to the master server and sending actions to game servers.

4. **Kubernetes Horizontal Pod Autoscaler (HPA)**: Automatically scales the number of game server pods based on CPU and memory utilization.

## Prerequisites

- .NET 6.0 SDK
- Docker
- Kubernetes cluster (Minikube, Docker Desktop, or a cloud provider)
- kubectl

## Getting Started

### Local Development

1. Build the solution:
   ```
   cd src
   dotnet build
   ```

2. Run the Master Server:
   ```
   cd src/MasterServer
   dotnet run
   ```

3. Run a Game Server:
   ```
   cd src/GameServer
   dotnet run
   dotnet run -- --master-host localhost --master-port 7000 --port 7100
   ```

4. Run the Client Simulator:
   ```
   cd src/ClientSimulator
   dotnet run
   dotnet run -- --master-host localhost --master-port 7000 --num-clients 10
   ```

### Kubernetes Deployment

1. Update the registry name in `build-and-deploy.sh` to point to your container registry.

2. Make the script executable:
   ```
   chmod +x build-and-deploy.sh
   ```

3. Run the build and deploy script:
   ```
   ./build-and-deploy.sh
   ```

4. Deploy the client simulator:
   ```
   kubectl apply -f k8s/client-simulator.yaml -n gs-template
   ```

5. Monitor the game server scaling:
   ```
   kubectl get hpa game-server-hpa -n gs-template --watch
   ```

## Testing

The client simulator will create multiple fake clients that connect to the master server, which then routes them to available game servers. The clients will send random actions to the game servers, simulating player activity.

As the load increases, the HPA will automatically scale up the number of game server pods to handle the increased load. When the load decreases, the HPA will scale down the number of pods.

## Monitoring

The project includes a basic Prometheus and Grafana setup for monitoring the Kubernetes cluster and the game servers.

### Deploying the Monitoring Stack

1. Deploy Prometheus:
   ```
   kubectl apply -f k8s/monitoring/prometheus-config.yaml
   kubectl apply -f k8s/monitoring/prometheus.yaml
   ```

2. Deploy Grafana:
   ```
   kubectl apply -f k8s/monitoring/grafana.yaml
   ```

3. Access the Grafana dashboard:
   ```
   kubectl port-forward svc/grafana 3000:3000 -n monitoring
   ```

   Then open your browser and navigate to `http://localhost:3000`. The default credentials are:
   - Username: admin
   - Password: admin

4. Access the Prometheus dashboard:
   ```
   kubectl port-forward svc/prometheus 9090:9090 -n monitoring
   ```

   Then open your browser and navigate to `http://localhost:9090`.

### Useful Metrics to Monitor

- CPU and memory usage of game server pods
- Number of game server pods
- Number of connected clients
- Network traffic
- Response times

## Future Enhancements

- Add Loki for log aggregation
- Implement custom metrics for scaling based on player count
- Add authentication and authorization
- Implement game state persistence
- Add WebSocket support for browser clients  
- Configure k8s for GameServer dependency on master server 
